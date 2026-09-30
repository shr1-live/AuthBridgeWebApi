using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AuthBridge.SmokeClient;

public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>
/// Protocol-level checks run through the official SDK client: discovery of exactly the
/// allowed tools, an in-tenant read (AUTH-104) and a cross-tenant denial (AUTH-204).
/// The identity is whatever the transport supplies; these checks assume a tenant A caller.
/// </summary>
public static class SmokeChecks
{
    public static readonly string[] ExpectedTools =
    [
        "get_authorization_status", "get_missing_documents", "get_authorization_history", "get_required_documents",
        "validate_authorization_request", "prepare_authorization_submission", "submit_authorization_request", "get_submission_status",
    ];

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(McpClient client, CancellationToken ct)
    {
        var results = new List<CheckResult>();

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var names = tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        results.Add(new("discovers exactly the eight allowed tools",
            names.SequenceEqual(ExpectedTools.Order(StringComparer.Ordinal)), string.Join(", ", names)));
        results.Add(new("exposes no approval, reset, SQL, shell or HTTP tool",
            !names.Any(n => n.Contains("approve") || n.Contains("reset") || n.Contains("sql") || n.Contains("shell") || n.Contains("http")),
            string.Join(", ", names)));

        var status = await CallAsync(client, "get_authorization_status", new() { ["authorizationId"] = "AUTH-104" }, ct);
        results.Add(new("AUTH-104 status is readable in tenant A",
            Ok(status) && Data(status).GetProperty("status").GetString() == "AwaitingDocuments"
                && status.GetProperty("isSimulation").GetBoolean(),
            status.ToString()));

        var missing = await CallAsync(client, "get_missing_documents", new() { ["authorizationId"] = "AUTH-104" }, ct);
        results.Add(new("AUTH-104 is missing only its ReferralLetter",
            Ok(missing) && Data(missing).GetProperty("missing").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["ReferralLetter"])
                && Data(missing).GetProperty("present").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["ImagingReport"]),
            missing.ToString()));

        var history = await CallAsync(client, "get_authorization_history", new() { ["authorizationId"] = "AUTH-104" }, ct);
        results.Add(new("AUTH-104 history is ordered and non-empty",
            Ok(history) && Data(history).GetProperty("total").GetInt32() >= 1, history.ToString()));

        var denied = await CallAsync(client, "get_authorization_status", new() { ["authorizationId"] = "AUTH-204" }, ct);
        results.Add(new("AUTH-204 (tenant B) is denied without leaking data",
            !Ok(denied) && denied.GetProperty("error").GetProperty("code").GetString() == "NOT_FOUND"
                && (!denied.TryGetProperty("data", out var leaked) || leaked.ValueKind == JsonValueKind.Null),
            denied.ToString()));

        return results;
    }

    public static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        CallToolResult result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        if (result.StructuredContent is not null)
            return JsonSerializer.SerializeToElement(result.StructuredContent);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
            ?? throw new InvalidOperationException($"{tool} returned no content.");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static bool Ok(JsonElement envelope) => envelope.GetProperty("ok").GetBoolean();

    public static JsonElement Data(JsonElement envelope) => envelope.GetProperty("data");
}

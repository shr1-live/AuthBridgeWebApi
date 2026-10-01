using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthBridge.Application.Common;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AuthBridge.Api.Assistant;

public sealed record AssistantMessage(string Role, string Text);

public sealed record AssistantChatRequest(IReadOnlyList<AssistantMessage>? Messages);

/// <summary>One MCP tool call the assistant made, shown to the user as it happened.</summary>
public sealed record AssistantToolStep(string Tool, JsonElement Input, bool Ok, string? ErrorCode, JsonElement? Result);

public sealed record AssistantReply(string Text, IReadOnlyList<AssistantToolStep> Steps, string Model);

public sealed record AssistantToolInfo(string Name, string? Title, string? Description, bool ReadOnly);

public sealed record AssistantInfo(bool Enabled, string Model, IReadOnlyList<AssistantToolInfo> Tools);

/// <summary>Counts messages per UTC day across the whole service.</summary>
public sealed class AssistantUsage(TimeProvider time)
{
    private readonly Lock _gate = new();
    private DateOnly _day;
    private int _count;

    public bool TryTake(int limit)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            if (today != _day)
                (_day, _count) = (today, 0);
            if (_count >= limit)
                return false;
            _count++;
            return true;
        }
    }
}

/// <summary>
/// The in-app assistant. It connects to this server's own /mcp endpoint with the caller's bearer
/// token, offers the model exactly the tools that endpoint lists, and runs the tool calls the
/// model asks for. Identity, tenant and role are therefore enforced by /mcp on every call, just
/// as for an external AI client. It has no tools of its own and no way to approve.
/// </summary>
public sealed class AssistantService(
    IAssistantModel model,
    IMcpLoopback loopback,
    AssistantUsage usage,
    IOptions<AssistantOptions> options,
    ILoggerFactory loggers)
{
    public const string NotConfigured = "ASSISTANT_NOT_CONFIGURED";
    public const string Unavailable = "ASSISTANT_UNAVAILABLE";
    private const int MaxMessages = 20;
    private const int MaxMessageLength = 4000;
    private const int MaxToolResultLength = 8000;

    private readonly ILogger _log = loggers.CreateLogger<AssistantService>();

    public async Task<Result<AssistantInfo>> DescribeAsync(string bearerToken, CancellationToken ct)
    {
        await using var client = await ConnectAsync(bearerToken, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return Result<AssistantInfo>.Success(new AssistantInfo(model.IsConfigured, model.Name,
            tools.OrderBy(t => Order(t.Name)).Select(t => new AssistantToolInfo(t.Name, t.ProtocolTool.Title, t.Description, t.ProtocolTool.Annotations?.ReadOnlyHint == true)).ToList()));
    }

    public async Task<Result<AssistantReply>> ChatAsync(string bearerToken, IReadOnlyList<AssistantMessage>? history, CancellationToken ct)
    {
        if (Validate(history) is { } invalid)
            return Result<AssistantReply>.Failure(invalid);
        if (!model.IsConfigured)
            return Fail(NotConfigured, "The assistant is not set up on this server. Add an Anthropic API key as Assistant__ApiKey.");
        if (!usage.TryTake(options.Value.DailyLimit))
            return Fail(Unavailable, "The demo assistant has reached today's message limit. Try again tomorrow.");

        await using var client = await ConnectAsync(bearerToken, ct);
        var mcpTools = await client.ListToolsAsync(cancellationToken: ct);
        var allowed = mcpTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var tools = new JsonArray(mcpTools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["input_schema"] = JsonNode.Parse(t.ProtocolTool.InputSchema.GetRawText()),
        }).ToArray());

        var messages = new JsonArray(history!.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Text }).ToArray());
        var steps = new List<AssistantToolStep>();
        try
        {
            for (var round = 0; round < options.Value.MaxToolRounds; round++)
            {
                var response = await model.CreateMessageAsync(SystemPrompt, tools, messages, ct);
                var content = response["content"] as JsonArray ?? [];
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                var calls = content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").ToList();
                if ((string?)response["stop_reason"] != "tool_use" || calls.Count == 0)
                    return Result<AssistantReply>.Success(new AssistantReply(TextOf(content), steps, model.Name));

                var results = new JsonArray();
                foreach (var call in calls)
                {
                    var (step, resultText) = await RunToolAsync(client, allowed, call, ct);
                    steps.Add(step);
                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = (string?)call["id"],
                        ["content"] = resultText,
                        ["is_error"] = !step.Ok,
                    });
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
            }
            return Result<AssistantReply>.Success(new AssistantReply(
                "I stopped after several tool calls without finishing. Try a more specific question, such as one request ID.", steps, model.Name));
        }
        catch (Exception ex) when (ex is AssistantModelException or HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogWarning("Assistant model call failed: {Reason}", ex.Message);
            return Fail(Unavailable, "The assistant could not reach the language model. Try again in a minute.");
        }
    }

    private static async Task<(AssistantToolStep Step, string ResultText)> RunToolAsync(
        McpClient client, HashSet<string> allowed, JsonObject call, CancellationToken ct)
    {
        var name = (string?)call["name"] ?? "";
        var input = call["input"] as JsonObject ?? [];
        var inputElement = JsonSerializer.SerializeToElement(input);
        if (!allowed.Contains(name))
        {
            // The model can only name tools that /mcp listed; anything else is refused here.
            const string refusal = """{"ok":false,"error":{"code":"UNKNOWN_TOOL","message":"That tool does not exist. Only the listed AuthBridge tools can be used."}}""";
            return (new AssistantToolStep(name, inputElement, false, "UNKNOWN_TOOL", JsonDocument.Parse(refusal).RootElement.Clone()), refusal);
        }

        var arguments = input.ToDictionary(p => p.Key, p => (object?)(p.Value is null ? null : JsonSerializer.SerializeToElement(p.Value)));
        CallToolResult result = await client.CallToolAsync(name, arguments, cancellationToken: ct);
        var envelope = result.StructuredContent is not null
            ? JsonSerializer.SerializeToElement(result.StructuredContent)
            : ParseOrWrap(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        var ok = result.IsError != true && envelope.TryGetProperty("ok", out var okProperty) && okProperty.ValueKind == JsonValueKind.True;
        var code = envelope.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c)
            ? c.GetString() : null;
        var text = envelope.GetRawText();
        return (new AssistantToolStep(name, inputElement, ok, ok ? null : code ?? "TOOL_ERROR", envelope),
            text.Length > MaxToolResultLength ? text[..MaxToolResultLength] : text);
    }

    private static JsonElement ParseOrWrap(string? text)
    {
        try
        {
            return JsonDocument.Parse(text ?? "{}").RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = new { code = "TOOL_ERROR", message = text } });
        }
    }

    private async Task<McpClient> ConnectAsync(string bearerToken, CancellationToken ct)
    {
        var http = loopback.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "authbridge-in-app-assistant",
        }, http, loggers, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, loggerFactory: loggers, cancellationToken: ct);
    }

    /// <summary>Workflow order (reads first), so the list reads the same every time.</summary>
    private static int Order(string tool)
    {
        var names = global::AuthBridge.Mcp.Tools.AuthBridgeTools.ToolNames;
        for (var i = 0; i < names.Count; i++)
            if (names[i] == tool)
                return i;
        return int.MaxValue;
    }

    private static string TextOf(JsonArray content) =>
        string.Join("\n\n", content.OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"]).Where(t => !string.IsNullOrWhiteSpace(t))).Trim();

    private static AppError? Validate(IReadOnlyList<AssistantMessage>? history)
    {
        if (history is not { Count: > 0 } || history.Count > MaxMessages)
            return Invalid($"Send between 1 and {MaxMessages} messages.");
        for (var i = 0; i < history.Count; i++)
        {
            var expected = i % 2 == 0 ? "user" : "assistant";
            if (history[i].Role != expected)
                return Invalid("Messages must alternate user, assistant, user, starting and ending with the user.");
            if (string.IsNullOrWhiteSpace(history[i].Text) || history[i].Text.Length > MaxMessageLength)
                return Invalid($"Each message needs 1 to {MaxMessageLength} characters.");
        }
        return history.Count % 2 == 1 ? null : Invalid("The last message must be from the user.");
    }

    private static AppError Invalid(string message) => new(ErrorCodes.InvalidInput, message, ErrorKind.Validation);

    private static Result<AssistantReply> Fail(string code, string message) =>
        Result<AssistantReply>.Failure(new AppError(code, message, ErrorKind.Unavailable));

    private const string SystemPrompt = """
        You are the assistant inside AuthBridge, a synthetic prior-authorization demo. You act only
        through the AuthBridge tools, which run with the signed-in user's own permissions.

        Rules:
        - All data is invented demo data. Never present it as real patient or payer data, and never give medical advice.
        - You cannot approve a submission or attach documents. A person approves on the review page, and documents
          are attached in the website. After prepare_authorization_submission, give the user the reviewUrl and ask
          them to approve it there.
        - Call get_authorization_status first to get the current version before validate_authorization_request or
          prepare_authorization_submission.
        - Call submit_authorization_request only after the user says they approved. Use the idempotency key
          "assistant-" followed by the proposal ID, and reuse it if you retry.
        - There is no tool to list or search requests. If asked, suggest the Requests page.
        - If a tool returns ok=false, explain the error code in plain words. NOT_FOUND means the request does not
          exist in this user's tenant. FORBIDDEN means the user's role is read-only.
        - Reply in short, plain English: a few sentences or a short list.

        Plain names: DEMO-PAYER-A is "Demo Health Plan A", DEMO-PAYER-B is "Demo Health Plan B". Services:
        DEMO-MRI MRI scan, DEMO-CT CT scan, DEMO-ULTRASOUND ultrasound scan, DEMO-PHYSIO physiotherapy,
        DEMO-REHAB rehab programme, DEMO-SURGERY planned surgery, DEMO-SPECIALIST specialist visit.
        Rule versions are usually "1".
        """;
}

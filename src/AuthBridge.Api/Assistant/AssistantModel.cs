using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AuthBridge.Api.Assistant;

public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    /// <summary>Anthropic API key. Server-side only (env Assistant__ApiKey or ANTHROPIC_API_KEY); never sent to the browser.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5-5";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public int MaxTokens { get; set; } = 1024;
    /// <summary>Model turns allowed per user message, so a confused model cannot loop forever.</summary>
    public int MaxToolRounds { get; set; } = 8;
    /// <summary>Messages one user may send per hour.</summary>
    public int PerUserHourlyLimit { get; set; } = 30;
    /// <summary>Messages the whole service may send per UTC day, to cap API spend on the public demo.</summary>
    public int DailyLimit { get; set; } = 300;
}

/// <summary>One call to the language model: the Messages API request and response, as JSON.</summary>
public interface IAssistantModel
{
    bool IsConfigured { get; }
    string Name { get; }
    Task<JsonObject> CreateMessageAsync(string system, JsonArray tools, JsonArray messages, CancellationToken ct);
}

public sealed class AssistantModelException(string message) : Exception(message);

/// <summary>Anthropic Messages API over plain HTTP. The key stays in this process.</summary>
public sealed class AnthropicAssistantModel(HttpClient http, IOptions<AssistantOptions> options) : IAssistantModel
{
    private readonly AssistantOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string Name => _options.Model;

    public async Task<JsonObject> CreateMessageAsync(string system, JsonArray tools, JsonArray messages, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["system"] = system,
            ["tools"] = tools.DeepClone(),
            ["messages"] = messages.DeepClone(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            // Status only: the body can echo request details and is not needed to diagnose.
            throw new AssistantModelException($"The model API returned {(int)response.StatusCode}.");
        return JsonNode.Parse(text) as JsonObject ?? throw new AssistantModelException("The model API returned an unexpected body.");
    }
}

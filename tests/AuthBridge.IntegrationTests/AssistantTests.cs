using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthBridge.Api.Assistant;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Seeding;
using AuthBridge.IntegrationTests.Harness;
using AuthBridge.Mcp.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AuthBridge.IntegrationTests;

/// <summary>Stands in for the language model: each test scripts what the "model" asks for.</summary>
public sealed class ScriptedModel : IAssistantModel
{
    public bool IsConfigured { get; set; } = true;
    public string Name => "scripted-test-model";
    public Func<JsonArray, JsonObject> Respond { get; set; } = _ => Text("Hello.");
    public JsonArray? LastTools { get; private set; }

    public Task<JsonObject> CreateMessageAsync(string system, JsonArray tools, JsonArray messages, CancellationToken ct)
    {
        LastTools = (JsonArray)tools.DeepClone();
        return Task.FromResult(Respond(messages));
    }

    public static JsonObject Text(string text) => new()
    {
        ["stop_reason"] = "end_turn",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    public static JsonObject ToolUse(string name, JsonObject input) => new()
    {
        ["stop_reason"] = "tool_use",
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "Let me check." },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = name, ["input"] = input }),
    };

    /// <summary>Calls one tool, then answers with the tool result it got back.</summary>
    public static Func<JsonArray, JsonObject> CallThenEcho(string name, JsonObject input) => messages =>
        messages[^1]?["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(b => (string?)b["type"] == "tool_result")
            ? Text("Result: " + (string?)blocks.OfType<JsonObject>().First()["content"])
            : ToolUse(name, (JsonObject)input.DeepClone());
}

/// <summary>Points the assistant's MCP client at the in-memory test server.</summary>
public sealed class TestServerLoopback(IServiceProvider services) : IMcpLoopback
{
    public HttpClient CreateClient() =>
        new(((TestServer)services.GetRequiredService<IServer>()).CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
}

[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class AssistantTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private readonly ScriptedModel _model = new();
    private WebApplicationFactory<Program> _assistantApp = null!;

    [OneTimeSetUp]
    public void StartAssistant() => _assistantApp = WithAssistant(_model);

    [OneTimeTearDown]
    public async Task StopAssistant() => await _assistantApp.DisposeAsync();

    [SetUp]
    public void ResetModel()
    {
        _model.IsConfigured = true;
        _model.Respond = _ => ScriptedModel.Text("Hello.");
    }

    private WebApplicationFactory<Program> WithAssistant(IAssistantModel model, int hourlyLimit = 30) =>
        App.WithWebHostBuilder(b =>
        {
            b.UseSetting("Assistant:PerUserHourlyLimit", hourlyLimit.ToString());
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(model);
                s.AddSingleton<IMcpLoopback>(sp => new TestServerLoopback(sp));
            });
        });

    private HttpClient Client(string? subject, WebApplicationFactory<Program>? app = null)
    {
        var client = (app ?? _assistantApp).CreateClient();
        if (subject is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", App.TokenFor(subject));
        return client;
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, string text) =>
        client.PostJsonAsync("/api/v1/assistant/messages", new { messages = new[] { new { role = "user", text } } });

    private static JsonElement OnlyStep(JsonElement reply)
    {
        var steps = reply.GetProperty("steps");
        Assert.That(steps.GetArrayLength(), Is.EqualTo(1), reply.ToString());
        return steps[0];
    }

    [Test]
    public async Task Describe_lists_exactly_the_eight_mcp_tools_and_no_approve()
    {
        using var client = Client(SeedUsers.CoordinatorA);
        var info = await (await client.GetAsync("/api/v1/assistant")).JsonAsync();
        var names = info.GetProperty("tools").EnumerateArray().Select(t => t.Str("name")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(info.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(names, Is.EquivalentTo(AuthBridgeTools.ToolNames));
            Assert.That(names, Has.None.Contains("approve"));
        });
    }

    [Test]
    public async Task Answers_by_calling_the_mcp_tools()
    {
        _model.Respond = ScriptedModel.CallThenEcho("get_missing_documents", new JsonObject { ["authorizationId"] = "AUTH-104" });
        using var client = Client(SeedUsers.CoordinatorA);
        var response = await AskAsync(client, "What's missing on AUTH-104?");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var reply = await response.JsonAsync();
        var step = OnlyStep(reply);
        Assert.Multiple(() =>
        {
            Assert.That(step.Str("tool"), Is.EqualTo("get_missing_documents"));
            Assert.That(step.GetProperty("ok").GetBoolean(), Is.True);
            Assert.That(step.GetProperty("result").GetProperty("data").GetProperty("missing")[0].GetString(), Is.EqualTo("ReferralLetter"));
            Assert.That(reply.Str("text"), Does.Contain("ReferralLetter"), "the model saw the tool result");
            Assert.That(_model.LastTools!.Select(t => (string?)t!["name"]), Is.EquivalentTo(AuthBridgeTools.ToolNames));
        });
    }

    [Test]
    public async Task Tool_calls_run_with_the_signed_in_users_tenant()
    {
        _model.Respond = ScriptedModel.CallThenEcho("get_authorization_status", new JsonObject { ["authorizationId"] = "AUTH-204" });
        using var tenantA = Client(SeedUsers.CoordinatorA);
        using var tenantB = Client(SeedUsers.CoordinatorB);
        var fromA = OnlyStep(await (await AskAsync(tenantA, "Status of AUTH-204?")).JsonAsync());
        var fromB = OnlyStep(await (await AskAsync(tenantB, "Status of AUTH-204?")).JsonAsync());
        Assert.Multiple(() =>
        {
            Assert.That(fromA.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(fromA.Str("errorCode"), Is.EqualTo("NOT_FOUND"), "tenant A cannot see tenant B through the assistant");
            Assert.That(fromB.GetProperty("ok").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task A_viewer_cannot_change_anything_through_the_assistant()
    {
        var version = await VersionAsync("AUTH-105");
        _model.Respond = ScriptedModel.CallThenEcho("prepare_authorization_submission",
            new JsonObject { ["authorizationId"] = "AUTH-105", ["expectedVersion"] = version.ToString() });
        using var viewer = Client(SeedUsers.ViewerA);
        var step = OnlyStep(await (await AskAsync(viewer, "Prepare AUTH-105")).JsonAsync());
        Assert.That(step.Str("errorCode"), Is.EqualTo("FORBIDDEN"));
        Assert.That(await VersionAsync("AUTH-105"), Is.EqualTo(version), "nothing changed");
    }

    [Test]
    public async Task The_model_cannot_use_a_tool_that_mcp_did_not_list()
    {
        _model.Respond = ScriptedModel.CallThenEcho("approve_submission", new JsonObject { ["proposalId"] = SeedIds.StaleProposal.ToString() });
        using var client = Client(SeedUsers.CoordinatorA);
        var reply = await (await AskAsync(client, "Approve it for me")).JsonAsync();
        Assert.That(OnlyStep(reply).Str("errorCode"), Is.EqualTo("UNKNOWN_TOOL"));
    }

    [Test]
    public async Task Without_a_model_key_chat_is_503_but_the_tools_still_list()
    {
        _model.IsConfigured = false;
        using var client = Client(SeedUsers.CoordinatorA);
        var chat = await AskAsync(client, "Hello");
        var info = await (await client.GetAsync("/api/v1/assistant")).JsonAsync();
        var code = await chat.ProblemCodeAsync();
        Assert.Multiple(() =>
        {
            Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(code, Is.EqualTo(AssistantService.NotConfigured));
            Assert.That(info.GetProperty("enabled").GetBoolean(), Is.False);
            Assert.That(info.GetProperty("tools").GetArrayLength(), Is.EqualTo(8));
        });
    }

    [Test]
    public async Task A_model_failure_is_a_clear_503()
    {
        _model.Respond = _ => throw new AssistantModelException("The model API returned 529.");
        using var client = Client(SeedUsers.CoordinatorA);
        var response = await AskAsync(client, "Hello");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo(AssistantService.Unavailable));
    }

    [Test]
    public async Task Malformed_history_is_400()
    {
        using var client = Client(SeedUsers.CoordinatorA);
        var response = await client.PostJsonAsync("/api/v1/assistant/messages",
            new { messages = new[] { new { role = "user", text = "Hi" }, new { role = "assistant", text = "Hello" } } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("INVALID_INPUT"));
    }

    [Test]
    public async Task Anonymous_callers_are_401()
    {
        using var client = Client(null);
        Assert.That((await AskAsync(client, "Hello")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Each_user_is_rate_limited()
    {
        await using var limited = WithAssistant(new ScriptedModel(), hourlyLimit: 2);
        using var client = Client(SeedUsers.CoordinatorA, limited);
        Assert.That((await AskAsync(client, "1")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await AskAsync(client, "2")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var third = await AskAsync(client, "3");
        Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(await third.ProblemCodeAsync(), Is.EqualTo(AssistantSetup.RateLimited));
        using var other = Client(SeedUsers.SecondCoordinatorA, limited);
        Assert.That((await AskAsync(other, "1")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "limits are per user");
    }
}

public class McpLoopbackTests
{
    [TestCase("http://0.0.0.0:10000", "http://127.0.0.1:10000/")]
    [TestCase("http://[::]:8080", "http://127.0.0.1:8080/")]
    [TestCase("http://+:80", "http://127.0.0.1:80/")]
    [TestCase("http://localhost:5243", "http://localhost:5243/")]
    public void Listening_addresses_become_loopback_urls(string address, string expected) =>
        Assert.That(ServerAddressLoopback.ToLoopback(address), Is.EqualTo(expected));
}

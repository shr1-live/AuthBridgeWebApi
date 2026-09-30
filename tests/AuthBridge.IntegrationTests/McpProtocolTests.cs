using System.Diagnostics;
using System.Net;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Seeding;
using AuthBridge.IntegrationTests.Harness;
using AuthBridge.SmokeClient;
using ModelContextProtocol.Client;

namespace AuthBridge.IntegrationTests;

/// <summary>Hosted Streamable HTTP MCP through the official SDK client, identity from the bearer token.</summary>
[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class HttpMcpTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private async Task<McpClient> ConnectAsync(string subject)
    {
        var http = App.ClientFor(subject);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    [Test]
    public async Task Smoke_checks_pass_over_http_for_a_tenant_a_coordinator()
    {
        await using var client = await ConnectAsync(SeedUsers.CoordinatorA);
        var results = await SmokeChecks.RunAsync(client, CancellationToken.None);
        foreach (var r in results)
            Assert.That(r.Passed, Is.True, $"{r.Name}: {r.Detail}");
    }

    [Test]
    public async Task Mcp_without_a_token_is_401()
    {
        var response = await App.CreateClient().PostAsync("/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Identity_follows_the_token_not_a_fixed_admin()
    {
        await using var tenantB = await ConnectAsync(SeedUsers.CoordinatorB);
        var own = await SmokeChecks.CallAsync(tenantB, "get_authorization_status", new() { ["authorizationId"] = "AUTH-204" }, CancellationToken.None);
        var foreign = await SmokeChecks.CallAsync(tenantB, "get_authorization_status", new() { ["authorizationId"] = "AUTH-104" }, CancellationToken.None);
        Assert.That(SmokeChecks.Ok(own), Is.True);
        Assert.That(SmokeChecks.Ok(foreign), Is.False);
        Assert.That(foreign.GetProperty("error").Str("code"), Is.EqualTo("NOT_FOUND"));
    }

    [Test]
    public async Task Inactive_subject_is_refused_by_every_tool()
    {
        await using var client = await ConnectAsync(SeedUsers.InactiveA);
        var result = await SmokeChecks.CallAsync(client, "get_authorization_status", new() { ["authorizationId"] = "AUTH-104" }, CancellationToken.None);
        Assert.That(result.GetProperty("error").Str("code"), Is.EqualTo("ACCESS_NOT_PROVISIONED"));
    }

    [Test]
    public async Task Viewer_cannot_write_through_mcp()
    {
        await using var client = await ConnectAsync(SeedUsers.ViewerA);
        var version = (await StatusAsync("AUTH-104")).Str("version");
        var result = await SmokeChecks.CallAsync(client, "validate_authorization_request",
            new() { ["authorizationId"] = "AUTH-104", ["expectedVersion"] = version }, CancellationToken.None);
        Assert.That(result.GetProperty("error").Str("code"), Is.EqualTo("FORBIDDEN"));
    }

    [Test]
    public async Task Full_flow_needs_a_human_ui_approval_between_prepare_and_submit()
    {
        await using var client = await ConnectAsync(SeedUsers.CoordinatorA);
        var ct = CancellationToken.None;

        var status = await SmokeChecks.CallAsync(client, "get_authorization_status", new() { ["authorizationId"] = "AUTH-105" }, ct);
        var version = SmokeChecks.Data(status).Str("version");
        var validated = await SmokeChecks.CallAsync(client, "validate_authorization_request",
            new() { ["authorizationId"] = "AUTH-105", ["expectedVersion"] = version }, ct);
        Assert.That(SmokeChecks.Data(validated).Str("status"), Is.EqualTo("ReadyToSubmit"));

        var prepared = await SmokeChecks.CallAsync(client, "prepare_authorization_submission",
            new() { ["authorizationId"] = "AUTH-105", ["expectedVersion"] = SmokeChecks.Data(validated).Str("version") }, ct);
        var proposalId = SmokeChecks.Data(prepared).Str("proposalId");
        Assert.That(SmokeChecks.Data(prepared).Str("reviewUrl"), Does.EndWith("/proposals/" + proposalId));
        Assert.That(prepared.GetProperty("isSimulation").GetBoolean(), Is.True);

        var early = await SmokeChecks.CallAsync(client, "submit_authorization_request",
            new() { ["proposalId"] = proposalId, ["idempotencyKey"] = "mcp-1" }, ct);
        Assert.That(early.GetProperty("error").Str("code"), Is.EqualTo("PROPOSAL_NOT_APPROVED"));

        // The human step: the Angular review page calls the approval endpoint.
        Assert.That((await ApproveAsync(Guid.Parse(proposalId))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var submitted = await SmokeChecks.CallAsync(client, "submit_authorization_request",
            new() { ["proposalId"] = proposalId, ["idempotencyKey"] = "mcp-1" }, ct);
        Assert.That(SmokeChecks.Ok(submitted), Is.True, submitted.ToString());
        var attemptId = SmokeChecks.Data(submitted).Str("attemptId");

        var polled = await SmokeChecks.CallAsync(client, "get_submission_status", new() { ["attemptId"] = attemptId }, ct);
        Assert.That(SmokeChecks.Data(polled).Str("state"), Is.EqualTo("Queued"));
    }

    [Test]
    public async Task Tool_inputs_are_validated()
    {
        await using var client = await ConnectAsync(SeedUsers.CoordinatorA);
        var ct = CancellationToken.None;
        var badGuid = await SmokeChecks.CallAsync(client, "get_submission_status", new() { ["attemptId"] = "nope" }, ct);
        Assert.That(badGuid.GetProperty("error").Str("code"), Is.EqualTo("INVALID_INPUT"));
        var badPage = await SmokeChecks.CallAsync(client, "get_authorization_history",
            new() { ["authorizationId"] = "AUTH-104", ["pageSize"] = 500 }, ct);
        Assert.That(badPage.GetProperty("error").Str("code"), Is.EqualTo("INVALID_INPUT"));
        var inactive = await SmokeChecks.CallAsync(client, "get_required_documents",
            new() { ["payerCode"] = "DEMO-PAYER-A", ["serviceCode"] = "DEMO-CT", ["ruleVersion"] = "2" }, ct);
        Assert.That(inactive.GetProperty("error").Str("code"), Is.EqualTo("CONFIGURATION_MISSING"));
    }
}

/// <summary>The local stdio host, launched as a real child process over the test database.</summary>
[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class StdioMcpTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private static string ServerDll => Path.Combine(AppContext.BaseDirectory, "AuthBridge.Mcp.dll");

    private StdioClientTransport Transport(string environment, string subject) => new(new StdioClientTransportOptions
    {
        Name = "authbridge-test",
        Command = "dotnet",
        Arguments = [ServerDll],
        WorkingDirectory = AppContext.BaseDirectory,
        EnvironmentVariables = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = environment,
            ["Database__Provider"] = Provider.ToString(),
            ["Database__ConnectionString"] = Database.ConnectionString,
            ["Mcp__LocalUserSubject"] = subject,
        },
    });

    [Test]
    public async Task Smoke_checks_pass_over_stdio()
    {
        await using var client = await McpClient.CreateAsync(Transport("Development", SeedUsers.CoordinatorA));
        var results = await SmokeChecks.RunAsync(client, CancellationToken.None);
        foreach (var r in results)
            Assert.That(r.Passed, Is.True, $"{r.Name}: {r.Detail}");
    }

    [Test]
    public async Task Stdio_refuses_an_inactive_configured_subject()
    {
        await using var client = await McpClient.CreateAsync(Transport("Development", SeedUsers.InactiveA));
        var result = await SmokeChecks.CallAsync(client, "get_authorization_status", new() { ["authorizationId"] = "AUTH-104" }, CancellationToken.None);
        Assert.That(result.GetProperty("error").Str("code"), Is.EqualTo("ACCESS_NOT_PROVISIONED"));
    }

    [Test]
    public async Task Stdio_host_refuses_to_start_outside_development()
    {
        var start = new ProcessStartInfo("dotnet", $"\"{ServerDll}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Mcp__LocalUserSubject"] = SeedUsers.CoordinatorA;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(process.ExitCode, Is.EqualTo(1));
        Assert.That(stderr, Does.Contain("Development"));
        Assert.That(await stdout, Is.Empty, "stdout stays protocol-only");
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthBridge.IntegrationTests;

/// <summary>
/// Synthetic demo: the Production host with no settings at all (a bare Render service) starts on
/// an ephemeral SQLite database with demo sign-in. This checks the zero-configuration startup
/// path only; provider behaviour is proven on real SQL Server and PostgreSQL elsewhere.
/// </summary>
[TestFixture]
public class DemoModeTests
{
    private sealed class DemoApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Simulation:Enabled", "false");
        }
    }

    private DemoApp _app = null!;

    [OneTimeSetUp]
    public void Start() => _app = new DemoApp();

    [OneTimeTearDown]
    public void Stop()
    {
        _app.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(AuthBridge.Api.Infrastructure.DemoMode.DefaultDatabasePath); } catch (IOException) { }
    }

    private async Task<HttpClient> SignedInAsync(string subject)
    {
        var client = _app.CreateClient();
        var token = await (await client.PostAsJsonAsync("/demo/token", new { subjectId = subject })).Content.ReadFromJsonAsync<Dictionary<string, object>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!["accessToken"].ToString());
        return client;
    }

    [Test]
    public async Task Starts_with_no_external_services_and_is_ready()
    {
        var ready = await _app.CreateClient().GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Demo_sign_in_lists_seeded_users_and_issues_working_tokens()
    {
        var users = await _app.CreateClient().GetStringAsync("/demo/users");
        Assert.That(users, Does.Contain("TENANT-A"));

        var client = await SignedInAsync(AuthBridge.Infrastructure.Seeding.SeedUsers.CoordinatorA);
        var list = await (await client.GetAsync("/api/v1/authorizations?pageSize=100")).JsonAsync();
        Assert.That(list.GetProperty("total").GetInt32(), Is.EqualTo(12));
    }

    [Test]
    public async Task Full_workflow_runs_on_the_demo_database()
    {
        var client = await SignedInAsync(AuthBridge.Infrastructure.Seeding.SeedUsers.CoordinatorA);
        var version = (await (await client.GetAsync("/api/v1/authorizations/AUTH-105")).JsonAsync()).Guid("version");
        var prepared = await client.PostJsonAsync("/api/v1/authorizations/AUTH-105/submission-proposals", new { expectedVersion = version });
        Assert.That(prepared.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var proposalId = (await prepared.JsonAsync()).Guid("proposalId");
        Assert.That((await client.PostAsync($"/api/v1/submission-proposals/{proposalId}/approve", null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var submitted = await client.PostJsonAsync("/api/v1/submissions", new { proposalId, idempotencyKey = "demo-1" });
        Assert.That(submitted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var replay = await client.PostJsonAsync("/api/v1/submissions", new { proposalId, idempotencyKey = "demo-1" });
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Development_endpoints_stay_unmapped()
    {
        Assert.That((await _app.CreateClient().GetAsync("/dev/users")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase("https://authbridge-web.vercel.app", true)]
    [TestCase("http://localhost:4200", true)]
    [TestCase("https://evil.example", false)]
    [TestCase("http://authbridge.vercel.app", false)]
    public async Task Cors_allows_vercel_and_localhost_only(string origin, bool allowed)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/authorizations");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        var response = await _app.CreateClient().SendAsync(request);
        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.EqualTo(allowed));
    }
}

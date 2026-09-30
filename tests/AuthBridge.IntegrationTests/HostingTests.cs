using System.Net;
using AuthBridge.Infrastructure;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthBridge.IntegrationTests;

[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class HostingTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/authorizations");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return await App.CreateClient().SendAsync(request);
    }

    [Test]
    public async Task Cors_allows_the_exact_ui_origin()
    {
        var response = await PreflightAsync(AuthBridgeApp.UiOrigin);
        Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single(), Is.EqualTo(AuthBridgeApp.UiOrigin));
    }

    [TestCase("https://evil.example")]
    [TestCase("http://localhost:4201")]
    [TestCase("https://localhost:4200")]
    public async Task Cors_rejects_unrelated_origins(string origin)
    {
        var response = await PreflightAsync(origin);
        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.False);
    }

    [Test]
    public async Task Health_endpoints_are_anonymous_and_ready_checks_the_database()
    {
        var client = App.CreateClient();
        Assert.That((await client.GetAsync("/health/live")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var ready = await client.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ready.JsonAsync()).Str("status"), Is.EqualTo("Healthy"));
    }

    [Test]
    public async Task Correlation_id_is_echoed_when_well_formed_and_replaced_otherwise()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "test-correlation-001");
        var echoed = await App.CreateClient().SendAsync(request);
        Assert.That(echoed.Headers.GetValues("X-Correlation-Id").Single(), Is.EqualTo("test-correlation-001"));

        request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "bad id <script>");
        var replaced = await App.CreateClient().SendAsync(request);
        Assert.That(replaced.Headers.GetValues("X-Correlation-Id").Single(), Does.Match("^[0-9a-f]{32}$"));
    }

    [Test]
    public async Task Development_sign_in_is_not_mapped_outside_development()
    {
        Assert.That((await App.CreateClient().GetAsync("/dev/users")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void LocalDev_auth_is_refused_in_production()
    {
        using var production = new ProductionApp(Database);
        var ex = Assert.Catch(() => production.CreateClient());
        Assert.That(ex!.GetBaseException().Message, Does.Contain("LocalDev is refused"));
    }

    [Test]
    public void Missing_database_provider_fails_startup()
    {
        using var app = new NoProviderApp();
        var ex = Assert.Catch(() => app.CreateClient());
        Assert.That(ex!.GetBaseException().Message, Does.Contain("Database:Provider"));
    }

    [Test]
    public void Wildcard_cors_origin_fails_startup()
    {
        using var app = new WildcardCorsApp(Database);
        var ex = Assert.Catch(() => app.CreateClient());
        Assert.That(ex!.GetBaseException().Message, Does.Contain("exact origin"));
    }

    private sealed class ProductionApp(TestDatabase db) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Database:Provider", db.Provider.ToString());
            builder.UseSetting("Database:ConnectionString", db.ConnectionString);
            builder.UseSetting("Auth:Mode", "LocalDev");
            builder.UseSetting("Auth:LocalDev:SigningKey", AuthBridgeApp.SigningKey);
            builder.UseSetting("Simulation:Enabled", "false");
        }
    }

    private sealed class NoProviderApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "");
            builder.UseSetting("Simulation:Enabled", "false");
        }
    }

    private sealed class WildcardCorsApp(TestDatabase db) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", db.Provider.ToString());
            builder.UseSetting("Database:ConnectionString", db.ConnectionString);
            builder.UseSetting("Auth:Mode", "LocalDev");
            builder.UseSetting("Auth:LocalDev:SigningKey", AuthBridgeApp.SigningKey);
            builder.UseSetting("Cors:AllowedOrigins:0", "https://*.vercel.app");
            builder.UseSetting("Simulation:Enabled", "false");
        }
    }
}

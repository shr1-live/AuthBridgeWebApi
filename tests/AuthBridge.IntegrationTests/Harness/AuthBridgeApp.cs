using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthBridge.Api.Auth;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Persistence;
using AuthBridge.Infrastructure.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AuthBridge.IntegrationTests.Harness;

/// <summary>Controllable clock shared by the app under test and the assertions.</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}

/// <summary>
/// The real API host (Testing environment) against a real test database. The background
/// simulator is off so tests drive it step by step; auth uses the LocalDev issuer, which the
/// host accepts only in Development/Testing.
/// </summary>
public sealed class AuthBridgeApp : WebApplicationFactory<Program>
{
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";
    public const string UiOrigin = "http://localhost:4200";

    private readonly TestDatabase _database;

    public AuthBridgeApp(TestDatabase database) => _database = database;

    public MutableTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _database.Provider.ToString());
        builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
        builder.UseSetting("Auth:Mode", "LocalDev");
        builder.UseSetting("Auth:LocalDev:SigningKey", SigningKey);
        builder.UseSetting("Auth:LocalDev:Issuer", LocalDevAuthOptions.DefaultIssuer);
        builder.UseSetting("Auth:LocalDev:Audience", "authenticated");
        builder.UseSetting("Cors:AllowedOrigins:0", UiOrigin);
        builder.UseSetting("Workflow:ReviewUrlBase", UiOrigin);
        builder.UseSetting("Simulation:Enabled", "false");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public string TokenFor(string subject) => Services.GetRequiredService<LocalDevTokenIssuer>().Issue(subject);

    public HttpClient ClientFor(string? subject)
    {
        var client = CreateClient();
        if (subject is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(subject));
        return client;
    }

    public async Task ResetAndSeedAsync()
    {
        await using var db = _database.CreateContext();
        await DatabaseSeeder.ResetAsync(db, CancellationToken.None);
        await DatabaseSeeder.SeedAsync(db, Clock, CancellationToken.None);
    }

    public AuthBridgeDbContext Db() => _database.CreateContext();

    public DatabaseProvider Provider => _database.Provider;
}

public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string> ProblemCodeAsync(this HttpResponseMessage response)
    {
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        var body = await response.JsonAsync();
        Assert.That(body.TryGetProperty("correlationId", out _), Is.True, "ProblemDetails carries correlationId");
        return body.GetProperty("code").GetString()!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Json);

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public static Guid Guid(this JsonElement e, string name) => e.GetProperty(name).GetGuid();
}

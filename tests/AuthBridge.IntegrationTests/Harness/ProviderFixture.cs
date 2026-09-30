using System.Net;
using System.Text.Json;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Seeding;

namespace AuthBridge.IntegrationTests.Harness;

/// <summary>
/// Base for tests that must hold on both providers. Derived classes carry
/// [TestFixture(DatabaseProvider.SqlServer)] and [TestFixture(DatabaseProvider.Postgres)].
/// One database and host per fixture; the synthetic data is reset and reseeded before each test.
/// </summary>
public abstract class ProviderFixture(DatabaseProvider provider)
{
    protected TestDatabase Database = null!;
    protected AuthBridgeApp App = null!;
    protected HttpClient CoordinatorA = null!;
    protected HttpClient ViewerA = null!;
    protected HttpClient CoordinatorB = null!;
    protected HttpClient SecondCoordinatorA = null!;

    protected DatabaseProvider Provider => provider;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        Database = await TestDatabase.CreateAsync(provider);
        App = new AuthBridgeApp(Database);
        CoordinatorA = App.ClientFor(SeedUsers.CoordinatorA);
        ViewerA = App.ClientFor(SeedUsers.ViewerA);
        CoordinatorB = App.ClientFor(SeedUsers.CoordinatorB);
        SecondCoordinatorA = App.ClientFor(SeedUsers.SecondCoordinatorA);
    }

    [SetUp]
    public Task ReseedAsync() => App.ResetAndSeedAsync();

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        CoordinatorA?.Dispose();
        ViewerA?.Dispose();
        CoordinatorB?.Dispose();
        SecondCoordinatorA?.Dispose();
        if (App is not null)
            await App.DisposeAsync();
        if (Database is not null)
            await Database.DisposeAsync();
    }

    protected async Task<JsonElement> StatusAsync(string id, HttpClient? client = null)
    {
        var response = await (client ?? CoordinatorA).GetAsync($"/api/v1/authorizations/{id}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return await response.JsonAsync();
    }

    protected async Task<Guid> VersionAsync(string id, HttpClient? client = null) => (await StatusAsync(id, client)).Guid("version");

    protected Task<HttpResponseMessage> AttachAsync(string id, string type, string fixture, Guid version, HttpClient? client = null) =>
        (client ?? CoordinatorA).PostJsonAsync($"/api/v1/authorizations/{id}/documents",
            new { documentType = type, fixtureKey = fixture, expectedVersion = version });

    protected Task<HttpResponseMessage> ValidateAsync(string id, Guid version, HttpClient? client = null) =>
        (client ?? CoordinatorA).PostJsonAsync($"/api/v1/authorizations/{id}/validate", new { expectedVersion = version });

    protected Task<HttpResponseMessage> PrepareAsync(string id, Guid version, HttpClient? client = null) =>
        (client ?? CoordinatorA).PostJsonAsync($"/api/v1/authorizations/{id}/submission-proposals", new { expectedVersion = version });

    protected Task<HttpResponseMessage> ApproveAsync(Guid proposalId, HttpClient? client = null) =>
        (client ?? CoordinatorA).PostAsync($"/api/v1/submission-proposals/{proposalId}/approve", null);

    protected Task<HttpResponseMessage> SubmitAsync(Guid proposalId, string key, HttpClient? client = null) =>
        (client ?? CoordinatorA).PostJsonAsync("/api/v1/submissions", new { proposalId, idempotencyKey = key });

    /// <summary>Prepare and approve a proposal for a ReadyToSubmit request; returns its ID.</summary>
    protected async Task<Guid> ApprovedProposalAsync(string id, HttpClient? client = null)
    {
        var prepared = await PrepareAsync(id, await VersionAsync(id, client), client);
        Assert.That(prepared.StatusCode, Is.EqualTo(HttpStatusCode.Created), await prepared.Content.ReadAsStringAsync());
        var proposalId = (await prepared.JsonAsync()).Guid("proposalId");
        var approved = await ApproveAsync(proposalId, client);
        Assert.That(approved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await approved.Content.ReadAsStringAsync());
        return proposalId;
    }
}

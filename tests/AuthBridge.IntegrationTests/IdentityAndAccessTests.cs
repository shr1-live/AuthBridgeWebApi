using System.Net;
using System.Net.Http.Headers;
using AuthBridge.Api.Auth;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Seeding;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthBridge.IntegrationTests;

[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class IdentityAndAccessTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private HttpClient WithToken(string token)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private LocalDevTokenIssuer Issuer => App.Services.GetRequiredService<LocalDevTokenIssuer>();

    [Test]
    public async Task Missing_token_is_401_problem()
    {
        var response = await App.CreateClient().GetAsync("/api/v1/authorizations");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("UNAUTHENTICATED"));
    }

    [Test]
    public async Task Invalid_signature_is_401()
    {
        var forged = Issuer.Issue(SeedUsers.CoordinatorA, signingKey: "a-completely-different-signing-key-0000000000");
        var response = await WithToken(forged).GetAsync("/api/v1/authorizations");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Wrong_issuer_is_401()
    {
        var token = Issuer.Issue(SeedUsers.CoordinatorA, issuer: "https://attacker.example/auth/v1");
        Assert.That((await WithToken(token).GetAsync("/api/v1/authorizations")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Wrong_audience_is_401()
    {
        var token = Issuer.Issue(SeedUsers.CoordinatorA, audience: "anon");
        Assert.That((await WithToken(token).GetAsync("/api/v1/authorizations")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Expired_token_is_401()
    {
        var token = Issuer.Issue(SeedUsers.CoordinatorA, lifetime: TimeSpan.FromMinutes(-10));
        Assert.That((await WithToken(token).GetAsync("/api/v1/authorizations")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Garbage_token_is_401()
    {
        Assert.That((await WithToken("not.a.jwt").GetAsync("/api/v1/authorizations")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Inactive_subject_is_403_access_not_provisioned()
    {
        var response = await App.ClientFor(SeedUsers.InactiveA).GetAsync("/api/v1/authorizations");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("ACCESS_NOT_PROVISIONED"));
    }

    [Test]
    public async Task Valid_token_for_unmapped_subject_is_403()
    {
        var response = await App.ClientFor(Guid.NewGuid().ToString()).GetAsync("/api/v1/authorizations");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("ACCESS_NOT_PROVISIONED"));
    }

    [Test]
    public async Task Me_reports_server_side_tenant_and_role()
    {
        var me = await (await ViewerA.GetAsync("/api/v1/me")).JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(me.Str("tenantId"), Is.EqualTo(SeedUsers.TenantA));
            Assert.That(me.Str("role"), Is.EqualTo("Viewer"));
            Assert.That(me.GetProperty("canWrite").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task Cross_tenant_read_is_indistinguishable_from_missing()
    {
        var foreign = await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-204");
        var missing = await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-999");
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var a = await foreign.JsonAsync();
        var b = await missing.JsonAsync();
        Assert.That(a.Str("detail"), Is.EqualTo(b.Str("detail")));
        Assert.That(a.ToString(), Does.Not.Contain("TENANT-B").And.Not.Contain("DEMO-MRI"));

        foreach (var path in new[] { "missing-documents", "history" })
            Assert.That((await CoordinatorA.GetAsync($"/api/v1/authorizations/AUTH-204/{path}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), path);
    }

    [Test]
    public async Task Tenant_b_sees_only_its_own_requests()
    {
        var list = await (await CoordinatorB.GetAsync("/api/v1/authorizations?pageSize=100")).JsonAsync();
        var ids = list.GetProperty("items").EnumerateArray().Select(i => i.Str("authorizationId")).ToList();
        Assert.That(ids, Has.Count.EqualTo(8));
        Assert.That(ids, Has.All.StartWith("AUTH-2"));
    }

    [Test]
    public async Task Cross_tenant_write_is_404_and_changes_nothing()
    {
        var before = await VersionAsync("AUTH-204", CoordinatorB);
        var response = await AttachAsync("AUTH-204", "ReferralLetter", "FX-REFERRAL-SIGNED", before, CoordinatorA);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await VersionAsync("AUTH-204", CoordinatorB), Is.EqualTo(before));
    }

    [Test]
    public async Task Viewer_writes_are_403_and_change_nothing()
    {
        var version = await VersionAsync("AUTH-104", ViewerA);
        var calls = new[]
        {
            await AttachAsync("AUTH-104", "ReferralLetter", "FX-REFERRAL-SIGNED", version, ViewerA),
            await ValidateAsync("AUTH-104", version, ViewerA),
            await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105", ViewerA), ViewerA),
            await ApproveAsync(SeedIds.StaleProposal, ViewerA),
            await SubmitAsync(SeedIds.StaleProposal, "viewer-key", ViewerA),
        };
        foreach (var response in calls)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), response.RequestMessage?.RequestUri?.ToString());
            Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("FORBIDDEN"));
        }
        Assert.That(await VersionAsync("AUTH-104", ViewerA), Is.EqualTo(version));
        await using var db = App.Db();
        Assert.That(await db.Proposals.CountAsync(), Is.EqualTo(4), "no proposal created by the viewer");
    }

    [Test]
    public async Task Tokens_and_bodies_are_not_echoed_in_error_responses()
    {
        var token = App.TokenFor(SeedUsers.CoordinatorA);
        var response = await CoordinatorA.GetAsync("/api/v1/authorizations/" + new string('X', 41));
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(text, Does.Not.Contain(token));
    }
}

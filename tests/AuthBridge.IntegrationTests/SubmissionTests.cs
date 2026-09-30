using System.Net;
using AuthBridge.Domain;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Seeding;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.IntegrationTests;

[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class SubmissionTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private async Task<int> AttemptCountAsync(string publicId)
    {
        await using var db = App.Db();
        var id = SeedIds.Request(publicId);
        return await db.Attempts.CountAsync(a => a.RequestId == id);
    }

    [Test]
    public async Task Proposal_is_actor_bound_expires_in_five_minutes_and_links_to_the_ui()
    {
        var response = await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(response.Headers.Location?.ToString(), Does.StartWith("/api/v1/submission-proposals/"));
        var p = await response.JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(p.Str("state"), Is.EqualTo("PendingApproval"));
            Assert.That(p.Str("actorId"), Is.EqualTo(SeedUsers.CoordinatorA));
            Assert.That(p.GetProperty("expiresAtUtc").GetDateTimeOffset() - p.GetProperty("createdAtUtc").GetDateTimeOffset(),
                Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(p.Str("reviewUrl"), Is.EqualTo($"{AuthBridgeApp.UiOrigin}/proposals/{p.Guid("proposalId")}"));
            Assert.That(p.Str("simulatedAction"), Does.Contain("No real payer"));
        });
    }

    [Test]
    public async Task Request_must_be_ready_and_complete_to_prepare()
    {
        var missing = await PrepareAsync("AUTH-104", await VersionAsync("AUTH-104"));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(await missing.ProblemCodeAsync(), Is.EqualTo("MISSING_DOCUMENTS"));
    }

    [Test]
    public async Task Unapproved_proposal_cannot_submit()
    {
        var prepared = await (await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105"))).JsonAsync();
        var response = await SubmitAsync(prepared.Guid("proposalId"), "k-unapproved");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("PROPOSAL_NOT_APPROVED"));
        Assert.That(await AttemptCountAsync("AUTH-105"), Is.Zero);
    }

    [Test]
    public async Task Approved_submission_is_atomic_and_idempotent()
    {
        var proposalId = await ApprovedProposalAsync("AUTH-105");

        var first = await SubmitAsync(proposalId, "k-105");
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), await first.Content.ReadAsStringAsync());
        var attempt = await first.JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(attempt.Str("state"), Is.EqualTo("Queued"));
            Assert.That(attempt.Str("requestStatus"), Is.EqualTo("Submitted"));
            Assert.That(attempt.GetProperty("isReplay").GetBoolean(), Is.False);
        });

        // Same key, same payload: the original attempt, even though the proposal is now consumed.
        var replay = await SubmitAsync(proposalId, "k-105");
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var replayed = await replay.JsonAsync();
        Assert.That(replayed.Guid("attemptId"), Is.EqualTo(attempt.Guid("attemptId")));
        Assert.That(replayed.GetProperty("isReplay").GetBoolean(), Is.True);

        // Replay still works after the proposal would have expired.
        App.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.That((await (await SubmitAsync(proposalId, "k-105")).JsonAsync()).Guid("attemptId"), Is.EqualTo(attempt.Guid("attemptId")));

        // A different key after a submission conflicts; no second attempt.
        var another = await SubmitAsync(proposalId, "k-105-other");
        Assert.That(another.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await another.ProblemCodeAsync(), Is.EqualTo("ALREADY_SUBMITTED"));

        await using var db = App.Db();
        var requestId = SeedIds.Request("AUTH-105");
        Assert.Multiple(async () =>
        {
            Assert.That(await db.Attempts.CountAsync(a => a.RequestId == requestId), Is.EqualTo(1));
            Assert.That((await db.Requests.SingleAsync(r => r.Id == requestId)).Status, Is.EqualTo(AuthorizationStatus.Submitted));
            Assert.That((await db.Proposals.SingleAsync(p => p.Id == proposalId)).ConsumedAtUtc, Is.Not.Null);
            Assert.That(await db.History.CountAsync(h => h.RequestId == requestId && h.NewStatus == AuthorizationStatus.Submitted), Is.EqualTo(1));
            Assert.That(await db.AuditEvents.CountAsync(a => a.Operation == "Submit" && a.Outcome == "Queued"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Same_key_with_a_different_payload_is_an_idempotency_conflict()
    {
        var p105 = await ApprovedProposalAsync("AUTH-105");
        Assert.That((await SubmitAsync(p105, "shared-key")).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var p106 = await ApprovedProposalAsync("AUTH-106");
        var response = await SubmitAsync(p106, "shared-key");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("IDEMPOTENCY_CONFLICT"));
        Assert.That(await AttemptCountAsync("AUTH-106"), Is.Zero);
    }

    [Test]
    public async Task Idempotency_keys_are_scoped_per_actor()
    {
        // Tenant B reusing tenant A's key is a different scope, not a replay of A's attempt.
        var pa = await ApprovedProposalAsync("AUTH-105");
        var a = await (await SubmitAsync(pa, "same-key")).JsonAsync();
        var pb = await ApprovedProposalAsync("AUTH-203", CoordinatorB);
        var b = await SubmitAsync(pb, "same-key", CoordinatorB);
        Assert.That(b.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That((await b.JsonAsync()).Guid("attemptId"), Is.Not.EqualTo(a.Guid("attemptId")));
    }

    [Test]
    public async Task Expired_proposal_cannot_be_approved_or_submitted()
    {
        var prepared = await (await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105"))).JsonAsync();
        App.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var approve = await ApproveAsync(prepared.Guid("proposalId"));
        Assert.That(await approve.ProblemCodeAsync(), Is.EqualTo("PROPOSAL_EXPIRED"));

        var proposalId = await ApprovedProposalAsync("AUTH-106");
        App.Clock.Advance(TimeSpan.FromMinutes(6));
        var submit = await SubmitAsync(proposalId, "k-expired");
        Assert.That(submit.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await submit.ProblemCodeAsync(), Is.EqualTo("PROPOSAL_EXPIRED"));
        Assert.That(await AttemptCountAsync("AUTH-106"), Is.Zero);

        var review = await (await CoordinatorA.GetAsync($"/api/v1/submission-proposals/{proposalId}")).JsonAsync();
        Assert.That(review.Str("state"), Is.EqualTo("Expired"));
    }

    [Test]
    public async Task Wrong_actor_cannot_approve_or_submit()
    {
        var prepared = await (await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105"))).JsonAsync();
        var proposalId = prepared.Guid("proposalId");

        var approve = await ApproveAsync(proposalId, SecondCoordinatorA);
        Assert.That(approve.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await ApproveAsync(proposalId)).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var submit = await SubmitAsync(proposalId, "k-wrong-actor", SecondCoordinatorA);
        Assert.That(submit.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var foreign = await SubmitAsync(proposalId, "k-foreign", CoordinatorB);
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "other tenant cannot even see it");
        Assert.That((await CoordinatorB.GetAsync($"/api/v1/submission-proposals/{proposalId}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await AttemptCountAsync("AUTH-105"), Is.Zero);
    }

    [Test]
    public async Task Seeded_stale_proposal_cannot_submit()
    {
        var response = await SubmitAsync(SeedIds.StaleProposal, "k-stale");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("VERSION_CONFLICT"));
        var review = await (await CoordinatorA.GetAsync($"/api/v1/submission-proposals/{SeedIds.StaleProposal}")).JsonAsync();
        Assert.That(review.Str("state"), Is.EqualTo("Stale"));
    }

    [Test]
    public async Task Document_change_after_approval_invalidates_the_proposal()
    {
        var proposalId = await ApprovedProposalAsync("AUTH-105");
        await AttachAsync("AUTH-105", "ReferralLetter", "FX-REFERRAL-SIGNED", await VersionAsync("AUTH-105"));

        var submit = await SubmitAsync(proposalId, "k-after-change");
        Assert.That(submit.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await submit.ProblemCodeAsync(), Is.EqualTo("VERSION_CONFLICT"));
        Assert.That(await AttemptCountAsync("AUTH-105"), Is.Zero);
    }

    [Test]
    public async Task Document_change_before_approval_blocks_approval()
    {
        var prepared = await (await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105"))).JsonAsync();
        await AttachAsync("AUTH-105", "ReferralLetter", "FX-REFERRAL-SIGNED", await VersionAsync("AUTH-105"));
        var approve = await ApproveAsync(prepared.Guid("proposalId"));
        Assert.That(await approve.ProblemCodeAsync(), Is.EqualTo("VERSION_CONFLICT"));
    }

    [Test]
    public async Task Approval_is_repeatable_but_a_get_never_approves()
    {
        var prepared = await (await PrepareAsync("AUTH-105", await VersionAsync("AUTH-105"))).JsonAsync();
        var proposalId = prepared.Guid("proposalId");
        for (var i = 0; i < 3; i++)
            await CoordinatorA.GetAsync($"/api/v1/submission-proposals/{proposalId}");
        Assert.That((await (await CoordinatorA.GetAsync($"/api/v1/submission-proposals/{proposalId}")).JsonAsync()).Str("state"),
            Is.EqualTo("PendingApproval"));
        Assert.That((await CoordinatorA.GetAsync($"/api/v1/submission-proposals/{proposalId}/approve")).StatusCode,
            Is.EqualTo(HttpStatusCode.MethodNotAllowed));

        Assert.That((await ApproveAsync(proposalId)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var again = await ApproveAsync(proposalId);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await again.JsonAsync()).Str("state"), Is.EqualTo("Approved"));
    }

    [Test]
    public async Task Consumed_proposal_cannot_be_reapproved()
    {
        var proposalId = await ApprovedProposalAsync("AUTH-105");
        await SubmitAsync(proposalId, "k-consume");
        var approve = await ApproveAsync(proposalId);
        Assert.That(await approve.ProblemCodeAsync(), Is.EqualTo("PROPOSAL_CONSUMED"));
    }

    [Test]
    public async Task Idempotency_key_header_is_accepted_and_bad_keys_are_400()
    {
        var proposalId = await ApprovedProposalAsync("AUTH-105");
        var bad = await SubmitAsync(proposalId, new string('k', 129));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/submissions")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { proposalId }),
        };
        request.Headers.Add("Idempotency-Key", "header-key-1");
        var response = await CoordinatorA.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task Concurrent_different_key_submissions_create_at_most_one_attempt()
    {
        var proposalId = await ApprovedProposalAsync("AUTH-105");
        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => SubmitAsync(proposalId, $"race-{i}")));

        var codes = responses.Select(r => r.StatusCode).ToList();
        Assert.That(codes.Count(c => c == HttpStatusCode.Accepted), Is.EqualTo(1), string.Join(",", codes));
        Assert.That(codes.Where(c => c != HttpStatusCode.Accepted), Has.All.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await AttemptCountAsync("AUTH-105"), Is.EqualTo(1));
    }

    [Test]
    public async Task Concurrent_same_key_submissions_all_resolve_to_one_attempt()
    {
        var proposalId = await ApprovedProposalAsync("AUTH-106");
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SubmitAsync(proposalId, "same-race-key")));

        var ok = responses.Where(r => r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK).ToList();
        Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Accepted), Is.EqualTo(1));
        var ids = new HashSet<Guid>();
        foreach (var r in ok)
            ids.Add((await r.JsonAsync()).Guid("attemptId"));
        Assert.That(ids, Has.Count.EqualTo(1), "every successful response names the same attempt");
        Assert.That(await AttemptCountAsync("AUTH-106"), Is.EqualTo(1));
    }

    [Test]
    public async Task Submission_status_is_tenant_scoped()
    {
        var seeded = SeedIds.Attempt("AUTH-107");
        Assert.That((await CoordinatorA.GetAsync($"/api/v1/submissions/{seeded}")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ViewerA.GetAsync($"/api/v1/submissions/{seeded}")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await CoordinatorB.GetAsync($"/api/v1/submissions/{seeded}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}

using System.Net;
using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using AuthBridge.Domain;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Seeding;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthBridge.IntegrationTests;

[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class SimulatorTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    /// <summary>One simulator step in its own DI scope, exactly as the worker runs it.</summary>
    private async Task<bool> StepAsync(IServiceProvider? services = null)
    {
        await using var scope = (services ?? App.Services).CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPayerSimulationService>()
            .ProcessNextAsync(SimulationContext.Create(), CancellationToken.None);
    }

    private async Task<Guid> SubmitAsync(string id)
    {
        var proposalId = await ApprovedProposalAsync(id);
        var response = await SubmitAsync(proposalId, "sim-" + id);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        return (await response.JsonAsync()).Guid("attemptId");
    }

    /// <summary>Drains the seeded AUTH-107 attempt so a test observes only its own work.</summary>
    private async Task DrainSeededAsync()
    {
        for (var i = 0; i < 10 && await StepAsync(); i++)
            App.Clock.Advance(TimeSpan.FromSeconds(6));
    }

    private async Task<(string Attempt, string Request)> StateAsync(Guid attemptId)
    {
        var body = await (await CoordinatorA.GetAsync($"/api/v1/submissions/{attemptId}")).JsonAsync();
        return (body.Str("state"), body.Str("requestStatus"));
    }

    [Test]
    public async Task Seeded_queued_attempt_is_processed_to_approval()
    {
        var attemptId = SeedIds.Attempt("AUTH-107");
        Assert.That(await StateAsync(attemptId), Is.EqualTo(("Queued", "Submitted")));

        Assert.That(await StepAsync(), Is.True);
        Assert.That(await StateAsync(attemptId), Is.EqualTo(("Processing", "UnderReview")));

        Assert.That(await StepAsync(), Is.False, "the decision is not due until the step delay passes");
        App.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.That(await StepAsync(), Is.True);
        Assert.That(await StateAsync(attemptId), Is.EqualTo(("Completed", "Approved")));

        var body = await (await CoordinatorA.GetAsync($"/api/v1/submissions/{attemptId}")).JsonAsync();
        Assert.That(body.Str("payerReference"), Does.StartWith("SIM-"));
        App.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(await StepAsync(), Is.False, "nothing left to do");
    }

    [TestCase("AUTH-105", "Approved")]
    [TestCase("AUTH-106", "Denied")]
    public async Task Outcome_comes_only_from_the_fixture_scenario(string id, string expected)
    {
        await DrainSeededAsync();
        var attemptId = await SubmitAsync(id);
        App.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.That(await StepAsync(), Is.True);
        App.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.That(await StepAsync(), Is.True);
        Assert.That(await StateAsync(attemptId), Is.EqualTo(("Completed", expected)));

        var history = await (await CoordinatorA.GetAsync($"/api/v1/authorizations/{id}/history")).JsonAsync();
        var statuses = history.GetProperty("items").EnumerateArray().Select(i => i.Str("newStatus")).TakeLast(3);
        Assert.That(statuses, Is.EqualTo(new[] { "Submitted", "UnderReview", expected }));
    }

    [Test]
    public async Task Fail_once_then_approve_retries_after_backoff()
    {
        await DrainSeededAsync();
        var attemptId = await SubmitAsync("AUTH-110");
        App.Clock.Advance(TimeSpan.FromSeconds(6));
        await StepAsync(); // Queued -> Processing
        App.Clock.Advance(TimeSpan.FromSeconds(6));
        await StepAsync(); // transient failure

        var failed = await (await CoordinatorA.GetAsync($"/api/v1/submissions/{attemptId}")).JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(failed.Str("state"), Is.EqualTo("Failed"));
            Assert.That(failed.GetProperty("failureCount").GetInt32(), Is.EqualTo(1));
            Assert.That(failed.Str("requestStatus"), Is.EqualTo("UnderReview"), "a transient failure is not a decision");
        });

        Assert.That(await StepAsync(), Is.False, "retry waits for its backoff");
        App.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.That(await StepAsync(), Is.True);
        Assert.That(await StateAsync(attemptId), Is.EqualTo(("Completed", "Approved")));
    }

    [Test]
    public async Task Restart_resumes_processing_work_exactly_once()
    {
        var attemptId = SeedIds.Attempt("AUTH-107");
        Assert.That(await StepAsync(), Is.True); // now Processing, persisted

        // A brand-new host over the same database stands in for a restarted Render instance.
        await using (var restarted = new AuthBridgeApp(Database))
        {
            restarted.Clock.Set(App.Clock.GetUtcNow() + TimeSpan.FromSeconds(30));
            Assert.That(await StepAsync(restarted.Services), Is.True);
            Assert.That(await StepAsync(restarted.Services), Is.False);
        }

        await using var db = App.Db();
        var requestId = SeedIds.Request("AUTH-107");
        Assert.Multiple(async () =>
        {
            Assert.That((await db.Attempts.SingleAsync(a => a.Id == attemptId)).State, Is.EqualTo(AttemptState.Completed));
            Assert.That(await db.History.CountAsync(h => h.RequestId == requestId && h.NewStatus == AuthorizationStatus.UnderReview), Is.EqualTo(1));
            Assert.That(await db.History.CountAsync(h => h.RequestId == requestId && h.NewStatus == AuthorizationStatus.Approved), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Competing_workers_apply_each_step_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => StepAsync()));
        Assert.That(results, Has.Some.True);

        App.Clock.Advance(TimeSpan.FromSeconds(6));
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => StepAsync()));

        await using var db = App.Db();
        var requestId = SeedIds.Request("AUTH-107");
        Assert.That(await db.History.CountAsync(h => h.RequestId == requestId && h.NewStatus == AuthorizationStatus.UnderReview), Is.EqualTo(1));
        Assert.That(await db.History.CountAsync(h => h.RequestId == requestId && h.NewStatus == AuthorizationStatus.Approved), Is.EqualTo(1));
    }

    [Test]
    public async Task Decision_is_not_visible_before_the_simulator_records_it()
    {
        await DrainSeededAsync();
        var attemptId = await SubmitAsync("AUTH-105");
        var (attempt, request) = await StateAsync(attemptId);
        Assert.That(attempt, Is.EqualTo("Queued"));
        Assert.That(request, Is.Not.EqualTo("Approved"));
        var status = await StatusAsync("AUTH-105");
        Assert.That(status.Str("status"), Is.EqualTo("Submitted"));
    }
}

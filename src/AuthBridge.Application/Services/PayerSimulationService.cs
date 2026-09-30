using AuthBridge.Application.Common;
using AuthBridge.Application.Persistence;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using AuthBridge.Domain.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthBridge.Application.Services;

/// <summary>
/// Durable simulated payer. Each call advances one due attempt by exactly one persisted step:
/// Queued -> Processing (request UnderReview) -> Completed (request Approved/Denied), with a
/// retryable Failed step for the FailOnceThenApprove fixture. Every step replaces version
/// tokens, so a restarted or concurrent worker cannot apply the same step twice.
/// The outcome comes only from the request's fixture scenario.
/// </summary>
public sealed class PayerSimulationService(
    IAuthBridgeStore store,
    TimeProvider time,
    IOptions<SimulationOptions> options,
    ILogger<PayerSimulationService> logger) : IPayerSimulationService
{
    public async Task<bool> ProcessNextAsync(SimulationContext context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var attempt = await store.FindNextDueAttemptAsync(now, ct);
        if (attempt is null)
            return false;

        var request = attempt.Request!;
        var settings = options.Value;

        if (attempt.State == AttemptState.Queued)
            BeginReview(attempt, request, context, now, settings);
        else
            Decide(attempt, request, context, now, settings);

        try
        {
            await store.SaveChangesAsync(ct);
            logger.LogInformation("Simulator advanced attempt {AttemptId} to {State}", attempt.Id, attempt.State);
        }
        catch (StoreConcurrencyException)
        {
            // Another worker already took this step; nothing is applied twice.
            store.DiscardChanges();
            logger.LogInformation("Simulator step for attempt {AttemptId} was already applied elsewhere", attempt.Id);
        }
        return true;
    }

    private void BeginReview(SubmissionAttempt attempt, AuthorizationRequest request, SimulationContext context, DateTimeOffset now, SimulationOptions settings)
    {
        if (!AuthorizationStateMachine.CanTransition(request.Status, AuthorizationStatus.UnderReview))
        {
            FailPermanently(attempt, request, context, now, $"Request was {request.Status}, expected Submitted");
            return;
        }
        attempt.State = AttemptState.Processing;
        attempt.ProcessingStartedAtUtc = now;
        attempt.NextStepAtUtc = now + settings.StepDelay;
        attempt.Touch(now);
        Mapping.Transition(request, AuthorizationStatus.UnderReview, context.ActorId,
            $"Simulated payer {request.PayerCode} started review", context.CorrelationId, now, store.AddHistory);
        store.AddAudit(Audit(context, request, attempt, "Processing", now));
    }

    private void Decide(SubmissionAttempt attempt, AuthorizationRequest request, SimulationContext context, DateTimeOffset now, SimulationOptions settings)
    {
        if (request.Status != AuthorizationStatus.UnderReview)
        {
            FailPermanently(attempt, request, context, now, $"Request was {request.Status}, expected UnderReview");
            return;
        }

        if (request.DemoScenario == DemoScenario.FailOnceThenApprove && attempt.FailureCount == 0)
        {
            attempt.FailureCount++;
            attempt.State = AttemptState.Failed;
            attempt.LastError = "Simulated transient payer error (fixture scenario); will retry.";
            attempt.NextStepAtUtc = attempt.FailureCount >= settings.MaxFailures ? null : now + settings.RetryDelay;
            attempt.Touch(now);
            store.AddAudit(Audit(context, request, attempt, "TransientFailure", now));
            return;
        }

        var outcome = request.DemoScenario == DemoScenario.Deny ? AuthorizationStatus.Denied : AuthorizationStatus.Approved;
        attempt.State = AttemptState.Completed;
        attempt.CompletedAtUtc = now;
        attempt.NextStepAtUtc = null;
        attempt.PayerReference = "SIM-" + attempt.Id.ToString("N")[..10].ToUpperInvariant();
        attempt.Touch(now);
        Mapping.Transition(request, outcome, context.ActorId,
            $"Simulated payer {request.PayerCode} decision: {outcome} (reference {attempt.PayerReference})",
            context.CorrelationId, now, store.AddHistory);
        store.AddAudit(Audit(context, request, attempt, outcome.ToString(), now));
    }

    private void FailPermanently(SubmissionAttempt attempt, AuthorizationRequest request, SimulationContext context, DateTimeOffset now, string reason)
    {
        attempt.State = AttemptState.Failed;
        attempt.LastError = reason;
        attempt.NextStepAtUtc = null;
        attempt.Touch(now);
        store.AddAudit(Audit(context, request, attempt, "Failed", now));
    }

    private static AuditEvent Audit(SimulationContext context, AuthorizationRequest request, SubmissionAttempt attempt, string outcome, DateTimeOffset now) =>
        Mapping.Audit(context.ActorId, request.TenantId, "SimulatePayer", "SubmissionAttempt", attempt.Id.ToString(), outcome, context.CorrelationId, now);
}

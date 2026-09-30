using AuthBridge.Application.Common;
using AuthBridge.Application.Dtos;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using AuthBridge.Domain.Rules;

namespace AuthBridge.Application.Services;

internal static class Mapping
{
    public static AuthorizationSummaryDto ToSummary(AuthorizationRequest r)
    {
        var required = r.RequirementSet?.RequiredDocuments.Select(d => d.DocumentType).ToHashSet(StringComparer.Ordinal) ?? [];
        var valid = r.Documents.Count(d => d.IsValid && required.Contains(d.DocumentType));
        return new(r.PublicId, DtoText.Of(r.Status), r.PayerCode, r.ServiceCode, r.Member?.DisplayLabel ?? "", r.Version,
            r.UpdatedAtUtc, required.Count, valid);
    }

    public static RuleReferenceDto ToRule(RequirementSet s) =>
        new(s.Id, s.PayerCode, s.ServiceCode, s.RuleVersion, s.IsActive, s.IsDemo);

    public static AuthorizationStatusDto ToStatus(AuthorizationRequest r, Guid? attemptId) =>
        new(r.PublicId, r.TenantId, DtoText.Of(r.Status), r.Version, r.PayerCode, r.ServiceCode,
            r.Member?.DisplayLabel ?? "", r.DemoScenario.ToString(), ToRule(r.RequirementSet!),
            r.Documents.OrderBy(d => d.DocumentType, StringComparer.Ordinal)
                .Select(d => new DocumentDto(d.DocumentType, d.FixtureKey, d.IsValid, d.CreatedAtUtc)).ToList(),
            attemptId, r.CreatedAtUtc, r.UpdatedAtUtc);

    public static CompletenessResult Evaluate(AuthorizationRequest r) =>
        CompletenessEvaluator.Evaluate(
            r.RequirementSet!.RequiredDocuments.Select(d => d.DocumentType),
            r.Documents.Select(d => new PresentDocument(d.DocumentType, d.FixtureKey, d.IsValid)));

    public static MissingDocumentsDto ToMissing(AuthorizationRequest r, CompletenessResult c) =>
        new(r.PublicId, r.RequirementSet!.RuleVersion, c.Required, c.Present, c.Missing, c.Invalid, c.IsComplete);

    public static HistoryEntryDto ToHistory(AuthorizationHistory h) =>
        new(h.Id, h.PreviousStatus?.ToString(), h.NewStatus.ToString(), h.ActorId, h.Reason, h.OccurredAtUtc, h.CorrelationId);

    public static SubmissionDto ToSubmission(SubmissionAttempt a, AuthorizationRequest r, bool isReplay) =>
        new(a.Id, r.PublicId, a.ProposalId, DtoText.Of(a.State), DtoText.Of(r.Status), a.PayerReference,
            a.FailureCount, a.LastError, a.CreatedAtUtc, a.CompletedAtUtc, isReplay);

    public static AuthorizationHistory History(Guid requestId, AuthorizationStatus? from, AuthorizationStatus to,
        string actorId, string reason, string correlationId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            PreviousStatus = from,
            NewStatus = to,
            ActorId = actorId,
            Reason = Truncate(reason, FieldLimits.Reason),
            OccurredAtUtc = now,
            CorrelationId = correlationId,
        };

    public static AuditEvent Audit(string actorId, string tenantId, string operation, string targetType, string? targetId,
        string outcome, string correlationId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            TenantId = tenantId,
            Operation = operation,
            TargetType = targetType,
            TargetId = targetId is null ? null : Truncate(targetId, FieldLimits.Target),
            Outcome = outcome,
            OccurredAtUtc = now,
            CorrelationId = correlationId,
        };

    public static AuditEvent Audit(CallerContext c, string operation, string targetType, string? targetId, string outcome, DateTimeOffset now) =>
        Audit(c.ActorId, c.TenantId, operation, targetType, targetId, outcome, c.CorrelationId, now);

    /// <summary>
    /// Moves a request to a new status through the state machine, replacing its version and
    /// recording history. Throws if the transition is not allowed: callers check first.
    /// </summary>
    public static void Transition(AuthorizationRequest r, AuthorizationStatus to, string actorId, string reason,
        string correlationId, DateTimeOffset now, Action<AuthorizationHistory> addHistory)
    {
        if (!AuthorizationStateMachine.CanTransition(r.Status, to))
            throw new InvalidOperationException($"Transition {r.Status} -> {to} is not allowed.");
        addHistory(History(r.Id, r.Status, to, actorId, reason, correlationId, now));
        r.Status = to;
        r.Touch(now);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

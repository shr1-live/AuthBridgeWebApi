using AuthBridge.Domain;
using AuthBridge.Domain.Entities;

namespace AuthBridge.Application.Persistence;

public sealed record AuthorizationListFilter(AuthorizationStatus? Status, string? PayerCode, string? ServiceCode, string? Search);

/// <summary>
/// The narrow set of persistence operations the services need. One instance is scoped to
/// one operation; <see cref="SaveChangesAsync"/> commits everything added or modified
/// through it atomically.
/// </summary>
public interface IAuthBridgeStore
{
    Task<UserAccess?> FindUserAccessAsync(string subjectId, CancellationToken ct);

    Task<(IReadOnlyList<AuthorizationRequest> Items, int Total)> ListRequestsAsync(
        string tenantId, AuthorizationListFilter filter, int page, int pageSize, CancellationToken ct);

    /// <summary>Tenant-scoped, tracked, with member, documents and pinned requirement set.</summary>
    Task<AuthorizationRequest?> FindRequestAsync(string tenantId, string publicId, CancellationToken ct);

    /// <summary>Unscoped, tracked; for the internal simulator and for proposals already tenant-checked.</summary>
    Task<AuthorizationRequest?> FindRequestByIdAsync(Guid requestId, CancellationToken ct);

    Task<RequirementSet?> FindRequirementSetAsync(string payerCode, string serviceCode, string ruleVersion, CancellationToken ct);

    Task<(IReadOnlyList<AuthorizationHistory> Items, int Total)> ListHistoryAsync(
        Guid requestId, int page, int pageSize, CancellationToken ct);

    Task<SubmissionProposal?> FindProposalAsync(Guid proposalId, CancellationToken ct);
    Task<SubmissionAttempt?> FindAttemptAsync(Guid attemptId, CancellationToken ct);
    Task<SubmissionAttempt?> FindAttemptByIdempotencyKeyAsync(string tenantId, string actorId, string idempotencyKey, CancellationToken ct);
    Task<SubmissionAttempt?> FindAttemptForRequestAsync(Guid requestId, CancellationToken ct);

    /// <summary>Oldest Queued, Processing or retryable Failed attempt whose next step is due.</summary>
    Task<SubmissionAttempt?> FindNextDueAttemptAsync(DateTimeOffset now, CancellationToken ct);

    void AddDocument(RequestDocument document);
    void AddHistory(AuthorizationHistory entry);
    void AddAudit(AuditEvent auditEvent);
    void AddProposal(SubmissionProposal proposal);
    void AddAttempt(SubmissionAttempt attempt);

    /// <exception cref="StoreConcurrencyException">A Version token no longer matched.</exception>
    /// <exception cref="StoreUniqueConstraintException">A unique index rejected the write.</exception>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Drops pending tracked changes, e.g. after a failed save, so an audit can still be written.</summary>
    void DiscardChanges();
}

public sealed class StoreConcurrencyException(Exception inner) : Exception("Optimistic concurrency conflict.", inner);

public sealed class StoreUniqueConstraintException(Exception inner) : Exception("Unique constraint violation.", inner);

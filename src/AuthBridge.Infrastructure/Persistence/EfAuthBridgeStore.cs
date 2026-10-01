using AuthBridge.Application.Persistence;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthBridge.Infrastructure.Persistence;

/// <summary>EF Core implementation of the store over a scoped <see cref="AuthBridgeDbContext"/>.</summary>
public sealed class EfAuthBridgeStore(AuthBridgeDbContext db) : IAuthBridgeStore
{
    public Task<UserAccess?> FindUserAccessAsync(string subjectId, CancellationToken ct) =>
        db.UserAccess.AsNoTracking().FirstOrDefaultAsync(x => x.SubjectId == subjectId, ct);

    public async Task<(IReadOnlyList<AuthorizationRequest> Items, int Total)> ListRequestsAsync(
        string tenantId, AuthorizationListFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Requests.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (filter.Status is { } status)
            query = query.Where(r => r.Status == status);
        if (filter.PayerCode is { } payer)
            query = query.Where(r => r.PayerCode == payer);
        if (filter.ServiceCode is { } service)
            query = query.Where(r => r.ServiceCode == service);
        if (filter.Search is { } search)
            query = query.Where(r => r.PublicId.Contains(search));

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(r => r.Member)
            .Include(r => r.Documents)
            .Include(r => r.RequirementSet).ThenInclude(s => s!.RequiredDocuments)
            .AsSplitQuery()
            .OrderBy(r => r.PublicId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<AuthorizationRequest?> FindRequestAsync(string tenantId, string publicId, CancellationToken ct) =>
        RequestGraph().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.PublicId == publicId, ct);

    public Task<AuthorizationRequest?> FindRequestByIdAsync(Guid requestId, CancellationToken ct) =>
        RequestGraph().FirstOrDefaultAsync(r => r.Id == requestId, ct);

    public Task<RequirementSet?> FindRequirementSetAsync(string payerCode, string serviceCode, string ruleVersion, CancellationToken ct) =>
        db.RequirementSets.AsNoTracking().Include(s => s.RequiredDocuments)
            .FirstOrDefaultAsync(s => s.PayerCode == payerCode && s.ServiceCode == serviceCode && s.RuleVersion == ruleVersion, ct);

    public async Task<(IReadOnlyList<AuthorizationHistory> Items, int Total)> ListHistoryAsync(
        Guid requestId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.History.AsNoTracking().Where(h => h.RequestId == requestId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(h => h.OccurredAtUtc).ThenBy(h => h.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<SubmissionProposal?> FindProposalAsync(Guid proposalId, CancellationToken ct) =>
        db.Proposals
            .Include(p => p.Request).ThenInclude(r => r!.Documents)
            .Include(p => p.Request).ThenInclude(r => r!.RequirementSet).ThenInclude(s => s!.RequiredDocuments)
            .Include(p => p.Request).ThenInclude(r => r!.Member)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct);

    public Task<SubmissionAttempt?> FindAttemptAsync(Guid attemptId, CancellationToken ct) =>
        db.Attempts.Include(a => a.Request).FirstOrDefaultAsync(a => a.Id == attemptId, ct);

    public Task<SubmissionAttempt?> FindAttemptByIdempotencyKeyAsync(string tenantId, string actorId, string idempotencyKey, CancellationToken ct) =>
        db.Attempts.Include(a => a.Request)
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.ActorId == actorId && a.IdempotencyKey == idempotencyKey, ct);

    public Task<SubmissionAttempt?> FindAttemptForRequestAsync(Guid requestId, CancellationToken ct) =>
        db.Attempts.AsNoTracking().FirstOrDefaultAsync(a => a.RequestId == requestId, ct);

    public Task<SubmissionAttempt?> FindNextDueAttemptAsync(DateTimeOffset now, CancellationToken ct) =>
        db.Attempts.Include(a => a.Request)
            .Where(a => a.State == AttemptState.Queued
                        || a.State == AttemptState.Processing
                        || (a.State == AttemptState.Failed && a.NextStepAtUtc != null))
            .Where(a => a.NextStepAtUtc == null || a.NextStepAtUtc <= now)
            .OrderBy(a => a.NextStepAtUtc).ThenBy(a => a.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    public void AddDocument(RequestDocument document) => db.Documents.Add(document);
    public void AddHistory(AuthorizationHistory entry) => db.History.Add(entry);
    public void AddAudit(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);
    public void AddProposal(SubmissionProposal proposal) => db.Proposals.Add(proposal);
    public void AddAttempt(SubmissionAttempt attempt) => db.Attempts.Add(attempt);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new StoreConcurrencyException(ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new StoreUniqueConstraintException(ex);
        }
    }

    public void DiscardChanges() => db.ChangeTracker.Clear();

    private IQueryable<AuthorizationRequest> RequestGraph() =>
        db.Requests
            .Include(r => r.Member)
            .Include(r => r.Documents)
            .Include(r => r.RequirementSet).ThenInclude(s => s!.RequiredDocuments)
            .AsSplitQuery();

    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        SqlException sql => sql.Number is 2601 or 2627,
        PostgresException pg => pg.SqlState == PostgresErrorCodes.UniqueViolation,
        SqliteException lite => lite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT (demo mode)
        _ => false,
    };
}

using System.Text.Json;
using System.Text.Json.Serialization;
using AuthBridge.Domain.Entities;
using AuthBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.Infrastructure.DataTransfer;

/// <summary>
/// Provider-neutral export of every AuthBridge table with stable IDs. This, not a SQL Server
/// backup, is how LocalDB data moves to PostgreSQL.
/// </summary>
public sealed class DataSnapshot
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public List<SyntheticMember> Members { get; set; } = [];
    public List<RequirementSet> RequirementSets { get; set; } = [];
    public List<RequiredDocument> RequiredDocuments { get; set; } = [];
    public List<AuthorizationRequest> Requests { get; set; } = [];
    public List<RequestDocument> Documents { get; set; } = [];
    public List<AuthorizationHistory> History { get; set; } = [];
    public List<SubmissionProposal> Proposals { get; set; } = [];
    public List<SubmissionAttempt> Attempts { get; set; } = [];
    public List<UserAccess> UserAccess { get; set; } = [];
    public List<AuditEvent> AuditEvents { get; set; } = [];

    public IReadOnlyDictionary<string, int> Counts() => new Dictionary<string, int>
    {
        ["Members"] = Members.Count,
        ["RequirementSets"] = RequirementSets.Count,
        ["RequiredDocuments"] = RequiredDocuments.Count,
        ["Requests"] = Requests.Count,
        ["Documents"] = Documents.Count,
        ["History"] = History.Count,
        ["Proposals"] = Proposals.Count,
        ["Attempts"] = Attempts.Count,
        ["UserAccess"] = UserAccess.Count,
        ["AuditEvents"] = AuditEvents.Count,
    };
}

public static class DataTransferService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<DataSnapshot> ExportAsync(AuthBridgeDbContext db, TimeProvider time, CancellationToken ct)
    {
        var snapshot = new DataSnapshot
        {
            ExportedAtUtc = time.GetUtcNow(),
            Members = await db.Members.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
            RequirementSets = await db.RequirementSets.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
            RequiredDocuments = await db.RequiredDocuments.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
            Requests = await db.Requests.AsNoTracking().OrderBy(x => x.PublicId).ToListAsync(ct),
            Documents = await db.Documents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
            History = await db.History.AsNoTracking().OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).ToListAsync(ct),
            Proposals = await db.Proposals.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
            Attempts = await db.Attempts.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct),
            UserAccess = await db.UserAccess.AsNoTracking().OrderBy(x => x.SubjectId).ToListAsync(ct),
            AuditEvents = await db.AuditEvents.AsNoTracking().OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).ToListAsync(ct),
        };
        return snapshot;
    }

    /// <summary>Validates a snapshot's internal references before anything is written.</summary>
    public static IReadOnlyList<string> Validate(DataSnapshot s)
    {
        var errors = new List<string>();
        if (s.FormatVersion != 1)
            errors.Add($"Unsupported format version {s.FormatVersion}.");
        var members = s.Members.Select(x => x.Id).ToHashSet();
        var sets = s.RequirementSets.Select(x => x.Id).ToHashSet();
        var requests = s.Requests.Select(x => x.Id).ToHashSet();
        var proposals = s.Proposals.Select(x => x.Id).ToHashSet();

        void Check(bool ok, string message) { if (!ok) errors.Add(message); }
        Check(s.Requests.Select(r => r.PublicId).Distinct().Count() == s.Requests.Count, "Duplicate PublicId values.");
        foreach (var r in s.Requests)
        {
            Check(members.Contains(r.MemberId), $"{r.PublicId}: unknown member.");
            Check(sets.Contains(r.RequirementSetId), $"{r.PublicId}: unknown requirement set.");
        }
        foreach (var d in s.RequiredDocuments) Check(sets.Contains(d.RequirementSetId), $"Required document {d.Id}: unknown set.");
        foreach (var d in s.Documents) Check(requests.Contains(d.RequestId), $"Document {d.Id}: unknown request.");
        foreach (var h in s.History) Check(requests.Contains(h.RequestId), $"History {h.Id}: unknown request.");
        foreach (var p in s.Proposals) Check(requests.Contains(p.RequestId), $"Proposal {p.Id}: unknown request.");
        foreach (var a in s.Attempts)
        {
            Check(requests.Contains(a.RequestId), $"Attempt {a.Id}: unknown request.");
            Check(proposals.Contains(a.ProposalId), $"Attempt {a.Id}: unknown proposal.");
        }
        return errors;
    }

    /// <summary>Imports into an empty database, preserving IDs, then verifies counts.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> ImportAsync(AuthBridgeDbContext db, DataSnapshot s, CancellationToken ct)
    {
        var errors = Validate(s);
        if (errors.Count > 0)
            throw new InvalidOperationException("Snapshot is invalid:\n" + string.Join('\n', errors));
        if (await db.Requests.AnyAsync(ct) || await db.UserAccess.AnyAsync(ct))
            throw new InvalidOperationException("Target database is not empty; import only into a freshly migrated database.");

        // Navigation collections are re-supplied from their own lists.
        foreach (var set in s.RequirementSets) set.RequiredDocuments = [];
        foreach (var request in s.Requests) request.Documents = [];

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Members.AddRange(s.Members);
        db.RequirementSets.AddRange(s.RequirementSets);
        db.RequiredDocuments.AddRange(s.RequiredDocuments);
        db.Requests.AddRange(s.Requests);
        db.Documents.AddRange(s.Documents);
        db.History.AddRange(s.History);
        db.UserAccess.AddRange(s.UserAccess);
        db.AuditEvents.AddRange(s.AuditEvents);
        await db.SaveChangesAsync(ct);
        db.Proposals.AddRange(s.Proposals);
        await db.SaveChangesAsync(ct);
        db.Attempts.AddRange(s.Attempts);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        var actual = await ExportAsync(db, TimeProvider.System, ct);
        var expected = s.Counts();
        var mismatches = actual.Counts().Where(kv => expected[kv.Key] != kv.Value).ToList();
        if (mismatches.Count > 0)
            throw new InvalidOperationException("Count check failed: " + string.Join(", ", mismatches.Select(m => $"{m.Key}={m.Value}, expected {expected[m.Key]}")));
        return actual.Counts();
    }
}

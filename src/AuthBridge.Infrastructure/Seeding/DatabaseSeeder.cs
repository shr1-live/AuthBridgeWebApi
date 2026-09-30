using AuthBridge.Domain.Entities;
using AuthBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.Infrastructure.Seeding;

public static class DatabaseSeeder
{
    public static Task<bool> HasDataAsync(AuthBridgeDbContext db, CancellationToken ct) =>
        db.Requests.AnyAsync(ct);

    /// <summary>Inserts the deterministic fixture set into an empty database.</summary>
    public static async Task SeedAsync(AuthBridgeDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (await HasDataAsync(db, ct))
            throw new InvalidOperationException("The database already contains data. Reset it explicitly before reseeding.");

        var s = SeedData.Build(time.GetUtcNow());
        db.Members.AddRange(s.Members);
        db.RequirementSets.AddRange(s.RequirementSets);
        db.Requests.AddRange(s.Requests);
        db.History.AddRange(s.History);
        await db.SaveChangesAsync(ct);
        // Proposals before attempts: the attempt foreign key needs the proposal row.
        db.Proposals.AddRange(s.Proposals);
        await db.SaveChangesAsync(ct);
        db.Attempts.AddRange(s.Attempts);
        await UpsertUsersAsync(db, s.Users, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes every AuthBridge row. Callers must have confirmed the target is disposable.</summary>
    public static async Task ResetAsync(AuthBridgeDbContext db, CancellationToken ct)
    {
        await db.AuditEvents.ExecuteDeleteAsync(ct);
        await db.Attempts.ExecuteDeleteAsync(ct);
        await db.Proposals.ExecuteDeleteAsync(ct);
        await db.History.ExecuteDeleteAsync(ct);
        await db.Documents.ExecuteDeleteAsync(ct);
        await db.Requests.ExecuteDeleteAsync(ct);
        await db.RequiredDocuments.ExecuteDeleteAsync(ct);
        await db.RequirementSets.ExecuteDeleteAsync(ct);
        await db.Members.ExecuteDeleteAsync(ct);
        await db.UserAccess.ExecuteDeleteAsync(ct);
    }

    private static async Task UpsertUsersAsync(AuthBridgeDbContext db, IEnumerable<UserAccess> users, CancellationToken ct)
    {
        foreach (var user in users)
        {
            if (!await db.UserAccess.AnyAsync(u => u.SubjectId == user.SubjectId, ct))
                db.UserAccess.Add(user);
        }
    }
}

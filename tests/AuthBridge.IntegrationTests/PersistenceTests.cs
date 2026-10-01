using System.Text.Json;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.DataTransfer;
using AuthBridge.Infrastructure.Persistence;
using AuthBridge.Infrastructure.Seeding;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.IntegrationTests;

/// <summary>Provider-specific mapping and constraint behaviour, proven on the real engines.</summary>
[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class PersistenceTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    private async Task<Exception?> SaveExpectingFailureAsync(Action<AuthBridgeDbContext> change)
    {
        await using var db = App.Db();
        change(db);
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException ex)
        {
            return ex;
        }
    }

    [Test]
    public async Task Seed_is_deterministic_twenty_requests_across_two_tenants()
    {
        await using var db = App.Db();
        Assert.Multiple(async () =>
        {
            Assert.That(await db.Requests.CountAsync(), Is.EqualTo(34));
            Assert.That(await db.Requests.CountAsync(r => r.TenantId == SeedUsers.TenantA), Is.EqualTo(20));
            Assert.That(await db.Requests.CountAsync(r => r.TenantId == SeedUsers.TenantB), Is.EqualTo(14));
            Assert.That((await db.Requests.SingleAsync(r => r.PublicId == "AUTH-104")).Id, Is.EqualTo(SeedIds.Request("AUTH-104")));
            Assert.That((await db.Requests.SingleAsync(r => r.PublicId == "AUTH-109")).Status, Is.EqualTo(AuthorizationStatus.Approved));
            Assert.That((await db.Attempts.SingleAsync(a => a.Id == SeedIds.Attempt("AUTH-107"))).State, Is.EqualTo(AttemptState.Queued));
        });
    }

    [Test]
    public async Task Enums_are_stored_as_plain_strings()
    {
        await using var db = App.Db();
        // Raw SQL bypasses the EF value converter, so this reads exactly what the column holds.
        var sql = Provider == DatabaseProvider.Postgres
            ? "SELECT \"Status\" AS \"Value\" FROM authbridge.\"AuthorizationRequests\" WHERE \"PublicId\" = 'AUTH-104'"
            : "SELECT Status AS Value FROM authbridge.AuthorizationRequests WHERE PublicId = 'AUTH-104'";
        var value = await db.Database.SqlQueryRaw<string>(sql).SingleAsync();
        Assert.That(value, Is.EqualTo("AwaitingDocuments"));
    }

    [Test]
    public async Task Timestamps_round_trip_as_utc()
    {
        await using var db = App.Db();
        var request = await db.Requests.AsNoTracking().SingleAsync(r => r.PublicId == "AUTH-101");
        Assert.That(request.CreatedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(request.CreatedAtUtc, Is.EqualTo(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public async Task Column_bounds_are_enforced_by_the_database()
    {
        var ex = await SaveExpectingFailureAsync(db => db.UserAccess.Add(new UserAccess
        {
            SubjectId = Guid.NewGuid().ToString(),
            TenantId = new string('T', 41),
            Role = UserRole.Viewer,
            DisplayLabel = "x",
        }));
        Assert.That(ex, Is.Not.Null, "41-character tenant id rejected");
    }

    [Test]
    public async Task Duplicate_public_id_is_rejected()
    {
        await using var read = App.Db();
        var template = await read.Requests.AsNoTracking().SingleAsync(r => r.PublicId == "AUTH-101");
        var ex = await SaveExpectingFailureAsync(db =>
        {
            template.Id = Guid.NewGuid();
            template.Documents = [];
            db.Requests.Add(template);
        });
        Assert.That(ex, Is.Not.Null);
    }

    [Test]
    public async Task Duplicate_document_type_per_request_is_rejected()
    {
        var ex = await SaveExpectingFailureAsync(db => db.Documents.Add(new RequestDocument
        {
            Id = Guid.NewGuid(),
            RequestId = SeedIds.Request("AUTH-104"),
            DocumentType = "ImagingReport",
            FixtureKey = "FX-IMAGING-CURRENT",
            IsValid = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        Assert.That(ex, Is.Not.Null);
    }

    [Test]
    public async Task Second_attempt_for_a_request_is_rejected_by_the_database()
    {
        var ex = await SaveExpectingFailureAsync(db => db.Attempts.Add(new SubmissionAttempt
        {
            Id = Guid.NewGuid(),
            RequestId = SeedIds.Request("AUTH-107"),
            TenantId = SeedUsers.TenantA,
            ActorId = SeedUsers.CoordinatorA,
            ProposalId = SeedIds.StaleProposal,
            IdempotencyKey = "direct-insert",
            PayloadHash = new string('a', 64),
            State = AttemptState.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Version = Guid.NewGuid(),
        }));
        Assert.That(ex, Is.Not.Null);
    }

    [Test]
    public async Task Duplicate_payer_service_rule_version_is_rejected()
    {
        var ex = await SaveExpectingFailureAsync(db => db.RequirementSets.Add(new RequirementSet
        {
            Id = Guid.NewGuid(),
            PayerCode = "DEMO-PAYER-A",
            ServiceCode = "DEMO-MRI",
            RuleVersion = "1",
            IsActive = false,
            IsDemo = true,
        }));
        Assert.That(ex, Is.Not.Null);
    }

    [Test]
    public async Task Version_token_detects_a_lost_update()
    {
        await using var first = App.Db();
        await using var second = App.Db();
        var a = await first.Requests.SingleAsync(r => r.PublicId == "AUTH-101");
        var b = await second.Requests.SingleAsync(r => r.PublicId == "AUTH-101");
        a.Touch(DateTimeOffset.UtcNow);
        await first.SaveChangesAsync();
        b.Touch(DateTimeOffset.UtcNow);
        Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Test]
    public async Task Export_then_import_into_an_empty_database_preserves_ids_counts_and_history()
    {
        await using var source = App.Db();
        var snapshot = await DataTransferService.ExportAsync(source, TimeProvider.System, CancellationToken.None);
        var json = JsonSerializer.Serialize(snapshot, DataTransferService.Json);

        await using var target = await TestDatabase.CreateAsync(Provider);
        await using var db = target.CreateContext();
        var counts = await DataTransferService.ImportAsync(db,
            JsonSerializer.Deserialize<DataSnapshot>(json, DataTransferService.Json)!, CancellationToken.None);

        Assert.That(counts, Is.EqualTo(snapshot.Counts()));
        await using var check = target.CreateContext();
        var history = await check.History.Where(h => h.RequestId == SeedIds.Request("AUTH-109"))
            .OrderBy(h => h.OccurredAtUtc).Select(h => h.NewStatus).ToListAsync();
        Assert.That(history.Last(), Is.EqualTo(AuthorizationStatus.Approved));
        Assert.That((await check.Requests.SingleAsync(r => r.PublicId == "AUTH-104")).Version, Is.EqualTo(SeedIds.Version("AUTH-104")));
    }

    [Test]
    public async Task Import_refuses_a_non_empty_target()
    {
        await using var db = App.Db();
        var snapshot = await DataTransferService.ExportAsync(db, TimeProvider.System, CancellationToken.None);
        Assert.ThrowsAsync<InvalidOperationException>(() => DataTransferService.ImportAsync(db, snapshot, CancellationToken.None));
    }

    [Test]
    public void Snapshot_with_dangling_references_fails_validation()
    {
        var snapshot = new DataSnapshot();
        snapshot.Requests.Add(new AuthorizationRequest { Id = Guid.NewGuid(), PublicId = "AUTH-X", MemberId = Guid.NewGuid() });
        Assert.That(DataTransferService.Validate(snapshot), Is.Not.Empty);
    }
}

/// <summary>PostgreSQL-only: the domain schema stays private to the backend.</summary>
[TestFixture]
public class PostgresSchemaTests
{
    private TestDatabase _db = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => _db = await TestDatabase.CreateAsync(DatabaseProvider.Postgres);

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
    }

    [Test]
    public async Task Row_level_security_is_enabled_on_every_domain_table()
    {
        await using var db = _db.CreateContext();
        var without = await db.Database.SqlQueryRaw<string>("""
            SELECT c.relname AS "Value" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'authbridge' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory' AND NOT c.relrowsecurity
            """).ToListAsync();
        Assert.That(without, Is.Empty);
    }

    [Test]
    public async Task Public_role_has_no_usage_on_the_domain_schema()
    {
        await using var db = _db.CreateContext();
        var granted = await db.Database.SqlQueryRaw<bool>(
            "SELECT has_schema_privilege('public', 'authbridge', 'USAGE') AS \"Value\"").SingleAsync();
        Assert.That(granted, Is.False);
    }

    [Test]
    public async Task A_role_like_supabase_authenticated_sees_nothing()
    {
        await using var db = _db.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authbridge_probe') THEN CREATE ROLE authbridge_probe NOLOGIN; END IF;
            END $$;
            """);
        var canSelect = await db.Database.SqlQueryRaw<bool>(
            "SELECT has_table_privilege('authbridge_probe', 'authbridge.\"AuthorizationRequests\"', 'SELECT') AS \"Value\"").SingleAsync();
        Assert.That(canSelect, Is.False);
    }
}

/// <summary>The migration path: LocalDB/SQL Server export imported into PostgreSQL.</summary>
[TestFixture]
public class CrossProviderMigrationTests
{
    [Test]
    public async Task SqlServer_export_imports_into_postgres_with_matching_counts_and_ids()
    {
        await using var sql = await TestDatabase.CreateAsync(DatabaseProvider.SqlServer);
        await using var pg = await TestDatabase.CreateAsync(DatabaseProvider.Postgres);

        await using (var db = sql.CreateContext())
            await DatabaseSeeder.SeedAsync(db, TimeProvider.System, CancellationToken.None);

        DataSnapshot snapshot;
        await using (var db = sql.CreateContext())
            snapshot = await DataTransferService.ExportAsync(db, TimeProvider.System, CancellationToken.None);
        var json = JsonSerializer.Serialize(snapshot, DataTransferService.Json);

        await using (var db = pg.CreateContext())
        {
            var counts = await DataTransferService.ImportAsync(db, JsonSerializer.Deserialize<DataSnapshot>(json, DataTransferService.Json)!, CancellationToken.None);
            Assert.That(counts, Is.EqualTo(snapshot.Counts()));
        }

        await using var check = pg.CreateContext();
        var publicIds = await check.Requests.OrderBy(r => r.PublicId).Select(r => r.PublicId).ToListAsync();
        Assert.That(publicIds, Is.EqualTo(snapshot.Requests.Select(r => r.PublicId).Order(StringComparer.Ordinal)));
        var auth104 = await check.Requests.Include(r => r.Documents).SingleAsync(r => r.PublicId == "AUTH-104");
        Assert.That(auth104.Id, Is.EqualTo(SeedIds.Request("AUTH-104")));
        Assert.That(auth104.Documents.Select(d => d.DocumentType), Is.EqualTo(new[] { "ImagingReport" }));
    }
}

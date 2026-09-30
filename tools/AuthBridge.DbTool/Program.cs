using System.Text.Json;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.DataTransfer;
using AuthBridge.Infrastructure.Persistence;
using AuthBridge.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;

// AuthBridge operator tool.
//   dotnet run --project tools/AuthBridge.DbTool -- <command> --provider SqlServer|Postgres [options]
// The connection string is read from the environment variable named by --connection-env
// (default AUTHBRIDGE_MIGRATION_CONNECTION), never from the command line, so it does not
// land in shell history. With --provider SqlServer and no variable set, LocalDB is used.

const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Database=AuthBridge;Trusted_Connection=True;TrustServerCertificate=True";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        Commands:
          migrate                         Apply this provider's migrations
          seed [--reset]                  Insert the synthetic fixture set (reset needs a local target)
          export --out <file>             Write every table to a provider-neutral JSON snapshot
          import --in <file>              Load a snapshot into an empty, migrated database and check counts
          verify                          Print row counts and the key fixture checks
          grant-access --subject <uuid> --tenant <id> --role Viewer|Coordinator [--label <text>] [--inactive]
        Options:
          --provider SqlServer|Postgres   Required
          --connection-env <VAR>          Default AUTHBRIDGE_MIGRATION_CONNECTION
          --allow-nonlocal-reset          Permit --reset against a non-local host (never use on shared data)
        """);
    return 0;
}

var command = args[0];
var options = ParseOptions(args.Skip(1).ToArray());

if (!options.TryGetValue("provider", out var providerText) || !Enum.TryParse<DatabaseProvider>(providerText, out var provider))
{
    Console.Error.WriteLine("--provider SqlServer|Postgres is required.");
    return 2;
}

var connectionEnv = options.GetValueOrDefault("connection-env", "AUTHBRIDGE_MIGRATION_CONNECTION");
var connectionString = Environment.GetEnvironmentVariable(connectionEnv);
if (string.IsNullOrWhiteSpace(connectionString))
{
    if (provider != DatabaseProvider.SqlServer)
    {
        Console.Error.WriteLine($"Set {connectionEnv} to the PostgreSQL connection string.");
        return 2;
    }
    connectionString = LocalDb;
}

var builder = new DbContextOptionsBuilder<AuthBridgeDbContext>();
DependencyInjection.Configure(builder, provider, connectionString);
await using var db = new AuthBridgeDbContext(builder.Options);
var ct = CancellationToken.None;

Console.Error.WriteLine($"Provider {provider}, target {Describe(connectionString)}");

switch (command)
{
    case "migrate":
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        Console.WriteLine(pending.Count == 0 ? "No pending migrations." : "Applying: " + string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
        Console.WriteLine("Applied: " + string.Join(", ", await db.Database.GetAppliedMigrationsAsync(ct)));
        return 0;

    case "seed":
        if (options.ContainsKey("reset"))
        {
            if (!IsLocal(connectionString) && !options.ContainsKey("allow-nonlocal-reset"))
            {
                Console.Error.WriteLine("Refusing to reset a non-local database. Reseeding deletes every AuthBridge row.");
                return 3;
            }
            await DatabaseSeeder.ResetAsync(db, ct);
            Console.WriteLine("Existing AuthBridge rows deleted.");
        }
        else if (await DatabaseSeeder.HasDataAsync(db, ct))
        {
            Console.WriteLine("Database already has data; nothing seeded. Use --reset on a local database to reseed.");
            return 0;
        }
        await DatabaseSeeder.SeedAsync(db, TimeProvider.System, ct);
        await PrintCountsAsync(db);
        return 0;

    case "export":
        if (!options.TryGetValue("out", out var outPath))
        {
            Console.Error.WriteLine("--out <file> is required.");
            return 2;
        }
        var snapshot = await DataTransferService.ExportAsync(db, TimeProvider.System, ct);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(snapshot, DataTransferService.Json), ct);
        Console.WriteLine($"Exported to {outPath}");
        foreach (var (table, count) in snapshot.Counts()) Console.WriteLine($"  {table,-18} {count}");
        return 0;

    case "import":
        if (!options.TryGetValue("in", out var inPath))
        {
            Console.Error.WriteLine("--in <file> is required.");
            return 2;
        }
        var loaded = JsonSerializer.Deserialize<DataSnapshot>(await File.ReadAllTextAsync(inPath, ct), DataTransferService.Json)
            ?? throw new InvalidOperationException("Snapshot file is empty.");
        var counts = await DataTransferService.ImportAsync(db, loaded, ct);
        Console.WriteLine("Imported; counts match the snapshot:");
        foreach (var (table, count) in counts) Console.WriteLine($"  {table,-18} {count}");
        return 0;

    case "verify":
        await PrintCountsAsync(db);
        var auth104 = await db.Requests.AsNoTracking().Include(r => r.Documents).FirstOrDefaultAsync(r => r.PublicId == "AUTH-104", ct);
        Console.WriteLine(auth104 is null ? "AUTH-104 missing" : $"AUTH-104 {auth104.TenantId} {auth104.Status} docs={string.Join(",", auth104.Documents.Select(d => d.DocumentType))}");
        return 0;

    case "grant-access":
        if (!options.TryGetValue("subject", out var subject) || !Guid.TryParse(subject, out _)
            || !options.TryGetValue("tenant", out var tenant) || string.IsNullOrWhiteSpace(tenant)
            || !options.TryGetValue("role", out var roleText) || !Enum.TryParse<UserRole>(roleText, out var role))
        {
            Console.Error.WriteLine("--subject <uuid> --tenant <id> --role Viewer|Coordinator are required.");
            return 2;
        }
        var access = await db.UserAccess.FirstOrDefaultAsync(u => u.SubjectId == subject, ct);
        if (access is null)
        {
            access = new UserAccess { SubjectId = subject };
            db.UserAccess.Add(access);
        }
        access.TenantId = tenant;
        access.Role = role;
        access.IsActive = !options.ContainsKey("inactive");
        access.DisplayLabel = options.GetValueOrDefault("label", $"{role} ({tenant})");
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"Access for {subject}: {tenant} {role} active={access.IsActive}");
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Run with --help.");
        return 2;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            continue;
        var key = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? args[++i] : "true";
    }
    return result;
}

static bool IsLocal(string connectionString)
{
    var lower = connectionString.ToLowerInvariant();
    return lower.Contains("(localdb)") || lower.Contains("host=localhost") || lower.Contains("host=127.0.0.1")
        || lower.Contains("server=localhost") || lower.Contains("server=127.0.0.1");
}

// Prints host and database only; credentials never reach the console.
static string Describe(string connectionString)
{
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2 && p[0].Trim().ToLowerInvariant() is "server" or "host" or "database" or "data source" or "initial catalog")
        .Select(p => $"{p[0].Trim()}={p[1].Trim()}");
    return string.Join("; ", parts);
}

static async Task PrintCountsAsync(AuthBridgeDbContext db)
{
    var snapshot = await DataTransferService.ExportAsync(db, TimeProvider.System, CancellationToken.None);
    foreach (var (table, count) in snapshot.Counts()) Console.WriteLine($"  {table,-18} {count}");
}

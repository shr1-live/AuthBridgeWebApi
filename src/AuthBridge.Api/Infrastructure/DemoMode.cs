using System.Security.Cryptography;
using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Persistence;
using AuthBridge.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.Api.Infrastructure;

/// <summary>
/// Synthetic demo (<c>Auth:Mode=Demo</c>, the appsettings default) needs no settings at all, so a
/// bare Render service with no environment variables still starts:
/// <list type="bullet">
/// <item>Signing key: random per process when Auth:Demo:SigningKey is unset, so demo tokens cannot
/// be forged and expire with the process.</item>
/// <item>Database: Render Postgres when DATABASE_URL or Database:ConnectionString is set; otherwise
/// a local SQLite file recreated and seeded on every start (data resets on restart).</item>
/// <item>CORS: localhost and any https://*.vercel.app origin, in addition to configured ones.</item>
/// </list>
/// Only unset values are filled in, so explicit configuration always wins. Synthetic data only.
/// </summary>
public static class DemoMode
{
    public static readonly string DefaultDatabasePath = Path.Combine(Path.GetTempPath(), "authbridge-demo.db");

    public static bool IsEnabled(IConfiguration configuration) => configuration["Auth:Mode"] == "Demo";

    public static void ApplyDefaults(ConfigurationManager config)
    {
        if (!IsEnabled(config))
            return;
        if (string.IsNullOrWhiteSpace(config["Auth:Demo:SigningKey"]))
            config["Auth:Demo:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        if (string.IsNullOrWhiteSpace(config["Database:ConnectionString"]))
        {
            config["Database:Provider"] = nameof(DatabaseProvider.Sqlite);
            config["Database:ConnectionString"] = $"Data Source={DefaultDatabasePath}";
        }
    }

    /// <summary>Brings the demo database up to date and seeds it when empty.</summary>
    public static async Task PrepareDatabaseAsync(WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
            return;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthBridgeDbContext>();
        if (app.Configuration["Database:Provider"] == nameof(DatabaseProvider.Sqlite))
        {
            // No SQLite migrations exist: the ephemeral file is rebuilt from the model every start.
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            app.Logger.LogWarning("Synthetic demo on an ephemeral SQLite database; data resets on restart.");
        }
        else
        {
            await db.Database.MigrateAsync();
        }
        if (!await DatabaseSeeder.HasDataAsync(db, CancellationToken.None))
            await DatabaseSeeder.SeedAsync(db, TimeProvider.System, CancellationToken.None);
    }

    /// <summary>Origins allowed in demo mode on top of the configured list.</summary>
    public static bool IsDemoOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && ((uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase))
            || uri.Host is "localhost" or "127.0.0.1");
}

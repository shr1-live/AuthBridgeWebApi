using AuthBridge.Application.Persistence;
using AuthBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthBridge.Infrastructure;

public enum DatabaseProvider
{
    SqlServer,
    Postgres,
    /// <summary>Ephemeral synthetic demo only; never used to prove provider behaviour.</summary>
    Sqlite,
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public DatabaseProvider Provider { get; set; }
    public string ConnectionString { get; set; } = "";
}

public static class DependencyInjection
{
    public const string SqlServerMigrationsAssembly = "AuthBridge.Migrations.SqlServer";
    public const string PostgresMigrationsAssembly = "AuthBridge.Migrations.Postgres";

    public static IServiceCollection AddAuthBridgePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var options = ReadOptions(configuration);
        services.AddDbContext<AuthBridgeDbContext>(b => Configure(b, options.Provider, options.ConnectionString));
        services.AddScoped<IAuthBridgeStore, EfAuthBridgeStore>();
        return services;
    }

    public static DatabaseOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(DatabaseOptions.Section);
        var providerText = section["Provider"];
        // Explicit selection only: a missing or misspelt provider must fail, not default silently.
        if (!Enum.TryParse<DatabaseProvider>(providerText, ignoreCase: false, out var provider) || !Enum.IsDefined(provider))
            throw new InvalidOperationException("Database:Provider must be 'SqlServer' or 'Postgres'.");
        var connectionString = section["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Database:ConnectionString is not configured.");
        return new DatabaseOptions { Provider = provider, ConnectionString = connectionString };
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, DatabaseProvider provider, string connectionString) =>
        provider switch
        {
            DatabaseProvider.SqlServer => builder.UseSqlServer(connectionString, o => o
                .MigrationsAssembly(SqlServerMigrationsAssembly)
                .MigrationsHistoryTable("__EFMigrationsHistory", AuthBridgeDbContext.Schema)),
            DatabaseProvider.Postgres => builder.UseNpgsql(WithPostgresDefaults(connectionString), o => o
                .MigrationsAssembly(PostgresMigrationsAssembly)
                .MigrationsHistoryTable("__EFMigrationsHistory", AuthBridgeDbContext.Schema)),
            DatabaseProvider.Sqlite => builder.UseSqlite(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    /// <summary>
    /// Supabase does not use GSSAPI. Npgsql otherwise probes for it on Linux, which fails
    /// noisily in the slim runtime image and slowed the first readiness check past its
    /// timeout. An explicit setting in the connection string still wins.
    /// </summary>
    public static bool IsPostgresUrl(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    public static string WithPostgresDefaults(string connectionString)
    {
        connectionString = connectionString.Trim();
        var builder = new Npgsql.NpgsqlConnectionStringBuilder();
        if (IsPostgresUrl(connectionString))
        {
            if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
                || string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath.Length <= 1
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Invalid PostgreSQL URL. Supply Render's database URL; do not include quotes.");
            var credentials = uri.UserInfo.Split(':', 2);
            builder.Host = uri.Host;
            builder.Port = uri.Port > 0 ? uri.Port : 5432;
            builder.Database = Uri.UnescapeDataString(uri.AbsolutePath[1..]);
            builder.Username = Uri.UnescapeDataString(credentials[0]);
            if (credentials.Length == 2) builder.Password = Uri.UnescapeDataString(credentials[1]);
            foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length != 2 || !pair[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                    || !Enum.TryParse<Npgsql.SslMode>(pair[1].Replace("-", ""), true, out var sslMode))
                    throw new InvalidOperationException("Unsupported PostgreSQL URL option. Use an Npgsql connection string for custom options.");
                builder.SslMode = sslMode;
            }
        }
        else builder.ConnectionString = connectionString;
        if (!connectionString.Contains("GSS Encryption Mode", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("GssEncryptionMode", StringComparison.OrdinalIgnoreCase))
            builder.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }
}

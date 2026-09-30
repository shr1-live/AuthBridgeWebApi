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
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    /// <summary>
    /// Supabase does not use GSSAPI. Npgsql otherwise probes for it on Linux, which fails
    /// noisily in the slim runtime image and slowed the first readiness check past its
    /// timeout. An explicit setting in the connection string still wins.
    /// </summary>
    public static string WithPostgresDefaults(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("GSS Encryption Mode", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("GssEncryptionMode", StringComparison.OrdinalIgnoreCase))
            builder.GssEncryptionMode = Npgsql.GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }
}

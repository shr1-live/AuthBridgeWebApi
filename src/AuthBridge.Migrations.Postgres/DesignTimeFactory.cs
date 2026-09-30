using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthBridge.Migrations.Postgres;

/// <summary>
/// Used by dotnet-ef. Adding a migration does not connect; applying one uses
/// AUTHBRIDGE_MIGRATION_CONNECTION, which holds the migration (owner) credentials and is
/// never part of the runtime image.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AuthBridgeDbContext>
{
    public AuthBridgeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AUTHBRIDGE_MIGRATION_CONNECTION")
            ?? "Host=localhost;Database=authbridge_design;Username=postgres;Password=postgres";
        var builder = new DbContextOptionsBuilder<AuthBridgeDbContext>();
        DependencyInjection.Configure(builder, DatabaseProvider.Postgres, connectionString);
        return new AuthBridgeDbContext(builder.Options);
    }
}

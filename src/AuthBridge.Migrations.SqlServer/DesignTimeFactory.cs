using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthBridge.Migrations.SqlServer;

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
            ?? @"Server=(localdb)\MSSQLLocalDB;Database=AuthBridge_Design;Trusted_Connection=True;TrustServerCertificate=True";
        var builder = new DbContextOptionsBuilder<AuthBridgeDbContext>();
        DependencyInjection.Configure(builder, DatabaseProvider.SqlServer, connectionString);
        return new AuthBridgeDbContext(builder.Options);
    }
}

using AuthBridge.Infrastructure;
using AuthBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AuthBridge.IntegrationTests.Harness;

/// <summary>
/// A real, disposable database per test fixture. SQL Server comes from AUTHBRIDGE_TEST_SQLSERVER
/// (CI service container) or LocalDB on Windows; PostgreSQL from AUTHBRIDGE_TEST_POSTGRES (CI
/// service container) or a Testcontainers instance. If neither is reachable the fixture is
/// reported as Ignored with the reason - never silently passed.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private static PostgreSqlContainer? _container;
    private static readonly SemaphoreSlim ContainerLock = new(1, 1);

    private TestDatabase(DatabaseProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public DatabaseProvider Provider { get; }
    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(DatabaseProvider provider)
    {
        var name = "authbridge_test_" + Guid.NewGuid().ToString("N")[..10];
        var connectionString = provider switch
        {
            DatabaseProvider.SqlServer => SqlServerConnection(name),
            DatabaseProvider.Postgres => await PostgresConnectionAsync(name),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        var db = new TestDatabase(provider, connectionString);
        await using var context = db.CreateContext();
        try
        {
            await context.Database.MigrateAsync();
        }
        catch (Exception ex) when (ex is SqlException or NpgsqlException or InvalidOperationException)
        {
            Assert.Ignore($"{provider} is not reachable for integration tests: {ex.GetBaseException().Message}");
        }
        return db;
    }

    public AuthBridgeDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<AuthBridgeDbContext>();
        DependencyInjection.Configure(builder, Provider, ConnectionString);
        return new AuthBridgeDbContext(builder.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = CreateContext();
        try
        {
            if (Provider == DatabaseProvider.Postgres)
                NpgsqlConnection.ClearAllPools();
            else
                SqlConnection.ClearAllPools();
            await context.Database.EnsureDeletedAsync();
        }
        catch (Exception)
        {
            // Best effort: a leftover disposable test database is harmless.
        }
    }

    private static string SqlServerConnection(string database)
    {
        var configured = Environment.GetEnvironmentVariable("AUTHBRIDGE_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!OperatingSystem.IsWindows())
                Assert.Ignore("SQL Server: set AUTHBRIDGE_TEST_SQLSERVER (LocalDB exists only on Windows).");
            configured = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";
        }
        return new SqlConnectionStringBuilder(configured) { InitialCatalog = database }.ConnectionString;
    }

    private static async Task<string> PostgresConnectionAsync(string database)
    {
        var configured = Environment.GetEnvironmentVariable("AUTHBRIDGE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            await ContainerLock.WaitAsync();
            try
            {
                if (_container is null)
                {
                    var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                    try
                    {
                        await container.StartAsync();
                    }
                    catch (Exception ex)
                    {
                        Assert.Ignore("PostgreSQL: set AUTHBRIDGE_TEST_POSTGRES or start Docker for Testcontainers. " + ex.GetBaseException().Message);
                    }
                    _container = container;
                }
                configured = _container.GetConnectionString();
            }
            finally
            {
                ContainerLock.Release();
            }
        }
        return new NpgsqlConnectionStringBuilder(configured) { Database = database }.ConnectionString;
    }
}

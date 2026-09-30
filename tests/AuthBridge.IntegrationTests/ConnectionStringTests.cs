using AuthBridge.Infrastructure;
using Npgsql;

namespace AuthBridge.IntegrationTests;

public class ConnectionStringTests
{
    [TestCase("postgres")]
    [TestCase("postgresql")]
    public void Render_url_decodes_credentials_and_preserves_tls(string scheme)
    {
        var result = new NpgsqlConnectionStringBuilder(DependencyInjection.WithPostgresDefaults(
            $"{scheme}://demo:p%40ss%3Bword@database.internal:5433/authbridge?sslmode=require"));
        Assert.Multiple(() =>
        {
            Assert.That(result.Host, Is.EqualTo("database.internal"));
            Assert.That(result.Port, Is.EqualTo(5433));
            Assert.That(result.Database, Is.EqualTo("authbridge"));
            Assert.That(result.Username, Is.EqualTo("demo"));
            Assert.That(result.Password, Is.EqualTo("p@ss;word"));
            Assert.That(result.SslMode, Is.EqualTo(SslMode.Require));
        });
    }

    [Test]
    public void Render_url_without_port_uses_postgres_default()
    {
        var result = new NpgsqlConnectionStringBuilder(DependencyInjection.WithPostgresDefaults(
            "postgresql://demo:password@database.internal/authbridge"));
        Assert.That(result.Port, Is.EqualTo(5432));
    }

    [Test]
    public void Existing_npgsql_connection_string_still_works()
    {
        var result = new NpgsqlConnectionStringBuilder(DependencyInjection.WithPostgresDefaults(
            "Host=localhost;Database=authbridge;Username=demo;Password=test;SSL Mode=VerifyFull"));
        Assert.That(result.SslMode, Is.EqualTo(SslMode.VerifyFull));
    }
}

using AuthBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuthBridge.Api.Infrastructure;

/// <summary>Readiness: the configured database answers within a bounded time.</summary>
public sealed class DatabaseHealthCheck(AuthBridgeDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Bounded, with room for the first connection after a cold start.
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await db.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database unreachable");
        }
        catch (Exception)
        {
            // No exception text in the response: it could carry connection details.
            return HealthCheckResult.Unhealthy("Database unreachable");
        }
    }
}

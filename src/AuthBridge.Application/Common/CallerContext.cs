using AuthBridge.Domain;

namespace AuthBridge.Application.Common;

/// <summary>
/// Trusted identity for one operation. Built only from a validated token subject plus the
/// server-managed UserAccess mapping (or the Development-only local stdio subject); never
/// from tenant or role values supplied by a client.
/// </summary>
public sealed record CallerContext(string ActorId, string TenantId, UserRole Role, string CorrelationId)
{
    public bool CanWrite => Role == UserRole.Coordinator;
}

/// <summary>Identity the simulator runs under; it acts across tenants on queued work only.</summary>
public sealed record SimulationContext(string ActorId, string CorrelationId)
{
    public static SimulationContext Create() => new("system:payer-simulator", "sim-" + Guid.NewGuid().ToString("N")[..12]);
}

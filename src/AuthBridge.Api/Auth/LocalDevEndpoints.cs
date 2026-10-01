using AuthBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.Api.Auth;

public sealed record DevTokenRequest(string SubjectId);

/// <summary>
/// Development-only sign-in used before the real Supabase project is configured. It issues
/// tokens only for subjects that already exist in UserAccess. Mapped only when Auth:Mode is
/// LocalDev and the environment is Development; startup refuses LocalDev in production.
/// </summary>
public static class LocalDevEndpoints
{
    public static void MapLocalDevAuth(this WebApplication app, string prefix = "/dev")
    {
        var group = app.MapGroup(prefix).RequireCors(CorsSetup.PolicyName);

        group.MapGet("/users", async (AuthBridgeDbContext db, CancellationToken ct) =>
            await db.UserAccess.AsNoTracking().OrderBy(u => u.TenantId).ThenBy(u => u.DisplayLabel)
                .Select(u => new { u.SubjectId, u.TenantId, Role = u.Role.ToString(), u.IsActive, u.DisplayLabel })
                .ToListAsync(ct));

        group.MapPost("/token", async (DevTokenRequest body, AuthBridgeDbContext db, LocalDevTokenIssuer issuer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.SubjectId) || !await db.UserAccess.AnyAsync(u => u.SubjectId == body.SubjectId, ct))
                return Results.Problem(statusCode: 400, title: "INVALID_INPUT", detail: "Unknown demo subject.");
            var lifetime = TimeSpan.FromMinutes(30);
            return Results.Ok(new { accessToken = issuer.Issue(body.SubjectId, lifetime), expiresIn = (int)lifetime.TotalSeconds, tokenType = "bearer" });
        });
    }
}

public static class CorsSetup
{
    public const string PolicyName = "authbridge-ui";

    /// <summary>
    /// Exact origins only. A wildcard, a path or a non-http(s) scheme fails startup. The synthetic
    /// demo additionally accepts localhost and https://*.vercel.app.
    /// </summary>
    public static void AddAuthBridgeCors(this WebApplicationBuilder builder)
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var normalized = origins.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o =>
        {
            if (o.Contains('*') || !Uri.TryCreate(o, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.AbsolutePath != "/")
                throw new InvalidOperationException($"Cors:AllowedOrigins entry '{o}' must be an exact origin such as https://app.example.com.");
            return uri.GetLeftPart(UriPartial.Authority);
        }).ToArray();

        var demo = Infrastructure.DemoMode.IsEnabled(builder.Configuration);
        builder.Services.AddCors(o => o.AddPolicy(PolicyName, p => p
            .SetIsOriginAllowed(origin => normalized.Contains(origin, StringComparer.OrdinalIgnoreCase)
                || (demo && Infrastructure.DemoMode.IsDemoOrigin(origin)))
            .WithMethods("GET", "POST")
            .WithHeaders("Authorization", "Content-Type", "X-Correlation-Id", "Idempotency-Key")
            .WithExposedHeaders("X-Correlation-Id", "Location")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
    }
}

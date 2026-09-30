using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AuthBridge.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthBridge.Api.Auth;

public enum AuthMode
{
    /// <summary>Tokens issued by the Supabase project, verified against its JWKS.</summary>
    Supabase,
    /// <summary>Locally signed tokens for Development and automated tests only. Refused elsewhere.</summary>
    LocalDev,
}

public sealed class SupabaseAuthOptions
{
    /// <summary>e.g. https://&lt;project-ref&gt;.supabase.co/auth/v1 - copied from the real project, never guessed.</summary>
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "authenticated";
    /// <summary>e.g. https://&lt;project-ref&gt;.supabase.co/auth/v1/.well-known/jwks.json</summary>
    public string JwksUri { get; set; } = "";
}

public sealed class LocalDevAuthOptions
{
    public const string DefaultIssuer = "authbridge-local-dev";
    public string Issuer { get; set; } = DefaultIssuer;
    public string Audience { get; set; } = "authenticated";
    /// <summary>At least 32 bytes. Development/Testing only; never a production secret.</summary>
    public string SigningKey { get; set; } = "";
}

public static class AuthSetup
{
    public const string SubjectClaim = "sub";

    public static AuthMode AddAuthBridgeAuthentication(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Auth");
        if (!Enum.TryParse<AuthMode>(section["Mode"], ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException("Auth:Mode must be 'Supabase' or 'LocalDev'.");

        var env = builder.Environment;
        if (mode == AuthMode.LocalDev && !(env.IsDevelopment() || env.IsEnvironment("Testing")))
            throw new InvalidOperationException("Auth:Mode=LocalDev is refused outside the Development and Testing environments.");

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = mode == AuthMode.Supabase;
                options.TokenValidationParameters = mode == AuthMode.Supabase
                    ? SupabaseParameters(section.GetSection("Supabase").Get<SupabaseAuthOptions>() ?? new(), options)
                    : LocalDevParameters(section.GetSection("LocalDev").Get<LocalDevAuthOptions>() ?? new());
                options.Events = new JwtBearerEvents
                {
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ProblemWriter.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized,
                            "UNAUTHENTICATED", "A valid bearer access token is required.");
                    },
                };
            });
        builder.Services.AddAuthorization();

        if (mode == AuthMode.LocalDev)
        {
            builder.Services.Configure<LocalDevAuthOptions>(section.GetSection("LocalDev"));
            builder.Services.AddSingleton<LocalDevTokenIssuer>();
        }
        return mode;
    }

    private static TokenValidationParameters SupabaseParameters(SupabaseAuthOptions supabase, JwtBearerOptions options)
    {
        if (!Uri.TryCreate(supabase.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Auth:Supabase:Issuer must be the project's https issuer URL.");
        if (!Uri.TryCreate(supabase.JwksUri, UriKind.Absolute, out var jwks) || jwks.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Auth:Supabase:JwksUri must be the project's https JWKS URL.");

        // Asymmetric signing keys come from the project's JWKS and are refreshed automatically.
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            jwks.ToString(), new JwksConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = supabase.Issuer,
            ValidateAudience = true,
            ValidAudience = supabase.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = SubjectClaim,
        };
    }

    private static TokenValidationParameters LocalDevParameters(LocalDevAuthOptions local)
    {
        if (Encoding.UTF8.GetByteCount(local.SigningKey) < 32)
            throw new InvalidOperationException("Auth:LocalDev:SigningKey must be at least 32 bytes.");
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = local.Issuer,
            ValidateAudience = true,
            ValidAudience = local.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(local.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = SubjectClaim,
        };
    }

    public static string? Subject(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? user.FindFirst(SubjectClaim)?.Value : null;
}

/// <summary>Reads a bare JWKS document (Supabase publishes one) into signing keys.</summary>
public sealed class JwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        var keys = new JsonWebKeySet(json);
        var configuration = new OpenIdConnectConfiguration();
        foreach (var key in keys.GetSigningKeys())
            configuration.SigningKeys.Add(key);
        return configuration;
    }
}

/// <summary>
/// Development/Testing token issuer so the full workflow can run before the Supabase project
/// is configured. Registered only in LocalDev mode, which startup refuses in production.
/// </summary>
public sealed class LocalDevTokenIssuer(Microsoft.Extensions.Options.IOptions<LocalDevAuthOptions> options, TimeProvider time)
{
    public string Issue(string subject, TimeSpan? lifetime = null, string? audience = null, string? issuer = null, string? signingKey = null)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? o.SigningKey)), SecurityAlgorithms.HmacSha256);
        var expires = now + (lifetime ?? TimeSpan.FromHours(1));
        var token = new JwtSecurityToken(
            issuer: issuer ?? o.Issuer,
            audience: audience ?? o.Audience,
            claims: [new Claim(AuthSetup.SubjectClaim, subject), new Claim("role", "authenticated")],
            notBefore: expires < now ? expires.AddMinutes(-5) : now,
            expires: expires,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

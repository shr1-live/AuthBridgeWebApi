namespace AuthBridge.Api.Infrastructure;

/// <summary>
/// Checks every required setting before anything is wired, and reports all the missing ones in
/// one message using the environment-variable names a host like Render expects, so a fresh
/// deployment needs one fix-and-redeploy instead of one per setting.
/// </summary>
public static class StartupConfiguration
{
    public static void Validate(IConfiguration configuration)
    {
        var problems = new List<string>();

        var provider = configuration["Database:Provider"];
        if (provider is not ("SqlServer" or "Postgres"))
            problems.Add("Database:Provider (env Database__Provider) must be 'SqlServer' or 'Postgres' — use Postgres on Render.");
        if (string.IsNullOrWhiteSpace(configuration["Database:ConnectionString"]))
            problems.Add("Database:ConnectionString (env Database__ConnectionString) is empty — the Supabase runtime-role connection string.");

        var mode = configuration["Auth:Mode"];
        if (mode is not ("Supabase" or "LocalDev"))
            problems.Add("Auth:Mode (env Auth__Mode) must be 'Supabase' or 'LocalDev' — use Supabase when deployed.");
        if (mode == "Supabase")
        {
            if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:Issuer"]))
                problems.Add("Auth:Supabase:Issuer (env Auth__Supabase__Issuer) is empty — https://<project-ref>.supabase.co/auth/v1");
            if (string.IsNullOrWhiteSpace(configuration["Auth:Supabase:JwksUri"]))
                problems.Add("Auth:Supabase:JwksUri (env Auth__Supabase__JwksUri) is empty — https://<project-ref>.supabase.co/auth/v1/.well-known/jwks.json");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "AuthBridge cannot start: required settings are missing. Set these environment variables " +
                "(Render: service > Environment) and redeploy:\n  - " + string.Join("\n  - ", problems));
    }
}

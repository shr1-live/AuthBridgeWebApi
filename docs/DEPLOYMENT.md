# Deployment

**Status: prepared, not deployed.** Nothing in this repository has been published to Render,
Vercel or Supabase. Every step below is manual and needs the owner's go-ahead.

| Piece | Host | Artifact |
| --- | --- | --- |
| Backend: API, `/mcp` and simulator in one container | Render, one free Docker web service | `Dockerfile`, `render.yaml` |
| Frontend | Vercel (Hobby) | `AuthBridgeWebApp/vercel.json` |
| Database and Auth | the existing Supabase project | `DbTool`, `db/postgres/runtime-role.sql` |

## Synthetic demo (zero configuration)

`appsettings.json` defaults to `Auth:Mode=Demo`, so a Render service with **no environment
variables** starts on its own:

- Sign-in is the seeded demo-user picker at `/demo/users` and `/demo/token`. Anyone can pick a
  user. Tokens are signed with `Auth__Demo__SigningKey`, or with a random per-process key when it
  is unset.
- The database is `DATABASE_URL` or `Database__ConnectionString` when set (the Blueprint's Render
  Postgres, migrated and seeded on start). Otherwise it is an ephemeral SQLite file that is
  recreated and reseeded on every start, so **data resets whenever the service restarts or
  sleeps**.
- CORS also accepts `https://*.vercel.app` and localhost.

The Vercel build defaults to `https://authbridge-api.onrender.com`. If the Render URL differs,
set `NG_APP_API_BASE_URL`, or enter the URL on the sign-in page. Synthetic data only. The
Supabase path below is the real deployment: set `Auth__Mode=Supabase` and its settings.

Check each provider's current free-tier terms before deploying. Free plans have quotas, and
nothing here guarantees zero charges. Do not add paid add-ons, a second Render service or a
background worker service.

## What must be known first

These come from the real Supabase project. None of them are in the repository:

| Value | Where it comes from | Used by |
| --- | --- | --- |
| Project URL `https://<ref>.supabase.co` | Supabase → Project Settings → API | frontend `NG_APP_SUPABASE_URL` |
| Publishable (anon) key | same page | frontend `NG_APP_SUPABASE_PUBLISHABLE_KEY` |
| Issuer `https://<ref>.supabase.co/auth/v1` | derived from the URL; confirm against a real token's `iss` | backend `Auth__Supabase__Issuer` |
| JWKS `https://<ref>.supabase.co/auth/v1/.well-known/jwks.json` | Supabase JWT settings | backend `Auth__Supabase__JwksUri` |
| Runtime connection string | Supabase → Connect, for the `authbridge_runtime` role | backend `Database__ConnectionString` |

**Signing keys.** The backend accepts only asymmetric signatures (ES256/RS256) from the JWKS.
If the project still signs with the legacy shared HS256 secret, move it to asymmetric JWT
signing keys in Supabase first. Signature checks are not disabled as a workaround, and the
shared secret is never copied to the backend.

## 1. Database (Supabase)

Follow DATABASE_MIGRATION.md: migrate with owner credentials, run `runtime-role.sql`, then
seed or import, then `grant-access` for each real demo user. Supabase users are managed by
Supabase. Create dedicated demo accounts in the dashboard, or let users sign up. No passwords
are stored in the domain database.

## 2. Backend (Render)

1. Push this repository (already on GitHub) and create a Blueprint from `render.yaml`. It
   defines one `plan: free` Docker service with `healthCheckPath: /health/ready` and
   `autoDeploy: false`.
2. Enter the `sync: false` values in the dashboard: connection string, issuer, JWKS URL, the
   exact Vercel origin in `Cors__AllowedOrigins__0`, and `Workflow__ReviewUrlBase` (the same
   origin).
3. Deploy. The container binds `0.0.0.0:$PORT`, runs as a non-root user, and contains neither
   `appsettings.Development.json` nor the migration assemblies.
4. Check:

   ```bash
   curl https://<service>.onrender.com/health/live    # {"status":"live"}
   curl https://<service>.onrender.com/health/ready   # {"status":"Healthy"}
   curl -i https://<service>.onrender.com/api/v1/me   # 401 problem+json
   ```

Free instances sleep when idle, and the first request after that takes a while (often about
a minute). The simulator runs only while the instance is awake. The UI shows "Backend waking
up" and retries. Do not add keep-alive pings to get around the idle behaviour.

## 3. Frontend (Vercel)

1. Import `shr1-live/AuthBridgeWebApp`. `vercel.json` sets the Angular framework,
   `npm run build`, output `dist/authbridge-ui/browser` (read from a real build), an SPA
   rewrite so deep links such as `/proposals/<id>` work on refresh, and security headers.
2. Set the environment variables `NG_APP_API_BASE_URL` (the Render URL), `NG_APP_SUPABASE_URL`
   and `NG_APP_SUPABASE_PUBLISHABLE_KEY`. On Vercel, `scripts/write-env.mjs` fails the build if
   any of them is missing or not https, or if the key is a secret or service-role key.
3. Deploy, then add the resulting exact origin to Render's `Cors__AllowedOrigins__0` and
   `Workflow__ReviewUrlBase`.

## 4. Hosted MCP

`https://<service>.onrender.com/mcp` accepts Streamable HTTP with `Authorization: Bearer
<Supabase access token>`. Verify it with:

```powershell
$env:AUTHBRIDGE_ACCESS_TOKEN = "<token of a tenant A user>"   # from a signed-in session
./scripts/smoke-http.ps1 -Url https://<service>.onrender.com/mcp
```

See TOOL_CONTRACTS.md for what is and is not claimed about third-party MCP hosts.

## Secrets checklist

- The frontend bundle holds the API URL, Supabase URL and publishable key only. The build
  refuses secrets.
- Render holds the runtime connection string. Owner credentials stay with the operator,
  entered per session.
- Git holds no real secret. `.env` is ignored, and the only keys in the repository are the
  Development/Testing LocalDev signing key and CI's throwaway container passwords, which
  protect nothing.
- Logs record method, path, status and correlation ID. They never record headers, tokens,
  query strings or bodies.

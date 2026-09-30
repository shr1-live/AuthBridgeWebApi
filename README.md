# AuthBridge API

Backend for AuthBridge, a **synthetic** healthcare prior-authorization demo. One ASP.NET Core
(.NET 10) service hosts:

- the controller JSON API that the Angular app uses ([shr1-live/AuthBridgeWebApp](https://github.com/shr1-live/AuthBridgeWebApp))
- an authenticated MCP endpoint (`/mcp`, Streamable HTTP) for AI hosts, plus a local stdio MCP host
- a durable simulated payer that runs in-process

Data lives in SQL Server LocalDB for Windows development and in Supabase PostgreSQL for
deployment. Users sign in with Supabase Auth, and the API validates their access tokens.

No real payer, patient or clinical data is involved. Approvals and denials are simulated from
fixture scenarios.

## Quick start (Windows)

Requires the .NET SDK 10.0.302+ (pinned in `global.json`) and SQL Server LocalDB. Docker is
needed only for the PostgreSQL tests and the container build.

```powershell
./scripts/setup-local.ps1                                        # migrate and seed LocalDB (local data only)
dotnet run --project src/AuthBridge.Api --launch-profile http    # http://localhost:5243
./scripts/smoke-stdio.ps1                                        # MCP over stdio via the official SDK client
./scripts/smoke-http.ps1                                         # MCP over HTTP with a Development bearer token
dotnet test                                                      # unit + integration, both SQL providers
```

Then run the frontend from `AuthBridgeWebApp` (`npm install && npm start`) and follow
[docs/DEMO.md](docs/DEMO.md).

In Development the API uses `Auth:Mode=LocalDev`, so the workflow runs before the Supabase
project is configured. Startup refuses that mode in any other environment.

## Linux / Docker

LocalDB exists only on Windows. On Linux or macOS, use PostgreSQL, or a SQL Server container:

```bash
docker run -d --name ab-pg -e POSTGRES_PASSWORD=localtest -p 55432:5432 postgres:17-alpine
export AUTHBRIDGE_MIGRATION_CONNECTION="Host=localhost;Port=55432;Database=authbridge;Username=postgres;Password=localtest"
dotnet run --project tools/AuthBridge.DbTool -- migrate --provider Postgres
dotnet run --project tools/AuthBridge.DbTool -- seed --provider Postgres

export ASPNETCORE_ENVIRONMENT=Development Database__Provider=Postgres \
       Database__ConnectionString="$AUTHBRIDGE_MIGRATION_CONNECTION"
dotnet run --project src/AuthBridge.Api --launch-profile http

# Integration tests without LocalDB
export AUTHBRIDGE_TEST_SQLSERVER="Server=localhost,1433;User Id=sa;Password=<pw>;TrustServerCertificate=True"
export AUTHBRIDGE_TEST_POSTGRES="Host=localhost;Port=55432;Username=postgres;Password=localtest"
dotnet test
```

Build and run the production image:

```bash
docker build -t authbridge-api .
docker run -p 10000:10000 -e PORT=10000 -e Database__Provider=Postgres -e Database__ConnectionString=... \
  -e Auth__Mode=Supabase -e Auth__Supabase__Issuer=... -e Auth__Supabase__JwksUri=... \
  -e Cors__AllowedOrigins__0=https://<ui-origin> authbridge-api
```

## Layout

| Path | What |
| --- | --- |
| `src/AuthBridge.Domain` | Entities, state machine, completeness evaluator, fixture allowlist |
| `src/AuthBridge.Application` | Services behind both the API and MCP, DTOs, CallerContext, store abstraction |
| `src/AuthBridge.Infrastructure` | EF Core model and store, provider selection, seed, export/import |
| `src/AuthBridge.Migrations.SqlServer` / `.Postgres` | Separate migrations per provider |
| `src/AuthBridge.Api` | Controllers, Supabase JWT validation, CORS, health, `/mcp`, simulator worker |
| `src/AuthBridge.Mcp` | The eight MCP tools and the Development-only stdio host |
| `tools/AuthBridge.DbTool` | migrate, seed, export, import, verify, grant-access |
| `tools/AuthBridge.SmokeClient` | Official SDK client that checks either transport |
| `tests/` | NUnit unit tests; integration tests against real SQL Server and PostgreSQL |
| `db/postgres/runtime-role.sql` | Restricted runtime role, applied manually |
| `Dockerfile`, `render.yaml` | Render deployment artifacts (not deployed) |

## Documentation

[Architecture](docs/ARCHITECTURE.md) · [API contracts](docs/API_CONTRACTS.md) ·
[MCP tool contracts](docs/TOOL_CONTRACTS.md) · [Deployment](docs/DEPLOYMENT.md) ·
[Database migration](docs/DATABASE_MIGRATION.md) · [Demo](docs/DEMO.md) ·
[Verification evidence](docs/VERIFICATION.md) · [Environment](docs/ENVIRONMENT.md)

## Status

All ten implementation steps are complete locally. Nothing has been deployed. The Supabase
project reference, keys and connection string were not provided, so real-issuer token
validation, the Render → Supabase connection and the exposed-schema check are still to be done
(see [docs/VERIFICATION.md](docs/VERIFICATION.md) for exactly what was and was not run).

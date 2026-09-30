# Verification evidence

Recorded 30 September 2026 on the development machine: Windows 11, .NET SDK 10.0.302,
LocalDB `MSSQLLocalDB` 17.0, Docker 29.7.2, Node 24.18.0, Angular 22.2.0. Every result below
was produced by a command that was actually run. Things that were **not** run are listed at
the end.

## Automated tests

| Suite | Command | Result |
| --- | --- | --- |
| Backend unit (NUnit) | `dotnet test tests/AuthBridge.UnitTests` | **39 passed**, 0 failed, 0 skipped |
| Backend integration (NUnit) | `dotnet test tests/AuthBridge.IntegrationTests` | **214 passed**, 0 failed, 0 skipped (2 m 38 s) |
| Frontend unit (Vitest via `ng test`) | `npm test` | **28 passed**, 5 files |
| Build | `dotnet build` | 0 warnings (warnings are errors), 0 errors |
| Frontend production build | `npm run build` | initial bundle 285.8 kB, under the 500 kB budget; output `dist/authbridge-ui/browser` |

### Provider coverage

Each integration fixture below ran twice: against **SQL Server LocalDB** and against real
**PostgreSQL 17** (the Testcontainers `postgres:17-alpine` image). Each run used a freshly
migrated database, reseeded before every test. No EF InMemory or SQLite was used.

| Fixture | What it proves (section 12 checks) |
| --- | --- |
| `IdentityAndAccessTests` | Missing, garbage, forged-signature, wrong-issuer, wrong-audience and expired tokens → 401. Inactive or unmapped subject → 403. Cross-tenant reads and writes → 404 with identical wording and nothing changed. All five viewer writes → 403 with nothing changed. Tokens are not echoed in errors |
| `QueryAndDocumentTests` | AUTH-104 status, missing documents and ordered history. Reads leave versions, history and audit unchanged. Invalid paging, status, search and ID → 400. Inactive and unknown rules → CONFIGURATION_MISSING with no invented requirements. The version-1 requirements table. Attachment replaces the parent version. Same-state validation is audited with no fake transition. Stale versions → 409. Only allowlisted fixtures of the right type attach. Documents lock after submission. Terminal states refuse validation and resubmission |
| `SubmissionTests` | Proposals are actor-bound with a 5-minute expiry and a `reviewUrl`. Unapproved, expired, wrong-actor, stale (seeded AUTH-103) and changed-after-approval proposals cannot submit. The attempt, status, consumption, history and audit commit atomically. Same-key replay returns the original attempt even after expiry. Same key with another payload → IDEMPOTENCY_CONFLICT. A new key after submission → ALREADY_SUBMITTED. **12 concurrent different-key submissions → exactly one attempt. 8 concurrent same-key submissions → one attempt, all naming the same ID.** A GET never approves (405) |
| `SimulatorTests` | Queued → UnderReview → Approved or Denied, taken from the fixture scenario. FailOnceThenApprove retries after backoff. **A restart on a new host resumes Processing exactly once.** Competing workers apply each step once. No decision appears before it is recorded |
| `HttpMcpTests` | Official SDK client over Streamable HTTP with a bearer token. All smoke checks pass (exactly 8 tools, AUTH-104 read, AUTH-204 denied). No token → 401. Identity follows the token. An inactive subject is refused. A viewer cannot write. The full prepare → UI approve → submit flow. Tool input validation |
| `StdioMcpTests` | The real `AuthBridge.Mcp.dll` child process. Smoke checks pass. An inactive configured subject is refused. **The host exits 1 outside Development, with stdout empty** |
| `HostingTests` | CORS allows only the exact UI origin, rejecting other hosts, ports and schemes. Health live and ready. Correlation IDs are validated. `/dev/*` is unmapped outside Development. **LocalDev auth is refused in Production.** A missing provider or a wildcard CORS origin fails startup |
| `PersistenceTests` | Deterministic seed of 20 requests (12 in A, 8 in B). Enums are stored as strings, read with raw SQL. UTC timestamps round-trip. The database enforces column bounds, and unique PublicId, request/document type, one attempt per request and payer/service/rule version. The Version token detects a lost update. Export → import into an empty database preserves IDs, counts and history. A non-empty target and dangling references are refused |

PostgreSQL-only: `PostgresSchemaTests` checks that RLS is enabled on every domain table, that
PUBLIC has no USAGE on `authbridge`, and that a new role (standing in for `authenticated`) has
no SELECT. `CrossProviderMigrationTests` runs a LocalDB export, a PostgreSQL import, then
checks matching counts and IDs.

## Manual end-to-end checks (run, and passed)

| Check | How | Result |
| --- | --- | --- |
| Browser workflow | Playwright driving Microsoft Edge (headless) against `ng serve` and the API on LocalDB | Deep link while signed out → login → back to the page. Viewer is read-only. List gives 12 and 4 when filtered. AUTH-204 "Not found". AUTH-104 attach → Validate → ReadyToSubmit. Approve is disabled until ticked, then approved. A reload of the proposal deep link keeps it approved. Submit leads to progress, where the simulator recorded Approved with a `SIM-` reference. The timeline shows `UnderReview → Approved` |
| Stdio MCP against the dev database | `./scripts/smoke-stdio.ps1` | 6/6 checks passed |
| HTTP MCP against a running API | `smoke-http` with a Development token | 6/6 checks passed. `/mcp` without a token → 401 |
| Docker image | `docker build`, then run with `PORT=10000`, `ASPNETCORE_ENVIRONMENT=Production` and Postgres | Listening on `0.0.0.0:10000`. `/health/live` and `/health/ready` healthy (0.55 s cold). API and `/mcp` → 401. `/dev/token` → 404. Non-root UID 1654. No `appsettings.Development.json` or migration assemblies in the image. The seeded queued attempt was processed against Postgres |
| Image refuses LocalDev | Run with `Auth__Mode=LocalDev` | Startup fails: "LocalDev is refused outside the Development and Testing environments" |
| Runtime role script | `db/postgres/runtime-role.sql` on the migrated container | The role reads 20 rows through RLS. `CREATE TABLE` fails with permission denied. An unrelated role has no SELECT |
| Frontend secret guard | `write-env.mjs` with `sb_secret_…`, and with `VERCEL=1` and no URLs | Both exit 1. The production bundle contains only the publishable placeholder key |

Two defects were found and fixed during verification:

1. **Colliding settings files.** The stdio host's `appsettings.json` collided with the API's
   during `dotnet publish` (NETSDK1152). They are renamed to `mcpsettings*.json`, so they can
   no longer shadow the API's configuration.
2. **Slow cold readiness on Postgres.** The first readiness check exceeded its timeout because
   Npgsql probed for GSSAPI on Linux. The code now defaults `GSS Encryption Mode=Disable`, and
   the readiness budget is 5 s.

## Not run, and why

| Check | Why not | What is needed |
| --- | --- | --- |
| Token validation against the **real Supabase issuer and JWKS** | Project reference and keys not provided; probing is not allowed | Set `Auth__Supabase__*`, sign in a real user, and call `/api/v1/me` |
| Render → Supabase connection, pooler choice, TLS verification | No connection string; no deployment authorized | DATABASE_MIGRATION.md "Supabase-specific checks" |
| Supabase **exposed-schemas** setting | Dashboard access not provided | Confirm `authbridge` is not exposed; run the grants query |
| Real Render or Vercel deployment, cold start on the free plan | Deployment not authorized | DEPLOYMENT.md |
| Supabase sign-in in the browser (the `supabase` authMode path) | No project values; covered only by build and unit tests | Build with real `NG_APP_SUPABASE_*` and sign in |
| GitHub Actions CI runs | Workflows are committed; whether they pass on GitHub's runners was not observed | Check the Actions tab after the push |
| Third-party MCP hosts (ChatGPT, Claude) over OAuth | Not implemented. The bearer-token SDK client is what was tested | See TOOL_CONTRACTS.md |
| Visual check across browsers, and at phone width | Only headless Edge at 1200 px was run | Manual check |

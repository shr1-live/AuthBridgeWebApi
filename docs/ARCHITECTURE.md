# Architecture

AuthBridge is a synthetic prior-authorization demo. It has one backend and one frontend, and
two ways to reach the same business logic: a JSON API for the Angular app, and MCP tools for
an AI host.

```
Angular SPA (Vercel) ──bearer JWT──▶ /api/v1 controllers ─┐
                                                          ├─▶ application services ─▶ IAuthBridgeStore ─▶ EF Core ─▶ SQL Server | PostgreSQL
AI host ──MCP client──▶ /mcp (HTTP, bearer JWT) ──────────┤
Local AI host ──stdio──▶ AuthBridge.Mcp (Development) ────┘
                                   in-process payer simulator ─┘
```

The browser never talks to the database. It uses Supabase directly only to sign in; every
domain read and write goes through the backend.

## Projects

| Project | Responsibility | Depends on |
| --- | --- | --- |
| `AuthBridge.Domain` | Entities, enums, `AuthorizationStateMachine`, `CompletenessEvaluator`, fixture allowlist, field limits | nothing |
| `AuthBridge.Application` | Services, DTOs, `CallerContext`, `Result<T>`, input rules, `IAuthBridgeStore` | Domain |
| `AuthBridge.Infrastructure` | `AuthBridgeDbContext`, `EfAuthBridgeStore`, provider selection, seed data, export/import | Application |
| `AuthBridge.Migrations.SqlServer` | SQL Server migrations only | Infrastructure |
| `AuthBridge.Migrations.Postgres` | PostgreSQL migrations only, including schema hardening | Infrastructure |
| `AuthBridge.Mcp` | The eight MCP tools (`AuthBridgeTools`) and the local stdio host | Infrastructure |
| `AuthBridge.Api` | Controllers, JWT validation, CORS, health, hosted `/mcp`, simulator worker | Infrastructure, Mcp (tool class only) |
| `tools/AuthBridge.DbTool` | Operator CLI: migrate, seed, export, import, verify, grant-access | Infrastructure, both migration assemblies |
| `tools/AuthBridge.SmokeClient` | Official SDK client that checks either MCP transport | ModelContextProtocol |

Domain and Application reference no MVC, EF Core or MCP types. Controllers and tools are thin
adapters: they resolve the caller, call one service method, and translate the `Result<T>`.

## Identity

1. The Angular app signs in with Supabase Auth and sends the access token as `Authorization: Bearer`.
2. The API validates the signature against the project's JWKS (ES256/RS256), plus issuer,
   audience `authenticated` and expiry. `MapInboundClaims` is off, so `sub` is read verbatim.
3. `CallerContextResolver` looks up `sub` in the server-managed `UserAccess` table. A missing or
   inactive mapping returns 403 `ACCESS_NOT_PROVISIONED`. Tenant and role come only from that
   table, never from the token's `user_metadata` or from request arguments.
4. Viewers read their own tenant. Coordinators may also write in their own tenant.

Two other identity sources exist, both fenced off:

- **LocalDev** (`Auth:Mode=LocalDev`): an HS256 issuer so the workflow runs before Supabase is
  configured. Startup refuses it outside Development and Testing, and `/dev/*` is mapped only in
  Development.
- **Stdio MCP**: acts as `Mcp:LocalUserSubject`, resolved through `UserAccess`. The host refuses
  to start unless `DOTNET_ENVIRONMENT=Development`.

Hosted `/mcp` has no fixed identity. Each request is authenticated by its own bearer token.

## Workflow

```
Draft ─▶ AwaitingDocuments ─▶ ReadyToSubmit ─▶ Submitted ─▶ UnderReview ─▶ Approved | Denied
  └────────────────────────────▲    │
                               └────┘ (document change invalidates readiness)
```

- **Reads never mutate.** Integration tests assert that versions, history and audit counts do
  not change.
- **Validation** moves a pre-submission request to `ReadyToSubmit` or `AwaitingDocuments`,
  depending on its pinned requirement set. If the status does not change, validation is
  audited but writes no history row.
- **Document attachment** works only before submission. It replaces the request `Version`,
  which invalidates every proposal prepared against the old version.
- **An inactive or unknown rule** returns `CONFIGURATION_MISSING`. No requirements are invented.

### Human approval and submission

1. `PrepareAsync` (from the UI or an MCP tool) creates a proposal. The proposal is bound to the
   actor and to the request version, and expires 5 minutes after creation. The response carries
   `reviewUrl`.
2. The coordinator who prepared it opens the review page, ticks the confirmation and clicks
   **Approve**. Angular then calls `POST /submission-proposals/{id}/approve`. No MCP tool can
   approve.
3. `SubmitAsync` requires an approved, unexpired, unconsumed proposal from the same actor,
   whose version still matches the request, and a complete request. One `SaveChanges` call then
   commits the attempt, the `Submitted` status, the proposal consumption, the history row and the
   audit row together.

Idempotency lookup comes first. If the same caller sends the same key with the same proposal,
the original attempt is returned, even after the proposal is consumed or expired. The same key
with a different proposal returns `IDEMPOTENCY_CONFLICT`. A new key after a submission returns
`ALREADY_SUBMITTED`. Races are settled by unique indexes (one attempt per request, one per
proposal, one per tenant/actor/key) and by version tokens. A race loser re-reads what the winner
stored, so no race ever creates a second attempt.

## Concurrency and persistence

- Requests, proposals and attempts each carry a `Version: Guid` that the application manages.
  It is configured with `IsConcurrencyToken` and replaced on every mutation. The code uses no
  rowversion, xmin, stored procedures or database enums.
- Enums are stored as strings, timestamps as `DateTimeOffset` in UTC, and text columns are
  bounded to match `FieldLimits`.
- `Database:Provider` must be set explicitly. Each provider has its own migration assembly and
  its own history table, `authbridge.__EFMigrationsHistory`.
- PostgreSQL connections default to `GSS Encryption Mode=Disable`, because Supabase does not
  use GSSAPI and the Linux probe for it failed noisily and slowed cold readiness.

## Payer simulator

`SimulationWorker` is a `BackgroundService` inside the API. Each loop creates a DI scope and
calls `IPayerSimulationService.ProcessNextAsync`, which advances one due attempt by one step:

| From | To | Request status |
| --- | --- | --- |
| Queued | Processing | Submitted → UnderReview |
| Processing | Completed | UnderReview → Approved or Denied, taken from the fixture's `DemoScenario` |
| Processing (FailOnceThenApprove, first try) | Failed, retry after backoff | unchanged |
| Failed (retry due) | Completed | UnderReview → Approved |

Every step is persisted and version-checked. After a restart, work resumes from the database,
and two competing workers cannot apply the same step twice (both behaviours are
integration-tested). A new submission wakes the worker. When idle, it polls with backoff from
5 s up to 15 s. On a Render free instance the worker sleeps while the service sleeps, so
decisions wait until the next request wakes it. The UI says so rather than promising a time.

## Deliberate limits

- One process hosts the API, `/mcp` and the worker. There are no queues or separate services.
- Fixtures are allowlisted metadata records. Nothing is uploaded.
- Data is synthetic. The code makes no clinical or coverage claims.

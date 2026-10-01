# API contracts

Base path `/api/v1`. Every route needs `Authorization: Bearer <Supabase access token>`.
JSON is camelCase. `{id}` on authorization routes is the public ID (`AUTH-104`); proposal and
submission IDs are GUIDs.

## Routes

| Method | Route | Service | Success |
| --- | --- | --- | --- |
| GET | `/authorizations?status&payerCode&serviceCode&search&page=1&pageSize=20` | `ListAsync` | 200 `PagedResult<AuthorizationSummary>` |
| GET | `/authorizations/{id}` | `GetStatusAsync` | 200 `AuthorizationStatus` |
| GET | `/authorizations/{id}/missing-documents` | `GetMissingDocumentsAsync` | 200 `MissingDocuments` |
| GET | `/authorizations/{id}/history?page=1&pageSize=50` | `GetHistoryAsync` | 200 `PagedResult<HistoryEntry>` |
| POST | `/authorizations/{id}/documents` `{documentType, fixtureKey, expectedVersion}` | `AttachFixtureAsync` | 200 `DocumentAttachment` |
| POST | `/authorizations/{id}/validate` `{expectedVersion}` | `ValidateAsync` | 200 `ValidationResult` |
| POST | `/authorizations/{id}/submission-proposals` `{expectedVersion}` | `PrepareAsync` | **201** `Proposal`, `Location` header |
| GET | `/submission-proposals/{id}` | `GetAsync` | 200 `Proposal` |
| POST | `/submission-proposals/{id}/approve` | `ApproveAsync` | 200 `Proposal` |
| POST | `/submissions` `{proposalId, idempotencyKey}` (or an `Idempotency-Key` header) | `SubmitAsync` | **202** new attempt, **200** replay |
| GET | `/submissions/{id}` | `GetStatusAsync` | 200 `Submission` |

Read-only additions the UI needs:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/me` | Caller's tenant, role and `canWrite`, taken from `UserAccess` |
| GET | `/requirements?payerCode&serviceCode&ruleVersion` | `GetRequiredDocumentsAsync` |
| GET | `/document-fixtures` | The fixture allowlist (metadata only) |

In-app assistant (see HOW_MCP_WORKS.md):

| Method | Route | Success |
| --- | --- | --- |
| GET | `/assistant` | 200 `{enabled, model, tools[]}`. The tools come live from this server's own `/mcp` `tools/list` |
| POST | `/assistant/messages` `{messages: [{role, text}]}` | 200 `{text, steps[], model}`. Each step is one MCP tool call: `{tool, input, ok, errorCode, result}` |

The conversation alternates user and assistant, starts and ends with the user, has at most 20
messages and at most 4000 characters each. The server keeps no chat state.

Unauthenticated routes: `GET /health/live` (process only) and `GET /health/ready` (database,
bounded to 5 s, returns 503 when unhealthy). `/dev/users` and `/dev/token` exist only in
Development with `Auth:Mode=LocalDev`. In `Auth:Mode=Demo` the same sign-in is at `/demo/users` and
`/demo/token`, limited to 30 requests a minute per client address.

GET never approves or submits. `GET /submission-proposals/{id}/approve` returns 405.

## Errors

Every error is `application/problem+json`, with `code` and `correlationId` extensions. Responses
never include exception text, stack traces or SQL.

```json
{ "status": 409, "title": "VERSION_CONFLICT", "detail": "The record changed since it was read. Reload and retry.",
  "code": "VERSION_CONFLICT", "correlationId": "5f0c…" }
```

| Status | Codes |
| --- | --- |
| 400 | `INVALID_INPUT` — malformed JSON, bad GUID, oversized or ill-formed field, pagination outside page ≥ 1 and pageSize 1–100, unknown status, non-allowlisted fixture |
| 401 | `UNAUTHENTICATED` — missing token, or one with a bad signature, issuer, audience or expiry |
| 403 | `FORBIDDEN` (role, or wrong proposal actor), `ACCESS_NOT_PROVISIONED` (no active `UserAccess`) |
| 404 | `NOT_FOUND` — missing record, or one in another tenant, with identical wording for both |
| 409 | `VERSION_CONFLICT`, `INVALID_STATE`, `ALREADY_SUBMITTED`, `PROPOSAL_NOT_APPROVED`, `PROPOSAL_EXPIRED`, `PROPOSAL_CONSUMED`, `IDEMPOTENCY_CONFLICT` |
| 422 | `MISSING_DOCUMENTS`, `CONFIGURATION_MISSING` |
| 429 | `RATE_LIMITED` — demo sign-in (30 a minute per address) or assistant messages (per user per hour) |
| 503 | `ASSISTANT_NOT_CONFIGURED` (no Anthropic key on the server), `ASSISTANT_UNAVAILABLE` (model unreachable or the daily cap reached) |
| 500 | `INTERNAL_ERROR` — generic text; quote the correlation ID |

## Bounds

Public ID, payer and service codes: 40 characters. Document type: 60. Fixture key: 80.
Idempotency key: 128, limited to `[A-Za-z0-9._:-]`. Codes match `^[A-Za-z0-9][A-Za-z0-9-]*$`.

## Headers

- `X-Correlation-Id`: echoed if it matches `^[A-Za-z0-9._-]{8,64}$`, otherwise replaced. Every
  response carries one.
- CORS: only the exact origins in `Cors:AllowedOrigins` are allowed, with methods GET and POST
  and headers `Authorization`, `Content-Type`, `X-Correlation-Id` and `Idempotency-Key`. A
  wildcard entry fails startup. `/mcp` has no CORS policy, so browsers cannot call it cross-origin.
- `Cache-Control: no-store` and `X-Content-Type-Options: nosniff` are set on every response.

## DTO shapes

The canonical definitions are in `src/AuthBridge.Application/Dtos/Dtos.cs`, mirrored in
`AuthBridgeWebApp/src/app/core/api/models.ts`. Proposal `state` is one of `PendingApproval`,
`Approved`, `Expired`, `Consumed` or `Stale`. The server computes it from time, the version and
consumption.

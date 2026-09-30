# MCP tool contracts

Server: the official C# SDK (`ModelContextProtocol` 2.2.0). The same `AuthBridgeTools` class
serves both transports:

| Transport | Where | Identity |
| --- | --- | --- |
| stdio | `src/AuthBridge.Mcp` (`dotnet AuthBridge.Mcp.dll`) | `Mcp:LocalUserSubject`, resolved through `UserAccess`. **Development only**: the host exits with code 1 otherwise |
| Streamable HTTP | `POST /mcp` on the API, stateless | The request's bearer token, validated like the API's, then `UserAccess` |

Stateless HTTP keeps no session in memory, so a Render restart drops nothing. On stdio, stdout
carries only protocol messages and logs go to stderr (this is tested).

## Tools (exactly eight)

| Tool | Arguments | Service | Annotations |
| --- | --- | --- | --- |
| `get_authorization_status` | `authorizationId` | `GetStatusAsync` | read-only, idempotent |
| `get_missing_documents` | `authorizationId` | `GetMissingDocumentsAsync` | read-only, idempotent |
| `get_authorization_history` | `authorizationId`, `page`=1, `pageSize`=20 | `GetHistoryAsync` | read-only, idempotent |
| `get_required_documents` | `payerCode`, `serviceCode`, `ruleVersion` | `GetRequiredDocumentsAsync` | read-only, idempotent |
| `validate_authorization_request` | `authorizationId`, `expectedVersion` (GUID) | `ValidateAsync` | write, not destructive |
| `prepare_authorization_submission` | `authorizationId`, `expectedVersion` | `PrepareAsync` | write; returns `reviewUrl` |
| `submit_authorization_request` | `proposalId`, `idempotencyKey` | `SubmitAsync` | write, idempotent by key |
| `get_submission_status` | `attemptId` | submission `GetStatusAsync` | read-only, idempotent |

Absent by design: approval, document attachment by path, SQL, shell, generic HTTP and reset.
Setting `confirmed=true` in a model's arguments has no effect, because no tool accepts it.
Approval happens only through a human click in the Angular UI.

## Result envelope

Every tool returns structured content, plus the same JSON as text:

```json
{ "ok": true, "isSimulation": true,
  "source": "AuthBridge synthetic demo (simulated payer; no real payer or patient data)",
  "data": { "...": "the service DTO, including ruleVersion where relevant" } }
```

A failure has `ok: false`, `error: { code, message }` and no `data`. The error codes match the
API's (see API_CONTRACTS.md). A cross-tenant ID returns `NOT_FOUND` and reveals nothing.

## Typical AI-host sequence

1. `get_missing_documents AUTH-104` shows that `ReferralLetter` is missing.
2. A human attaches the fixture in the UI. Attachment is deliberately not an MCP tool.
3. `get_authorization_status` returns the current `version`.
4. `validate_authorization_request` moves the request to `ReadyToSubmit`.
5. `prepare_authorization_submission` returns a `reviewUrl`. The host shows the link to the user.
6. The user opens the link, reviews the proposal and clicks **Approve**.
7. `submit_authorization_request` with a chosen `idempotencyKey`. Reuse the key when retrying.
8. `get_submission_status` until the persisted state is `Completed`.

## Tested interoperability, and what is not claimed

These are tested with the official SDK client, in `tools/AuthBridge.SmokeClient` and the
integration tests:

- Stdio: discovery of exactly eight tools, the AUTH-104 read, the AUTH-204 denial, refusal of
  an inactive subject, and refusal to start outside Development.
- HTTP with a bearer token: the same checks, identity following the token (a tenant B token
  sees AUTH-204 and not AUTH-104), viewer write denial, the full
  prepare → UI approve → submit flow, and 401 without a token.

**Not claimed:** turnkey OAuth connection from ChatGPT, Claude or other hosts. Supabase Auth
does not by itself provide the OAuth discovery and dynamic client registration that some MCP
hosts require, and no such flow was implemented or tested. Today a host needs a user's
Supabase access token supplied as a bearer header, and must refresh it before expiry (Supabase
access tokens are short-lived, one hour by default). Authentication is never weakened to make
a host connect.

## Local client configuration (stdio)

```json
{
  "mcpServers": {
    "authbridge": {
      "command": "dotnet",
      "args": ["D:/MCP/AuthBridgeWebApi/src/AuthBridge.Mcp/bin/Debug/net10.0/AuthBridge.Mcp.dll"],
      "env": { "DOTNET_ENVIRONMENT": "Development" }
    }
  }
}
```

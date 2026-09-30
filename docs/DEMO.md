# Demo script

Everything below is local and synthetic. Allow about 10 minutes.

## Start

```powershell
# Backend (repository AuthBridgeWebApi)
./scripts/setup-local.ps1                      # migrate and reseed LocalDB
dotnet run --project src/AuthBridge.Api --launch-profile http    # http://localhost:5243

# Frontend (repository AuthBridgeWebApp), in a second terminal
npm install
npm start                                      # http://localhost:4200
```

Leave `NG_APP_SUPABASE_*` unset and the login page offers the backend's Development sign-in.

## Fixture map

| ID | Tenant | Situation |
| --- | --- | --- |
| AUTH-101 | A | Draft, no documents |
| AUTH-103 | A | ReadyToSubmit, with an approved but **stale** proposal (submitting it gives 409 VERSION_CONFLICT) |
| AUTH-104 | A | MRI, valid ImagingReport, **missing ReferralLetter** |
| AUTH-105 | A | Complete, ReadyToSubmit → simulator **approves** |
| AUTH-106 | A | Complete, ReadyToSubmit → simulator **denies** |
| AUTH-107 | A | Submitted, with a queued attempt the worker picks up at startup |
| AUTH-108 | A | Pinned to an **inactive** rule → CONFIGURATION_MISSING |
| AUTH-109 | A | Terminal Approved |
| AUTH-110 | A | ReadyToSubmit, **FailOnceThenApprove** (retry shown in progress) |
| AUTH-111 | A | Has an invalid TreatmentSummary |
| AUTH-112 | A | Terminal Denied |
| AUTH-201…208 | B | Tenant B. **AUTH-204** mirrors 104 and is invisible to tenant A |

Users: Demo Coordinator (A), Demo Viewer (A), Demo Coordinator (B), Second Coordinator (A),
and an inactive coordinator.

## Walkthrough

1. Sign in as **Demo Viewer (Tenant A)**. The list shows 12 requests. On AUTH-104 there are no
   action buttons ("read-only access"). Sign out.
2. Sign in as **Demo Coordinator (Tenant A)**. Filter Status = ReadyToSubmit: 4 results.
3. Open `/authorizations/AUTH-204`. It shows "Not found", exactly like a missing ID.
4. Open **AUTH-104**. The checklist shows ReferralLetter missing. Attach `FX-REFERRAL-SIGNED`,
   then **Validate**: AwaitingDocuments → ReadyToSubmit, and the timeline gains the transition.
5. **Prepare submission for review.** The review page opens with a 5-minute countdown.
   **Approve** stays disabled until the confirmation box is ticked. Tick it and approve.
6. **Submit to simulated payer.** The progress page moves from Queued to Under review to
   Decision: Approved, with a `SIM-…` reference, over about 10–20 seconds.
7. Optional: prepare AUTH-106 and let the review expire (wait 5 minutes). The page shows it
   expired, and approval is refused.

## The same through MCP

```powershell
./scripts/smoke-stdio.ps1     # stdio: 8 tools, AUTH-104 read, AUTH-204 denied
./scripts/smoke-http.ps1      # hosted /mcp with a Development bearer token
```

Configure a local MCP host as shown in TOOL_CONTRACTS.md and ask it: "What is missing on
AUTH-104?", then "Prepare AUTH-105 for submission". Open the returned `reviewUrl` in the
browser to approve, then ask the host to submit and check status.

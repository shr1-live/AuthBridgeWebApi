# Verified development environment

Recorded 30 September 2026, checked on the machine rather than assumed.
Step 1 of the implementation specification asks for this before scaffolding.

## Present and verified

| Requirement | Found | Notes |
| --- | --- | --- |
| .NET SDK | **10.0.302** | 8.0.422 also installed; the solution pins 10 via `global.json` |
| Node.js | **v24.18.0** | satisfies Angular CLI 22's `^24.15.0` |
| npm | **11.16.0** | |
| Angular CLI | **22.2.0** (to install) | engines: `node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0` — checked against the registry, not guessed |
| SQL Server LocalDB | **MSSQLLocalDB** | the instance the spec names for initial Windows development |
| Docker | **29.7.2** | for the Render image and the Postgres integration-test container |
| dotnet-ef | **10.0.10** (global) | matches the SDK major version |

No blockers for steps 1 through 8.

## Not available, and what it blocks

**Supabase project configuration.** The specification states a hosted project
exists but deliberately does not supply its reference, URL, keys or connection
string, and forbids inventing or probing for them. That blocks only the parts
that need the real project:

- validating tokens against the real issuer and JWKS (§5)
- the Render → Supabase connection string and pooling choice (§3)
- confirming the exposed-schema settings and runtime role (§5)

Everything else proceeds. Identity work is built against isolated test-only
auth fixtures, which the specification permits and requires to stay disabled in
deployed settings. PostgreSQL behaviour is proven against a disposable local
Postgres container rather than the hosted project, as §3 allows.

**Angular CLI** is not installed globally. It is added per project so the
version is pinned in `package.json` rather than depending on a machine-wide
install.

## Workspace layout

The two repositories are separate checkouts under `D:\MCP`, opened together
through `AuthBridge.code-workspace`:

| Folder | Repository | Deploys to |
| --- | --- | --- |
| `AuthBridgeWebApi` | shr1-live/AuthBridgeWebApi | Render (Docker) |
| `AuthBridgeWebApp` | shr1-live/AuthBridgeWebApp | Vercel (static) |

They are separate because the Angular application must sit at its repository
root for Vercel to detect the framework. Nesting a frontend under `web/` in an
earlier project caused repeated deployment failures - the host built from a
root with no `package.json` and guessed the wrong framework.

Both clones commit as `shr1-live`; the identity is set per clone in
`.git/config` and does not travel with the repository.

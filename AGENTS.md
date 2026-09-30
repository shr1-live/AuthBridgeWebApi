# Agent instructions (AuthBridgeWebApi)

- Synthetic data only. Never add real payer names, patient data or clinical claims.
- Approval is a human click in the Angular UI. Never add an approve, reset, SQL, shell,
  file-path or generic HTTP MCP tool. The smoke tests require exactly eight tools.
- Keep controllers and MCP tools thin. Business rules belong in `AuthBridge.Application`
  services and in the pure Domain rules.
- Identity comes from the validated token subject plus `UserAccess`. Never read tenant or role
  from request input or token metadata.
- Keep both providers working. A model change needs a migration in **both**
  `AuthBridge.Migrations.SqlServer` and `AuthBridge.Migrations.Postgres` (commands are in
  docs/DATABASE_MIGRATION.md). Never apply one provider's migrations to the other.
- No rowversion, xmin, stored procedures or database enums. Replace the `Version` Guid on
  every mutation.
- Prove provider behaviour with integration tests against real SQL Server and PostgreSQL,
  never EF InMemory or SQLite.
- Do not deploy, alter the Supabase project, or put secrets in git or the frontend.
- Commit as `shr1-live`, and push to `origin/main` right after each commit.

Build and test: `dotnet build` (warnings are errors), then `dotnet test`. PostgreSQL needs
Docker running, or `AUTHBRIDGE_TEST_POSTGRES` set.

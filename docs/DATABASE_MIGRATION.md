# Database migration runbook

Development runs on SQL Server LocalDB. Deployment runs on Supabase PostgreSQL. Each provider
has its own migration assembly, and the two must never be crossed.

| Provider | Assembly | Migrations |
| --- | --- | --- |
| SQL Server | `src/AuthBridge.Migrations.SqlServer` | `InitialCreate` |
| PostgreSQL | `src/AuthBridge.Migrations.Postgres` | `InitialCreate`, `PrivateSchemaHardening` (revokes Data API roles and PUBLIC, enables RLS) |

Nothing migrates on container startup. Migration is a separate, reviewed operator step, run
with owner credentials that never go into Render or the image.

## Commands that were actually used

Adding migrations needs no database connection:

```bash
dotnet ef migrations add <Name> --project src/AuthBridge.Migrations.SqlServer --startup-project src/AuthBridge.Migrations.SqlServer --output-dir Migrations
dotnet ef migrations add <Name> --project src/AuthBridge.Migrations.Postgres  --startup-project src/AuthBridge.Migrations.Postgres  --output-dir Migrations
```

Applying them goes through the DbTool, which reads `AUTHBRIDGE_MIGRATION_CONNECTION`:

```powershell
# LocalDB (the default when the variable is unset)
dotnet run --project tools/AuthBridge.DbTool -- migrate --provider SqlServer
dotnet run --project tools/AuthBridge.DbTool -- seed --provider SqlServer --reset   # local only

# PostgreSQL
$env:AUTHBRIDGE_MIGRATION_CONNECTION = "<owner connection string, typed in this session only>"
dotnet run --project tools/AuthBridge.DbTool -- migrate --provider Postgres
```

To review the SQL before applying it:

```bash
dotnet ef migrations script --idempotent --project src/AuthBridge.Migrations.Postgres --startup-project src/AuthBridge.Migrations.Postgres -o postgres.sql
```

`seed --reset` refuses non-local hosts unless you pass `--allow-nonlocal-reset`. Never use that
flag on shared data.

## LocalDB → Supabase PostgreSQL

A SQL Server backup cannot be restored into PostgreSQL. Data moves through a
provider-neutral JSON snapshot instead, which keeps stable IDs.

If the data is only the synthetic fixtures, skip steps 1 and 3 and run `seed` on the target.

1. **Export from LocalDB**

   ```powershell
   dotnet run --project tools/AuthBridge.DbTool -- export --provider SqlServer --out authbridge-snapshot.json
   ```

   It prints row counts per table. Keep them.

2. **Migrate the empty PostgreSQL target** with owner credentials:

   ```powershell
   $env:AUTHBRIDGE_MIGRATION_CONNECTION = "<Supabase owner connection string>"
   dotnet run --project tools/AuthBridge.DbTool -- migrate --provider Postgres
   ```

3. **Import**

   ```powershell
   dotnet run --project tools/AuthBridge.DbTool -- import --provider Postgres --in authbridge-snapshot.json
   ```

   The import first checks every reference in the snapshot, refuses a non-empty target, runs
   in one transaction, then re-counts every table and fails on any mismatch.

4. **Check**

   ```powershell
   dotnet run --project tools/AuthBridge.DbTool -- verify --provider Postgres
   ```

   Compare the counts with step 1, and check that AUTH-104 is `TENANT-A AwaitingDocuments docs=ImagingReport`.

5. **Create the runtime role.** Run `db/postgres/runtime-role.sql` manually as the owner, with
   a password set in that session only.

6. **Map real users.** Supabase user IDs are UUIDs from the project's Auth users. Grant access
   with:

   ```powershell
   dotnet run --project tools/AuthBridge.DbTool -- grant-access --provider Postgres --subject <auth user uuid> --tenant TENANT-A --role Coordinator --label "Demo Coordinator"
   ```

7. **Switch the backend.** Set Render's `Database__Provider=Postgres` and
   `Database__ConnectionString` to the runtime role's connection string (see DEPLOYMENT.md).

Steps 1–4 were run in automated tests: `CrossProviderMigrationTests` goes from a LocalDB export
to a PostgreSQL import with matching counts and IDs. They have not been run against the real
Supabase project, because its credentials were not provided.

## Supabase-specific checks (manual; not yet run)

- **Exposed schemas:** in the Supabase dashboard (API settings), `authbridge` must not be
  listed as an exposed schema.
- **Grants:** as the owner, run

  ```sql
  SELECT r, has_schema_privilege(r, 'authbridge', 'USAGE')
  FROM unnest(ARRAY['anon','authenticated','authbridge_runtime']) r;
  ```

  Expect `false`, `false`, `true`.
- **Connection:** use the connection string copied from the project. Choose the session pooler
  if Render cannot reach the direct IPv6 host. Do not assemble pooler usernames or hosts by
  hand. Keep `Maximum Pool Size` small (5 is plenty). Check the pooler's current
  prepared-statement rules before relying on them. Session mode supports them; transaction mode
  may not.
- **TLS:** Supabase requires TLS. Keep `SSL Mode=Require` or stricter in the connection string.
  For full certificate verification, use `VerifyFull` with the project's CA certificate.

## Rollback

- **Before cut-over:** nothing changes for users. Drop the target's `authbridge` schema and repeat.
- **After cut-over:** point `Database__ConnectionString` back to the previous database and
  redeploy. Writes made since the cut-over exist only in the new database. Export them with
  the DbTool first if you need them.
- **Schema rollback:** `dotnet ef database update <PreviousMigration>` against that provider's
  assembly, after reviewing the generated `Down`. `PrivateSchemaHardening`'s `Down` only
  disables RLS; it does not re-grant privileges.

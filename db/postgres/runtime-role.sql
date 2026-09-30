-- AuthBridge: dedicated runtime role for the Render backend (PostgreSQL / Supabase).
--
-- Run MANUALLY, once, as the project owner after `DbTool migrate`, in a reviewed session.
-- It is not a migration and nothing runs it automatically. Replace the password placeholder
-- in your SQL session only; never commit a real value.
--
-- The migration/owner role creates and alters tables. The runtime role can only read and
-- write rows in the authbridge schema: no DDL and no access to other schemas.

CREATE ROLE authbridge_runtime LOGIN PASSWORD '<set-a-strong-password-in-session>' NOINHERIT;

GRANT USAGE ON SCHEMA authbridge TO authbridge_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA authbridge TO authbridge_runtime;
REVOKE ALL ON authbridge."__EFMigrationsHistory" FROM authbridge_runtime;
GRANT SELECT ON authbridge."__EFMigrationsHistory" TO authbridge_runtime;

-- RLS is enabled on every domain table by the PrivateSchemaHardening migration. The runtime
-- role is not the table owner, so it needs an explicit policy. Tenant scoping is enforced in
-- the application layer (CallerContext from the verified token plus UserAccess): a direct
-- Npgsql connection does not carry the user's JWT, so auth.uid() is NOT populated here.
-- These policies give the backend role row access and nothing else; anon and authenticated
-- still have no grants and no policies at all.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['SyntheticMembers', 'RequirementSets', 'RequiredDocuments', 'AuthorizationRequests',
                           'RequestDocuments', 'AuthorizationHistory', 'SubmissionProposals', 'SubmissionAttempts',
                           'UserAccess', 'AuditEvents'] LOOP
    EXECUTE format('CREATE POLICY authbridge_runtime_rows ON authbridge.%I FOR ALL TO authbridge_runtime USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- Verification (expect authbridge_runtime = true, anon and authenticated = false):
-- SELECT r, has_schema_privilege(r, 'authbridge', 'USAGE')
-- FROM unnest(ARRAY['authbridge_runtime', 'anon', 'authenticated']) AS r;

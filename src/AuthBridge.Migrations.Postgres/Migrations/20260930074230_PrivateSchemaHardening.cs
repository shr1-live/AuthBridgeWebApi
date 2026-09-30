using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthBridge.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Keeps the authbridge schema unreachable from the Supabase Data API roles and enables
    /// row level security as defence in depth. Role checks are conditional so the same
    /// migration runs on a plain PostgreSQL test instance where anon/authenticated do not exist.
    /// The schema must also stay out of Supabase's "Exposed schemas" setting; that is a
    /// project setting and is verified manually (docs/DATABASE_MIGRATION.md).
    /// </summary>
    public partial class PrivateSchemaHardening : Migration
    {
        private static readonly string[] Tables =
        [
            "SyntheticMembers", "RequirementSets", "RequiredDocuments", "AuthorizationRequests",
            "RequestDocuments", "AuthorizationHistory", "SubmissionProposals", "SubmissionAttempts",
            "UserAccess", "AuditEvents",
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE r text;
                BEGIN
                  FOREACH r IN ARRAY ARRAY['anon', 'authenticated'] LOOP
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
                      EXECUTE format('REVOKE ALL ON SCHEMA authbridge FROM %I', r);
                      EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA authbridge FROM %I', r);
                      EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA authbridge FROM %I', r);
                      EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA authbridge REVOKE ALL ON TABLES FROM %I', r);
                    END IF;
                  END LOOP;
                END $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA authbridge FROM PUBLIC;");

            // With RLS enabled and no permissive policy, any non-owner role that is ever
            // granted access sees no rows until an explicit policy is written for it.
            foreach (var table in Tables)
                migrationBuilder.Sql($"ALTER TABLE authbridge.\"{table}\" ENABLE ROW LEVEL SECURITY;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                migrationBuilder.Sql($"ALTER TABLE authbridge.\"{table}\" DISABLE ROW LEVEL SECURITY;");
        }
    }
}

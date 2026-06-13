using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantPreviewRoleOnProvisionedTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Backfill for existing deployments: the dataset-foundation migration granted
            // the sandboxed `formforge_preview` role SELECT on every table that existed at
            // that point, but Designer-provisioned tables created AFTER it were left
            // ungranted — so dataset Preview (which runs as this role) failed with
            // "42501: permission denied for table <name>" once a designer had been bound.
            // DdlEmitter now grants the role per-table at provision time (covers all FUTURE
            // tables); this one-time, idempotent re-grant covers tables already provisioned
            // before that fix shipped. Guarded so it no-ops where the role was never created
            // (a DB whose migration user lacked CREATEROLE). The grant deliberately spans
            // ALL public tables, then re-revokes the same sensitive/internal floor as the
            // foundation migration so password hashes, tokens and MFA secrets stay hidden.
            migrationBuilder.Sql(@"
DO $$
DECLARE t text;
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    GRANT SELECT ON ALL TABLES IN SCHEMA public TO formforge_preview;

    FOREACH t IN ARRAY ARRAY[
      'users', 'roles', 'refresh_tokens', 'password_reset_tokens',
      'mfa_backup_codes', 'mfa_sessions', 'schema_audit_log',
      'mutation_audit_log', 'dataset_audit_log', 'custom_dataset'
    ]
    LOOP
      IF to_regclass('public.' || t) IS NOT NULL THEN
        EXECUTE format('REVOKE SELECT ON public.%I FROM formforge_preview', t);
      END IF;
    END LOOP;
  END IF;
END
$$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Irreversible by design: this migration only widened SELECT grants for the
            // read-only preview role. Revoking them on Down would risk removing grants the
            // foundation migration legitimately established, so Down is a deliberate no-op.
        }
    }
}

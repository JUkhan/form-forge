using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestrictPreviewRoleUsersColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // `users` is exposed to the Query Builder (it is deliberately NOT in
            // DatasetAllowlist.SystemTables) so datasets can join to it for display names /
            // email of a record's creator. But the earlier migrations fully REVOKEd SELECT on
            // `users` from the sandboxed formforge_preview role, so any preview touching it
            // failed with 42501. Replace that all-or-nothing revoke with a COLUMN-LEVEL grant:
            // only the non-sensitive identity columns are readable; password_hash, MFA secrets,
            // theme preference, timestamps, etc. stay denied (a SELECT of any of them errors
            // with "permission denied for column ..."). This is the DB-layer enforcement; the
            // catalog (DatasetAllowlist.RestrictedColumns) also hides the other columns from
            // the palette — the two lists MUST stay in sync.
            //
            // Guarded so it no-ops where the role was never created (a DB whose migration user
            // lacked CREATEROLE). Clear any table-level grant first, then grant the columns.
            migrationBuilder.Sql(@"
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    REVOKE SELECT ON public.users FROM formforge_preview;
    GRANT SELECT (id, display_name, email, is_active)
      ON public.users TO formforge_preview;
  END IF;
END
$$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Revert to the previous posture: no access to `users` for the preview role.
            migrationBuilder.Sql(@"
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    REVOKE SELECT (id, display_name, email, is_active)
      ON public.users FROM formforge_preview;
    REVOKE SELECT ON public.users FROM formforge_preview;
  END IF;
END
$$;
");
        }
    }
}

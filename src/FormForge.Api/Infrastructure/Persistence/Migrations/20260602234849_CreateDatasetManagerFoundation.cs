using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateDatasetManagerFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // AC-1 (AR-57): create the `datasets` schema first so all later Dataset
            // VIEW DDL (Epic 9–11) can live in `datasets.{name}`, eliminating naming
            // collisions with `public`. Idempotent so re-runs never error.
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS datasets;");

            // AC-4 (AR-58): DEFAULT false backfills every existing role row in the same
            // transaction — no explicit UPDATE needed for the false case.
            migrationBuilder.AddColumn<bool>(
                name: "can_manage_datasets",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // AC-4: flip the seeded platform-admin role (deterministic UUID from the
            // CreateRolesRolePermissionsAndUserRoles migration) to true. ::uuid cast for
            // Npgsql compatibility.
            migrationBuilder.Sql(
                "UPDATE roles SET can_manage_datasets = true " +
                "WHERE id = '00000000-0000-0000-0000-000000000001'::uuid;");

            migrationBuilder.CreateTable(
                name: "custom_dataset",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    dataset_name = table.Column<string>(type: "text", nullable: false),
                    is_custom_query = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    query = table.Column<string>(type: "text", nullable: true),
                    builder_state = table.Column<string>(type: "jsonb", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_dataset", x => x.id);
                    table.ForeignKey(
                        name: "fk_custom_dataset_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_custom_dataset_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "dataset_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "text", nullable: true),
                    dataset_name = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    previous_values = table.Column<string>(type: "jsonb", nullable: true),
                    new_values = table.Column<string>(type: "jsonb", nullable: true),
                    ddl = table.Column<string>(type: "text", nullable: true),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_audit_log", x => x.id);
                    table.CheckConstraint("ck_dataset_audit_log_operation", "operation IN ('CREATE', 'UPDATE', 'DELETE')");
                    table.ForeignKey(
                        name: "fk_dataset_audit_log_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_dataset_created_by",
                table: "custom_dataset",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_custom_dataset_updated_by",
                table: "custom_dataset",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "idx_custom_dataset_dataset_name",
                table: "custom_dataset",
                column: "dataset_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dataset_audit_log_actor_id",
                table: "dataset_audit_log",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "idx_dataset_audit_log_dataset_name_timestamp",
                table: "dataset_audit_log",
                columns: new[] { "dataset_name", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_dataset_audit_log_operation",
                table: "dataset_audit_log",
                column: "operation");

            // Decision 6.7 / FR-72 — dedicated read-only PostgreSQL role for Dataset
            // preview execution (Epic 11). Created here, with least privilege, so the
            // preview connection pool can run user/builder queries as a non-privileged
            // principal. The role is created WITHOUT a password on purpose — secrets
            // never live in migrations; ops/Aspire sets it via DATASET_PREVIEW_DB_PASSWORD.
            // If the migration user lacks CREATEROLE this block errors and role creation
            // must be deferred to a manual DBA step (CI/test runs as superuser, so it is
            // fine there).
            migrationBuilder.Sql(@"
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    CREATE ROLE formforge_preview LOGIN NOINHERIT;
  END IF;
END
$$;

GRANT SELECT ON ALL TABLES IN SCHEMA public TO formforge_preview;

-- Revoke internal/sensitive tables individually. Wrapped in a guard so names that do
-- not (yet) exist in this schema — e.g. mfa_sessions, which architecture §6.7 lists as
-- forward-looking — are skipped instead of aborting the migration.
DO $$
DECLARE t text;
BEGIN
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
END
$$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Tear down the preview role first. DROP OWNED BY revokes every privilege the
            // role holds in this database (incl. the GRANT SELECT above) and drops any
            // objects it owns, so the subsequent DROP ROLE cannot fail on dependencies.
            migrationBuilder.Sql(@"
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'formforge_preview') THEN
    DROP OWNED BY formforge_preview;
    DROP ROLE formforge_preview;
  END IF;
END
$$;");

            // DropTable cascades the table's indexes, FK and CHECK constraints.
            migrationBuilder.DropTable(
                name: "custom_dataset");

            migrationBuilder.DropTable(
                name: "dataset_audit_log");

            migrationBuilder.DropColumn(
                name: "can_manage_datasets",
                table: "roles");

            // Safe: this migration is the only creator of the schema and no VIEWs live
            // here yet (those arrive in later stories).
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS datasets;");
        }
    }
}

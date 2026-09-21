using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    // Seeds the hidden `platform-dev` system role (deterministic id ...03) into every
    // schema this migration set is replayed into (public and each tenant schema), and adds
    // the per-tenant developer credentials columns to `tenants`. The seed uses
    // ON CONFLICT DO NOTHING so replaying is idempotent. can_manage_datasets is true so the
    // developer can use the Dataset Manager (RequireDatasetManagement) without any
    // role_permissions rows.
    //
    // No Designer/BuildTargetModel partial: the [Migration] attributes live here, and the
    // up-to-date model is captured by FormForgeDbContextModelSnapshot.
    [DbContext(typeof(FormForgeDbContext))]
    [Migration("20260921000000_SeedPlatformDevRole")]
    public partial class SeedPlatformDevRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "dev_user_email",
                table: "tenants",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dev_user_password_encrypted",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                "INSERT INTO roles (id, name, description, is_system, can_manage_datasets, created_at) " +
                "VALUES ('00000000-0000-0000-0000-000000000003', 'platform-dev', NULL, true, true, " +
                "TIMESTAMPTZ '2026-01-01 00:00:00+00') " +
                "ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                "DELETE FROM roles WHERE id = '00000000-0000-0000-0000-000000000003';");

            migrationBuilder.DropColumn(
                name: "dev_user_password_encrypted",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "dev_user_email",
                table: "tenants");
        }
    }
}

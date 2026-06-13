using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRolePermissionCanExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<bool>(
                name: "can_export",
                table: "role_permissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: preserve current behaviour. Until now the export endpoint
            // required 'read', so every role that can read could already export.
            // Grant can_export to those rows so no one loses export access on
            // deploy; admins can revoke per-role afterward. Rows created after
            // this migration default to false.
            migrationBuilder.Sql(
                "UPDATE role_permissions SET can_export = true WHERE can_read = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "can_export",
                table: "role_permissions");
        }
    }
}

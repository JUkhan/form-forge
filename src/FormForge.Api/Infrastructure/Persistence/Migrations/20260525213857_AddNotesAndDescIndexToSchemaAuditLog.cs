using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotesAndDescIndexToSchemaAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "idx_schema_audit_log_designer_id_created_at",
                table: "schema_audit_log");

            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "schema_audit_log",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_schema_audit_log_designer_id_created_at_desc",
                table: "schema_audit_log",
                columns: new[] { "designer_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "idx_schema_audit_log_designer_id_created_at_desc",
                table: "schema_audit_log");

            migrationBuilder.DropColumn(
                name: "notes",
                table: "schema_audit_log");

            migrationBuilder.CreateIndex(
                name: "idx_schema_audit_log_designer_id_created_at",
                table: "schema_audit_log",
                columns: new[] { "designer_id", "created_at" });
        }
    }
}

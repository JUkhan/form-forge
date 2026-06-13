using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateSchemaAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.CreateTable(
                name: "schema_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    designer_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    from_version = table.Column<int>(type: "integer", nullable: true),
                    to_version = table.Column<int>(type: "integer", nullable: false),
                    ddl_operation = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    columns_added = table.Column<string[]>(type: "text[]", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_audit_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_schema_audit_log_correlation_id",
                table: "schema_audit_log",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "idx_schema_audit_log_designer_id_created_at",
                table: "schema_audit_log",
                columns: new[] { "designer_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "schema_audit_log");
        }
    }
}

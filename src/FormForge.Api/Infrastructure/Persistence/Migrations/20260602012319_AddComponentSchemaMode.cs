using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComponentSchemaMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // FR-54 AC-2: the AddColumn DEFAULT 'CRUD' backfills all existing rows
            // in the same transaction — no explicit UPDATE statement needed.
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "component_schemas",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "CRUD");

            // Decision 1.8: enforce the CHECK at the DB level. EF Core does not emit
            // PostgreSQL CHECK constraints from configuration, so author it via raw SQL.
            migrationBuilder.Sql(
                "ALTER TABLE component_schemas ADD CONSTRAINT ck_component_schemas_mode CHECK (mode IN ('CRUD', 'VIEW'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Drop the constraint before the column it guards.
            migrationBuilder.Sql("ALTER TABLE component_schemas DROP CONSTRAINT IF EXISTS ck_component_schemas_mode;");
            migrationBuilder.DropColumn(
                name: "mode",
                table: "component_schemas");
        }
    }
}

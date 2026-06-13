using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishedVersionUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // CHECK constraint pins the casing of `status` before the partial unique
            // index relies on it. The index filter `(status = 'Published')` is
            // case-sensitive in PostgreSQL; without this CHECK a `'published'`
            // (lowercase) row would silently bypass both the in-memory demote loop
            // and the index, violating FR-13 (at-most-one-Published per designer).
            migrationBuilder.Sql(
                "ALTER TABLE component_schema_versions " +
                "ADD CONSTRAINT ck_component_schema_versions_status " +
                "CHECK (status IN ('Draft', 'Published', 'Archived'));");

            migrationBuilder.CreateIndex(
                name: "uq_one_published_per_designer",
                table: "component_schema_versions",
                column: "designer_id",
                unique: true,
                filter: "(status = 'Published')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "uq_one_published_per_designer",
                table: "component_schema_versions");

            migrationBuilder.Sql(
                "ALTER TABLE component_schema_versions " +
                "DROP CONSTRAINT IF EXISTS ck_component_schema_versions_status;");
        }
    }
}

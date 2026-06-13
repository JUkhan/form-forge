using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropOnePublishedPerDesignerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // A designer may now have multiple Published versions at once — drop
            // the at-most-one-Published partial unique index. The status CHECK
            // constraint (ck_component_schema_versions_status) is intentionally
            // kept; it only validates casing and is orthogonal to this change.
            migrationBuilder.DropIndex(
                name: "uq_one_published_per_designer",
                table: "component_schema_versions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "uq_one_published_per_designer",
                table: "component_schema_versions",
                column: "designer_id",
                unique: true,
                filter: "(status = 'Published')");
        }
    }
}

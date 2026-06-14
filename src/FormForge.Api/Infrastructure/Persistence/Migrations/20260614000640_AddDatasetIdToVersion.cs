using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasetIdToVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Optional per-version dataset binding. Nullable: existing rows backfill to
            // NULL (no dataset), preserving current behaviour. References a CustomDataset
            // by id; when set, the record-list endpoint reads rows from that dataset's
            // backing VIEW instead of the provisioned dynamic table.
            migrationBuilder.AddColumn<Guid>(
                name: "dataset_id",
                table: "component_schema_versions",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "dataset_id",
                table: "component_schema_versions");
        }
    }
}

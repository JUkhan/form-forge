using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthFilterFieldKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Optional per-version auth filter. Nullable: existing rows backfill to
            // NULL (no filter), preserving current behaviour. 63-char cap matches the
            // fieldKey / PostgreSQL identifier limit it stores.
            migrationBuilder.AddColumn<string>(
                name: "auth_filter_field_key",
                table: "component_schema_versions",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "auth_filter_field_key",
                table: "component_schema_versions");
        }
    }
}

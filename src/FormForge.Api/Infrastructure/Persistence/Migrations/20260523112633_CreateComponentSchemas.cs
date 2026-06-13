using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateComponentSchemas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "component_schemas",
                columns: table => new
                {
                    designer_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_component_schemas", x => x.designer_id);
                    table.ForeignKey(
                        name: "fk_component_schemas_users",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "component_schema_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    designer_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    root_element = table.Column<string>(type: "jsonb", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_component_schema_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_component_schema_versions_schema",
                        column: x => x.designer_id,
                        principalTable: "component_schemas",
                        principalColumn: "designer_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_component_schema_versions_users",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_component_schema_versions_created_by",
                table: "component_schema_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "idx_component_schema_versions_designer_id",
                table: "component_schema_versions",
                column: "designer_id");

            migrationBuilder.CreateIndex(
                name: "uq_component_schema_versions_designer_version",
                table: "component_schema_versions",
                columns: new[] { "designer_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_component_schemas_created_by",
                table: "component_schemas",
                column: "created_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "component_schema_versions");

            migrationBuilder.DropTable(
                name: "component_schemas");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMenuBindingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<int>(
                name: "bound_version",
                table: "menus",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "designer_id",
                table: "menus",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provisioning_error",
                table: "menus",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provisioning_status",
                table: "menus",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_menus_designer_id",
                table: "menus",
                column: "designer_id");

            migrationBuilder.CreateIndex(
                name: "idx_menus_provisioning_status_pending",
                table: "menus",
                column: "provisioning_status",
                filter: "(provisioning_status = 'Pending')");

            migrationBuilder.AddForeignKey(
                name: "fk_menus_bound_designer",
                table: "menus",
                column: "designer_id",
                principalTable: "component_schemas",
                principalColumn: "designer_id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "fk_menus_bound_designer",
                table: "menus");

            migrationBuilder.DropIndex(
                name: "idx_menus_designer_id",
                table: "menus");

            migrationBuilder.DropIndex(
                name: "idx_menus_provisioning_status_pending",
                table: "menus");

            migrationBuilder.DropColumn(
                name: "bound_version",
                table: "menus");

            migrationBuilder.DropColumn(
                name: "designer_id",
                table: "menus");

            migrationBuilder.DropColumn(
                name: "provisioning_error",
                table: "menus");

            migrationBuilder.DropColumn(
                name: "provisioning_status",
                table: "menus");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "platform_admins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_admins", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_tenants_created_by",
                table: "tenants",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "uq_platform_admins_user_email",
                table: "platform_admins",
                column: "user_email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tenants_platform_admins",
                table: "tenants",
                column: "created_by",
                principalTable: "platform_admins",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_tenants_platform_admins",
                table: "tenants");

            migrationBuilder.DropTable(
                name: "platform_admins");

            migrationBuilder.DropIndex(
                name: "idx_tenants_created_by",
                table: "tenants");
        }
    }
}

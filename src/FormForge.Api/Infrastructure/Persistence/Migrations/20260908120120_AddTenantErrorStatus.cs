using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantErrorStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants",
                sql: "status IN ('Provisioning', 'Active', 'Suspended', 'Error')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants",
                sql: "status IN ('Provisioning', 'Active', 'Suspended')");
        }
    }
}

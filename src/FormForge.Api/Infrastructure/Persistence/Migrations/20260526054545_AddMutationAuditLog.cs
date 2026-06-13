using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMutationAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.CreateTable(
                name: "mutation_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    designer_id = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    new_values = table.Column<string>(type: "jsonb", nullable: true),
                    previous_values = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mutation_audit_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_mutation_audit_log_correlation_id",
                table: "mutation_audit_log",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "idx_mutation_audit_log_designer_id_timestamp_desc",
                table: "mutation_audit_log",
                columns: new[] { "designer_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_mutation_audit_log_record_id_timestamp_desc",
                table: "mutation_audit_log",
                columns: new[] { "record_id", "timestamp" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "mutation_audit_log");
        }
    }
}

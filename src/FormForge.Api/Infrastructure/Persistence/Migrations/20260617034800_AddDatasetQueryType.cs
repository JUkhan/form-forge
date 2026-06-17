using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasetQueryType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Parameterized-query feature — distinguishes a dataset materialized as a VIEW
            // ("view") from one stored as a record only ("query"). NOT NULL with a "view"
            // default so every existing row backfills to the original VIEW semantics.
            migrationBuilder.AddColumn<string>(
                name: "query_type",
                table: "custom_dataset",
                type: "text",
                nullable: false,
                defaultValue: "view");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "query_type",
                table: "custom_dataset");
        }
    }
}

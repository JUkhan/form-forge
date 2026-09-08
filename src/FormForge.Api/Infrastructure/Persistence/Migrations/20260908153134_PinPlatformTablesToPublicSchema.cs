using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FormForge.Api.Infrastructure.Persistence.Migrations
{
    // Story 12.6 — pins Tenant/PlatformAdmin/TenantUserIndexEntry to schema "public"
    // explicitly in the EF model (FormForgeDbContext.OnModelCreating), so every query
    // against them is fully schema-qualified regardless of the connection's current
    // search_path. This migration exists only to keep the EF Core design-time model
    // snapshot in sync with that model change (so a future `dotnet ef migrations add`
    // does not re-propose the same delta) — it intentionally emits NO DDL.
    //
    // Why not the auto-generated RenameTable(..., newSchema: "public") EF proposes for
    // this delta: `dotnet ef migrations add` initially generated exactly that, and it
    // is correct ONLY for the main "public" deployment, where these three tables are
    // already physically in `public` (every prior migration used unqualified names,
    // and this app's connection's default search_path resolves to `public`) — so the
    // rename is a harmless same-schema no-op there. But TenantProvisioningService
    // (Story 12.2) replays this entire unmodified migration set into EVERY new
    // tenant's own schema via a connection whose search_path targets that tenant's
    // schema (Design Notes: "every migration runs unmodified... resolved through the
    // connection's effective schema resolution"). Under that replay, a literal
    // `newSchema: "public"` is NOT relative to the tenant's schema — it tries to
    // physically relocate the tenant-local `tenants`/`platform_admins`/
    // `tenant_user_index` copy this same migration set just created into the actual
    // global `public` schema, colliding with the row already provisioned in the main
    // deployment (`42P07: relation "tenants" already exists in schema "public"`) or a
    // previously-provisioned tenant's copy. Emitting no DDL here avoids that entirely:
    // the model-level schema pin does the real work at query time; this migration is a
    // snapshot-bookkeeping placeholder, safe to replay into any schema any number of
    // times.
    /// <inheritdoc />
    public partial class PinPlatformTablesToPublicSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
        }
    }
}

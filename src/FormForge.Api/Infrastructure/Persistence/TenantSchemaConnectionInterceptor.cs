using FormForge.Api.Features.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace FormForge.Api.Infrastructure.Persistence;

// Story 12.6 (FR-74 / architecture.md §7.6) — resolves the Postgres `search_path` for
// FormForgeDbContext's EF-managed connection from the scoped ITenantContext, at the
// moment each physical connection opens (never at DbContext construction time).
//
// Why per-connection-open and not per-DbContext-construction: TenantContextMiddleware
// itself resolves FormForgeDbContext (to look up the `tenants` row) BEFORE it calls
// ITenantContext.Set() in the same request scope — so SchemaName is still null when
// the middleware's own query opens its connection (correctly landing on `public`,
// where `Tenants` lives). Every later query in the same request — after Set() has run —
// opens its own fresh connection under EF Core's default implicit connection management,
// which passes back through this same interceptor and by then reads the resolved schema.
// Registered Scoped (see Program.cs) and resolved via the AddDbContext(sp, options)
// overload so one instance backs the whole request, matching ITenantContext's lifetime.
//
// Bug fix (found while verifying this story): rebuild the connection string from the
// ORIGINAL configured "formforge" connection string, captured once here — never from
// `connection.ConnectionString` at open time. EF Core's implicit connection management
// reuses the SAME NpgsqlConnection object across multiple open/close cycles within one
// DbContext's lifetime, and Npgsql's default `Persist Security Info=false` strips the
// password from `.ConnectionString` after the FIRST successful open. Reading it back
// from the connection on a SECOND open (as this interceptor originally did) would
// silently rebuild a password-less connection string and fail every open after the
// first with "No password has been provided but the backend requires one".
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class TenantSchemaConnectionInterceptor(
    ITenantContext tenantContext, IConfiguration configuration) : DbConnectionInterceptor
{
    private readonly string _baseConnectionString = configuration.GetConnectionString("formforge")
        ?? throw new InvalidOperationException("Connection string 'formforge' not configured.");

    public override InterceptionResult ConnectionOpening(
        System.Data.Common.DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        ApplySearchPath(connection);
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        System.Data.Common.DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        ApplySearchPath(connection);
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    // `{tenant schema}, public` when a tenant is resolved (so Tenant/PlatformAdmin/
    // TenantUserIndexEntry — pinned to schema "public" explicitly in the EF model —
    // still resolve regardless of search_path), or just `public` when no tenant is
    // resolved (never a redundant "public, public"). No other fallback exists.
    private void ApplySearchPath(System.Data.Common.DbConnection connection)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
            return;

        var schema = tenantContext.SchemaName ?? "public";
        var searchPath = schema == "public" ? "public" : $"{schema}, public";
        var csb = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = searchPath,
        };
        npgsqlConnection.ConnectionString = csb.ConnectionString;
    }
}

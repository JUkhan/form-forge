using FormForge.Api.Common.Logging;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Tenancy;

// Story 12.3 (FR-74 / architecture.md §7.3) — resolves the request's tenant from the
// validated JWT `tenantId` claim and populates ITenantContext for the rest of the
// pipeline. Registered between app.UseAuthentication() and app.UseAuthorization() in
// Program.cs (see this story's Design Notes for why — HttpContext.User must already
// be populated, which only holds after UseAuthentication(); this still satisfies
// "before RequireAuth/permission checks" since those run at the UseAuthorization()
// stage).
//
// Convention-based middleware (not IMiddleware) so InvokeAsync's extra parameters are
// resolved per-request from the scoped container, same pattern the framework itself
// uses — FormForgeDbContext and ITenantContext are both Scoped.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Instantiated at runtime by app.UseMiddleware<TenantContextMiddleware>() — the analyzer cannot see the factory path.")]
internal sealed partial class TenantContextMiddleware
{
    private const string TenantIdClaimType = "tenantId";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        FormForgeDbContext db,
        ITenantLookupCache lookupCache,
        ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lookupCache);
        ArgumentNullException.ThrowIfNull(tenantContext);

        // No claim at all — anonymous request, or a legacy token issued before this
        // story. Pass through untouched; ITenantContext stays unset (I/O matrix row 3).
        var tenantIdClaim = context.User.FindFirst(TenantIdClaimType)?.Value;
        if (string.IsNullOrEmpty(tenantIdClaim))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A claim that fails to parse as a Guid is exactly as untrustworthy as one that
        // references a missing tenant — reject the same way (401), never let a
        // malformed claim silently fall through as "no tenant".
        if (!Guid.TryParse(tenantIdClaim, out var tenantId))
        {
            LogMalformedTenantClaim(_logger, tenantIdClaim);
            await WriteTenantInvalidAsync(context).ConfigureAwait(false);
            return;
        }

        var entry = lookupCache.TryGet(tenantId);
        if (entry is null)
        {
            var tenant = await db.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.SchemaName, t.Status })
                .FirstOrDefaultAsync(context.RequestAborted)
                .ConfigureAwait(false);

            // Defense-in-depth: a stale/forged claim referencing a tenant that no
            // longer exists never reaches a handler.
            if (tenant is null)
            {
                LogTenantNotFound(_logger, tenantId);
                await WriteTenantInvalidAsync(context).ConfigureAwait(false);
                return;
            }

            entry = new TenantLookupEntry(tenant.SchemaName, tenant.Status);
            lookupCache.Set(tenantId, entry);
        }

        // Defense-in-depth: Suspended/Provisioning tenants must not authenticate any
        // request even if a still-valid JWT was minted while the tenant was Active.
        if (!string.Equals(entry.Status, "Active", StringComparison.Ordinal))
        {
            LogTenantNotActive(_logger, tenantId, entry.Status);
            await WriteTenantInactiveAsync(context).ConfigureAwait(false);
            return;
        }

        tenantContext.Set(tenantId, entry.SchemaName);

        await _next(context).ConfigureAwait(false);
    }

    // Mirrors the RateLimiter OnRejected problem-body shape in Program.cs (the closest
    // precedent for a non-endpoint, non-filter pipeline stage writing a problem+json
    // body directly) and the `code`/`messageKey`/`correlationId` extension fields every
    // Results.Problem(...) call in AuthEndpoints.cs carries.
    private static Task WriteTenantInvalidAsync(HttpContext context) => WriteProblemAsync(
        context,
        title: "Tenant invalid",
        detail: "The tenant referenced by this token could not be resolved.",
        code: "TENANT_INVALID",
        messageKey: "auth.tenantInvalid");

    private static Task WriteTenantInactiveAsync(HttpContext context) => WriteProblemAsync(
        context,
        title: "Tenant inactive",
        detail: "This tenant is not currently active.",
        code: "TENANT_INACTIVE",
        messageKey: "auth.tenantInactive");

    private static Task WriteProblemAsync(HttpContext context, string title, string detail, string code, string messageKey)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(
            new
            {
                type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.2",
                status = StatusCodes.Status401Unauthorized,
                title,
                detail,
                code,
                messageKey,
                correlationId = context.GetCorrelationId(),
            },
            context.RequestAborted);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "TenantContextMiddleware — malformed tenantId claim rejected. ClaimValue={ClaimValue}")]
    private static partial void LogMalformedTenantClaim(ILogger logger, string claimValue);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "TenantContextMiddleware — tenantId claim references a missing tenant. TenantId={TenantId}")]
    private static partial void LogTenantNotFound(ILogger logger, Guid tenantId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "TenantContextMiddleware — rejected request for non-Active tenant. TenantId={TenantId} Status={Status}")]
    private static partial void LogTenantNotActive(ILogger logger, Guid tenantId, string status);
}

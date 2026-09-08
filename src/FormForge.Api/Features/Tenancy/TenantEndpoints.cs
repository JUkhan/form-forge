using FormForge.Api.Common;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.Tenancy.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Tenancy;

// Story 12.5 — platform-super-admin-only tenant onboarding surface. Mounted at its own
// top-level /api/admin/tenants group in Program.cs (never nested under the existing
// /api/admin MapGroup, whose "platform-admin" policy a platform-super-admin JWT never
// satisfies — see this story's Boundaries). Mirrors RoleEndpoints.cs/AdminEndpoints.cs
// for structure: static handlers, Results.Problem envelopes, PagedResult<T> for the list.
internal static partial class TenantEndpoints
{
    internal static RouteGroupBuilder MapTenantEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/", GetTenantsHandler)
             .WithSummary("List all tenants (paginated)")
             .Produces<PagedResult<TenantDto>>(StatusCodes.Status200OK);

        group.MapPost("/", CreateTenantHandler)
             .AddValidationFilter<CreateTenantRequest>()
             .WithSummary("Create a tenant: provision its schema and onboard its first admin")
             .Produces<CreateTenantResponse>(StatusCodes.Status201Created)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status409Conflict)
             .Produces(StatusCodes.Status422UnprocessableEntity)
             .Produces(StatusCodes.Status500InternalServerError);

        return group;
    }

    private static async Task<IResult> GetTenantsHandler(
        FormForgeDbContext db,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        pageSize = Math.Min(Math.Max(pageSize, 1), 100);
        page = Math.Max(page, 1);

        // Newest first — the platform-super-admin's own just-created row (which this
        // page's poll hook watches) is always on page 1 without a client-side sort.
        var query = db.Tenants.AsNoTracking().OrderByDescending(t => t.CreatedAt);

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Same long-arithmetic Skip-overflow guard as RoleService.GetRolesAsync.
        var skip = (int)Math.Min(int.MaxValue, ((long)page - 1L) * pageSize);

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(t => new TenantDto(t.Id, t.Name, t.SchemaName, t.Status, t.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Results.Ok(new PagedResult<TenantDto>(items, total, page, pageSize));
    }

    private static async Task<IResult> CreateTenantHandler(
        CreateTenantRequest request,
        FormForgeDbContext db,
        ITenantProvisioningService provisioningService,
        ITenantOnboardingService onboardingService,
        ITemporaryPasswordGenerator passwordGenerator,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(provisioningService);
        ArgumentNullException.ThrowIfNull(onboardingService);
        ArgumentNullException.ThrowIfNull(passwordGenerator);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // Reuse Story 12.2's exact identifier validation (SafeIdentifier.TryCreate is the
        // same call ProvisionSchemaAsync makes internally) BEFORE any row is inserted —
        // mirrors DesignerService.CreateAsync's precedent for designerId — so an
        // invalid schema_name never creates a row (I/O matrix: "No row created").
        if (!SafeIdentifier.TryCreate(request.SchemaName, out var safeSchemaName, out var failureCode, out var idError))
        {
            return failureCode == SafeIdentifierError.ReservedKeyword
                ? SchemaNameReservedKeywordProblem(idError!)
                : SchemaNameInvalidProblem(idError!);
        }

        var schemaName = safeSchemaName!.Value;

        // Reuse TenantProvisioningService.IsReservedSchemaName — the same PG
        // system-schema check ProvisionSchemaAsync makes internally (e.g. "public"),
        // which SafeIdentifier's format/keyword rules don't cover. Checked here too,
        // BEFORE the row is inserted, so this class of invalid identifier also never
        // creates a row — same "no row created" guarantee as the SafeIdentifier branch
        // above, not merely a post-insert provisioning failure.
        if (TenantProvisioningService.IsReservedSchemaName(schemaName))
        {
            return SchemaNameReservedKeywordProblem(
                $"'{schemaName}' is a reserved PostgreSQL system schema and cannot be used.");
        }

        // App-level collision pre-check, ahead of uq_tenants_schema_name — mirrors
        // ProvisionSchemaAsync's own pre-check (Story 12.2) so a colliding schema_name
        // also never creates a row, not merely fails provisioning after the insert.
        var collision = await db.Tenants.AsNoTracking()
            .AnyAsync(t => t.SchemaName == schemaName, ct)
            .ConfigureAwait(false);
        if (collision)
        {
            return SchemaNameConflictProblem();
        }

        // Story 12.4's platform-admin token shape: "userId" carries the platform_admins.id.
        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        Guid? createdBy = Guid.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

        var tenant = new Tenant
        {
            Name = request.Name.Trim(),
            SchemaName = schemaName,
            Status = "Provisioning",
            CreatedBy = createdBy,
        };
        db.Tenants.Add(tenant);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && string.Equals(pg.ConstraintName, "uq_tenants_schema_name", StringComparison.Ordinal))
        {
            return SchemaNameConflictProblem();
        }

        // Decision (human-approved) — the Create Tenant form takes only name +
        // schema_name; the first tenant-admin's placeholder identity and temporary
        // password are entirely server-derived/generated here.
        var temporaryPassword = passwordGenerator.Generate();

        // schemaName allows '_' (a valid Postgres identifier char, per SafeIdentifier), but
        // '_' is not a valid domain-label character — browsers' native <input type="email">
        // validation (WHATWG) rejects it, blocking the derived admin from ever logging in
        // via the login form. Map '_' to '-' for the email's domain label only; the actual
        // schema name (and its uniqueness guarantee) is untouched.
        var adminEmail = $"admin@{ToEmailDomainLabel(schemaName)}.tenant.local";
        const string adminDisplayName = "Tenant Admin";

        try
        {
            await provisioningService.ProvisionSchemaAsync(tenant, ct).ConfigureAwait(false);
            await onboardingService
                .OnboardTenantAsync(tenant, adminEmail, adminDisplayName, temporaryPassword, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnect/timeout, not a provisioning failure — propagate as-is.
            // The row is left at 'Provisioning'; TenantProvisioningRecoveryService's
            // startup scan is the backstop, same as any other crash-before-catch window.
            throw;
        }
#pragma warning disable CA1031 // Decision (human-approved): the endpoint's own catch
        // block is what turns a mid-flow provisioning/onboarding failure into a visible
        // 'Error' status instead of an untracked 500 — additive to, not a replacement
        // for, TenantProvisioningRecoveryService's unrelated flag-only scan (which only
        // ever catches a crash before this catch block runs at all).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var logger = loggerFactory.CreateLogger(nameof(TenantEndpoints));
            tenant.Status = "Error";
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // best-effort status write — the original exception is
            // what gets logged/returned either way.
            catch (Exception saveEx)
#pragma warning restore CA1031
            {
                LogErrorStatusWriteFailed(logger, saveEx, tenant.Id);
            }

            LogProvisioningOrOnboardingFailed(logger, ex, tenant.Id);

            return Results.Problem(
                detail: "Tenant provisioning failed. The tenant's status has been set to 'Error'.",
                title: "Tenant provisioning failed",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "TENANT_PROVISIONING_FAILED",
                    ["messageKey"] = "tenants.provisioningFailed",
                });
        }

        return Results.Created(
            $"/api/admin/tenants/{tenant.Id}",
            new CreateTenantResponse(
                new TenantDto(tenant.Id, tenant.Name, tenant.SchemaName, tenant.Status, tenant.CreatedAt),
                temporaryPassword));
    }

    // A domain label must start and end with an alphanumeric character (WHATWG email
    // regex), but schemaName may start/end with '_' (-> '-' after substitution). Pad with
    // a digit rather than trimming, so distinct schema names can't collapse onto the same
    // derived email.
    private static string ToEmailDomainLabel(string schemaName)
    {
        var label = schemaName.Replace('_', '-');
        if (label[0] == '-')
        {
            label = "0" + label;
        }

        if (label[^1] == '-')
        {
            label += "0";
        }

        return label;
    }

    private static IResult SchemaNameInvalidProblem(string detail) =>
        Results.Problem(
            detail: detail,
            title: "Invalid schema name",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "TENANT_SCHEMA_NAME_INVALID",
                ["messageKey"] = "tenants.schemaNameInvalid",
                ["errors"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["schemaName"] = [detail],
                },
            });

    private static IResult SchemaNameReservedKeywordProblem(string detail) =>
        Results.Problem(
            detail: detail,
            title: "Reserved schema name",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "TENANT_SCHEMA_NAME_RESERVED",
                ["messageKey"] = "tenants.schemaNameReserved",
                ["errors"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["schemaName"] = [detail],
                },
            });

    private static IResult SchemaNameConflictProblem()
    {
        const string Detail = "A tenant with this schema_name already exists.";
        return Results.Problem(
            detail: Detail,
            title: "Schema name already exists",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "TENANT_SCHEMA_NAME_CONFLICT",
                ["messageKey"] = "tenants.schemaNameConflict",
                ["errors"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["schemaName"] = [Detail],
                },
            });
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "TenantEndpoints — tenant {TenantId} provisioning/onboarding failed; status set to 'Error'")]
    private static partial void LogProvisioningOrOnboardingFailed(ILogger logger, Exception ex, Guid tenantId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "TenantEndpoints — tenant {TenantId} failed provisioning/onboarding AND writing status='Error' back also failed; row is left stuck for TenantProvisioningRecoveryService to flag")]
    private static partial void LogErrorStatusWriteFailed(ILogger logger, Exception ex, Guid tenantId);
}

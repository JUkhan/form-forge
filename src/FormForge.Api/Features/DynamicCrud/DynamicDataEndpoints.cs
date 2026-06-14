using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Dapper;
using FormForge.Api.Common;
using FormForge.Api.Common.Endpoints;
using FormForge.Api.Common.Logging;
using FormForge.Api.Features.Datasets;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.Permissions;
using FormForge.Api.Features.Provisioning;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.DynamicCrud;

// Registered in Program.cs under /api/data/{designerId}. Story 6.1 adds the
// list-records GET handler; create/update/delete handlers arrive in Stories 6.2-6.6.
internal static class DynamicDataEndpoints
{
    private static readonly Action<ILogger, string, Guid, int, Exception?> LogCascadeSoftDeleteSkipped =
        LoggerMessage.Define<string, Guid, int>(
            LogLevel.Warning,
            new EventId(1, "CascadeSoftDeleteSkipped"),
            "Cascade soft-delete skipped for {DesignerId}/{RecordId}: schema declares {ChildCount} " +
            "child designer(s) but none have a Published schema. Parent will be soft-deleted " +
            "without cascading to child rows.");

    internal static RouteGroupBuilder MapDynamicDataEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Story 6.1 — GET /api/data/{designerId}?page&pageSize&sort&filter[k]=v.
        // The endpoint inherits RequireRateLimiting("data-read") from the group
        // (Program.cs); per-endpoint .RequirePermission("read") enforces canRead.
        group.MapGet("/", ListRecordsHandler)
             .WithSummary("List records from a provisioned dynamic table with pagination, filtering, and sorting.")
             .Produces<PagedResult<DynamicRecord>>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("read");

        // Story 7-followup — GET /api/data/{designerId}/export?format=csv|xlsx|pdf.
        // Streams the full filter/sort-honored result set as a file. The literal
        // "/export" path segment is more specific than "/{id:guid}" so it must
        // come before the get-by-id route in the registration order.
        group.MapGet("/export", ExportRecordsHandler)
             .WithSummary("Export records (CSV / XLSX / PDF) honoring the current filter and sort.")
             .Produces(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("export");

        // Designer-backed Dropdown options. Returns ONLY {value,label} pairs from
        // another designer's table, paginated + searchable + cascading-filterable.
        // Auth-only (no per-designer "read"): a form author may legitimately need a
        // lookup whose target designer the filler can't otherwise read. The literal
        // "/options" segment is more specific than "/{id:guid}" so it must precede it.
        group.MapGet("/options", ListOptionsHandler)
             .WithSummary("List {value,label} options for a Designer-backed dropdown (paginated, searchable, cascading).")
             .Produces<PagedResult<DropdownOption>>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity);

        // TreeView — GET /api/data/{designerId}/tree?parentId&page&pageSize&search.
        // Returns ONE level of a self-referencing adjacency-list tree: root nodes when
        // parentId is omitted, otherwise the direct children of parentId. Each row carries
        // a derived `_has_children` flag; `hasNextPage` drives the per-level paginator.
        // The literal "/tree" segment is more specific than "/{id:guid}" so it precedes it.
        group.MapGet("/tree", ListTreeNodesHandler)
             .WithSummary("List one level of a TreeView self-referencing tree (lazy, paginated, searchable).")
             .Produces<TreeLevelResult>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("read");

        // TreeView "All select" — GET /api/data/{designerId}/tree/descendants?parentId=.
        // Returns the ids of every live descendant of parentId (recursive), so selecting a
        // node can cascade-select its whole subtree even across un-expanded branches.
        group.MapGet("/tree/descendants", ListTreeDescendantsHandler)
             .WithSummary("List the ids of all live descendants of a TreeView node (recursive).")
             .Produces<TreeDescendantsResult>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("read");

        // TreeView — POST /api/data/{designerId}/tree. Creates a node, writing the
        // self-FK parent_<table>_id from the body's `parentId` (null → a root node).
        // Mutate mode hits the backend directly (no parent-form batching). The literal
        // "/tree" segment precedes "/{id:guid}".
        group.MapPost("/tree", CreateTreeNodeHandler)
             .WithSummary("Create a node in a TreeView self-referencing tree under an optional parent.")
             .Produces<DynamicRecord>(StatusCodes.Status201Created)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("create")
             .RequireRateLimiting("data-write");

        // Story 6.2 — GET /api/data/{designerId}/{id}?include=children.
        // {id:guid} route constraint handles UUID validation before the handler runs
        // (non-UUID path segments return 400 from ASP.NET automatically).
        group.MapGet("/{id:guid}", GetRecordHandler)
             .WithSummary("Get a single record from a provisioned dynamic table by ID.")
             .Produces<DynamicRecord>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("read");

        // Story 6.3 — POST /api/data/{designerId}. Rate limit overrides the group
        // default: "data-write" (60 req/min) vs group "data-read" (300 req/min).
        group.MapPost("/", CreateRecordHandler)
             .WithSummary("Create a new record in a provisioned dynamic table.")
             .Produces<DynamicRecord>(StatusCodes.Status201Created)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("create")
             .RequireRateLimiting("data-write");

        // Story 6.4 — PUT /api/data/{designerId}/{id}. Rate limit overrides group
        // default: "data-write" (60 req/min) vs group "data-read" (300 req/min).
        group.MapPut("/{id:guid}", UpdateRecordHandler)
             .WithSummary("Partially update a record in a provisioned dynamic table.")
             .Produces<DynamicRecord>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("update")
             .RequireRateLimiting("data-write");

        // Story 6.5 — DELETE /api/data/{designerId}/{id}. Rate limit overrides group
        // default: "data-write" (60 req/min) vs group "data-read" (300 req/min).
        group.MapDelete("/{id:guid}", DeleteRecordHandler)
             .WithSummary("Soft-delete a record in a provisioned dynamic table.")
             .Produces<DynamicRecord>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("delete")
             .RequireRateLimiting("data-write");

        // Story 6.6 — PUT /api/data/{designerId}/{id}/restore. Permission: "update"
        // (closest semantic fit; no separate "restore" action flag exists in the
        // current CanCreate/Read/Update/Delete model). The literal "/restore" segment
        // is more specific than the bare "/{id:guid}" PUT, so ASP.NET routing picks
        // this handler when present.
        group.MapPut("/{id:guid}/restore", RestoreRecordHandler)
             .WithSummary("Restore a soft-deleted record and its cascade children.")
             .Produces<DynamicRecord>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("update")
             .RequireRateLimiting("data-write");

        // SingleRecord component — GET /api/data/{designerId}/single?authFilterColumn=col.
        // Returns the ONE record owned by the requesting user (the row whose
        // authFilterColumn equals the JWT user id), or 204 No Content when the user has
        // not created their record yet. The owner column is resolved server-side from the
        // token so a client cannot read another user's row. The literal "/single" segment
        // is registered before the "/{id:guid}" routes above only by URL shape — "single"
        // is not a guid, so routing never confuses the two.
        group.MapGet("/single", GetSingleRecordHandler)
             .WithSummary("Get the current user's single record for a SingleRecord component (scoped by authFilterColumn).")
             // Always 200: an existing record, or a default object (designer fields with
             // empty defaults, no id) when the user has none yet so the form renders populated.
             .Produces<DynamicRecord>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("read");

        // SingleRecord component — POST /api/data/{designerId}/single?authFilterColumn=col.
        // Creates the user's record, stamping authFilterColumn with the JWT user id so the
        // GET/PUT scoping above can find and own it. Shares CreateRecordCoreAsync with the
        // generic POST; the only difference is the owner column comes from the component
        // property rather than the version's configured AuthFilterFieldKey.
        group.MapPost("/single", CreateSingleRecordHandler)
             .WithSummary("Create the current user's single record for a SingleRecord component.")
             .Produces<DynamicRecord>(StatusCodes.Status201Created)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("create")
             .RequireRateLimiting("data-write");

        // SingleRecord component — PUT /api/data/{designerId}/single/{id}?authFilterColumn=col.
        // Updates the user's record. Before writing, UpdateRecordCoreAsync verifies the
        // target row's authFilterColumn equals the JWT user id (404 otherwise — never leak
        // another user's record id) so this cannot be used to edit someone else's row.
        group.MapPut("/single/{id:guid}", UpdateSingleRecordHandler)
             .WithSummary("Update the current user's single record for a SingleRecord component.")
             .Produces<DynamicRecord>(StatusCodes.Status200OK)
             .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
             .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
             .Produces<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)
             .RequirePermission("update")
             .RequireRateLimiting("data-write");

        return group;
    }

    internal static async Task<IResult> ListRecordsHandler(
        string designerId,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IPermissionService permissionService,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct,
        int page = 1,
        int pageSize = 25,
        string? sort = null,
        // Story 7-followup — soft-deleted rows are hidden by default. The UI's
        // 'Show deleted' toggle flips this to true and the handler skips the
        // implicit is_deleted=false filter — but ONLY for platform admins (see
        // ApplyDefaultSoftDeleteFilter). An explicit filter[isDeleted] in the
        // query string wins over the default for admins; non-admins always get
        // live-only regardless of either knob.
        bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(permissionService);

        // AR-4 + Story 5.1 — defence-in-depth identifier check before any DB read.
        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        // Pagination clamping (mirrors AuditEndpoints).
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        // AC-4 — locate the bound version. Null means the menu was never bound or
        // provisioning has not reached Success, i.e. the table is not provisioned.
        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        // Decision 1.4 — registry cache is the column source of truth. Cache miss
        // re-populates from EF; we never fall back to `SELECT *` or pg_attribute.
        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        // AC-2 — allowed sort columns: user fieldKeys + four system PG columns.
        var allowedSortCols = BuildAllowedColumnSet(entry.Columns, DynamicQueryBuilder.SystemSortPgColumns);

        // AC-3 — allowed filter columns: user fieldKeys + five system PG columns.
        var allowedFilterCols = BuildAllowedColumnSet(entry.Columns, DynamicQueryBuilder.SystemFilterPgColumns);

        // AC-2 — parse + validate sort.
        var sortResult = DynamicQueryBuilder.ParseSort(sort, allowedSortCols);
        if (!sortResult.IsSuccess)
            return Problems.ValidationFailed(sortResult.ErrorMessage ?? "Invalid sort parameter.");

        // AC-3 — parse filter[*] from raw query string and whitelist each key.
        if (!TryParseAndValidateFilters(httpContext.Request.Query, allowedFilterCols, entry.Columns, out var filters, out var filterError))
            return Problems.ValidationFailed(filterError!);

        // Dataset binding — when this version is bound to a dataset, the list reads from
        // the dataset's backing VIEW instead of the provisioned table, applying the same
        // pagination / filtering / sorting / auth-filter. Read fresh (like the auth filter)
        // so an admin binding/unbinding from the Component Library takes effect on the next
        // request without a version bump or registry invalidation. No implicit soft-delete
        // filter is applied — a dataset's VIEW defines its own row set.
        var listDatasetId = await GetDatasetIdAsync(db, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);
        if (listDatasetId is { } boundListDataset)
        {
            var datasetAuthFieldKey = await GetAuthFilterFieldKeyAsync(db, safeId!.Value, boundVersion.Value, ct)
                .ConfigureAwait(false);
            return await ListRecordsFromDatasetAsync(
                boundListDataset, sortResult.Sorts, filters, page, pageSize,
                datasetAuthFieldKey, httpContext, datasetRowQueryService, ct).ConfigureAwait(false);
        }

        var isAdmin = await IsPlatformAdminAsync(httpContext, permissionService, ct).ConfigureAwait(false);
        filters = ApplyDefaultSoftDeleteFilter(filters, includeDeleted, isAdmin);

        // Auth filter — when the bound version names an AuthFilterFieldKey, scope the
        // list to rows whose column equals the requesting user's id. No admin bypass:
        // the filter applies to everyone, including platform admins. A server-injected,
        // trusted predicate that overrides any client-supplied value for the same column.
        var authFilterFieldKey = await GetAuthFilterFieldKeyAsync(db, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);
        filters = ApplyAuthFilter(filters, authFilterFieldKey, entry.Columns, httpContext);

        // AC-1, AC-3, AC-6 — assemble + execute SELECT and COUNT on the dynamic
        // CRUD connection with the mandatory 5-second commandTimeout (Decision 1.6).
        var (selectSql, selectParams) = DynamicQueryBuilder.BuildSelectQuery(
            safeId, entry.Columns, sortResult.Sorts, filters, page, pageSize, entry.DerivedColumns);
        var (countSql, countParams) = DynamicQueryBuilder.BuildCountQuery(safeId, entry.Columns, filters);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, countParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var records = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(row => new DynamicRecord(
                    row.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (object?)kvp.Value,
                        StringComparer.Ordinal)))
                .ToList();

            return Results.Ok(new PagedResult<DynamicRecord>(records, total, page, pageSize));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // TreeView — one level of a self-referencing adjacency-list tree. Roots when
    // parentId is omitted, otherwise the live direct children of parentId. Returns
    // pageSize rows + a hasNextPage flag (fetches pageSize+1 internally) and a per-row
    // `_has_children` boolean. `search` substring-matches any node-template field.
    internal static async Task<IResult> ListTreeNodesHandler(
        string designerId,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        DdlEmitter ddlEmitter,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct,
        string? parentId = null,
        int page = 1,
        int pageSize = 25,
        string? search = null,
        string? authFilterColumn = null,
        // Dataset-backed TreeView: when datasetId + keyField + parentField are supplied,
        // the level is read from the dataset VIEW (keyField = node id, parentField = self
        // reference) instead of the provisioned table. Mutations still hit the base table.
        string? datasetId = null,
        string? keyField = null,
        string? parentField = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(datasetRowQueryService);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        if (Guid.TryParse(datasetId, out var treeDatasetId)
            && !string.IsNullOrWhiteSpace(keyField)
            && !string.IsNullOrWhiteSpace(parentField))
        {
            return await ListTreeNodesFromDatasetAsync(
                treeDatasetId, keyField!.Trim(), parentField!.Trim(), parentId, page, pageSize,
                search, authFilterColumn, httpContext, datasetRowQueryService, ct).ConfigureAwait(false);
        }

        Guid? parentGuid = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, CultureInfo.InvariantCulture, out var pg))
                return Problems.ValidationFailed("Query parameter 'parentId' is not a valid UUID.");
            parentGuid = pg;
        }

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);
        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = await GetOrPopulateEntryAsync(db, schemaRegistry, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);

        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // The self-FK is created when a designer CONTAINING a TreeView that references
            // this table is provisioned — which may not have happened yet (publish order,
            // or hitting the endpoint before the host is bound). Self-heal by adding the
            // self-FK on demand (idempotent), then re-check. Without it the
            // parent_<table>_id predicate below would be a 42703.
            if (!await TableHasColumnAsync(conn, safeId!.Value, fkColumnName, ct).ConfigureAwait(false))
            {
                await ddlEmitter.EnsureSelfReferenceColumnAsync(safeId!.Value, ct).ConfigureAwait(false);
                if (!await TableHasColumnAsync(conn, safeId!.Value, fkColumnName, ct).ConfigureAwait(false))
                    return Problems.ValidationFailed(
                        "This designer is not configured as a TreeView tree (no self-reference column).");
            }

            // Optional per-component auth filter: scope the level to the user's own nodes.
            var ownerResult = ResolveTreeOwnerFilter(authFilterColumn, entry, httpContext, out var ownerColumn, out var ownerId);
            if (ownerResult is not null) return ownerResult;

            var offset = (long)(page - 1) * pageSize;
            var (sql, parameters) = DynamicQueryBuilder.BuildTreeLevelQuery(
                safeId, entry.Columns, fkColumnName, parentGuid, search, pageSize + 1, offset, ownerColumn, ownerId);

            var rawRows = (await conn.QueryAsync(new CommandDefinition(
                sql, parameters, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false))
                .Cast<IDictionary<string, object>>()
                .ToList();

            var hasNextPage = rawRows.Count > pageSize;
            if (hasNextPage) rawRows.RemoveAt(rawRows.Count - 1);

            var records = rawRows
                .Select(row => new DynamicRecord(row.ToDictionary(
                    kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal)))
                .ToList();

            return Results.Ok(new TreeLevelResult(records, hasNextPage, page, pageSize));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // TreeView "All select" — recursive descendant ids of a node, for cascade selection.
    internal static async Task<IResult> ListTreeDescendantsHandler(
        string designerId,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        DdlEmitter ddlEmitter,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct,
        string? parentId = null,
        string? authFilterColumn = null,
        string? datasetId = null,
        string? keyField = null,
        string? parentField = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(datasetRowQueryService);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        // Dataset-backed tree: descendant key values come from the dataset VIEW (recursive).
        if (Guid.TryParse(datasetId, out var treeDatasetId)
            && !string.IsNullOrWhiteSpace(keyField)
            && !string.IsNullOrWhiteSpace(parentField))
        {
            if (string.IsNullOrWhiteSpace(parentId))
                return Problems.ValidationFailed("Query parameter 'parentId' is required.");
            var authUserId = Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var duid)
                ? duid : (Guid?)null;
            var dResult = await datasetRowQueryService.GetTreeDescendantsAsync(
                treeDatasetId, keyField!.Trim(), parentField!.Trim(), parentId.Trim(),
                NormalizeDatasetAuthColumn(authFilterColumn), ct, authUserId).ConfigureAwait(false);
            return dResult.Outcome switch
            {
                DatasetRowsOutcome.Success => Results.Ok(new TreeDescendantsResult(dResult.Ids ?? Array.Empty<string>())),
                DatasetRowsOutcome.NotFound => Results.Ok(new TreeDescendantsResult(Array.Empty<string>())),
                _ => Problems.ValidationFailed(dResult.ErrorDetail ?? "Invalid dataset tree query."),
            };
        }

        if (string.IsNullOrWhiteSpace(parentId)
            || !Guid.TryParse(parentId, CultureInfo.InvariantCulture, out var parentGuid))
            return Problems.ValidationFailed("Query parameter 'parentId' is required and must be a UUID.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);
        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = await GetOrPopulateEntryAsync(db, schemaRegistry, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);
        var ownerResult = ResolveTreeOwnerFilter(authFilterColumn, entry, httpContext, out var ownerColumn, out var ownerId);
        if (ownerResult is not null) return ownerResult;

        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await TableHasColumnAsync(conn, safeId!.Value, fkColumnName, ct).ConfigureAwait(false))
            {
                await ddlEmitter.EnsureSelfReferenceColumnAsync(safeId!.Value, ct).ConfigureAwait(false);
                if (!await TableHasColumnAsync(conn, safeId!.Value, fkColumnName, ct).ConfigureAwait(false))
                    // No self-FK and none could be created → the node has no descendants.
                    return Results.Ok(new TreeDescendantsResult(Array.Empty<string>()));
            }

            var (sql, parameters) = DynamicQueryBuilder.BuildTreeDescendantIdsQuery(
                safeId, fkColumnName, parentGuid, ownerColumn, ownerId);
            var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(
                sql, parameters, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false))
                .Select(g => g.ToString())
                .ToList();

            return Results.Ok(new TreeDescendantsResult(ids));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // TreeView — create a node, writing the self-FK parent_<table>_id from the body's
    // `parentId` (null/absent → a root node). Mutate mode writes directly to the table.
    internal static async Task<IResult> CreateTreeNodeHandler(
        string designerId,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        DdlEmitter ddlEmitter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(payloadValidator);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        // parentId is a system field (the tree edge), not a user-authored value — read it
        // straight off the body; the payload validator ignores it (not a schema column).
        Guid? parentGuid = null;
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("parentId", out var parentProp))
        {
            if (parentProp.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParse(parentProp.GetString(), CultureInfo.InvariantCulture, out var pg))
                    return Problems.ValidationFailed("Field 'parentId' is not a valid UUID.");
                parentGuid = pg;
            }
            else if (parentProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return Problems.ValidationFailed("Field 'parentId' must be a UUID string or null.");
            }
        }

        // authFilterColumn is a system field (the per-component owner column), not a
        // user-authored value — read it straight off the body like parentId.
        string? authFilterColumn = null;
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("authFilterColumn", out var afcProp)
            && afcProp.ValueKind == JsonValueKind.String)
        {
            authFilterColumn = afcProp.GetString();
        }

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);
        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = await GetOrPopulateEntryAsync(db, schemaRegistry, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);

        var validationResult = payloadValidator.Validate(body, entry.Columns);
        if (!validationResult.IsValid)
        {
            return Results.ValidationProblem(
                validationResult.FieldErrors,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
        }

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
            && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

        if (!DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
                validationResult.CoercedValues, out var coercedValues, out var invalidFkColumn))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [invalidFkColumn!] = [$"Value for field '{invalidFkColumn}' is not a valid UUID."],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
        }

        // Optional per-component auth filter: stamp the owner column with the creating
        // user so reads/edits scope to them. Validates the column + a known actor.
        var ownerResult = ResolveTreeOwnerFilter(authFilterColumn, entry, httpContext, out var ownerColumn, out _);
        if (ownerResult is not null) return ownerResult;
        if (ownerColumn is not null)
            coercedValues = StampAuthFilterOwner(coercedValues, ownerColumn, entry.Columns, actorId);

        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);
        // Exclude the system-owned self-FK from the user-column set so a colliding
        // fieldKey can't double-bind the column (the explicit FK value always wins).
        var insertCols = entry.Columns
            .Where(c => !string.Equals(c.ColumnName, fkColumnName, StringComparison.Ordinal))
            .ToList();

        var insertedAt = DateTimeOffset.UtcNow;
        Guid newRecordId;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Self-heal the self-FK on demand if a TreeView host hasn't provisioned it yet
            // (idempotent), then re-check. See the matching note in ListTreeNodesHandler.
            if (!await TableHasColumnAsync(conn, safeId!.Value, fkColumnName, ct).ConfigureAwait(false))
            {
                await ddlEmitter.EnsureSelfReferenceColumnAsync(safeId!.Value, ct).ConfigureAwait(false);
                if (!await TableHasColumnAsync(conn, safeId!.Value, fkColumnName, ct).ConfigureAwait(false))
                    return Problems.ValidationFailed(
                        "This designer is not configured as a TreeView tree (no self-reference column).");
            }

            string sql;
            DynamicParameters parameters;
            if (parentGuid.HasValue)
            {
                // Parent must exist and be live, else the FK insert would 500 (or orphan).
                var parentIsDeleted = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
                    $"SELECT \"is_deleted\" FROM \"{safeId!.Value}\" WHERE \"id\" = @p_id",
                    new { p_id = parentGuid.Value }, commandTimeout: 5, cancellationToken: ct))
                    .ConfigureAwait(false);
                if (parentIsDeleted is null)
                    return Problems.ValidationFailed("Parent node not found.");
                if (parentIsDeleted is true)
                    return Problems.ValidationFailed("Parent node has been deleted.");

                (sql, parameters, newRecordId, insertedAt) = DynamicQueryBuilder.BuildChildInsertQuery(
                    safeId, insertCols, coercedValues, fkColumnName, parentGuid.Value, actorId);
            }
            else
            {
                (sql, parameters, newRecordId, insertedAt) = DynamicQueryBuilder.BuildInsertQuery(
                    safeId, insertCols, coercedValues, actorId);
            }

            await conn.ExecuteAsync(new CommandDefinition(
                sql, parameters, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
        {
            DesignerId    = safeId!.Value,
            RecordId      = newRecordId,
            Operation     = "CREATE",
            ActorId       = actorId,
            Timestamp     = insertedAt,
            NewValues     = JsonSerializer.Serialize(coercedValues),
            CorrelationId = httpContext.GetCorrelationId(),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var responseValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"]               = newRecordId,
            ["created_at"]       = insertedAt,
            ["created_by"]       = actorId,
            ["updated_at"]       = insertedAt,
            ["updated_by"]       = actorId,
            ["is_deleted"]       = false,
            ["cascade_event_id"] = null,
        };
        foreach (var col in entry.Columns)
            responseValues[col.ColumnName] = coercedValues.TryGetValue(col.ColumnName, out var v) ? v : null;

        return Results.Created(
            $"/api/data/{Uri.EscapeDataString(safeId!.Value)}/{newRecordId}",
            new DynamicRecord(responseValues));
    }

    // Designer-backed Dropdown options. Returns ONLY {value,label} pairs from the
    // target designer's table, paginated + searchable + cascading-filterable.
    internal static async Task<IResult> ListOptionsHandler(
        string designerId,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        CancellationToken ct,
        int version = 0,
        string? labelField = null,
        string? valueField = null,
        string? search = null,
        int page = 1,
        int pageSize = 50)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");
        if (version < 1)
            return Problems.ValidationFailed("A valid 'version' query parameter is required.");
        if (string.IsNullOrWhiteSpace(labelField) || string.IsNullOrWhiteSpace(valueField))
            return Problems.ValidationFailed("'labelField' and 'valueField' query parameters are required.");

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 50;

        // The table is named after the designerId and only exists once the designer
        // has been provisioned (any successful menu binding).
        var provisioned = await db.Menus
            .AsNoTracking()
            .AnyAsync(m => m.DesignerId == safeId!.Value && m.ProvisioningStatus == "Success", ct)
            .ConfigureAwait(false);
        if (!provisioned)
            return Problems.TableNotProvisioned();

        // Columns come from the requested version's registry entry (cache miss
        // re-populates from EF — same pattern as ListRecordsHandler).
        var entry = schemaRegistry.TryGet(safeId!.Value, version);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == version)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (rootElementJson is null)
                return Problems.ValidationFailed("Unknown designer version.");
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(safeId.Value, version, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        // Label/value must each be a user column or the system "id" (PK lookups).
        var selectable = new HashSet<string>(StringComparer.Ordinal) { "id" };
        foreach (var c in entry.Columns) selectable.Add(c.ColumnName);
        if (!selectable.Contains(labelField))
            return Problems.ValidationFailed($"Unknown label field '{labelField}'.");
        if (!selectable.Contains(valueField))
            return Problems.ValidationFailed($"Unknown value field '{valueField}'.");

        // Cascading filters: filter[col]=value, validated against the target columns.
        var allowedFilterCols = BuildAllowedColumnSet(entry.Columns, DynamicQueryBuilder.SystemFilterPgColumns);
        if (!TryParseAndValidateFilters(httpContext.Request.Query, allowedFilterCols, entry.Columns, out var parsed, out var filterError))
            return Problems.ValidationFailed(filterError!);

        // Options are always live-only — soft-deleted lookup rows must never appear.
        var filters = new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        if (!filters.ContainsKey("is_deleted")) filters["is_deleted"] = "false";

        var (selectSql, selectParams) = DynamicQueryBuilder.BuildOptionsQuery(
            safeId, valueField, labelField, entry.Columns, filters, search, page, pageSize);
        var (countSql, countParams) = DynamicQueryBuilder.BuildOptionsCountQuery(
            safeId, valueField, labelField, entry.Columns, filters, search);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, countParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var options = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(row => new DropdownOption(
                    row.TryGetValue("value", out var v) && v is not null
                        ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty,
                    row.TryGetValue("label", out var l) && l is not null
                        ? Convert.ToString(l, CultureInfo.InvariantCulture)
                        : null))
                .Where(o => o.Value.Length > 0)
                .ToList();

            return Results.Ok(new PagedResult<DropdownOption>(options, total, page, pageSize));
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Story 7-followup — GET /api/data/{designerId}/export?format=csv|xlsx|pdf.
    // Reuses ListRecordsHandler's resolution pipeline (SafeIdentifier → bound
    // version → schema registry → filter/sort parse) so the export is always
    // consistent with what the user sees on screen. Hard cap of MaxExportRows
    // keeps a casual "export everything" from OOM'ing the API process.
    private const int MaxExportRows = 100_000;

    private static readonly FrozenDictionary<string, Func<Export.IRecordExportWriter>> ExportWriterFactories =
        new Dictionary<string, Func<Export.IRecordExportWriter>>(StringComparer.OrdinalIgnoreCase)
        {
            ["csv"] = () => new Export.CsvExportWriter(),
            ["xlsx"] = () => new Export.XlsxExportWriter(),
            ["pdf"] = () => new Export.PdfExportWriter(),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static async Task<IResult> ExportRecordsHandler(
        string designerId,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IPermissionService permissionService,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct,
        string? format = null,
        string? sort = null,
        bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(permissionService);

        if (string.IsNullOrWhiteSpace(format)
            || !ExportWriterFactories.TryGetValue(format, out var writerFactory))
        {
            return Problems.ValidationFailed(
                "Query parameter 'format' must be one of: csv, xlsx, pdf.");
        }

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        // displayName drives the PDF/XLSX title and the download filename. Fall
        // back to designerId so the export still works on a schema without one.
        var displayName = await db.ComponentSchemas
            .AsNoTracking()
            .Where(s => s.DesignerId == safeId.Value)
            .Select(s => s.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var title = string.IsNullOrWhiteSpace(displayName) ? safeId.Value : displayName;

        var allowedSortCols = BuildAllowedColumnSet(entry.Columns, DynamicQueryBuilder.SystemSortPgColumns);
        var allowedFilterCols = BuildAllowedColumnSet(entry.Columns, DynamicQueryBuilder.SystemFilterPgColumns);

        var sortResult = DynamicQueryBuilder.ParseSort(sort, allowedSortCols);
        if (!sortResult.IsSuccess)
            return Problems.ValidationFailed(sortResult.ErrorMessage ?? "Invalid sort parameter.");

        if (!TryParseAndValidateFilters(httpContext.Request.Query, allowedFilterCols, entry.Columns, out var filters, out var filterError))
            return Problems.ValidationFailed(filterError!);

        // Dataset binding — export from the bound dataset's VIEW (same source as the list)
        // so the file matches what the user sees. Honors the same filter/sort/auth-filter.
        var exportDatasetId = await GetDatasetIdAsync(db, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);
        if (exportDatasetId is { } boundExportDataset)
        {
            var datasetAuthFieldKey = await GetAuthFilterFieldKeyAsync(db, safeId!.Value, boundVersion.Value, ct)
                .ConfigureAwait(false);
            return await ExportRecordsFromDatasetAsync(
                boundExportDataset, sortResult.Sorts, filters, datasetAuthFieldKey,
                title, writerFactory, httpContext, datasetRowQueryService, ct).ConfigureAwait(false);
        }

        var isAdmin = await IsPlatformAdminAsync(httpContext, permissionService, ct).ConfigureAwait(false);
        filters = ApplyDefaultSoftDeleteFilter(filters, includeDeleted, isAdmin);

        // Auth filter — scope the export to the requesting user's own rows when the
        // bound version names an AuthFilterFieldKey, identical to ListRecordsHandler.
        // Without this an export would leak every row regardless of the list scoping.
        var authFilterFieldKey = await GetAuthFilterFieldKeyAsync(db, safeId!.Value, boundVersion.Value, ct)
            .ConfigureAwait(false);
        filters = ApplyAuthFilter(filters, authFilterFieldKey, entry.Columns, httpContext);

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Count first so we can reject oversize exports BEFORE issuing the
            // unbounded SELECT — the cap is a memory bound, not a time bound,
            // so failing fast is the right behaviour.
            var (countSql, countParams) = DynamicQueryBuilder.BuildCountQuery(
                safeId, entry.Columns, filters);
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, countParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
            if (total > MaxExportRows)
            {
                return Problems.ValidationFailed(
                    $"Export would include {total} rows; the per-request cap is {MaxExportRows}. " +
                    "Narrow the filter and retry.");
            }

            var (exportSql, exportParams) = DynamicQueryBuilder.BuildExportQuery(
                safeId, entry.Columns, sortResult.Sorts, filters, entry.DerivedColumns);
            // Larger command timeout than the paged read — exports legitimately
            // need to scan more rows, but not unboundedly (~30s ceiling).
            var rawRows = await conn.QueryAsync(new CommandDefinition(
                exportSql, exportParams, commandTimeout: 30, cancellationToken: ct)).ConfigureAwait(false);

            var materialized = rawRows
                .Cast<IDictionary<string, object?>>()
                .ToList();

            // Column display order: id, then user fieldKeys (matches the on-screen
            // list page), then created_at, updated_at. System bookkeeping columns
            // (created_by/updated_by/cascade_event_id/is_deleted) are intentionally
            // omitted from exports — users want their data, not our row metadata.
            var columnNames = new List<string>(entry.Columns.Count + entry.DerivedColumns.Count + 3) { "id" };
            foreach (var c in entry.Columns) columnNames.Add(c.ColumnName);
            // Derived columns (Designer-dropdown labels, Repeater counts) come right
            // after the real user columns, mirroring the on-screen list order.
            foreach (var d in entry.DerivedColumns) columnNames.Add(d.ResultAlias);
            columnNames.Add("created_at");
            columnNames.Add("updated_at");

            var writer = writerFactory();
            var sanitizedFileBase = SanitizeFileNameSegment(title);
            var fileName = $"{sanitizedFileBase}.{writer.FileExtension}";

            // Buffer the rendered file in memory first so the response writes
            // atomically — Results.Stream would hand the writer a response body
            // mid-stream and any mid-write exception would be unrecoverable
            // (we'd have already sent 200 + partial bytes).
            using var ms = new MemoryStream();
            await writer.WriteAsync(ms, title, columnNames, materialized, ct).ConfigureAwait(false);
            return Results.File(
                fileContents: ms.ToArray(),
                contentType: writer.ContentType,
                fileDownloadName: fileName);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Filenames can't contain / \ : * ? " < > | on Windows, and `/` is a path
    // separator on POSIX. Strip them so the Content-Disposition header is safe
    // regardless of the user's OS. Whitespace collapses to '_' for readability.
    private static string SanitizeFileNameSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "records";
        var buf = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') continue;
            buf.Append(char.IsWhiteSpace(ch) ? '_' : ch);
        }
        var s = buf.ToString().Trim('_', '.');
        return s.Length == 0 ? "records" : s;
    }

    // Story 6.2 — GET /api/data/{designerId}/{id}?include=children. Mirrors the
    // ListRecordsHandler shape (SafeIdentifier → EF binding lookup → registry → Dapper)
    // but skips sort/filter parsing and adds optional child-row fetch via Repeater FK.
    internal static async Task<IResult> GetRecordHandler(
        string designerId,
        Guid id,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        CancellationToken ct,
        string? include = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        // AR-4 + Story 5.1 — defence-in-depth identifier check before any DB read.
        // {id:guid} constraint already validated id; designerId has no constraint.
        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        var includeChildren = "children".Equals(include, StringComparison.OrdinalIgnoreCase);

        // Collect child schemas via EF BEFORE opening the Dapper connection (Dev Notes §6).
        // Child versions are not tracked in ChildRepeaterDesignerIds, so the registry
        // can't be used here — go straight to the latest Published version per child.
        var childSchemaMap = new Dictionary<string, (SafeIdentifier SafeId, IReadOnlyList<ColumnDefinition> Columns)>(
            StringComparer.Ordinal);

        if (includeChildren && entry.ChildRepeaterDesignerIds.Count > 0)
        {
            foreach (var childId in entry.ChildRepeaterDesignerIds)
            {
                if (!SafeIdentifier.TryCreate(childId, out var safeChildId, out _)) continue;
                var childRootJson = await db.ComponentSchemaVersions
                    .AsNoTracking()
                    .Where(v => v.DesignerId == safeChildId!.Value && v.Status == "Published")
                    .OrderByDescending(v => v.Version)
                    .Select(v => v.RootElement)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (childRootJson is null) continue;
                var (childColumns, _) = RootElementParser.ParseFull(childRootJson);
                childSchemaMap[safeChildId!.Value] = (safeChildId!, childColumns);
            }
        }

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(safeId, entry.Columns, id);
            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var rows = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(row => new DynamicRecord(
                    row.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (object?)kvp.Value,
                        StringComparer.Ordinal)))
                .ToList();

            if (rows.Count == 0)
                return Problems.RecordNotFound();

            var parentRecord = rows[0];

            if (includeChildren && childSchemaMap.Count > 0)
            {
                var children = new Dictionary<string, IReadOnlyList<DynamicRecord>>(StringComparer.Ordinal);
                var fkColName = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);

                foreach (var (childDesignerId, (safeChildId, childColumns)) in childSchemaMap)
                {
                    var (childSql, childParams) = DynamicQueryBuilder.BuildGetChildrenQuery(
                        safeChildId, childColumns, fkColName, id);
                    var childRawRows = await conn.QueryAsync(new CommandDefinition(
                        childSql, childParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
                    var childRecords = childRawRows
                        .Cast<IDictionary<string, object>>()
                        .Select(row => new DynamicRecord(
                            row.ToDictionary(
                                kvp => kvp.Key,
                                kvp => (object?)kvp.Value,
                                StringComparer.Ordinal)))
                        .ToList();
                    children[childDesignerId] = childRecords;
                }

                // DynamicRecord.Values is read-only — copy into a mutable dict to merge children.
                var merged = parentRecord.Values.ToDictionary(
                    kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
                merged["children"] = children;
                return Results.Ok(new DynamicRecord(merged));
            }

            return Results.Ok(parentRecord);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // SingleRecord component — GET /api/data/{designerId}/single?authFilterColumn=col[&include=children].
    // Returns the single record owned by the requesting user (the row whose authFilterColumn
    // equals the JWT user id), or 204 No Content when the user has not created it yet. The
    // owner column is resolved server-side from the token, so the client can never read
    // another user's row. Mirrors GetRecordHandler's resolution + optional child-row fetch,
    // but locates the row by the owner column (created_at ASC, the canonical first row) rather
    // than by id.
    internal static async Task<IResult> GetSingleRecordHandler(
        string designerId,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        CancellationToken ct,
        string? authFilterColumn = null,
        string? include = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");
        if (string.IsNullOrWhiteSpace(authFilterColumn))
            return Problems.ValidationFailed("Query parameter 'authFilterColumn' is required.");
        authFilterColumn = authFilterColumn.Trim();

        var userId = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userId, out _))
            return Problems.ValidationFailed("Could not determine the current user.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        if (!IsKnownUserColumn(entry.Columns, authFilterColumn))
            return Problems.ValidationFailed(
                $"Auth filter column '{authFilterColumn}' is not a field on this designer.");

        var includeChildren = "children".Equals(include, StringComparison.OrdinalIgnoreCase);

        // Collect child schemas via EF BEFORE opening the Dapper connection (mirrors
        // GetRecordHandler) so a single-record form with Repeaters loads its existing rows.
        var childSchemaMap = new Dictionary<string, (SafeIdentifier SafeId, IReadOnlyList<ColumnDefinition> Columns)>(
            StringComparer.Ordinal);
        if (includeChildren && entry.ChildRepeaterDesignerIds.Count > 0)
        {
            foreach (var childId in entry.ChildRepeaterDesignerIds)
            {
                if (!SafeIdentifier.TryCreate(childId, out var safeChildId, out _)) continue;
                var childRootJson = await db.ComponentSchemaVersions
                    .AsNoTracking()
                    .Where(v => v.DesignerId == safeChildId!.Value && v.Status == "Published")
                    .OrderByDescending(v => v.Version)
                    .Select(v => v.RootElement)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                if (childRootJson is null) continue;
                var (childColumns, _) = RootElementParser.ParseFull(childRootJson);
                childSchemaMap[safeChildId!.Value] = (safeChildId!, childColumns);
            }
        }

        // Scope to the owner's single live row. The auth predicate is server-trusted (the
        // user id comes from the token, never the client) and overrides any client value.
        var filters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [authFilterColumn] = userId!,
            ["is_deleted"] = "false",
        };
        var sorts = new[] { new DynamicQueryBuilder.SortParam("created_at", "ASC") };

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var (selectSql, selectParams) = DynamicQueryBuilder.BuildSelectQuery(
                safeId, entry.Columns, sorts, filters, page: 1, pageSize: 1, entry.DerivedColumns);
            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var rows = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(row => new DynamicRecord(
                    row.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (object?)kvp.Value,
                        StringComparer.Ordinal)))
                .ToList();

            // No row yet → return a default object carrying every designer field (and,
            // when requested, empty child collections) instead of 204. The object has NO
            // `id`, so the SingleRecord form stays in create mode (the client keys
            // update-vs-create off a string id) but renders pre-populated with the
            // designer's fields rather than a blank form.
            if (rows.Count == 0)
                return Results.Ok(BuildDefaultSingleRecord(entry, includeChildren, childSchemaMap));

            var parentRecord = rows[0];

            if (includeChildren && childSchemaMap.Count > 0
                && parentRecord.Values.TryGetValue("id", out var idObj) && idObj is Guid parentId)
            {
                var children = new Dictionary<string, IReadOnlyList<DynamicRecord>>(StringComparer.Ordinal);
                var fkColName = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);

                foreach (var (childDesignerId, (safeChildId, childColumns)) in childSchemaMap)
                {
                    var (childSql, childParams) = DynamicQueryBuilder.BuildGetChildrenQuery(
                        safeChildId, childColumns, fkColName, parentId);
                    var childRawRows = await conn.QueryAsync(new CommandDefinition(
                        childSql, childParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
                    var childRecords = childRawRows
                        .Cast<IDictionary<string, object>>()
                        .Select(row => new DynamicRecord(
                            row.ToDictionary(
                                kvp => kvp.Key,
                                kvp => (object?)kvp.Value,
                                StringComparer.Ordinal)))
                        .ToList();
                    children[childDesignerId] = childRecords;
                }

                var merged = parentRecord.Values.ToDictionary(
                    kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
                merged["children"] = children;
                return Results.Ok(new DynamicRecord(merged));
            }

            return Results.Ok(parentRecord);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Builds the "no record yet" default object for the SingleRecord GET. Every
    // user-authored field from the designer schema is present with a type-appropriate
    // empty default so the form renders fully populated; system columns (id/created_at/…)
    // are intentionally omitted so the client treats this as create mode. When children
    // are requested, each declared Repeater child is included as an empty array, matching
    // the shape of a real record's `children` map.
    private static DynamicRecord BuildDefaultSingleRecord(
        SchemaRegistryEntry entry,
        bool includeChildren,
        IReadOnlyDictionary<string, (SafeIdentifier SafeId, IReadOnlyList<ColumnDefinition> Columns)> childSchemaMap)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var col in entry.Columns)
        {
            // BOOLEAN → false (a checkbox is unchecked, not indeterminate); every other
            // type → null so a numeric/text/date field renders empty rather than with a
            // fabricated value.
            values[col.ColumnName] =
                string.Equals(col.PgType, "BOOLEAN", StringComparison.Ordinal) ? false : (object?)null;
        }

        if (includeChildren && childSchemaMap.Count > 0)
        {
            var children = new Dictionary<string, IReadOnlyList<DynamicRecord>>(StringComparer.Ordinal);
            foreach (var childDesignerId in childSchemaMap.Keys)
                children[childDesignerId] = [];
            values["children"] = children;
        }

        return new DynamicRecord(values);
    }

    // Story 6.3 — POST /api/data/{designerId}. Thin route delegate: the owner column
    // comes from the version's configured AuthFilterFieldKey (no component override).
    internal static Task<IResult> CreateRecordHandler(
        string designerId,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        CancellationToken ct) =>
        CreateRecordCoreAsync(
            designerId, body, httpContext, db, schemaRegistry, connectionFactory,
            payloadValidator, authFilterColumnOverride: null, ct);

    // SingleRecord component — POST /api/data/{designerId}/single?authFilterColumn=col.
    // Same create path as the generic POST, but the owning-user column is named by the
    // component (authFilterColumn) and stamped server-side from the JWT, independent of
    // the version's configured AuthFilterFieldKey. A blank column is rejected up front —
    // without a valid owner column the row would be created unowned and readable by anyone.
    internal static Task<IResult> CreateSingleRecordHandler(
        string designerId,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        CancellationToken ct,
        string? authFilterColumn = null)
    {
        if (string.IsNullOrWhiteSpace(authFilterColumn))
            return Task.FromResult(Problems.ValidationFailed("Query parameter 'authFilterColumn' is required."));
        return CreateRecordCoreAsync(
            designerId, body, httpContext, db, schemaRegistry, connectionFactory,
            payloadValidator, authFilterColumnOverride: authFilterColumn.Trim(), ct);
    }

    // Mirrors the GetRecordHandler/ListRecordsHandler pattern (SafeIdentifier → EF
    // binding → registry) then runs Layer 2 validation, executes a parameterised INSERT
    // via Dapper, and appends a mutation audit log row via EF (separate transactions per
    // Decision 1.6). JsonElement body is bound from the request body by ASP.NET Core's
    // minimal API model binder; malformed JSON returns 400 before this handler runs.
    //
    // authFilterColumnOverride: when non-null (SingleRecord component path), it names the
    // owning-user column to stamp instead of the version's AuthFilterFieldKey, and MUST be
    // a real user column on this version (else 422). When null (generic POST), the version
    // config decides, preserving the original behaviour exactly.
    private static async Task<IResult> CreateRecordCoreAsync(
        string designerId,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        string? authFilterColumnOverride,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(payloadValidator);

        // AR-4 + Story 5.1 — defence-in-depth identifier check before any DB read.
        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        // Decision 1.4 — registry cache is the column source of truth. Cache miss
        // re-populates from EF; we never fall back to `SELECT *` or pg_attribute.
        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        // AC-3 — Layer 2 payload validation. System columns are silently dropped
        // because they aren't in entry.Columns (AR-20 / Decision 3.3). Override
        // the default status (Results.ValidationProblem defaults to 400) to the
        // 422 required by Decision 3.1.
        var validationResult = payloadValidator.Validate(body, entry.Columns);
        if (!validationResult.IsValid)
        {
            return Results.ValidationProblem(
                validationResult.FieldErrors,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
        }

        // actorId is null for service accounts / unauthenticated traffic that
        // bypasses RequireAuth() (defensive — that path should never reach here).
        var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
            && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

        // Story 6.7 — children extraction + pre-flight (before any DB connection).
        // Unknown child designerIds → AC-10 (VALIDATION_FAILED). Per-child schemas
        // are loaded from EF (latest Published) — AC-11 returns TABLE_NOT_PROVISIONED
        // when no Published version exists.
        var childrenRaw = RepeaterWriteCoordinator.ParseChildrenElement(body);
        var childSchemas = new Dictionary<string, SchemaRegistryEntry>(StringComparer.Ordinal);
        var filteredChildren = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var childLoadResult = await TryLoadChildSchemasAsync(
            childrenRaw, entry, db, schemaRegistry, filteredChildren, childSchemas, ct)
            .ConfigureAwait(false);
        if (childLoadResult is not null) return childLoadResult;

        if (!RepeaterWriteCoordinator.TryParseAndValidateChildren(
                filteredChildren, childSchemas, payloadValidator,
                out var parsedChildren, out var childValidationError))
        {
            return childValidationError!;
        }

        bool hasChildren = parsedChildren.Count > 0
            && parsedChildren.Values.Any(list => list.Count > 0);

        // A field whose fieldKey matches the parent_<parentDesignerId>_id FK column
        // (e.g. a designer-backed dropdown that picks the parent record) targets a UUID
        // column, but the validator coerced it as TEXT — binding the string into the
        // INSERT yields PG 42804. Re-coerce it to Guid so it persists as a real UUID.
        if (!DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
                validationResult.CoercedValues, out var coercedValues, out var invalidFkColumn))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [invalidFkColumn!] = [$"Value for field '{invalidFkColumn}' is not a valid UUID."],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
        }

        // Auth filter — stamp the owning-user column with the creating user's id so the
        // row is owned by its creator (the field is hidden in the form, so the client never
        // supplies it). This is what makes the read-side scoping (ApplyAuthFilter /
        // GetSingleRecordHandler) match. The column source differs by caller:
        //   * generic POST  → the version's configured AuthFilterFieldKey (may be null).
        //   * SingleRecord  → the component's authFilterColumn, which MUST be a real user
        //                     column AND have a known actor — otherwise the row would be
        //                     created unowned and readable by any user.
        string? authFilterFieldKey;
        if (authFilterColumnOverride is not null)
        {
            if (!IsKnownUserColumn(entry.Columns, authFilterColumnOverride))
                return Problems.ValidationFailed(
                    $"Auth filter column '{authFilterColumnOverride}' is not a field on this designer.");
            if (actorId is null)
                return Problems.ValidationFailed("Could not determine the current user for the single-record auth filter.");
            authFilterFieldKey = authFilterColumnOverride;
        }
        else
        {
            authFilterFieldKey = await GetAuthFilterFieldKeyAsync(db, safeId!.Value, boundVersion.Value, ct)
                .ConfigureAwait(false);
        }
        coercedValues = StampAuthFilterOwner(coercedValues, authFilterFieldKey, entry.Columns, actorId);

        var (sql, parameters, newRecordId, insertedAt) = DynamicQueryBuilder.BuildInsertQuery(
            safeId, entry.Columns, coercedValues, actorId);

        IReadOnlyList<RepeaterWriteCoordinator.WriteAuditEntry> childAuditEntries =
            Array.Empty<RepeaterWriteCoordinator.WriteAuditEntry>();

        // AC-8 / AC-15 — 5-second commandTimeout on every Dapper call (Decision 1.6).
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            if (hasChildren)
            {
                // AC-1 — wrap parent INSERT + child INSERTs in one transaction.
                var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        sql, parameters, transaction: tx,
                        commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                    childAuditEntries = await RepeaterWriteCoordinator.InsertChildrenAsync(
                        safeId!.Value, newRecordId, parsedChildren, childSchemas,
                        actorId, conn, tx, ct).ConfigureAwait(false);

                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                // AC-2 — no children → existing single-INSERT path, no transaction overhead.
                await conn.ExecuteAsync(new CommandDefinition(
                    sql, parameters, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
            }
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        // AC-4 / AC-13 — append the mutation audit row (parent + each child).
        // The audit INSERTs run via EF in a separate transaction from the Dapper
        // writes (Decision 1.6).
        var correlationId = httpContext.GetCorrelationId();
        var newValuesJson = JsonSerializer.Serialize(coercedValues);
        db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
        {
            DesignerId    = safeId!.Value,
            RecordId      = newRecordId,
            Operation     = "CREATE",
            ActorId       = actorId,
            Timestamp     = insertedAt,
            NewValues     = newValuesJson,
            CorrelationId = correlationId,
        });
        foreach (var childAudit in childAuditEntries)
        {
            db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
            {
                DesignerId     = childAudit.DesignerId,
                RecordId       = childAudit.RecordId,
                Operation      = childAudit.Operation,
                ActorId        = actorId,
                Timestamp      = childAudit.Timestamp,
                NewValues      = childAudit.NewValuesJson,
                PreviousValues = childAudit.PreviousValuesJson,
                CorrelationId  = correlationId,
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // AC-1 — build the response from the inserted values. No re-SELECT: all
        // PG types here are deterministic (no triggers / server transforms), so
        // what we sent is what is stored.
        var responseValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"]               = newRecordId,
            ["created_at"]       = insertedAt,
            ["created_by"]       = actorId,
            ["updated_at"]       = insertedAt,
            ["updated_by"]       = actorId,
            ["is_deleted"]       = false,
            ["cascade_event_id"] = null,
        };
        foreach (var col in entry.Columns)
        {
            responseValues[col.ColumnName] = coercedValues.TryGetValue(col.ColumnName, out var v)
                ? v : null;
        }

        return Results.Created(
            $"/api/data/{Uri.EscapeDataString(safeId!.Value)}/{newRecordId}",
            new DynamicRecord(responseValues));
    }

    // Story 6.4 — PUT /api/data/{designerId}/{id}. Thin route delegate. Optional
    // `authFilterColumn` query param (TreeView per-component auth filter) scopes the edit:
    // the row must be owned by the requesting user, and the owner column is re-stamped so
    // ownership can't be reassigned. Absent → generic behaviour (no ownership pre-check).
    internal static Task<IResult> UpdateRecordHandler(
        string designerId,
        Guid id,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        CancellationToken ct,
        string? authFilterColumn = null) =>
        UpdateRecordCoreAsync(
            designerId, id, body, httpContext, db, schemaRegistry, connectionFactory,
            payloadValidator,
            requireOwnerColumn: string.IsNullOrWhiteSpace(authFilterColumn) ? null : authFilterColumn.Trim(),
            ct);

    // SingleRecord component — PUT /api/data/{designerId}/single/{id}?authFilterColumn=col.
    // Same update path as the generic PUT, but the target row's authFilterColumn must equal
    // the JWT user id (verified inside the core) so a user can only edit their own record.
    internal static Task<IResult> UpdateSingleRecordHandler(
        string designerId,
        Guid id,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        CancellationToken ct,
        string? authFilterColumn = null)
    {
        if (string.IsNullOrWhiteSpace(authFilterColumn))
            return Task.FromResult(Problems.ValidationFailed("Query parameter 'authFilterColumn' is required."));
        return UpdateRecordCoreAsync(
            designerId, id, body, httpContext, db, schemaRegistry, connectionFactory,
            payloadValidator, requireOwnerColumn: authFilterColumn.Trim(), ct);
    }

    // Mirrors CreateRecordCoreAsync's header (SafeIdentifier → EF binding → registry →
    // Layer 2 validation → actorId) and then runs SELECT-then-UPDATE on a single Dapper
    // connection so the handler can distinguish "record not found" (404) from "record
    // soft-deleted" (422 RECORD_DELETED) and capture previous_values for the audit log.
    // The audit INSERT runs via EF in a separate transaction (Decision 1.6) — same as POST.
    //
    // requireOwnerColumn: when non-null (SingleRecord component path), the SELECTed row's
    // value in that column must equal the JWT user id, else 404 (never leak another user's
    // record id). The column is also re-stamped to the actor on write so the update can't
    // reassign ownership. When null (generic PUT), behaviour is unchanged.
    private static async Task<IResult> UpdateRecordCoreAsync(
        string designerId,
        Guid id,
        JsonElement body,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDynamicPayloadValidator payloadValidator,
        string? requireOwnerColumn,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(payloadValidator);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        // AC-7 — Layer 2 type validation. Unknown keys (incl. system column names)
        // are silently dropped because they aren't in entry.Columns (AC-8).
        var validationResult = payloadValidator.Validate(body, entry.Columns);
        if (!validationResult.IsValid)
        {
            return Results.ValidationProblem(
                validationResult.FieldErrors,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
        }

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
            && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

        // A field whose fieldKey matches the parent_<parentDesignerId>_id FK column
        // (e.g. a designer-backed dropdown that picks the parent record) targets a UUID
        // column, but the validator coerced it as TEXT — binding the string into the
        // UPDATE SET clause yields PG 42804. Re-coerce it to Guid so it persists as a
        // real UUID.
        if (!DynamicQueryBuilder.TryCoerceReservedParentFkColumns(
                validationResult.CoercedValues, out var coercedValues, out var invalidFkColumn))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [invalidFkColumn!] = [$"Value for field '{invalidFkColumn}' is not a valid UUID."],
                },
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
        }

        // Story 6.7 — children extraction + pre-flight (before any DB connection).
        var childrenRaw = RepeaterWriteCoordinator.ParseChildrenElement(body);
        var childSchemas = new Dictionary<string, SchemaRegistryEntry>(StringComparer.Ordinal);
        var filteredChildren = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var childLoadResult = await TryLoadChildSchemasAsync(
            childrenRaw, entry, db, schemaRegistry, filteredChildren, childSchemas, ct)
            .ConfigureAwait(false);
        if (childLoadResult is not null) return childLoadResult;

        if (!RepeaterWriteCoordinator.TryParseAndValidateChildren(
                filteredChildren, childSchemas, payloadValidator,
                out var parsedChildren, out var childValidationError))
        {
            return childValidationError!;
        }

        bool hasChildren = parsedChildren.Count > 0
            && parsedChildren.Values.Any(list => list.Count > 0);

        // Story 6.7 — pre-flight SELECT existing child IDs by FK (before tx opens)
        // for determining which children to SOFT-DELETE and validating submitted
        // child ids (AC-12). Each child designer uses its own short-lived connection
        // (Dev Notes §11 — deferred optimization to batch).
        var existingChildIdsByDesignerId = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        if (hasChildren)
        {
            var fkCol = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);
            foreach (var (childDesignerId, childList) in parsedChildren)
            {
                if (!SafeIdentifier.TryCreate(childDesignerId, out var childSafeId2, out _)) continue;
                var (selSql, selParams) = DynamicQueryBuilder.BuildSelectChildIdsByFkQuery(
                    childSafeId2!, fkCol, id);

                var preflightConn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
                HashSet<Guid> existingIds;
                try
                {
                    existingIds = (await preflightConn.QueryAsync<Guid>(new CommandDefinition(
                        selSql, selParams, commandTimeout: 5, cancellationToken: ct))
                        .ConfigureAwait(false)).ToHashSet();
                }
                finally
                {
                    await preflightConn.DisposeAsync().ConfigureAwait(false);
                }
                existingChildIdsByDesignerId[childDesignerId] = existingIds;

                // AC-12 — verify submitted ids exist among non-deleted children of this parent.
                foreach (var childRec in childList.Where(c => c.ExistingId.HasValue))
                {
                    if (!existingIds.Contains(childRec.ExistingId!.Value))
                        return Problems.ChildNotFound();
                }
            }
        }

        // SELECT-then-UPDATE on a single connection. Single finally for cleanup;
        // earlyResult captures the AC-4 (404 NOT_FOUND) and AC-2 (422 RECORD_DELETED)
        // exit branches without disposing the connection mid-flow.
        IResult? earlyResult = null;
        var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
        var previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        DateTimeOffset updatedAt = default;
        IReadOnlyList<RepeaterWriteCoordinator.WriteAuditEntry> childAuditEntries =
            Array.Empty<RepeaterWriteCoordinator.WriteAuditEntry>();

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // AC-10 — 5-second commandTimeout on every Dapper call (Decision 1.6).
            var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(
                safeId, entry.Columns, id);
            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct))
                .ConfigureAwait(false);
            var rows = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(r => r.ToDictionary(
                    kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal))
                .ToList();

            if (rows.Count == 0)
            {
                // AC-4
                earlyResult = Problems.RecordNotFound();
            }
            else
            {
                existingRow = rows[0];
                // AC-2 — distinct from 404; client must restore (Story 6.6) before
                // retrying. Pattern match accepts only the boolean literal true.
                if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is true)
                {
                    earlyResult = Problems.RecordDeleted();
                }
                else if (requireOwnerColumn is not null &&
                         !RowIsOwnedBy(existingRow, requireOwnerColumn, entry.Columns, actorId))
                {
                    // SingleRecord ownership gate — the row must belong to the requesting
                    // user. 404 (not 403) so another user's record id is never confirmed.
                    earlyResult = Problems.RecordNotFound();
                }
                else
                {
                    // Re-stamp the owner column so an update cannot reassign ownership to a
                    // different user (no-op for the generic PUT path, where it stays null).
                    if (requireOwnerColumn is not null)
                        coercedValues = StampAuthFilterOwner(coercedValues, requireOwnerColumn, entry.Columns, actorId);

                    // AC-3 — capture previous_values BEFORE overlaying coerced values.
                    // Only the fields being changed are snapshotted (Dev Notes §2).
                    foreach (var colName in coercedValues.Keys)
                    {
                        previousValues[colName] = existingRow.TryGetValue(colName, out var prev) ? prev : null;
                    }

                    var (updateSql, updateParams, ts) = DynamicQueryBuilder.BuildUpdateQuery(
                        safeId, entry.Columns, coercedValues, id, actorId);
                    updatedAt = ts;

                    if (hasChildren)
                    {
                        // AC-4..AC-7 — wrap parent UPDATE + child operations in one transaction.
                        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                                updateSql, updateParams, transaction: tx,
                                commandTimeout: 5, cancellationToken: ct))
                                .ConfigureAwait(false);

                            if (rowsAffected == 0)
                            {
                                // Concurrent soft-delete raced between SELECT and UPDATE.
                                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                                earlyResult = Problems.RecordDeleted();
                            }
                            else
                            {
                                childAuditEntries = await RepeaterWriteCoordinator.UpsertAndPruneChildrenAsync(
                                    safeId!.Value, id, parsedChildren,
                                    existingChildIdsByDesignerId, childSchemas,
                                    actorId, conn, tx, ct).ConfigureAwait(false);

                                await tx.CommitAsync(ct).ConfigureAwait(false);

                                existingRow["updated_at"] = updatedAt;
                                existingRow["updated_by"] = actorId;
                                foreach (var (colName, val) in coercedValues)
                                    existingRow[colName] = val;
                            }
                        }
                        catch
                        {
                            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            throw;
                        }
                        finally
                        {
                            await tx.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // AC-8 — no children → existing single-UPDATE path, no transaction overhead.
                        var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                            updateSql, updateParams, commandTimeout: 5, cancellationToken: ct))
                            .ConfigureAwait(false);

                        if (rowsAffected == 0)
                        {
                            // Concurrent soft-delete raced between the SELECT and UPDATE;
                            // is_deleted=false in the WHERE clause made the UPDATE a no-op.
                            earlyResult = Problems.RecordDeleted();
                        }
                        else
                        {
                            // Overlay AFTER the previousValues snapshot. The response is built
                            // from this same dict — no re-SELECT needed (Dev Notes §5).
                            existingRow["updated_at"] = updatedAt;
                            existingRow["updated_by"] = actorId;
                            foreach (var (colName, val) in coercedValues)
                                existingRow[colName] = val;
                        }
                    }
                }
            }
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        if (earlyResult is not null) return earlyResult;

        // AC-3 / AC-13 — append the audit row (parent + each child) in a separate
        // EF transaction (Decision 1.6).
        var correlationId = httpContext.GetCorrelationId();
        // Dev Notes §4 — empty payload serialises as null, not "{}". Applied to both
        // new_values and previous_values for symmetry.
        var newValuesJson = coercedValues.Count > 0
            ? JsonSerializer.Serialize(coercedValues)
            : null;
        var prevValuesJson = previousValues.Count > 0
            ? JsonSerializer.Serialize(previousValues)
            : null;

        db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
        {
            DesignerId     = safeId!.Value,
            RecordId       = id,
            Operation      = "UPDATE",
            ActorId        = actorId,
            Timestamp      = updatedAt,
            NewValues      = newValuesJson,
            PreviousValues = prevValuesJson,
            CorrelationId  = correlationId,
        });
        foreach (var childAudit in childAuditEntries)
        {
            db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
            {
                DesignerId     = childAudit.DesignerId,
                RecordId       = childAudit.RecordId,
                Operation      = childAudit.Operation,
                ActorId        = actorId,
                Timestamp      = childAudit.Timestamp,
                NewValues      = childAudit.NewValuesJson,
                PreviousValues = childAudit.PreviousValuesJson,
                CorrelationId  = correlationId,
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new DynamicRecord(existingRow));
    }

    // Story 6.5 — DELETE /api/data/{designerId}/{id}. Mirrors prior handler
    // headers (SafeIdentifier → EF binding → registry). Then:
    //   1. Pre-loads the descendant schema graph via EF (before opening Dapper)
    //      when ChildRepeaterDesignerIds is non-empty.
    //   2. Opens Dapper connection; SELECTs the parent row (existence + is_deleted).
    //   3. If children exist: opens a single NpgsqlTransaction, runs the parent
    //      UPDATE plus SoftDeleteCascade.ExecuteAsync inside it, commits atomically.
    //   4. If no children: single UPDATE on the same connection, no explicit tx.
    //   5. Appends a mutation_audit_log row via EF (Decision 1.6 separate tx).
    //   6. Returns 200 with the updated parent record (overlay pattern — no re-SELECT).
    // Resolves the schema-registry entry for (designerId, version), populating it from
    // EF on a cache miss (Decision 1.4 — registry is the column source of truth). Mirrors
    // the inline pattern used across the list/create/update handlers.
    private static async Task<SchemaRegistryEntry> GetOrPopulateEntryAsync(
        FormForgeDbContext db, ISchemaRegistry schemaRegistry, string designerId, int version, CancellationToken ct)
    {
        var entry = schemaRegistry.TryGet(designerId, version);
        if (entry is not null) return entry;

        var rootElementJson = await db.ComponentSchemaVersions
            .AsNoTracking()
            .Where(v => v.DesignerId == designerId && v.Version == version)
            .Select(v => v.RootElement)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
        entry = new SchemaRegistryEntry(designerId, version, columns, childIds, DateTimeOffset.UtcNow)
        {
            DerivedColumns = derivedColumns,
        };
        schemaRegistry.Populate(entry);
        return entry;
    }

    // TreeView — one level of a self-referencing tree: the live node rows + a
    // hasNextPage flag for the per-level paginator. Property names serialize camelCase
    // (rows, hasNextPage, page, pageSize), matching PagedResult.
    internal sealed record TreeLevelResult(
        IReadOnlyList<DynamicRecord> Rows, bool HasNextPage, int Page, int PageSize);

    // TreeView "All select" — recursive descendant ids of a node (camelCase `ids`).
    internal sealed record TreeDescendantsResult(IReadOnlyList<string> Ids);

    // Validates an optional TreeView auth-filter column and resolves the requesting user
    // id. Returns null on success (out params set when a filter is requested); otherwise an
    // error IResult (422 unknown column / 401 no user). No filter requested → null + nulls.
    private static IResult? ResolveTreeOwnerFilter(
        string? authFilterColumn,
        SchemaRegistryEntry entry,
        HttpContext httpContext,
        out string? ownerColumn,
        out Guid? ownerId)
    {
        ownerColumn = null;
        ownerId = null;
        if (string.IsNullOrWhiteSpace(authFilterColumn)) return null;

        var col = authFilterColumn.Trim();
        if (!IsKnownUserColumn(entry.Columns, col))
            return Problems.ValidationFailed($"Auth filter column '{col}' is not a field on this designer.");
        if (!Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var uid))
            return Results.Unauthorized();

        ownerColumn = col;
        ownerId = uid;
        return null;
    }

    // True when the given table physically carries the named column. Used to detect a
    // TreeView-declared self-FK (parent_<table>_id) on a table whose own RootElement
    // does not declare it, so the soft-delete cascade can inject the self-edge.
    private static async Task<bool> TableHasColumnAsync(
        NpgsqlConnection conn, string tableName, string columnName, CancellationToken ct)
    {
        const string sql =
            "SELECT EXISTS(SELECT 1 FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @t AND column_name = @c)";
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, new { t = tableName, c = columnName }, commandTimeout: 5, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    internal static async Task<IResult> DeleteRecordHandler(
        string designerId,
        Guid id,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        // Optional per-component auth filter (TreeView): the row must be owned by the
        // requesting user, else 404 (never confirm another user's record id).
        string? authFilterColumn = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
            && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

        IResult? earlyResult = null;
        var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
        DateTimeOffset updatedAt = default;
        Guid? cascadeEventId = null;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // A TreeView turns its node-template table into a self-referencing
            // adjacency-list tree by adding a self-FK parent_<table>_id. That FK is
            // declared by a TreeView in ANOTHER designer, so it is absent from THIS
            // designer's RootElement (and thus from entry.ChildRepeaterDesignerIds and
            // the schema graph). Detect it from the physical column and inject a
            // self-edge so deleting a node soft-deletes its whole subtree. (DB-level
            // ON DELETE CASCADE is a hard-delete cascade only; soft-delete needs the
            // explicit walk in SoftDeleteCascade.ExecuteAsync below.)
            var selfFkColumn = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);
            var isSelfRefTree = await TableHasColumnAsync(conn, safeId!.Value, selfFkColumn, ct)
                .ConfigureAwait(false);

            IReadOnlyList<string> effectiveChildIds = entry.ChildRepeaterDesignerIds;
            if (isSelfRefTree && !effectiveChildIds.Contains(safeId!.Value, StringComparer.Ordinal))
                effectiveChildIds = new List<string>(entry.ChildRepeaterDesignerIds) { safeId!.Value };

            // Load cascade schema graph via EF (independent of the Dapper conn).
            var cascadeGraph = new Dictionary<string, SoftDeleteCascade.NodeInfo>(StringComparer.Ordinal);
            if (effectiveChildIds.Count > 0)
            {
                await SoftDeleteCascade.BuildSchemaGraphAsync(
                    effectiveChildIds, db, schemaRegistry, cascadeGraph, ct).ConfigureAwait(false);

                if (isSelfRefTree)
                {
                    // BuildSchemaGraphAsync derives a node's ChildIds from its own
                    // RootElement, which (for a TreeView-driven tree) does not list
                    // itself — so the self node would not recurse. Force the self-edge
                    // into its ChildIds; ExecuteAsync's self-edge handling then walks
                    // each level, bounded by the is_deleted=false data filter.
                    cascadeGraph.TryGetValue(safeId!.Value, out var selfNode);
                    var selfChildIds = selfNode is null
                        ? new List<string>()
                        : new List<string>(selfNode.ChildIds);
                    if (!selfChildIds.Contains(safeId!.Value, StringComparer.Ordinal))
                        selfChildIds.Add(safeId!.Value);
                    cascadeGraph[safeId!.Value] = new SoftDeleteCascade.NodeInfo(safeId!, selfChildIds);
                }

                if (cascadeGraph.Count == 0)
                {
                    LogCascadeSoftDeleteSkipped(
                        loggerFactory.CreateLogger("FormForge.Api.Features.DynamicCrud"),
                        safeId!.Value, id, effectiveChildIds.Count, null);
                }
            }

            // SELECT the parent row (existence check for AC-4 + is_deleted for AC-7).
            var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(
                safeId, entry.Columns, id);
            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct))
                .ConfigureAwait(false);

            var rows = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(r => r.ToDictionary(
                    kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal))
                .ToList();

            if (rows.Count == 0)
            {
                earlyResult = Problems.RecordNotFound();
            }
            else
            {
                existingRow = rows[0];
                if (!string.IsNullOrWhiteSpace(authFilterColumn)
                    && !RowIsOwnedBy(existingRow, authFilterColumn.Trim(), entry.Columns, actorId))
                {
                    // Per-component auth filter: not the user's row → 404 (don't confirm it).
                    earlyResult = Problems.RecordNotFound();
                }
                else if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is true)
                {
                    earlyResult = Problems.RecordAlreadyDeleted();
                }
                else
                {
                    var hasCascade = cascadeGraph.Count > 0;
                    cascadeEventId = hasCascade ? Guid.NewGuid() : (Guid?)null;

                    var (updateSql, updateParams, ts) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
                        safeId, id, actorId, cascadeEventId);
                    updatedAt = ts;

                    if (hasCascade)
                    {
                        // AC-2 — all UPDATEs within a single NpgsqlTransaction.
                        // Explicit try/finally rather than `await using` so the implicit
                        // DisposeAsync does not drop ConfigureAwait (matches UserService).
                        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                                updateSql, updateParams, transaction: tx,
                                commandTimeout: 5, cancellationToken: ct))
                                .ConfigureAwait(false);

                            if (rowsAffected == 0)
                            {
                                // Concurrent double-delete raced between SELECT and UPDATE.
                                await tx.RollbackAsync(CancellationToken.None)
                                    .ConfigureAwait(false);
                                earlyResult = Problems.RecordAlreadyDeleted();
                            }
                            else
                            {
                                var visitedCascade = new HashSet<string>(StringComparer.Ordinal)
                                    { safeId!.Value };
                                try
                                {
                                    await SoftDeleteCascade.ExecuteAsync(
                                        safeId!.Value,
                                        effectiveChildIds,
                                        cascadeGraph, id,
                                        cascadeEventId!.Value, updatedAt, actorId,
                                        conn, tx, visitedCascade, ct)
                                        .ConfigureAwait(false);
                                    await tx.CommitAsync(ct).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // Rollback must complete even if the request was cancelled.
                                    await tx.RollbackAsync(CancellationToken.None)
                                        .ConfigureAwait(false);
                                    throw;
                                }
                            }
                        }
                        finally
                        {
                            await tx.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // No children — single UPDATE, no explicit transaction.
                        var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                            updateSql, updateParams, commandTimeout: 5, cancellationToken: ct))
                            .ConfigureAwait(false);

                        if (rowsAffected == 0)
                        {
                            earlyResult = Problems.RecordAlreadyDeleted();
                        }
                    }

                    if (earlyResult is null)
                    {
                        // Overlay updated system columns onto the SELECT result.
                        existingRow["is_deleted"]       = true;
                        existingRow["updated_at"]       = updatedAt;
                        existingRow["updated_by"]       = actorId;
                        existingRow["cascade_event_id"] = (object?)cascadeEventId;
                    }
                }
            }
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        if (earlyResult is not null) return earlyResult;

        // AC-3 — audit row via EF (separate transaction per Decision 1.6).
        var correlationId = httpContext.GetCorrelationId();
        db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
        {
            DesignerId     = safeId!.Value,
            RecordId       = id,
            Operation      = "SOFT_DELETE",
            ActorId        = actorId,
            Timestamp      = updatedAt,
            NewValues      = null,   // AC-3: soft-delete has no field-level diff
            PreviousValues = null,
            CorrelationId  = correlationId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new DynamicRecord(existingRow));
    }

    // Story 6.6 — PUT /api/data/{designerId}/{id}/restore. Mirrors the prior handler
    // headers (SafeIdentifier → EF binding → registry). Then:
    //   1. Pre-loads cascade schema graph (EF, before opening Dapper) only when
    //      entry.ChildRepeaterDesignerIds is non-empty — same pattern as
    //      DeleteRecordHandler.
    //   2. Opens Dapper connection, SELECTs the parent row (existence check for
    //      AC-5 + is_deleted check for AC-6).
    //   3. Reads the parent's cascade_event_id from the SELECT result.
    //   4. Always runs the parent UPDATE inside a single NpgsqlTransaction so the
    //      flat-cascade descendant UPDATEs are atomic with the parent.
    //        - BuildRestoreByIdQuery (parent)
    //        - SoftDeleteCascade.RestoreCascadeAsync (all descendants by cascade_event_id)
    //          — only when parent's cascade_event_id was non-null AND graph is non-empty.
    //   5. Commits, or rolls back on exception.
    //   6. Appends mutation_audit_log row via EF (Decision 1.6 separate transaction).
    //   7. Returns 200 with the restored parent record (overlay pattern — no re-SELECT).
    internal static async Task<IResult> RestoreRecordHandler(
        string designerId,
        Guid id,
        HttpContext httpContext,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        if (!SafeIdentifier.TryCreate(designerId, out var safeId, out _))
            return Problems.ValidationFailed("Invalid designer identifier.");

        var boundVersion = await ResolveEffectiveVersionAsync(db, safeId!.Value, ct).ConfigureAwait(false);

        if (boundVersion is null)
            return Problems.TableNotProvisioned();

        var entry = schemaRegistry.TryGet(safeId!.Value, boundVersion.Value);
        if (entry is null)
        {
            var rootElementJson = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == safeId.Value && v.Version == boundVersion.Value)
                .Select(v => v.RootElement)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            var (columns, childIds, derivedColumns) = RootElementParser.ParseWithDerived(rootElementJson);
            entry = new SchemaRegistryEntry(
                safeId.Value, boundVersion.Value, columns, childIds, DateTimeOffset.UtcNow)
            {
                DerivedColumns = derivedColumns,
            };
            schemaRegistry.Populate(entry);
        }

        var actorId = httpContext.User.FindFirst("userId")?.Value is { } userIdStr
            && Guid.TryParse(userIdStr, out var uid) ? uid : (Guid?)null;

        IResult? earlyResult = null;
        var existingRow = new Dictionary<string, object?>(StringComparer.Ordinal);
        DateTimeOffset updatedAt = default;
        Guid? parentCascadeEventId = null;

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Self-referencing TreeView tree: include the self node in the cascade graph
            // so a cascade-delete event on this table is restorable. The self-FK
            // (parent_<table>_id) is declared by a TreeView in another designer, so it is
            // absent from the schema graph — detect it from the physical column. (See the
            // matching note in DeleteRecordHandler.) RestoreCascadeAsync matches rows by
            // cascade_event_id and ignores ChildIds, so the self node just needs to exist.
            var selfFkColumn = DynamicQueryBuilder.BuildFkColumnName(safeId!.Value);
            var isSelfRefTree = await TableHasColumnAsync(conn, safeId!.Value, selfFkColumn, ct)
                .ConfigureAwait(false);

            IReadOnlyList<string> effectiveChildIds = entry.ChildRepeaterDesignerIds;
            if (isSelfRefTree && !effectiveChildIds.Contains(safeId!.Value, StringComparer.Ordinal))
                effectiveChildIds = new List<string>(entry.ChildRepeaterDesignerIds) { safeId!.Value };

            var cascadeGraph = new Dictionary<string, SoftDeleteCascade.NodeInfo>(StringComparer.Ordinal);
            if (effectiveChildIds.Count > 0)
            {
                await SoftDeleteCascade.BuildSchemaGraphAsync(
                    effectiveChildIds, db, schemaRegistry, cascadeGraph, ct).ConfigureAwait(false);

                if (isSelfRefTree && !cascadeGraph.ContainsKey(safeId!.Value))
                    cascadeGraph[safeId!.Value] = new SoftDeleteCascade.NodeInfo(safeId!, [safeId!.Value]);
            }

            // SELECT the parent row (existence check for AC-5 + is_deleted check for AC-6).
            var (selectSql, selectParams) = DynamicQueryBuilder.BuildGetByIdQuery(
                safeId, entry.Columns, id);
            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct))
                .ConfigureAwait(false);

            var rows = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(r => r.ToDictionary(
                    kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal))
                .ToList();

            if (rows.Count == 0)
            {
                earlyResult = Problems.RecordNotFound();
            }
            else
            {
                existingRow = rows[0];

                // AC-6 — record is already active; restore is a no-op client error.
                if (existingRow.TryGetValue("is_deleted", out var isDeletedVal) && isDeletedVal is false)
                {
                    earlyResult = Problems.RecordNotDeleted();
                }
                else
                {
                    // Read the parent's current cascade_event_id BEFORE the restore
                    // UPDATE clears it — needed to match child rows for cascade-restore.
                    // DBNull and null both fail the `is Guid` test, so individually-
                    // deleted parents leave parentCascadeEventId null (correct).
                    if (existingRow.TryGetValue("cascade_event_id", out var ceVal) && ceVal is Guid ceGuid)
                        parentCascadeEventId = ceGuid;

                    var (updateSql, updateParams, ts) = DynamicQueryBuilder.BuildRestoreByIdQuery(
                        safeId, id, actorId);
                    updatedAt = ts;

                    // Always open a transaction: cascade or not, atomicity is cheap and
                    // makes the code path uniform (Dev Notes §1).
                    var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                            updateSql, updateParams, transaction: tx,
                            commandTimeout: 5, cancellationToken: ct))
                            .ConfigureAwait(false);

                        if (rowsAffected == 0)
                        {
                            // Concurrent restore raced between SELECT and UPDATE.
                            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                            earlyResult = Problems.RecordNotDeleted();
                        }
                        else
                        {
                            // AC-2 — cascade-restore children that share the cascade event UUID.
                            // Children with different / NULL cascade_event_id are NOT touched (AC-3).
                            if (parentCascadeEventId.HasValue && cascadeGraph.Count > 0)
                            {
                                await SoftDeleteCascade.RestoreCascadeAsync(
                                    cascadeGraph,
                                    parentCascadeEventId.Value,
                                    actorId,
                                    updatedAt,
                                    conn,
                                    tx,
                                    ct).ConfigureAwait(false);
                            }

                            await tx.CommitAsync(ct).ConfigureAwait(false);

                            // Overlay restored system columns onto the SELECT result.
                            existingRow["is_deleted"]       = false;
                            existingRow["updated_at"]       = updatedAt;
                            existingRow["updated_by"]       = actorId;
                            existingRow["cascade_event_id"] = null;  // cleared on restore
                        }
                    }
                    catch
                    {
                        // Rollback must complete even if the request was cancelled.
                        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                    finally
                    {
                        await tx.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        if (earlyResult is not null) return earlyResult;

        // AC-4 — audit row via EF (separate transaction per Decision 1.6). RESTORE
        // has no field-level diff, so new_values and previous_values are null.
        var correlationId = httpContext.GetCorrelationId();
        db.MutationAuditLog.Add(new Domain.Entities.MutationAuditLogEntry
        {
            DesignerId     = safeId!.Value,
            RecordId       = id,
            Operation      = "RESTORE",
            ActorId        = actorId,
            Timestamp      = updatedAt,
            NewValues      = null,
            PreviousValues = null,
            CorrelationId  = correlationId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new DynamicRecord(existingRow));
    }

    // Resolves the schema version to use for a designer's provisioned table, or null
    // when the table is not provisioned (caller maps null → 404 TABLE_NOT_PROVISIONED).
    //
    // Two provisioning paths feed this:
    //   1. Menu binding — a successful bind pins the table to a BoundVersion. Preferred
    //      when present, preserving the original behaviour exactly.
    //   2. Menu-less (admin Table Provisioning tab) — no menu row exists, so we fall
    //      back to the highest version the table was actually CREATE/ALTER-provisioned
    //      against per the schema audit log. That is the menu-less analog of BoundVersion:
    //      the version whose columns the physical table is guaranteed to have.
    private static async Task<int?> ResolveEffectiveVersionAsync(
        FormForgeDbContext db, string designerId, CancellationToken ct)
    {
        var boundVersion = await db.Menus
            .AsNoTracking()
            .Where(m => m.DesignerId == designerId
                     && m.ProvisioningStatus == "Success"
                     && m.BoundVersion != null)
            .OrderByDescending(m => m.BoundVersion)
            .Select(m => (int?)m.BoundVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (boundVersion is not null) return boundVersion;

        return await db.SchemaAuditLog
            .AsNoTracking()
            .Where(a => a.DesignerId == designerId
                     && (a.DdlOperation == "CREATE" || a.DdlOperation == "ALTER"))
            .OrderByDescending(a => a.ToVersion)
            .Select(a => (int?)a.ToVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    // Story 6.7 — shared helper for CreateRecordHandler and UpdateRecordHandler.
    // Walks the raw children dict, rejects unknown designerIds (AC-10 → 422), loads
    // each child schema from the latest Published ComponentSchemaVersion (AC-11 →
    // 404 TABLE_NOT_PROVISIONED when none exists), caches into schemaRegistry, and
    // populates the supplied filteredChildren + childSchemas dicts. Returns null on
    // success; otherwise returns the IResult (422/404) the handler must return.
    private static async Task<IResult?> TryLoadChildSchemasAsync(
        Dictionary<string, JsonElement> childrenRaw,
        SchemaRegistryEntry parentEntry,
        FormForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        Dictionary<string, JsonElement> filteredChildren,
        Dictionary<string, SchemaRegistryEntry> childSchemas,
        CancellationToken ct)
    {
        if (childrenRaw.Count == 0) return null;

        foreach (var (childId, arrayEl) in childrenRaw)
        {
            // AC-10 — child designerId must be a declared Repeater child of the parent.
            if (!parentEntry.ChildRepeaterDesignerIds.Contains(childId))
            {
                return Problems.ValidationFailed(
                    $"Child designer '{childId}' is not a Repeater child of this designer.");
            }
            filteredChildren[childId] = arrayEl;
        }

        foreach (var childDesignerId in filteredChildren.Keys)
        {
            if (!SafeIdentifier.TryCreate(childDesignerId, out var childSafeId, out _))
                return Problems.ValidationFailed($"Invalid child designer identifier: {childDesignerId}");

            var childVersionRow = await db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == childSafeId!.Value && v.Status == "Published")
                .OrderByDescending(v => v.Version)
                .Select(v => new { v.Version, v.RootElement })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (childVersionRow is null)
                return Problems.TableNotProvisioned();

            var cached = schemaRegistry.TryGet(childSafeId!.Value, childVersionRow.Version);
            if (cached is null)
            {
                var (childColumns, childChildIds, childDerived) =
                    RootElementParser.ParseWithDerived(childVersionRow.RootElement);
                cached = new SchemaRegistryEntry(
                    childSafeId!.Value, childVersionRow.Version,
                    childColumns, childChildIds, DateTimeOffset.UtcNow)
                {
                    DerivedColumns = childDerived,
                };
                schemaRegistry.Populate(cached);
            }
            childSchemas[childDesignerId] = cached;
        }

        return null;
    }

    // Story 7-followup — soft-deleted rows must NOT appear in the default list /
    // export response, and viewing them at all is a platform-admin capability.
    //
    //   * Non-admins: is_deleted=false is FORCED, overriding any includeDeleted
    //     flag or hand-crafted filter[isDeleted] in the query string. A non-admin
    //     can never surface a tombstoned row, even via a direct API call.
    //   * Admins with includeDeleted=true: no implicit filter — see everything.
    //   * Admins with an explicit filter[isDeleted]: their choice wins.
    //   * Admins otherwise: default to live-only.
    //
    // The returned dictionary is always a fresh instance — the input is treated
    // as immutable.
    private static IReadOnlyDictionary<string, string> ApplyDefaultSoftDeleteFilter(
        IReadOnlyDictionary<string, string> filters, bool includeDeleted, bool isAdmin)
    {
        if (!isAdmin)
        {
            var forced = new Dictionary<string, string>(filters, StringComparer.Ordinal)
            {
                ["is_deleted"] = "false",
            };
            return forced;
        }
        if (includeDeleted) return filters;
        if (filters.ContainsKey("is_deleted")) return filters;
        var withDefault = new Dictionary<string, string>(filters, StringComparer.Ordinal)
        {
            ["is_deleted"] = "false",
        };
        return withDefault;
    }

    // Reads the per-version AuthFilterFieldKey (null when the version configures no
    // auth filter). Fetched fresh — NOT cached in the schema registry — so an admin
    // toggling the filter from the Component Library takes effect on the next request
    // without a version bump or cache invalidation.
    private static async Task<string?> GetAuthFilterFieldKeyAsync(
        FormForgeDbContext db, string designerId, int version, CancellationToken ct) =>
        await db.ComponentSchemaVersions
            .AsNoTracking()
            .Where(v => v.DesignerId == designerId && v.Version == version)
            .Select(v => v.AuthFilterFieldKey)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    // Reads the per-version dataset binding (null when the version is bound to no dataset).
    // Fetched fresh — NOT cached in the schema registry — so an admin binding/unbinding from
    // the Component Library takes effect on the next request without a version bump.
    private static async Task<Guid?> GetDatasetIdAsync(
        FormForgeDbContext db, string designerId, int version, CancellationToken ct) =>
        await db.ComponentSchemaVersions
            .AsNoTracking()
            .Where(v => v.DesignerId == designerId && v.Version == version)
            .Select(v => v.DatasetId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    // Translates the list endpoint's parsed equality filters (pgName -> value, the same
    // shape the dynamic-table path uses) into the canonical FilterGroupDto the dataset
    // row-query service consumes. Each entry becomes an AND-ed "= value" condition; the
    // dataset service validates each column against the VIEW and coerces the value.
    private static FilterGroupDto? BuildDatasetFilterGroup(IReadOnlyDictionary<string, string> filters)
    {
        if (filters.Count == 0) return null;
        var items = new List<FilterItemDto>(filters.Count);
        foreach (var (column, value) in filters)
        {
            items.Add(new FilterConditionDto(
                Id: column,
                Kind: "condition",
                TableName: string.Empty,
                ColumnName: column,
                Operator: "=",
                Value: JsonSerializer.SerializeToElement(value)));
        }
        return new FilterGroupDto("root", "group", "AND", items);
    }

    private static List<DatasetSortDto>? BuildDatasetSort(IReadOnlyList<DynamicQueryBuilder.SortParam> sorts) =>
        sorts.Count == 0
            ? null
            : sorts.Select(s => new DatasetSortDto(s.Column, s.Direction)).ToList();

    // Auth-scope column for the dataset path: the version's AuthFilterFieldKey passed straight
    // through (the dataset service validates it against the VIEW and binds the server-resolved
    // user id as the value). Fail-closed: if the dataset lacks the column the service returns
    // 422 rather than leaking every row.
    private static string? NormalizeDatasetAuthColumn(string? authFilterFieldKey) =>
        string.IsNullOrWhiteSpace(authFilterFieldKey) ? null : authFilterFieldKey.Trim();

    // Serves one level of a dataset-backed TreeView from the dataset VIEW. Rows come back keyed
    // by column name; the node id is the chosen keyField column (the SPA maps it to "id" so
    // expand / select / CRUD-by-id keep working). `_has_children` is computed by the query.
    private static async Task<IResult> ListTreeNodesFromDatasetAsync(
        Guid datasetId,
        string keyField,
        string parentField,
        string? parentId,
        int page,
        int pageSize,
        string? search,
        string? authFilterColumn,
        HttpContext httpContext,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct)
    {
        var request = new DatasetTreeLevelRequest(
            KeyColumn: keyField,
            ParentColumn: parentField,
            ParentId: string.IsNullOrWhiteSpace(parentId) ? null : parentId.Trim(),
            Search: search,
            Page: page,
            PageSize: pageSize,
            AuthFilterColumn: NormalizeDatasetAuthColumn(authFilterColumn));

        Guid? authUserId = Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var uid)
            ? uid : null;

        var result = await datasetRowQueryService.GetTreeLevelAsync(datasetId, request, ct, authUserId)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case DatasetRowsOutcome.Success:
                var lvl = result.Page!;
                var records = lvl.Data
                    .Select(row => new DynamicRecord(
                        row.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)))
                    .ToList();
                return Results.Ok(new TreeLevelResult(records, lvl.HasNextPage, lvl.Page, lvl.PageSize));
            case DatasetRowsOutcome.NotFound:
                return Problems.TableNotProvisioned();
            default:
                return Problems.ValidationFailed(result.ErrorDetail ?? "Invalid dataset tree query.");
        }
    }

    // Serves the paged record list from a bound dataset's VIEW. The rows come back keyed by
    // column name (the id is exposed as "<designerId>_id" per the dataset convention); they
    // are wrapped in DynamicRecord so system columns serialize to camelCase exactly as the
    // dynamic-table path does. The SPA maps "<designerId>_id" to "id" for update/delete.
    private static async Task<IResult> ListRecordsFromDatasetAsync(
        Guid datasetId,
        IReadOnlyList<DynamicQueryBuilder.SortParam> sorts,
        IReadOnlyDictionary<string, string> filters,
        int page,
        int pageSize,
        string? authFilterFieldKey,
        HttpContext httpContext,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct)
    {
        var request = new DatasetRowsRequest(
            Filters: BuildDatasetFilterGroup(filters),
            Sort: BuildDatasetSort(sorts),
            Columns: null,
            Page: page,
            PageSize: pageSize,
            AuthFilterColumn: NormalizeDatasetAuthColumn(authFilterFieldKey));

        Guid? authUserId = Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var uid)
            ? uid : null;

        var result = await datasetRowQueryService.GetRowsAsync(datasetId, request, ct, authUserId)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case DatasetRowsOutcome.Success:
                var dataPage = result.Page!;
                var records = dataPage.Data
                    .Select(row => new DynamicRecord(
                        row.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)))
                    .ToList();
                return Results.Ok(new PagedResult<DynamicRecord>(
                    records, dataPage.Total, dataPage.Page, dataPage.PageSize));
            case DatasetRowsOutcome.NotFound:
                // The bound dataset was deleted out from under the binding — treat the source
                // as unavailable rather than surfacing a 500.
                return Problems.TableNotProvisioned();
            default:
                return Problems.ValidationFailed(result.ErrorDetail ?? "Invalid dataset query.");
        }
    }

    // Streams an export from the bound dataset's VIEW (same source/scoping as the list), so
    // the file matches what the user sees on screen. All VIEW columns are exported in ordinal
    // order with the column names as headers.
    private static async Task<IResult> ExportRecordsFromDatasetAsync(
        Guid datasetId,
        IReadOnlyList<DynamicQueryBuilder.SortParam> sorts,
        IReadOnlyDictionary<string, string> filters,
        string? authFilterFieldKey,
        string title,
        Func<Export.IRecordExportWriter> writerFactory,
        HttpContext httpContext,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct)
    {
        var request = new DatasetExportRequest(
            Filters: BuildDatasetFilterGroup(filters),
            Sort: BuildDatasetSort(sorts),
            Columns: null,
            AuthFilterColumn: NormalizeDatasetAuthColumn(authFilterFieldKey));

        Guid? authUserId = Guid.TryParse(httpContext.User.FindFirst("userId")?.Value, out var uid)
            ? uid : null;

        var result = await datasetRowQueryService.GetExportAsync(datasetId, request, ct, authUserId)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case DatasetExportOutcome.Success:
                var data = result.Data!;
                var writer = writerFactory();
                var fileName = $"{SanitizeFileNameSegment(title)}.{writer.FileExtension}";
                using (var ms = new MemoryStream())
                {
                    await writer.WriteAsync(ms, title, data.Headers, data.Rows, ct).ConfigureAwait(false);
                    return Results.File(
                        fileContents: ms.ToArray(),
                        contentType: writer.ContentType,
                        fileDownloadName: fileName);
                }
            case DatasetExportOutcome.NotFound:
                return Problems.TableNotProvisioned();
            case DatasetExportOutcome.TooManyRows:
                return Problems.ValidationFailed(result.ErrorDetail ?? "Export result set is too large.");
            default:
                return Problems.ValidationFailed(result.ErrorDetail ?? "Invalid dataset export.");
        }
    }

    // Forces filters[authFilterFieldKey] = current user id when the version names a
    // valid user column for the auth filter. Returns the input unchanged when no
    // filter is configured, the field is not a known user column on this version, or
    // the request carries no parseable user id. The forced predicate overrides any
    // client-supplied value for the same column so scoping cannot be bypassed.
    private static IReadOnlyDictionary<string, string> ApplyAuthFilter(
        IReadOnlyDictionary<string, string> filters,
        string? authFilterFieldKey,
        IReadOnlyList<ColumnDefinition> userColumns,
        HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(authFilterFieldKey)) return filters;

        var isKnownColumn = false;
        foreach (var c in userColumns)
        {
            if (string.Equals(c.ColumnName, authFilterFieldKey, StringComparison.Ordinal))
            {
                isKnownColumn = true;
                break;
            }
        }
        if (!isKnownColumn) return filters;

        var userId = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userId, out _)) return filters;

        return new Dictionary<string, string>(filters, StringComparer.Ordinal)
        {
            [authFilterFieldKey] = userId!,
        };
    }

    // Returns coercedValues with the AuthFilterFieldKey column forced to the actor's
    // id (as a Guid — the column is expected to be UUID). No-op when no filter is
    // configured, the field is not a known user column on this version, or the actor
    // is unknown. Overrides any client-supplied value for that column.
    private static IReadOnlyDictionary<string, object?> StampAuthFilterOwner(
        IReadOnlyDictionary<string, object?> coercedValues,
        string? authFilterFieldKey,
        IReadOnlyList<ColumnDefinition> userColumns,
        Guid? actorId)
    {
        if (string.IsNullOrWhiteSpace(authFilterFieldKey) || actorId is null) return coercedValues;

        var isKnownColumn = false;
        foreach (var c in userColumns)
        {
            if (string.Equals(c.ColumnName, authFilterFieldKey, StringComparison.Ordinal))
            {
                isKnownColumn = true;
                break;
            }
        }
        if (!isKnownColumn) return coercedValues;

        return new Dictionary<string, object?>(coercedValues, StringComparer.Ordinal)
        {
            [authFilterFieldKey] = actorId.Value,
        };
    }

    // True when columnName is one of this version's user columns. Used by the SingleRecord
    // paths to reject an authFilterColumn that names a system/unknown column before it could
    // create an unowned row or scope a read to a non-existent predicate.
    private static bool IsKnownUserColumn(IReadOnlyList<ColumnDefinition> userColumns, string columnName)
    {
        foreach (var c in userColumns)
        {
            if (string.Equals(c.ColumnName, columnName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // True when the row's owner column identifies the actor. Used by the SingleRecord
    // update path to enforce that a user can only edit their own record. The owner column
    // may be UUID-typed (value comes back as Guid) OR TEXT-typed (value comes back as the
    // id's string form), so both shapes are matched. Returns false for a missing actor, an
    // unknown owner column, or a null/mismatched value — deny-by-default.
    private static bool RowIsOwnedBy(
        Dictionary<string, object?> row,
        string ownerColumn,
        IReadOnlyList<ColumnDefinition> userColumns,
        Guid? actorId)
    {
        if (actorId is null || !IsKnownUserColumn(userColumns, ownerColumn)) return false;
        if (!row.TryGetValue(ownerColumn, out var v) || v is null) return false;
        return v switch
        {
            Guid g => g == actorId.Value,
            // TEXT column storing the id: compare parsed-as-Guid first (tolerates casing /
            // formatting), then fall back to an ordinal string match.
            string s => Guid.TryParse(s, out var sg)
                ? sg == actorId.Value
                : string.Equals(s, actorId.Value.ToString(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // Resolves whether the calling user holds the platform-admin role. Mirrors
    // the check in usePermissionsQuery / the /admin route guard. Returns false
    // for a missing / unparseable userId claim (deny-by-default).
    private static async Task<bool> IsPlatformAdminAsync(
        HttpContext httpContext, IPermissionService permissionService, CancellationToken ct)
    {
        var userIdClaim = httpContext.User.FindFirst("userId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId)) return false;
        var perms = await permissionService.GetEffectivePermissionsAsync(userId, ct).ConfigureAwait(false);
        return perms.RoleIds.Contains(WellKnownRoles.PlatformAdminId);
    }

    private static HashSet<string> BuildAllowedColumnSet(
        IReadOnlyList<ColumnDefinition> userColumns, FrozenSet<string> systemColumns)
    {
        // Per-request union of user fieldKeys + system PG names. HashSet (not
        // FrozenSet) because the set is built once per request and discarded.
        var set = new HashSet<string>(systemColumns.Count + userColumns.Count, StringComparer.Ordinal);
        foreach (var s in systemColumns) set.Add(s);
        foreach (var c in userColumns) set.Add(c.ColumnName);
        return set;
    }

    private static bool TryParseAndValidateFilters(
        IQueryCollection query,
        HashSet<string> allowedFilterCols,
        IReadOnlyList<SchemaRegistry.ColumnDefinition> userColumns,
        out IReadOnlyDictionary<string, string> filters,
        out string? error)
    {
        // Build a per-request user-column lookup so we can reject malformed
        // values (e.g. "abc" against a NUMERIC column) up front, with a precise
        // 400, instead of leaking a 500 from Postgres' type-mismatch error.
        var userColPgType = new Dictionary<string, string>(userColumns.Count, StringComparer.Ordinal);
        foreach (var c in userColumns) userColPgType[c.ColumnName] = c.PgType;

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in query)
        {
            if (!key.StartsWith("filter[", StringComparison.Ordinal) ||
                !key.EndsWith(']'))
            {
                continue;
            }
            var clientKey = key[7..^1];
            if (clientKey.Length == 0)
            {
                filters = dict;
                error = "Empty filter key.";
                return false;
            }
            var pgName = DynamicQueryBuilder.MapClientFilterKeyToPgName(clientKey);
            if (!allowedFilterCols.Contains(pgName))
            {
                filters = dict;
                error = $"Filter key '{clientKey}' is not a known field on this resource.";
                return false;
            }
            var rawVal = value.ToString();
            // System columns validate by name (UUID / BOOLEAN literals); user
            // columns validate by PG type (numeric / date / boolean).
            if (!DynamicQueryBuilder.TryValidateFilterValue(pgName, rawVal, out var valueError))
            {
                filters = dict;
                error = $"Filter key '{clientKey}': {valueError}";
                return false;
            }
            if (userColPgType.TryGetValue(pgName, out var userPgType)
                && !DynamicQueryBuilder.TryValidateUserFilterValue(userPgType, rawVal, out valueError))
            {
                filters = dict;
                error = $"Filter key '{clientKey}': {valueError}";
                return false;
            }
            // Multiple values on the same key collapse to the first — AC-3 does
            // not specify an array filter contract for v1.
            dict[pgName] = rawVal;
        }
        filters = dict;
        error = null;
        return true;
    }

    private static class Problems
    {
        internal static IResult TableNotProvisioned() =>
            Results.Problem(
                detail: "The underlying table has not been provisioned yet.",
                title: "Table not provisioned",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "TABLE_NOT_PROVISIONED",
                    ["messageKey"] = "errors.tableNotProvisioned",
                });

        // Story 6.2 — GET returned zero rows; distinct from TABLE_NOT_PROVISIONED
        // so the client can decide whether to redirect to provisioning or surface
        // a "record not found" message.
        internal static IResult RecordNotFound() =>
            Results.Problem(
                detail: "Record not found.",
                title: "Record not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "NOT_FOUND",
                    ["messageKey"] = "errors.notFound",
                });

        // Story 6.4 — PUT attempted against a soft-deleted record; client must
        // restore the record (Story 6.6) before retrying the update.
        internal static IResult RecordDeleted() =>
            Results.Problem(
                detail: "Record is soft-deleted. Restore it before updating.",
                title: "Record is soft-deleted",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "RECORD_DELETED",
                    ["messageKey"] = "errors.recordDeleted",
                });

        // Story 6.5 — DELETE attempted against an already soft-deleted record.
        // Distinct from RECORD_DELETED (422 on PUT) because the operation context
        // differs: PUT against a deleted record is conceptually blocked by the
        // deletion state, whereas DELETE against an already-deleted record is
        // a client error — the record is already in the desired state.
        internal static IResult RecordAlreadyDeleted() =>
            Results.Problem(
                detail: "Record is already soft-deleted.",
                title: "Record is already soft-deleted",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "RECORD_ALREADY_DELETED",
                    ["messageKey"] = "errors.recordAlreadyDeleted",
                });

        // Story 6.6 — PUT /restore attempted against a record that is not soft-deleted.
        // Distinct from the other 422 variants: the record exists but is already
        // active, so restore is a no-op client error.
        internal static IResult RecordNotDeleted() =>
            Results.Problem(
                detail: "Record is not soft-deleted; nothing to restore.",
                title: "Record is not soft-deleted",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "RECORD_NOT_DELETED",
                    ["messageKey"] = "errors.recordNotDeleted",
                });

        // Story 6.7 — PUT children array contained an id that does not correspond to
        // a non-deleted child row linked to this parent.
        internal static IResult ChildNotFound() =>
            Results.Problem(
                detail: "Child record not found.",
                title: "Child record not found",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "CHILD_NOT_FOUND",
                    ["messageKey"] = "errors.childNotFound",
                });

        internal static IResult ValidationFailed(string detail) =>
            Results.Problem(
                title: "Validation failed",
                detail: detail,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "VALIDATION_FAILED",
                    ["messageKey"] = "errors.validationFailed",
                });
    }
}

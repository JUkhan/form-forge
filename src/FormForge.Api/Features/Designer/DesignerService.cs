using System.Text.Json.Nodes;
using FormForge.Api.Common;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Designer.Dtos;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.EventBus;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Designer;

internal enum CreateDesignerOutcome { Success, DesignerExists, IdentifierInvalid, IdentifierReservedKeyword }

internal sealed record CreateDesignerResult(
    CreateDesignerOutcome Outcome,
    string? ErrorDetail = null,
    DesignerResponse? Designer = null);

internal enum SaveVersionOutcome
{
    Success,
    DesignerNotFound,
    FieldKeyValidationFailed,
    VersionConflict,
    ViewReferenceRejected,
}

internal sealed record SaveVersionResult(
    SaveVersionOutcome Outcome,
    IReadOnlyList<FieldKeyValidationError>? FieldKeyErrors = null,
    string? ViewReferenceDesignerId = null,    // non-null only for ViewReferenceRejected
    DesignerResponse? Designer = null);

internal enum UpdateVersionOutcome
{
    Success,
    DesignerNotFound,
    VersionNotFound,
    FieldKeyValidationFailed,
    ViewReferenceRejected,
}

internal sealed record UpdateVersionResult(
    UpdateVersionOutcome Outcome,
    IReadOnlyList<FieldKeyValidationError>? FieldKeyErrors = null,
    string? ViewReferenceDesignerId = null,    // non-null only for ViewReferenceRejected
    DesignerResponse? Designer = null);

internal enum DuplicateOutcome
{
    Success,
    DesignerNotFound,
    DuplicateConflict,  // all candidate _copy[N] suffixes are taken (extremely rare)
    SourceIdTooLong,    // even the shortest candidate ("_copy") would exceed the 63-char column cap
}

internal sealed record DuplicateResult(
    DuplicateOutcome Outcome,
    DesignerResponse? Designer = null);

internal enum UpdateVersionStatusOutcome
{
    Success,
    DesignerNotFound,
    VersionNotFound,
    StatusUnchanged,    // target status == current status — no write, no event
    PublishConflict,    // concurrent publish hit the partial unique index (rare)
}

internal sealed record UpdateVersionStatusResult(
    UpdateVersionStatusOutcome Outcome,
    DesignerResponse? Designer = null);

internal enum SetAuthFilterFieldKeyOutcome
{
    Success,
    DesignerNotFound,
    VersionNotFound,
    FieldKeyNotInVersion,   // the supplied fieldKey is not a user field on this version
}

internal sealed record SetAuthFilterFieldKeyResult(
    SetAuthFilterFieldKeyOutcome Outcome,
    DesignerResponse? Designer = null);

internal interface IDesignerService
{
    Task<CreateDesignerResult> CreateAsync(CreateDesignerRequest request, Guid createdBy, CancellationToken ct);
    Task<PagedResult<DesignerListItem>> ListAsync(
        int page, int pageSize, string? sort, string? search, string? status, string? mode, CancellationToken ct);
    Task<DesignerResponse?> GetLatestAsync(string designerId, CancellationToken ct);
    Task<DesignerResponse?> GetVersionAsync(string designerId, int version, CancellationToken ct);
    Task<SaveVersionResult> SaveVersionAsync(string designerId, JsonNode? rootElement, Guid savedBy, CancellationToken ct);
    Task<UpdateVersionResult> UpdateVersionAsync(
        string designerId, int version, JsonNode? rootElement, string displayName, Guid savedBy, CancellationToken ct);
    Task<UpdateVersionStatusResult> UpdateVersionStatusAsync(
        string designerId, int version, string newStatus, Guid updatedBy, CancellationToken ct);
    Task<SetAuthFilterFieldKeyResult> SetAuthFilterFieldKeyAsync(
        string designerId, int version, string? fieldKey, CancellationToken ct);
    Task<DuplicateResult> DuplicateAsync(string designerId, Guid createdBy, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class DesignerService(
    FormForgeDbContext db,
    IDomainEventBus eventBus,
    ISchemaRegistry schemaRegistry) : IDesignerService
{
    public async Task<CreateDesignerResult> CreateAsync(
        CreateDesignerRequest request,
        Guid createdBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SafeIdentifier.TryCreate(request.DesignerId, out var safeId, out var failureCode, out var idError))
        {
            var outcome = failureCode == SafeIdentifierError.ReservedKeyword
                ? CreateDesignerOutcome.IdentifierReservedKeyword
                : CreateDesignerOutcome.IdentifierInvalid;
            return new CreateDesignerResult(outcome, idError);
        }

        var designerId = safeId!.Value;

        var exists = await db.ComponentSchemas
            .AnyAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (exists)
        {
            return new CreateDesignerResult(CreateDesignerOutcome.DesignerExists);
        }

        var now = DateTimeOffset.UtcNow;
        var schema = new ComponentSchema
        {
            DesignerId = designerId,
            DisplayName = request.DisplayName.Trim(),
            Mode = request.Mode,   // FR-54 — persisted on component_schemas.mode
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        var version1 = new ComponentSchemaVersion
        {
            DesignerId = designerId,
            Version = 1,
            Status = "Draft",
            RootElement = null,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        db.ComponentSchemas.Add(schema);
        db.ComponentSchemaVersions.Add(version1);

        // Translate the race-window unique-violation back to the documented 409
        // outcome (same pattern as RoleService). Two concurrent POSTs with the
        // same designerId both pass AnyAsync and only the second SaveChanges
        // hits PK_component_schemas or uq_component_schema_versions_designer_version.
        // Filter by constraint name so a future tracked entity sharing this
        // SaveChanges with its own unique violation is not misreported here.
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && (string.Equals(pg.ConstraintName, "PK_component_schemas", StringComparison.Ordinal)
                || string.Equals(pg.ConstraintName, "uq_component_schema_versions_designer_version", StringComparison.Ordinal)))
        {
            return new CreateDesignerResult(CreateDesignerOutcome.DesignerExists);
        }

        var response = ToResponse(schema, version1, includeVersions: false);
        return new CreateDesignerResult(CreateDesignerOutcome.Success, Designer: response);
    }

    public async Task<PagedResult<DesignerListItem>> ListAsync(
        int page,
        int pageSize,
        string? sort,
        string? search,
        string? status,
        string? mode,
        CancellationToken ct)
    {
        IQueryable<ComponentSchema> query = db.ComponentSchemas;

        // Free-text filter on the two human-facing identifiers. ILike is the PG
        // case-insensitive LIKE; `%` / `_` in the term act as wildcards, which is
        // acceptable for a search box (the value is still parameterized).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.DesignerId, term)
                || EF.Functions.ILike(s.DisplayName, term));
        }

        // Status filter targets the LATEST version's status — the same computed
        // field the list surfaces. Ignore unrecognized values rather than 400-ing
        // so a stale URL never breaks the page.
        if (status is "Draft" or "Published" or "Archived")
        {
            query = query.Where(s =>
                s.Versions.OrderByDescending(v => v.Version).Select(v => v.Status).FirstOrDefault() == status);
        }

        // FR-54 AC-7: mode filter for picker queries (e.g. ?mode=CRUD from PropertyInspector).
        // Ignore unrecognized values so a stale URL never breaks the page.
        if (mode is "CRUD" or "VIEW")
        {
            query = query.Where(s => s.Mode == mode);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Same overflow guard as RoleService: a very large `page` would otherwise
        // overflow into a negative int and crash Skip with ArgumentOutOfRangeException.
        var skip = (int)Math.Min(int.MaxValue, ((long)page - 1L) * pageSize);

        // Order on the entity BEFORE projecting. EF can translate ORDER BY over a
        // correlated subquery (latest status/version) here, but NOT when ordering a
        // post-projection DTO whose members are themselves subqueries.
        var items = await ApplySort(query, sort)
            .Skip(skip)
            .Take(pageSize)
            .Select(s => new DesignerListItem(
                s.DesignerId,
                s.DisplayName,
                s.Mode,
                s.Versions
                    .OrderByDescending(v => v.Version)
                    .Select(v => v.Status)
                    .FirstOrDefault() ?? "Draft",
                s.Versions
                    .OrderByDescending(v => v.Version)
                    .Select(v => (int?)v.Version)
                    .FirstOrDefault() ?? 1,
                s.CreatedAt,
                s.UpdatedAt,
                // EF Core generates a LEFT JOIN to users via the ComponentSchema → User
                // FK navigation (configured in FormForgeDbContext with DeleteBehavior.SetNull),
                // so a deleted creator yields null rather than a row drop.
                s.Creator != null ? s.Creator.DisplayName : null))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<DesignerListItem>(items, total, page, pageSize);
    }

    // Whitelisted single-column sort ("field:dir"). Sorting on a switch of known
    // members (never a raw string interpolated into SQL) keeps this injection-safe.
    // DesignerId is appended as a stable tiebreaker so pagination stays
    // deterministic when the primary sort key is non-unique (status, updatedAt, …).
    // Unknown/empty/malformed sort falls back to designerId asc.
    private static IQueryable<ComponentSchema> ApplySort(IQueryable<ComponentSchema> q, string? sort)
    {
        var field = "designerId";
        var desc = false;
        if (!string.IsNullOrWhiteSpace(sort))
        {
            var parts = sort.Split(':');
            if (parts.Length == 2)
            {
                field = parts[0];
                desc = string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);
            }
        }

        return field switch
        {
            "displayName" => OrderBy(q, s => s.DisplayName, desc),
            "status" => OrderBy(
                q,
                s => s.Versions.OrderByDescending(v => v.Version).Select(v => v.Status).FirstOrDefault(),
                desc),
            "latestVersion" => OrderBy(
                q,
                s => s.Versions.OrderByDescending(v => v.Version).Select(v => (int?)v.Version).FirstOrDefault(),
                desc),
            "updatedAt" => OrderBy(q, s => s.UpdatedAt, desc),
            "createdAt" => OrderBy(q, s => s.CreatedAt, desc),
            "creator" => OrderBy(q, s => s.Creator != null ? s.Creator.DisplayName : null, desc),
            "designerId" => desc
                ? q.OrderByDescending(s => s.DesignerId)
                : q.OrderBy(s => s.DesignerId),
            _ => q.OrderBy(s => s.DesignerId),
        };
    }

    private static IQueryable<ComponentSchema> OrderBy<TKey>(
        IQueryable<ComponentSchema> q,
        System.Linq.Expressions.Expression<Func<ComponentSchema, TKey>> key,
        bool desc) =>
        (desc ? q.OrderByDescending(key) : q.OrderBy(key)).ThenBy(s => s.DesignerId);

    public async Task<DesignerResponse?> GetLatestAsync(string designerId, CancellationToken ct)
    {
        var schema = await db.ComponentSchemas
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (schema is null) return null;

        // A schema row with zero versions violates the create-time invariant
        // (every schema gets version 1). Surface it as 404 rather than synthesizing
        // a "Draft v1" response that hides the data-integrity break.
        var latest = schema.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
        if (latest is null) return null;

        return ToResponse(schema, latest, includeVersions: true);
    }

    public async Task<SaveVersionResult> SaveVersionAsync(
        string designerId,
        JsonNode? rootElement,
        Guid savedBy,
        CancellationToken ct)
    {
        // 1. Validate fieldKeys BEFORE hitting the DB. This both saves a round
        //    trip on bad input and ensures the SPA gets a precise per-element
        //    error list it can render inline.
        var fkResult = FieldKeyValidator.Validate(rootElement);
        if (!fkResult.IsValid)
        {
            return new SaveVersionResult(
                SaveVersionOutcome.FieldKeyValidationFailed,
                FieldKeyErrors: fkResult.Errors);
        }

        var schema = await db.ComponentSchemas
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (schema is null)
        {
            return new SaveVersionResult(SaveVersionOutcome.DesignerNotFound);
        }

        // FR-54 AC-5: reject a VIEW-mode reference in a Dropdown or Repeater.
        var viewRefError = await FindViewReferenceAsync(rootElement, db, ct).ConfigureAwait(false);
        if (viewRefError is not null)
        {
            return new SaveVersionResult(
                SaveVersionOutcome.ViewReferenceRejected,
                ViewReferenceDesignerId: viewRefError);
        }

        // DB-side MAX avoids materialising every historical version row (and
        // its RootElement payload) into memory just to compute the next axis
        // position. The `(int?)` projection lets MaxAsync return null on the
        // (post-3.2 invariant break) empty-versions case, which `?? 0` then
        // promotes to v1 instead of throwing.
        var maxVersion = await db.ComponentSchemaVersions
            .Where(v => v.DesignerId == designerId)
            .Select(v => (int?)v.Version)
            .MaxAsync(ct)
            .ConfigureAwait(false);
        var nextVersion = (maxVersion ?? 0) + 1;

        var now = DateTimeOffset.UtcNow;
        var newVer = new ComponentSchemaVersion
        {
            DesignerId = designerId,
            Version = nextVersion,
            Status = "Draft",
            // Persist as serialised JSON; the GET endpoint reads it back through
            // JsonNode.Parse via ToResponse, so the round-trip is lossless.
            RootElement = rootElement?.ToJsonString(),
            CreatedBy = savedBy,
            CreatedAt = now,
        };
        schema.UpdatedAt = now;
        db.ComponentSchemaVersions.Add(newVer);

        // Concurrent-save race: two admins POSTing simultaneously both compute
        // the same nextVersion. The unique constraint on (designer_id, version)
        // will fire on the second SaveChanges; surface it as VersionConflict
        // rather than a 500 so the SPA can prompt for a retry.
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && string.Equals(
                pg.ConstraintName,
                "uq_component_schema_versions_designer_version",
                StringComparison.Ordinal))
        {
            return new SaveVersionResult(SaveVersionOutcome.VersionConflict);
        }

        var response = ToResponse(schema, newVer, includeVersions: false);
        return new SaveVersionResult(SaveVersionOutcome.Success, Designer: response);
    }

    public async Task<UpdateVersionResult> UpdateVersionAsync(
        string designerId,
        int version,
        JsonNode? rootElement,
        string displayName,
        Guid savedBy,
        CancellationToken ct)
    {
        // savedBy is plumbed for the Story 5.x schema audit-log subscriber; the
        // version row has no UpdatedBy column yet (matches UpdateVersionStatusAsync).
        _ = savedBy;

        // Same fieldKey pre-check as SaveVersionAsync — fail before touching the DB
        // so the SPA gets the precise per-element error list.
        var fkResult = FieldKeyValidator.Validate(rootElement);
        if (!fkResult.IsValid)
        {
            return new UpdateVersionResult(
                UpdateVersionOutcome.FieldKeyValidationFailed,
                FieldKeyErrors: fkResult.Errors);
        }

        var schema = await db.ComponentSchemas
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (schema is null)
        {
            return new UpdateVersionResult(UpdateVersionOutcome.DesignerNotFound);
        }

        // FR-54 AC-5: reject a VIEW-mode reference in a Dropdown or Repeater.
        var viewRefError = await FindViewReferenceAsync(rootElement, db, ct).ConfigureAwait(false);
        if (viewRefError is not null)
        {
            return new UpdateVersionResult(
                UpdateVersionOutcome.ViewReferenceRejected,
                ViewReferenceDesignerId: viewRefError);
        }

        var target = await db.ComponentSchemaVersions
            .FirstOrDefaultAsync(
                v => v.DesignerId == designerId && v.Version == version,
                ct)
            .ConfigureAwait(false);

        if (target is null)
        {
            return new UpdateVersionResult(UpdateVersionOutcome.VersionNotFound);
        }

        var now = DateTimeOffset.UtcNow;
        // In-place overwrite — no new version row. RootElement is re-serialised the
        // same way SaveVersionAsync persists it (lossless JsonNode round-trip).
        target.RootElement = rootElement?.ToJsonString();
        // DisplayName lives on the schema, so a save here is what persists a rename.
        schema.DisplayName = displayName.Trim();
        schema.UpdatedAt = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // In-place overwrite reuses the SAME (designerId, version) key, so any cached
        // SchemaRegistryEntry for this designer is now stale — its Columns/DerivedColumns
        // reflect the pre-save RootElement. Drop every cached version for the designer so
        // the next record-list/CRUD request re-parses the fresh RootElement (this is what
        // makes an edited Dropdown's derived label column appear without a re-provision or
        // an API restart). Publish/SaveVersion don't need this: publish re-provisions via
        // SchemaPublished, and SaveVersion writes a brand-new version (a fresh cache key).
        schemaRegistry.InvalidateDesigner(designerId);

        var response = ToResponse(schema, target, includeVersions: false);
        return new UpdateVersionResult(UpdateVersionOutcome.Success, Designer: response);
    }

    public async Task<UpdateVersionStatusResult> UpdateVersionStatusAsync(
        string designerId,
        int version,
        string newStatus,
        Guid updatedBy,
        CancellationToken ct)
    {
        // updatedBy is plumbed for the Story 5.x schema audit-log subscriber;
        // ComponentSchemaVersion has no UpdatedBy column yet.
        _ = updatedBy;

        // Load schema + all versions in one round trip so we can locate the target
        // under change tracking. Version counts per designer are bounded by save
        // frequency, so the materialization is cheap.
        var schema = await db.ComponentSchemas
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (schema is null)
        {
            return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.DesignerNotFound);
        }

        var target = schema.Versions.FirstOrDefault(v => v.Version == version);
        if (target is null)
        {
            return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.VersionNotFound);
        }

        // AC-4: same-status no-op — return the current state without a DB write
        // and without firing SchemaPublished. The endpoint maps StatusUnchanged
        // to 200 OK with the current DesignerResponse.
        if (string.Equals(target.Status, newStatus, StringComparison.Ordinal))
        {
            return new UpdateVersionStatusResult(
                UpdateVersionStatusOutcome.StatusUnchanged,
                Designer: ToResponse(schema, target, includeVersions: false));
        }

        var now = DateTimeOffset.UtcNow;

        // A designer may have MULTIPLE Published versions at once. Publishing a
        // version no longer demotes the others — Archive is the explicit way to
        // retire a published version. This is a single status flip (the
        // at-most-one-Published partial unique index has been dropped).
        if (string.Equals(newStatus, "Published", StringComparison.Ordinal))
        {
            target.Status = "Published";
            target.PublishedAt = now;
        }
        else // "Archived" — Draft → Archived and Published → Archived both flow here
        {
            target.Status = "Archived";
        }
        schema.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Fire SchemaPublished only AFTER the write succeeds so a failed save
        // never leaks a phantom event to subscribers (e.g. the schema registry).
        if (string.Equals(newStatus, "Published", StringComparison.Ordinal))
        {
            eventBus.Publish(new SchemaPublished(designerId, version));
        }

        var response = ToResponse(schema, target, includeVersions: false);
        return new UpdateVersionStatusResult(UpdateVersionStatusOutcome.Success, Designer: response);
    }

    public async Task<SetAuthFilterFieldKeyResult> SetAuthFilterFieldKeyAsync(
        string designerId,
        int version,
        string? fieldKey,
        CancellationToken ct)
    {
        var schema = await db.ComponentSchemas
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);
        if (schema is null)
        {
            return new SetAuthFilterFieldKeyResult(SetAuthFilterFieldKeyOutcome.DesignerNotFound);
        }

        var target = await db.ComponentSchemaVersions
            .FirstOrDefaultAsync(v => v.DesignerId == designerId && v.Version == version, ct)
            .ConfigureAwait(false);
        if (target is null)
        {
            return new SetAuthFilterFieldKeyResult(SetAuthFilterFieldKeyOutcome.VersionNotFound);
        }

        // Normalize: blank clears the filter; otherwise the value must be a user
        // fieldKey declared on THIS version's RootElement (the combobox only offers
        // valid keys, but the server is the independent guard).
        var normalized = string.IsNullOrWhiteSpace(fieldKey) ? null : fieldKey.Trim();
        if (normalized is not null)
        {
            var columns = RootElementParser.Parse(target.RootElement);
            var known = columns.Any(c => string.Equals(c.ColumnName, normalized, StringComparison.Ordinal));
            if (!known)
            {
                return new SetAuthFilterFieldKeyResult(SetAuthFilterFieldKeyOutcome.FieldKeyNotInVersion);
            }
        }

        target.AuthFilterFieldKey = normalized;
        schema.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var response = ToResponse(schema, target, includeVersions: false);
        return new SetAuthFilterFieldKeyResult(SetAuthFilterFieldKeyOutcome.Success, Designer: response);
    }

    public async Task<DesignerResponse?> GetVersionAsync(string designerId, int version, CancellationToken ct)
    {
        // Schema must still exist even if the version doesn't — distinguish a
        // missing designer from a missing version by loading them separately so
        // we can return 404 with the appropriate code on either condition.
        var schema = await db.ComponentSchemas
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (schema is null) return null;

        var ver = await db.ComponentSchemaVersions
            .FirstOrDefaultAsync(
                v => v.DesignerId == designerId && v.Version == version,
                ct)
            .ConfigureAwait(false);

        if (ver is null) return null;

        return ToResponse(schema, ver, includeVersions: false);
    }

    public async Task<DuplicateResult> DuplicateAsync(
        string designerId,
        Guid createdBy,
        CancellationToken ct)
    {
        var source = await db.ComponentSchemas
            .Include(s => s.Versions)
            .FirstOrDefaultAsync(s => s.DesignerId == designerId, ct)
            .ConfigureAwait(false);

        if (source is null)
        {
            return new DuplicateResult(DuplicateOutcome.DesignerNotFound);
        }

        // Latest version's rootElement is the basis for the copy. A schema with
        // zero versions (post-3.2 invariant break) yields a null rootElement,
        // which copies to an empty-canvas v1 — same shape as a fresh CreateAsync.
        var latestVersion = source.Versions
            .OrderByDescending(v => v.Version)
            .FirstOrDefault();
        var rootElementJson = latestVersion?.RootElement;

        // Probe candidate designerIds {id}_copy, {id}_copy2 … {id}_copy9 and
        // pick the first untaken slot. The 63-char ComponentSchema.DesignerId
        // limit means very long source IDs produce candidates that exceed the
        // column length — filter those out up front. If even the shortest
        // candidate ("_copy") would exceed 63 chars (source ≥59 chars) we
        // surface SourceIdTooLong instead of the misleading "too many copies"
        // DuplicateConflict.
        var candidates = new[] { $"{designerId}_copy" }
            .Concat(Enumerable.Range(2, 8).Select(i => $"{designerId}_copy{i}"))
            .Where(c => c.Length <= 63)
            .ToList();

        if (candidates.Count == 0)
        {
            return new DuplicateResult(DuplicateOutcome.SourceIdTooLong);
        }

        var now = DateTimeOffset.UtcNow;

        // Try each candidate; on the concurrent-duplicate race (two admins both
        // pass AnyAsync for the same slot, second SaveChanges hits the
        // PK_component_schemas unique violation) advance to the next candidate
        // instead of returning DuplicateConflict. The SPA does not retry on
        // 409, so the backend must walk the list itself.
        foreach (var candidate in candidates)
        {
            var taken = await db.ComponentSchemas
                .AnyAsync(s => s.DesignerId == candidate, ct)
                .ConfigureAwait(false);
            if (taken) continue;

            var newSchema = new ComponentSchema
            {
                DesignerId = candidate,
                DisplayName = $"Copy of {source.DisplayName}",
                Mode = source.Mode,   // duplicate inherits the source's mode (FR-54 AC-2 — immutable, copy as-is)
                CreatedBy = createdBy,
                CreatedAt = now,
            };
            var newV1 = new ComponentSchemaVersion
            {
                DesignerId = candidate,
                Version = 1,
                Status = "Draft",
                RootElement = rootElementJson,
                CreatedBy = createdBy,
                CreatedAt = now,
            };
            db.ComponentSchemas.Add(newSchema);
            db.ComponentSchemaVersions.Add(newV1);

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new DuplicateResult(
                    DuplicateOutcome.Success,
                    Designer: ToResponse(newSchema, newV1, includeVersions: false));
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: "23505" } pg
                && string.Equals(pg.ConstraintName, "PK_component_schemas", StringComparison.Ordinal))
            {
                // Concurrent admin took this slot. Detach our tracked entities
                // and try the next candidate.
                db.Entry(newV1).State = EntityState.Detached;
                db.Entry(newSchema).State = EntityState.Detached;
            }
        }

        return new DuplicateResult(DuplicateOutcome.DuplicateConflict);
    }

    // FR-54 AC-5 — Returns the first VIEW-mode designerId referenced by a
    // Repeater.rowDesignerId or designer-backed Dropdown.optionsDesignerId in the
    // authored RootElement, or null if no VIEW reference is present. The frontend
    // pickers already exclude VIEW components; this is the independent server guard.
    private static async Task<string?> FindViewReferenceAsync(
        JsonNode? rootElement, FormForgeDbContext db, CancellationToken ct)
    {
        if (rootElement is null) return null;

        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        CollectDesignerRefs(rootElement, referencedIds);
        if (referencedIds.Count == 0) return null;

        return await db.ComponentSchemas
            .AsNoTracking()
            .Where(s => referencedIds.Contains(s.DesignerId) && s.Mode == "VIEW")
            .Select(s => s.DesignerId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private static void CollectDesignerRefs(JsonNode node, HashSet<string> ids)
    {
        if (node is JsonObject obj)
        {
            var t = obj["type"]?.GetValue<string>();
            if (obj["properties"] is JsonObject props)
            {
                if (t == "Repeater")
                {
                    var rowId = props["rowDesignerId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(rowId)) ids.Add(rowId);
                }
                if (t == "Dropdown")
                {
                    var src = props["optionsSource"]?.GetValue<string>();
                    var optId = props["optionsDesignerId"]?.GetValue<string>();
                    if (src == "designer" && !string.IsNullOrEmpty(optId)) ids.Add(optId);
                }
            }

            if (obj["children"] is JsonArray children)
            {
                foreach (var child in children)
                {
                    if (child is not null) CollectDesignerRefs(child, ids);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            // Handle a JsonArray at any tree level (e.g. a top-level array or an
            // intermediate array property not named "children").
            foreach (var item in arr)
            {
                if (item is not null) CollectDesignerRefs(item, ids);
            }
        }
    }

    private static DesignerResponse ToResponse(
        ComponentSchema schema,
        ComponentSchemaVersion version,
        bool includeVersions)
    {
        // Whitespace check (not just empty) so a manually-injected " " RootElement
        // does not crash JsonNode.Parse. PG jsonb rejects this on write today, but
        // the guard costs nothing and survives future code paths that bypass jsonb
        // validation.
        JsonNode? rootNode = null;
        if (!string.IsNullOrWhiteSpace(version.RootElement))
        {
            rootNode = JsonNode.Parse(version.RootElement);
        }

        IReadOnlyList<DesignerVersionSummary>? versions = null;
        if (includeVersions && schema.Versions.Count > 0)
        {
            versions = schema.Versions
                .OrderBy(v => v.Version)
                .Select(v => new DesignerVersionSummary(v.Version, v.Status, v.CreatedAt, v.PublishedAt))
                .ToList();
        }

        return new DesignerResponse(
            schema.DesignerId,
            schema.DisplayName,
            schema.Mode,
            version.Status,
            version.Version,
            rootNode,
            schema.CreatedAt,
            schema.UpdatedAt,
            version.PublishedAt,
            version.AuthFilterFieldKey,
            versions);
    }
}

using Dapper;
using FormForge.Api.Common;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.Provisioning.Dtos;
using FormForge.Api.Features.SchemaRegistry;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Provisioning;

internal enum ProvisionTableOutcome
{
    Success,
    DesignerNotFound,
    NotCrudMode,
    VersionRequired,
    VersionNotFound,
    VersionNotPublished,
    UnsafeIdentifier,
    RepeaterCycle,
    InvalidPgType,
}

// Detail carries a human-readable message for InvalidPgType (which field / why);
// null for the other outcomes whose messageKey alone is sufficient.
internal sealed record ProvisionTableResult(ProvisionTableOutcome Outcome, string? Detail = null);

// Admin "Table Provisioned" tab (no menu binding). Lists CRUD Designers with
// derived provisioning state (table existence + latest schema_audit_log row) and
// enqueues a menu-less ProvisioningJob to create or sync the physical table. The
// pre-enqueue validations mirror MenuService.BindDesignerAsync exactly so the two
// provisioning entry points stay consistent.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class TableProvisioningService(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    CycleDetector cycleDetector,
    IProvisioningService provisioning)
{
    public async Task<PagedResult<TableProvisioningItem>> ListAsync(
        int page, int pageSize, string? search, CancellationToken ct)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 25;
        if (pageSize > 100) pageSize = 100;

        // 1. All CRUD designers (VIEW-mode components never provision a table),
        // optionally filtered by a case-insensitive substring of the display name
        // OR the designer id. ILIKE matches the existing dataset search convention
        // (DatasetService.ListAsync). The term is not escaped — '%'/'_' in a query
        // act as wildcards, same trade-off as the dataset search.
        var baseQuery = db.ComponentSchemas
            .AsNoTracking()
            .Where(c => c.Mode == "CRUD");

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            baseQuery = baseQuery.Where(c =>
                EF.Functions.ILike(c.DisplayName, pattern) || EF.Functions.ILike(c.DesignerId, pattern));
        }

        var total = await baseQuery.LongCountAsync(ct).ConfigureAwait(false);

        // Stable ordering across pages: DisplayName then DesignerId (unique tiebreak).
        var designers = await baseQuery
            .OrderBy(c => c.DisplayName)
            .ThenBy(c => c.DesignerId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new { c.DesignerId, c.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (designers.Count == 0)
            return new PagedResult<TableProvisioningItem>([], total, page, pageSize);

        var designerIds = designers.Select(d => d.DesignerId).ToList();

        // 2. Versions per designer (for the picker + latest-version display).
        var versionRows = await db.ComponentSchemaVersions
            .AsNoTracking()
            .Where(v => designerIds.Contains(v.DesignerId))
            .Select(v => new { v.DesignerId, v.Version, v.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var versionsByDesigner = versionRows
            .GroupBy(v => v.DesignerId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Published = g.Where(v => v.Status == "Published")
                                 .Select(v => v.Version)
                                 .OrderByDescending(v => v)
                                 .ToList(),
                    Latest = g.Max(v => (int?)v.Version),
                },
                StringComparer.Ordinal);

        // 3. Latest CREATE|ALTER audit row per designer (drives the derived status).
        var auditRows = await db.SchemaAuditLog
            .AsNoTracking()
            .Where(a => designerIds.Contains(a.DesignerId)
                        && (a.DdlOperation == "CREATE" || a.DdlOperation == "ALTER"))
            .Select(a => new { a.DesignerId, a.ToVersion, a.DdlOperation, a.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var latestAuditByDesigner = auditRows
            .GroupBy(a => a.DesignerId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.CreatedAt).First(),
                StringComparer.Ordinal);

        // 4. Which physical tables actually exist (one round-trip for all of them).
        var existingTables = await GetExistingTableNamesAsync(ct).ConfigureAwait(false);

        var items = designers
            .Select(d =>
            {
                versionsByDesigner.TryGetValue(d.DesignerId, out var v);
                latestAuditByDesigner.TryGetValue(d.DesignerId, out var audit);
                // table name == designerId (a validated SafeIdentifier).
                var isProvisioned = existingTables.Contains(d.DesignerId);

                return new TableProvisioningItem(
                    d.DesignerId,
                    d.DisplayName,
                    d.DesignerId,
                    isProvisioned,
                    v?.Published ?? [],
                    v?.Latest,
                    audit?.ToVersion,
                    audit?.DdlOperation,
                    audit?.CreatedAt);
            })
            // Preserve the DB page order (DisplayName, DesignerId) — do NOT re-sort
            // here or rows would shuffle relative to the server's paging window.
            .ToList();

        return new PagedResult<TableProvisioningItem>(items, total, page, pageSize);
    }

    public async Task<ProvisionTableResult> ProvisionAsync(
        string designerId, int? version, Guid? actorId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(designerId);

        if (version is null)
            return new ProvisionTableResult(ProvisionTableOutcome.VersionRequired);

        // Mode lives on the root component, not the version. VIEW-mode is display-only
        // and is never provisioned (FR-54). A missing schema row => DesignerNotFound.
        var mode = await db.ComponentSchemas
            .AsNoTracking()
            .Where(c => c.DesignerId == designerId)
            .Select(c => (string?)c.Mode)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (mode is null)
            return new ProvisionTableResult(ProvisionTableOutcome.DesignerNotFound);
        if (mode == "VIEW")
            return new ProvisionTableResult(ProvisionTableOutcome.NotCrudMode);

        var schemaVersion = await db.ComponentSchemaVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.DesignerId == designerId && v.Version == version.Value, ct)
            .ConfigureAwait(false);

        if (schemaVersion is null)
            return new ProvisionTableResult(ProvisionTableOutcome.VersionNotFound);
        if (schemaVersion.Status != "Published")
            return new ProvisionTableResult(ProvisionTableOutcome.VersionNotPublished);

        // Defence-in-depth: the table is named after the designerId, so it must be a
        // safe identifier before any DDL is emitted (DdlEmitter re-checks too).
        if (!SafeIdentifier.TryCreate(designerId, out _, out _))
            return new ProvisionTableResult(ProvisionTableOutcome.UnsafeIdentifier);

        // Same pre-provision guards as a menu bind: reject a Repeater cycle and any
        // malformed authored pgType BEFORE enqueueing, so a bad designer never emits
        // half a schema (symmetric with BindDesignerAsync — Stories 5.5 / B).
        if (await cycleDetector.HasCycleAsync(designerId, version.Value, ct).ConfigureAwait(false))
            return new ProvisionTableResult(ProvisionTableOutcome.RepeaterCycle);

        var invalidPgType = DesignerPgTypeValidator.FindFirstInvalid(schemaVersion.RootElement);
        if (invalidPgType is not null)
            return new ProvisionTableResult(
                ProvisionTableOutcome.InvalidPgType,
                $"Field '{invalidPgType.FieldKey}' has an invalid type '{invalidPgType.PgType}': {invalidPgType.Message}");

        // Menu-less job: MenuId null. FromVersion is left null — like a Retry, the
        // standalone path doesn't track the previous version; the audit row records
        // CREATE (new table) or ALTER (sync) based on live table existence.
        await provisioning
            .EnqueueAsync(
                new ProvisioningJob(MenuId: null, designerId, version.Value, actorId),
                CancellationToken.None)
            .ConfigureAwait(false);

        return new ProvisionTableResult(ProvisionTableOutcome.Success);
    }

    // Names of every base table in the public schema. Designer tables are named
    // after their designerId, so membership == "table is provisioned".
    private async Task<HashSet<string>> GetExistingTableNamesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            """;

        var connection = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var names = await connection
                .QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))
                .ConfigureAwait(false);
            return new HashSet<string>(names, StringComparer.Ordinal);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

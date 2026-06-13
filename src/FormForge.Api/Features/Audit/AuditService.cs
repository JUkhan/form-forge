using System.Diagnostics.CodeAnalysis;
using FormForge.Api.Common;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Features.Audit.Dtos;
using FormForge.Api.Features.Designer;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FormForge.Api.Features.Audit;

// Story 5.7 — pure-read service over schema_audit_log. EF reads only (no DDL).
// Returns null when the supplied designerId is not a valid identifier so the
// caller can map to HTTP 404 with code "DESIGNER_NOT_FOUND" (AC-1). An empty
// page (data: []) is the correct response for a syntactically valid designerId
// with no audit rows — NOT a 404.
[SuppressMessage("Performance", "CA1812", Justification = "Registered via DI.")]
internal sealed class AuditService(FormForgeDbContext db)
{
    public async Task<PagedResult<SchemaAuditEntryDto>?> GetSchemaAuditLogAsync(
        string designerId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (!SafeIdentifier.TryCreate(designerId, out _, out _))
            return null;

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var baseQuery = db.SchemaAuditLog
            .AsNoTracking()
            .Where(a => a.DesignerId == designerId)
            .OrderByDescending(a => a.CreatedAt);

        var total = await baseQuery.LongCountAsync(ct).ConfigureAwait(false);
        var entries = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Batch-resolve actor names: distinct non-null ActorIds → DisplayName lookup.
        // A separate SELECT instead of a JOIN so audit rows for deleted actors still
        // appear (left-join semantics with no users-table presence required).
        var actorIds = entries
            .Where(e => e.ActorId.HasValue)
            .Select(e => e.ActorId!.Value)
            .Distinct()
            .ToList();

        var actorNames = actorIds.Count > 0
            ? await db.Users
                .AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct)
                .ConfigureAwait(false)
            : new Dictionary<Guid, string>();

        var data = entries.Select(a => new SchemaAuditEntryDto(
            a.Id,
            a.CreatedAt,
            a.ActorId,
            a.ActorId.HasValue && actorNames.TryGetValue(a.ActorId.Value, out var n) ? n : null,
            a.DesignerId,
            a.FromVersion,
            a.ToVersion,
            a.DdlOperation,
            a.ColumnsAdded,
            a.ColumnsDropped,
            a.ColumnsDiff,
            a.CorrelationId,
            a.Notes))
            .ToList();

        return new PagedResult<SchemaAuditEntryDto>(data, total, page, pageSize);
    }

    // Story 6.8 — paginated read over mutation_audit_log. Mirrors GetSchemaAuditLogAsync:
    // null return on bad designerId → 404, empty page for valid designerId with no rows,
    // left-join actor-name resolution. Order by Timestamp DESC (entity uses Timestamp,
    // not CreatedAt — see Dev Notes §2 for the trap).
    public async Task<PagedResult<MutationAuditEntryDto>?> GetMutationAuditLogAsync(
        string designerId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (!SafeIdentifier.TryCreate(designerId, out _, out _))
            return null;

        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 25;

        var baseQuery = db.MutationAuditLog
            .AsNoTracking()
            .Where(a => a.DesignerId == designerId)
            .OrderByDescending(a => a.Timestamp);

        var total = await baseQuery.LongCountAsync(ct).ConfigureAwait(false);
        var entries = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var actorIds = entries
            .Where(e => e.ActorId.HasValue)
            .Select(e => e.ActorId!.Value)
            .Distinct()
            .ToList();

        var actorNames = actorIds.Count > 0
            ? await db.Users
                .AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct)
                .ConfigureAwait(false)
            : new Dictionary<Guid, string>();

        var data = entries.Select(a => new MutationAuditEntryDto(
            a.Id,
            a.Timestamp,
            a.ActorId,
            a.ActorId.HasValue && actorNames.TryGetValue(a.ActorId.Value, out var n) ? n : null,
            a.DesignerId,
            a.RecordId,
            a.Operation,
            a.PreviousValues,
            a.NewValues,
            a.CorrelationId))
            .ToList();

        return new PagedResult<MutationAuditEntryDto>(data, total, page, pageSize);
    }

    // Story 8.9 (FR-61 / AR-65) — paginated read over dataset_audit_log. Optional
    // datasetName and operation filters are ANDed when both are supplied. Returns an
    // empty page (data: []) when no rows match — never null/404 because the log is
    // global (not scoped to a single dataset). ActorName is stored directly in the
    // audit row, so no batch-resolve join is needed (unlike schema/mutation audit).
    // operation is normalized to UPPER before comparing the DB CHECK constraint values.
    public async Task<PagedResult<DatasetAuditEntryDto>> GetDatasetAuditLogAsync(
        string? datasetName,
        string? operation,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        IQueryable<DatasetAuditLogEntry> query = db.DatasetAuditLog.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(datasetName))
            query = query.Where(a => a.DatasetName == datasetName);

        if (!string.IsNullOrWhiteSpace(operation))
        {
            var normalizedOp = operation.ToUpperInvariant();
            query = query.Where(a => a.Operation == normalizedOp);
        }

        var orderedQuery = query.OrderByDescending(a => a.Timestamp);

        var total = await orderedQuery.LongCountAsync(ct).ConfigureAwait(false);
        var entries = await orderedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var data = entries
            .Select(a => new DatasetAuditEntryDto(
                a.Id,
                a.Timestamp,
                a.ActorId,
                a.ActorName,
                a.DatasetName,
                a.Operation,
                a.PreviousValues,
                a.NewValues,
                a.Ddl,
                a.Succeeded,
                a.CorrelationId))
            .ToList();

        return new PagedResult<DatasetAuditEntryDto>(data, total, page, pageSize);
    }
}

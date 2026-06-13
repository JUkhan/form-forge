using System.Text.Json;
using Dapper;
using FormForge.Api.Features.Designer;
using FormForge.Api.Features.SchemaRegistry;
using Npgsql;

namespace FormForge.Api.Features.DynamicCrud;

// Story 6.7 — orchestrates transactional nested writes for POST and PUT
// /api/data/{parentDesignerId}[/{id}] when the payload includes a `children` key.
// Design principles:
//   - Validation (payload type-checking, id existence) runs BEFORE any DB connection
//     opens, so early 422/404 returns never need a rollback.
//   - All DB writes run within a caller-supplied NpgsqlTransaction.
//   - Audit entries are assembled in-memory and returned to the handler for EF
//     persistence after the Dapper tx commits (Decision 1.6).
internal static class RepeaterWriteCoordinator
{
    // One audit entry per record touched (INSERT, UPDATE, or SOFT_DELETE).
    internal sealed record WriteAuditEntry(
        string DesignerId,
        Guid RecordId,
        string Operation,
        string? NewValuesJson,
        string? PreviousValuesJson,
        DateTimeOffset Timestamp);

    // Parsed child descriptor after validation.
    internal sealed record ChildRecord(
        Guid? ExistingId,
        IReadOnlyDictionary<string, object?> CoercedValues);

    // Extracts the `children` property from a request body JsonElement.
    // Returns an empty dict when the key is absent, the body is not an object,
    // or `children` is not a JSON object — enabling backward-compatible no-op.
    internal static Dictionary<string, JsonElement> ParseChildrenElement(JsonElement body)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object) return result;
        if (!body.TryGetProperty("children", out var childrenEl)
            || childrenEl.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in childrenEl.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
                result[prop.Name] = prop.Value;
        }
        return result;
    }

    // Validates and parses raw child JSON arrays into typed ChildRecord lists.
    // Each child element is validated via payloadValidator against its schema columns.
    // If any validation fails, validationError is set and the method returns false.
    // `id` in child elements is extracted before validation (system column, not in
    // userColumns, so the validator would silently drop it — we must read it first).
    internal static bool TryParseAndValidateChildren(
        Dictionary<string, JsonElement> childrenByDesignerId,
        Dictionary<string, SchemaRegistryEntry> childSchemas,
        IDynamicPayloadValidator payloadValidator,
        out Dictionary<string, List<ChildRecord>> parsedChildren,
        out IResult? validationError)
    {
        ArgumentNullException.ThrowIfNull(childrenByDesignerId);
        ArgumentNullException.ThrowIfNull(childSchemas);
        ArgumentNullException.ThrowIfNull(payloadValidator);

        parsedChildren = new Dictionary<string, List<ChildRecord>>(StringComparer.Ordinal);
        validationError = null;

        foreach (var (designerId, arrayEl) in childrenByDesignerId)
        {
            if (!childSchemas.TryGetValue(designerId, out var schema)) continue;
            var childList = new List<ChildRecord>();
            var idx = 0;

            foreach (var element in arrayEl.EnumerateArray())
            {
                // Extract `id` before the validator sees it — it is a system column
                // that the validator would silently drop if left in the element.
                Guid? existingId = null;
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idProp.GetString(), out var parsedId))
                {
                    existingId = parsedId;
                }

                var validation = payloadValidator.Validate(element, schema.Columns);
                if (!validation.IsValid)
                {
                    // Prefix field paths with child context so the client knows
                    // which record in which designer array failed.
                    var prefixed = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    foreach (var (k, v) in validation.FieldErrors)
                        prefixed[$"children[{designerId}][{idx}].{k}"] = v;

                    validationError = Results.ValidationProblem(
                        prefixed,
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["code"] = "VALIDATION_FAILED",
                            ["messageKey"] = "errors.validationFailed",
                        });
                    return false;
                }

                childList.Add(new ChildRecord(existingId, validation.CoercedValues));
                idx++;
            }

            parsedChildren[designerId] = childList;
        }

        return true;
    }

    // Inserts all parsed child records (POST path). Called within the
    // caller-supplied NpgsqlTransaction. parentDesignerId is used to derive
    // the FK column name via BuildFkColumnName. Returns audit entries for the
    // handler to persist via EF after commit.
    internal static async Task<IReadOnlyList<WriteAuditEntry>> InsertChildrenAsync(
        string parentDesignerId,
        Guid parentId,
        Dictionary<string, List<ChildRecord>> parsedChildren,
        Dictionary<string, SchemaRegistryEntry> childSchemas,
        Guid? actorId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentDesignerId);
        ArgumentNullException.ThrowIfNull(parsedChildren);
        ArgumentNullException.ThrowIfNull(childSchemas);
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);

        var auditEntries = new List<WriteAuditEntry>();
        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

        foreach (var (designerId, childList) in parsedChildren)
        {
            if (!childSchemas.TryGetValue(designerId, out var schema)) continue;
            if (!SafeIdentifier.TryCreate(designerId, out var childSafeId, out _)) continue;

            foreach (var child in childList)
            {
                var values = StripReservedFkColumn(child.CoercedValues, fkColumnName);
                var (sql, parameters, newId, insertedAt) = DynamicQueryBuilder.BuildChildInsertQuery(
                    childSafeId!, schema.Columns, values, fkColumnName, parentId, actorId);

                await conn.ExecuteAsync(new CommandDefinition(
                    sql, parameters, transaction: tx,
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                auditEntries.Add(new WriteAuditEntry(
                    designerId, newId, "CREATE",
                    values.Count > 0 ? JsonSerializer.Serialize(values) : null,
                    null,
                    insertedAt));
            }
        }

        return auditEntries;
    }

    // UPSERT + prune child records (PUT path). Called within the caller-supplied
    // NpgsqlTransaction. Relies on pre-flight existingChildIdsByDesignerId computed
    // by the handler before the transaction opened (SELECT by FK, is_deleted=false).
    // INSERT children with no ExistingId; UPDATE children with ExistingId present in
    // existingChildIds; SOFT-DELETE existing children whose IDs are absent from the
    // submitted set. Returns audit entries for EF persistence after commit.
    // PRECONDITION: The handler has already verified that every ChildRecord.ExistingId
    // appears in existingChildIdsByDesignerId (i.e., AC-12 check passed pre-flight).
    internal static async Task<IReadOnlyList<WriteAuditEntry>> UpsertAndPruneChildrenAsync(
        string parentDesignerId,
        Guid parentId,
        Dictionary<string, List<ChildRecord>> parsedChildren,
        Dictionary<string, HashSet<Guid>> existingChildIdsByDesignerId,
        Dictionary<string, SchemaRegistryEntry> childSchemas,
        Guid? actorId,
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentDesignerId);
        ArgumentNullException.ThrowIfNull(parsedChildren);
        ArgumentNullException.ThrowIfNull(existingChildIdsByDesignerId);
        ArgumentNullException.ThrowIfNull(childSchemas);
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);

        var auditEntries = new List<WriteAuditEntry>();
        var fkColumnName = DynamicQueryBuilder.BuildFkColumnName(parentDesignerId);

        foreach (var (designerId, childList) in parsedChildren)
        {
            if (!childSchemas.TryGetValue(designerId, out var schema)) continue;
            if (!SafeIdentifier.TryCreate(designerId, out var childSafeId, out _)) continue;

            existingChildIdsByDesignerId.TryGetValue(designerId, out var existingIds);
            existingIds ??= [];

            var toUpdate = childList.Where(c => c.ExistingId.HasValue).ToList();
            var toInsert = childList.Where(c => !c.ExistingId.HasValue).ToList();
            var submittedIds = toUpdate.Select(c => c.ExistingId!.Value).ToHashSet();

            // INSERT new children (no ExistingId)
            foreach (var child in toInsert)
            {
                var values = StripReservedFkColumn(child.CoercedValues, fkColumnName);
                var (sql, parameters, newId, insertedAt) = DynamicQueryBuilder.BuildChildInsertQuery(
                    childSafeId!, schema.Columns, values, fkColumnName, parentId, actorId);

                await conn.ExecuteAsync(new CommandDefinition(
                    sql, parameters, transaction: tx,
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                auditEntries.Add(new WriteAuditEntry(
                    designerId, newId, "CREATE",
                    values.Count > 0 ? JsonSerializer.Serialize(values) : null,
                    null, insertedAt));
            }

            // UPDATE existing children — capture previousValues from a SELECT first
            foreach (var child in toUpdate)
            {
                var values = StripReservedFkColumn(child.CoercedValues, fkColumnName);
                var (getPrevSql, getPrevParams) = DynamicQueryBuilder.BuildGetByIdQuery(
                    childSafeId!, schema.Columns, child.ExistingId!.Value);
                var rawPrevRows = await conn.QueryAsync(new CommandDefinition(
                    getPrevSql, getPrevParams, transaction: tx,
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                var prevRow = rawPrevRows
                    .Cast<IDictionary<string, object>>()
                    .Select(r => r.ToDictionary(k => k.Key, k => (object?)k.Value, StringComparer.Ordinal))
                    .FirstOrDefault();

                var previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (prevRow != null)
                {
                    foreach (var colName in values.Keys)
                        previousValues[colName] = prevRow.TryGetValue(colName, out var prev) ? prev : null;
                }

                var (updateSql, updateParams, updatedAt) = DynamicQueryBuilder.BuildUpdateQuery(
                    childSafeId!, schema.Columns, values, child.ExistingId!.Value, actorId);

                await conn.ExecuteAsync(new CommandDefinition(
                    updateSql, updateParams, transaction: tx,
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                auditEntries.Add(new WriteAuditEntry(
                    designerId, child.ExistingId.Value, "UPDATE",
                    values.Count > 0 ? JsonSerializer.Serialize(values) : null,
                    previousValues.Count > 0 ? JsonSerializer.Serialize(previousValues) : null,
                    updatedAt));
            }

            // SOFT-DELETE omitted children (cascade_event_id = NULL — individual soft-delete)
            var toSoftDelete = existingIds.Except(submittedIds).ToList();
            foreach (var omittedId in toSoftDelete)
            {
                var (deleteSql, deleteParams, deletedAt) = DynamicQueryBuilder.BuildSoftDeleteByIdQuery(
                    childSafeId!, omittedId, actorId, cascadeEventId: null);

                await conn.ExecuteAsync(new CommandDefinition(
                    deleteSql, deleteParams, transaction: tx,
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                auditEntries.Add(new WriteAuditEntry(
                    designerId, omittedId, "SOFT_DELETE", null, null, deletedAt));
            }
        }

        return auditEntries;
    }

    // The parent FK column (parent_<parentDesignerId>_id) is provisioned as UUID by
    // DdlEmitter and is owned entirely by the coordinator — it always equals parentId
    // and is injected as a typed Guid on INSERT. If a child row-form happens to declare
    // a fieldKey that collides with this reserved name, the validator coerces its value
    // as the field's own (non-UUID) type; binding that into the UPDATE SET clause (or a
    // second INSERT column) yields PG 42804 "uuid but expression is of type text" /
    // a duplicate-column error. Drop it so the system-owned value always wins.
    private static IReadOnlyDictionary<string, object?> StripReservedFkColumn(
        IReadOnlyDictionary<string, object?> coercedValues, string fkColumnName)
    {
        if (!coercedValues.ContainsKey(fkColumnName)) return coercedValues;
        var filtered = new Dictionary<string, object?>(coercedValues.Count, StringComparer.Ordinal);
        foreach (var (key, value) in coercedValues)
        {
            if (!string.Equals(key, fkColumnName, StringComparison.Ordinal))
                filtered[key] = value;
        }
        return filtered;
    }
}

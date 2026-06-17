using System.Text.Json;
using Dapper;
using FormForge.Api.Common;
using FormForge.Api.Domain.Entities;
using FormForge.Api.Domain.ValueTypes;
using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormForge.Api.Features.Datasets;

// Story 8.4 (FR-58 / AR-59) — Dataset lifecycle service. CreateAsync inserts the
// custom_dataset row and the backing PostgreSQL VIEW inside a single
// NpgsqlTransaction (synchronous atomicity), then writes the audit log via EF.
// Stories 8.5/8.6/8.7 add update/delete/get operations to this service.

// Story 11.2 (FR-71 / AR-66) — BuilderStateInvalid is a distinct outcome so the POST
// endpoint can emit 422 BUILDER_STATE_INVALID when server-side SQL generation from a
// builder_state blob supplied at create time fails (mirrors UpdateDatasetOutcome).
internal enum CreateDatasetOutcome { Success, NameConflict, InvalidQuery, BuilderStateInvalid }

internal sealed record CreateDatasetResult(
    CreateDatasetOutcome Outcome,
    DatasetDto? Dataset = null,
    string? ErrorDetail = null);

// Story 8.5 (FR-58 / FR-59) — outcomes of an UPDATE: optimistic-concurrency
// conflict (AR-60), name uniqueness conflict, and VIEW DDL failure are all
// distinct so the endpoint can map them to 409/409/422 respectively.
// Story 11.1 (FR-70 / AR-65) — BuilderStateInvalid is a distinct outcome so the endpoint
// can emit 422 BUILDER_STATE_INVALID when server-side SQL generation from builder_state
// fails (no left table, no columns, non-allowlisted table, unsafe expression, …).
internal enum UpdateDatasetOutcome { Success, NotFound, ConcurrencyConflict, NameConflict, InvalidQuery, BuilderStateInvalid }

internal sealed record UpdateDatasetResult(
    UpdateDatasetOutcome Outcome,
    DatasetDto? Dataset = null,
    string? ErrorDetail = null,
    int? CurrentVersion = null);

// Story 8.6 (FR-58 / AR-59) — outcomes of a DELETE: Success (row deleted + VIEW
// dropped, 204), NotFound (no such row, 404), and DdlFailure (the DROP VIEW threw
// after the transaction rolled back — the row still exists, so map to 500 rather
// than a misleading 404). See Dev Notes §2.
internal enum DeleteDatasetOutcome { Success, NotFound, DdlFailure }

internal sealed record DeleteDatasetResult(
    DeleteDatasetOutcome Outcome);

internal interface IDatasetService
{
    Task<CreateDatasetResult> CreateAsync(
        CreateDatasetRequest request,
        DatasetName name,
        Guid actorId,
        string? correlationId,
        CancellationToken ct);

    Task<UpdateDatasetResult> UpdateAsync(
        Guid datasetId,
        UpdateDatasetRequest request,
        DatasetName? newName,        // null if dataset_name was not provided in request
        Guid actorId,
        string? correlationId,
        CancellationToken ct);

    Task<DeleteDatasetResult> DeleteAsync(
        Guid datasetId,
        Guid actorId,
        string? correlationId,
        CancellationToken ct);

    Task<PagedResult<DatasetSummaryDto>> ListAsync(
        int page,
        int pageSize,
        string? search,
        string? sort,
        CancellationToken ct);

    Task<DatasetDto?> GetByIdAsync(
        Guid id,
        CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed partial class DatasetService(
    FormForgeDbContext db,
    DbConnectionFactory connectionFactory,
    DatasetViewManager viewManager,
    IDatasetAllowlist allowlist,
    ILogger<DatasetService> logger) : IDatasetService
{
    // Used only to substitute {_placeholder} tokens with $N when probing a parameterized
    // "query"-type SQL for SELECT-only validation (the bound values are irrelevant there).
    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public async Task<CreateDatasetResult> CreateAsync(
        CreateDatasetRequest request,
        DatasetName name,
        Guid actorId,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(name);

        // Resolve the actor's display name for the audit log (null if the user vanished).
        var actorName = await db.Users
            .Where(u => u.Id == actorId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Parameterized-query feature — resolve the query type ("view" default). A "query"
        // dataset is stored as a record only (no backing VIEW) and may carry {_placeholder}
        // tokens. An unrecognized value is rejected before any work.
        string queryType;
        if (request.QueryType is null)
        {
            queryType = DatasetQueryTypes.View;
        }
        else
        {
            var normalizedType = DatasetQueryTypes.Normalize(request.QueryType);
            if (normalizedType is null)
                return new CreateDatasetResult(CreateDatasetOutcome.InvalidQuery,
                    ErrorDetail: $"Unknown query type '{request.QueryType}'. Expected 'view' or 'query'.");
            queryType = normalizedType;
        }
        var isQueryType = queryType == DatasetQueryTypes.Query;

        // AC-2 — null/empty query becomes a placeholder VIEW definition.
        var effectiveQuery = string.IsNullOrWhiteSpace(request.Query)
            ? "SELECT 1 AS placeholder"
            : request.Query;

        // Story 11.2 — checkpoint (b) for CreateAsync: for builder mode, derive SQL from
        // builder_state on the server (the client cannot be trusted to send a safe query).
        // Only runs when a BuilderState blob is provided at create time (the current UI never
        // sends one; this gate future-proofs the API and resolves the deferred item from 11.1).
        // Runs before the viewDdl pre-build so the generated SQL flows into the VIEW DDL and
        // the INSERT below, keeping custom_dataset.query and the backing VIEW in sync (AC-4).
        // A "query"-type dataset never uses the visual builder (the UI disables it), so skip
        // builder regeneration entirely for it.
        var effectiveBuilderState = isQueryType ? null : request.BuilderState;
        var builderRegenerated = false;
        if (!isQueryType && !request.IsCustomQuery && !string.IsNullOrWhiteSpace(effectiveBuilderState))
        {
            var bsDto = BuilderStateSerializer.Deserialize(effectiveBuilderState);
            if (bsDto is null)
                return new CreateDatasetResult(CreateDatasetOutcome.BuilderStateInvalid,
                    ErrorDetail: "builder_state could not be parsed.");

            var generated = DatasetSqlGenerator.Generate(bsDto, allowlist);
            if (generated.HasErrors)
                return new CreateDatasetResult(CreateDatasetOutcome.BuilderStateInvalid,
                    ErrorDetail: string.Join("; ", generated.Errors));

            effectiveQuery = generated.ViewSql!;
            builderRegenerated = true;
        }

        // Persisted query column / returned DTO: the server-generated SQL when builder mode
        // actually regenerated it, otherwise the original behavior (request.Query, NULL for a
        // placeholder-only create). Keeps custom_dataset.query in sync with builder_state (AC-4)
        // without changing the placeholder-create contract (query stays NULL — Story 8.4 AC-2).
        var persistedQuery = builderRegenerated
            ? effectiveQuery
            : (string.IsNullOrWhiteSpace(request.Query) ? null : request.Query);

        // Pre-build the DDL so the audit log can record it even when the VIEW DDL fails.
        // A "query"-type dataset has no backing VIEW, so there is no DDL.
        var viewDdl = isQueryType ? null : DatasetViewManager.BuildCreateViewDdl(name, effectiveQuery);

        // Story 8.8 (AR-61) — checkpoint (a): enforce SELECT-only before any DDL. Runs
        // before the connection opens, so invalid input consumes no DB resources and
        // reaches no db.DatasetAuditLog.Add (AC-5). Custom Query mode and "query"-type both
        // enforce a user-provided query; builder-mode and placeholder-only creates bypass it
        // (Stories 11.x cover checkpoint b). For a "query"-type query the {_placeholder}
        // tokens are first substituted with positional $N params so the probe parses as a
        // SelectStmt (the tokens themselves are not valid SQL).
        if (!string.IsNullOrWhiteSpace(request.Query) && (request.IsCustomQuery || isQueryType))
        {
            var probe = isQueryType
                ? DatasetParameterResolver.Resolve(request.Query, EmptyParameters).Sql
                : request.Query;
            var enforcement = SqlSelectEnforcer.Validate(probe);
            if (!enforcement.IsValid)
                return new CreateDatasetResult(CreateDatasetOutcome.InvalidQuery,
                    ErrorDetail: enforcement.ErrorMessage);
        }

        var newId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var succeeded = false;
        string? pgErrorDetail = null;

        // INSERT + CREATE VIEW share one NpgsqlTransaction (AR-59). Dapper on a raw
        // connection — EF uses a separate connection pool, so it cannot be enlisted here.
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                const string insertSql = """
                    INSERT INTO custom_dataset
                        (id, dataset_name, is_custom_query, query_type, query, builder_state, version, created_at, created_by)
                    VALUES
                        (@id, @datasetName, @isCustomQuery, @queryType, @query, @builderState::jsonb, 1, @now, @createdBy)
                    """;

                await conn.ExecuteAsync(new CommandDefinition(
                    insertSql,
                    new
                    {
                        id = newId,
                        datasetName = name.Value,
                        isCustomQuery = request.IsCustomQuery,
                        queryType,
                        query = (object?)persistedQuery ?? DBNull.Value,
                        builderState = string.IsNullOrWhiteSpace(effectiveBuilderState)
                            ? (object?)DBNull.Value
                            : effectiveBuilderState,
                        now,
                        createdBy = (object?)actorId,
                    },
                    transaction: tx,
                    commandTimeout: 5,
                    cancellationToken: ct)).ConfigureAwait(false);

                // A "query"-type dataset is stored as a record only — no backing VIEW.
                if (!isQueryType)
                    await viewManager.CreateAsync(conn, tx, name, effectiveQuery, ct).ConfigureAwait(false);

                // Once committing, do not allow host shutdown to cancel mid-flight.
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                // Don't write audit log on request cancellation.
                throw;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation
                && pg.ConstraintName == "idx_custom_dataset_dataset_name")
            {
                // 23505 fires from the INSERT (idx_custom_dataset_dataset_name) before any
                // DDL is attempted — return immediately, write NO audit entry (AC-6 note).
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                LogNameConflict(logger, name.Value);
                return new CreateDatasetResult(CreateDatasetOutcome.NameConflict);
            }
            catch (NpgsqlException ex)
            {
                // DDL syntax/semantic failure (or any other PG error) — roll back and
                // record the message for the INVALID_QUERY detail (AC-4).
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                pgErrorDetail = ex.Message;
                LogViewCreateFailed(logger, ex, name.Value);
            }
            finally
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        // Audit log via EF, after the Dapper transaction has committed or rolled back.
        var newValues = succeeded
            ? JsonSerializer.Serialize(new
            {
                dataset_name = name.Value,
                is_custom_query = request.IsCustomQuery,
                query_type = queryType,
                query = persistedQuery,
            })
            : null;

        db.DatasetAuditLog.Add(new DatasetAuditLogEntry
        {
            DatasetName = name.Value,
            Operation = "CREATE",
            ActorId = actorId,
            ActorName = actorName,
            NewValues = newValues,
            Ddl = viewDdl,
            Succeeded = succeeded,
            CorrelationId = correlationId,
            Timestamp = now,
        });
        try
        {
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // audit write is best-effort; never fail the request after the main tx settled
        catch (Exception auditEx)
#pragma warning restore CA1031
        {
            // Audit write failed after the main transaction committed or rolled back.
            // Log but do not surface — the caller gets the correct Success/InvalidQuery result.
            LogAuditWriteFailed(logger, auditEx, name.Value);
        }

        if (succeeded)
        {
            var dto = new DatasetDto(
                Id: newId,
                DatasetName: name.Value,
                IsCustomQuery: request.IsCustomQuery,
                Query: persistedQuery,
                BuilderState: string.IsNullOrWhiteSpace(effectiveBuilderState) ? null : effectiveBuilderState,
                Version: 1,
                CreatedAt: now,
                CreatedBy: actorId,
                QueryType: queryType);
            return new CreateDatasetResult(CreateDatasetOutcome.Success, Dataset: dto);
        }

        return new CreateDatasetResult(CreateDatasetOutcome.InvalidQuery, ErrorDetail: pgErrorDetail);
    }

    // Story 8.5 (FR-58 / FR-59 / AR-59 / AR-60) — update an existing Dataset's
    // dataset_name, query, or builder_state. The row UPDATE (with optimistic-
    // concurrency compare-and-swap on version) and the backing VIEW DDL
    // (REPLACE and/or RENAME) execute inside a single NpgsqlTransaction, then the
    // audit log is written via EF. Omitted request fields keep their current value.
    public async Task<UpdateDatasetResult> UpdateAsync(
        Guid datasetId,
        UpdateDatasetRequest request,
        DatasetName? newName,
        Guid actorId,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolve the actor's display name for the audit log (null if the user vanished).
        var actorName = await db.Users
            .Where(u => u.Id == actorId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var succeeded = false;
        string? pgErrorDetail = null;

        // The connection is shared by the initial SELECT (read-committed) and the
        // subsequent transaction (UPDATE + VIEW DDL). Opened once; disposed in the
        // outer finally even on the early-return paths below (§1).
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Step B — load the current row (column aliases: this project does NOT set
            // Dapper's MatchNamesWithUnderscores, so snake_case must be aliased).
            const string selectSql = """
                SELECT id              AS "Id",
                       dataset_name    AS "DatasetName",
                       is_custom_query AS "IsCustomQuery",
                       query_type      AS "QueryType",
                       query           AS "Query",
                       builder_state   AS "BuilderState",
                       version         AS "Version",
                       created_at      AS "CreatedAt",
                       created_by      AS "CreatedBy"
                FROM custom_dataset
                WHERE id = @id
                """;
            var current = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
                new CommandDefinition(selectSql, new { id = datasetId },
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
            if (current is null)
            {
                return new UpdateDatasetResult(UpdateDatasetOutcome.NotFound);
            }

            // Step C — version compare-and-swap guard (AR-60). The DB always starts at
            // version 1, so version 0 / any mismatch here returns 409 before any write.
            if (current.Version != request.Version)
            {
                return new UpdateDatasetResult(UpdateDatasetOutcome.ConcurrencyConflict,
                    CurrentVersion: current.Version);
            }

            // Step D — compute effective new values (omitted fields keep current).
            var effectiveNewName = newName?.Value ?? current.DatasetName;
            var effectiveNewQuery = request.Query is not null ? request.Query : current.Query;
            var effectiveBuilderState = request.BuilderState is not null
                ? request.BuilderState : current.BuilderState;
            var effectiveIsCustomQuery = request.IsCustomQuery ?? current.IsCustomQuery;

            // Parameterized-query feature — resolve the effective query type (omitted = keep
            // current). A transition between "view" and "query" drives the VIEW create/drop in
            // Step F below. wasView/newIsView capture the before/after materialization.
            string effectiveQueryType;
            if (request.QueryType is null)
            {
                effectiveQueryType = current.QueryType;
            }
            else
            {
                var normalizedType = DatasetQueryTypes.Normalize(request.QueryType);
                if (normalizedType is null)
                    return new UpdateDatasetResult(UpdateDatasetOutcome.InvalidQuery,
                        ErrorDetail: $"Unknown query type '{request.QueryType}'. Expected 'view' or 'query'.");
                effectiveQueryType = normalizedType;
            }
            var wasView = current.QueryType == DatasetQueryTypes.View;
            var newIsView = effectiveQueryType == DatasetQueryTypes.View;

            // Story 11.1 (FR-70 / AR-66) — checkpoint (b): for builder mode, re-derive the
            // SQL SELECT from builder_state on the server (the client cannot be trusted to
            // send a safe query). Runs after the version/concurrency checks (Step C) so an
            // invalid state returns 422 BUILDER_STATE_INVALID before the UPDATE transaction
            // opens and before any audit row is written. The generated ViewSql overrides
            // effectiveNewQuery, which then flows into effectiveViewQuery (VIEW DDL) and the
            // row UPDATE below — keeping custom_dataset.query and the backing VIEW in sync (AC-5).
            // Builder regeneration applies only to "view"-type datasets (a "query" dataset has
            // no builder and no backing VIEW).
            var builderRegenerated = false;
            if (newIsView && !effectiveIsCustomQuery && !string.IsNullOrWhiteSpace(effectiveBuilderState))
            {
                var builderState = BuilderStateSerializer.Deserialize(effectiveBuilderState);
                if (builderState is null)
                    return new UpdateDatasetResult(UpdateDatasetOutcome.BuilderStateInvalid,
                        ErrorDetail: "builder_state could not be parsed.");

                var generated = DatasetSqlGenerator.Generate(builderState, allowlist);
                if (generated.HasErrors)
                    return new UpdateDatasetResult(UpdateDatasetOutcome.BuilderStateInvalid,
                        ErrorDetail: string.Join("; ", generated.Errors));

                effectiveNewQuery = generated.ViewSql;
                builderRegenerated = true;
            }

            var isRename = effectiveNewName != current.DatasetName;
            // A builder-mode regeneration that changes the stored SQL must also trigger a VIEW
            // REPLACE on a simultaneous rename (the rename branch only replaces when the query
            // changed). Custom-query semantics are unchanged.
            var queryChanged = (request.Query is not null && request.Query != current.Query)
                || (builderRegenerated && effectiveNewQuery != current.Query);
            var effectiveViewQuery = string.IsNullOrWhiteSpace(effectiveNewQuery)
                ? "SELECT 1 AS placeholder" : effectiveNewQuery;

            // §3 — effectiveNewName always comes from the validated newName or from
            // current.DatasetName (already in the DB), so TryCreate must succeed.
            if (!DatasetName.TryCreate(effectiveNewName, out var resolvedNewName, out _, out _))
                throw new InvalidOperationException(
                    $"'{effectiveNewName}' could not be re-validated as a DatasetName — programming error.");

            // Story 8.8 (AR-61) — checkpoint (a): enforce SELECT-only before the UPDATE
            // transaction. Inside the outer try, so the outer finally disposes conn on this
            // early return (same pattern as the NotFound / ConcurrencyConflict returns). Runs
            // for every Custom Query update and every "query"-type update with a non-empty
            // effective query — not just on query changes — to also catch mode-switches
            // (builder→custom) and any pre-existing non-SELECT stored queries. For a "query"
            // dataset the {_placeholder} tokens are first substituted with $N so the probe
            // parses. The !IsNullOrWhiteSpace guard lets empty-string overwrites bypass it.
            if (!string.IsNullOrWhiteSpace(effectiveNewQuery) && (effectiveIsCustomQuery || !newIsView))
            {
                var probe = !newIsView
                    ? DatasetParameterResolver.Resolve(effectiveNewQuery, EmptyParameters).Sql
                    : effectiveNewQuery;
                var enforcement = SqlSelectEnforcer.Validate(probe);
                if (!enforcement.IsValid)
                    return new UpdateDatasetResult(UpdateDatasetOutcome.InvalidQuery,
                        ErrorDetail: enforcement.ErrorMessage);
            }

            // Step E — pre-build the DDL string(s) for the audit log so a failed DDL is still
            // recorded. The DDL depends on the query-type transition:
            //   view → view  : RENAME (if renamed) and/or REPLACE the definition.
            //   view → query : DROP the existing VIEW (dataset becomes a record only).
            //   query → view : CREATE a VIEW under the new name.
            //   query → query: no DDL.
            string? primaryDdl;
            if (wasView && newIsView)
            {
                primaryDdl = isRename
                    ? DatasetViewManager.BuildRenameViewDdl(current.DatasetName, effectiveNewName)
                    : DatasetViewManager.BuildReplaceViewDdl(resolvedNewName!, effectiveViewQuery);
                if (isRename && queryChanged)
                {
                    primaryDdl = primaryDdl + "\n"
                        + DatasetViewManager.BuildReplaceViewDdl(resolvedNewName!, effectiveViewQuery);
                }
            }
            else if (wasView)
            {
                // view → query: the VIEW still exists under the current (pre-rename) name.
                primaryDdl = DatasetViewManager.BuildDropViewDdl(current.DatasetName);
            }
            else if (newIsView)
            {
                // query → view: no prior VIEW; create one under the new name.
                primaryDdl = DatasetViewManager.BuildCreateViewDdl(resolvedNewName!, effectiveViewQuery);
            }
            else
            {
                primaryDdl = null; // query → query: record only, no DDL.
            }

            // Story 11.4 (FR-73 AC-4) — annotate builder-generated DDL for audit log readability.
            if (builderRegenerated && primaryDdl is not null)
                primaryDdl = "-- Builder-generated\n" + primaryDdl;

            // Step F — UPDATE row + VIEW DDL inside one transaction (AR-59).
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                const string updateSql = """
                    UPDATE custom_dataset
                    SET dataset_name    = @newName,
                        is_custom_query = @isCustomQuery,
                        query_type      = @queryType,
                        query           = @query,
                        builder_state   = @builderState::jsonb,
                        version         = version + 1,
                        updated_at      = @now,
                        updated_by      = @updatedBy
                    WHERE id = @id AND version = @expectedVersion
                    """;
                var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                    updateSql,
                    new
                    {
                        newName = effectiveNewName,
                        isCustomQuery = effectiveIsCustomQuery,
                        queryType = effectiveQueryType,
                        query = string.IsNullOrWhiteSpace(effectiveNewQuery)
                            ? (object?)DBNull.Value : effectiveNewQuery,
                        builderState = effectiveBuilderState is null
                            ? (object?)DBNull.Value : effectiveBuilderState,
                        now,
                        updatedBy = (object?)actorId,
                        id = datasetId,
                        expectedVersion = request.Version,
                    },
                    transaction: tx, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

                if (rowsAffected == 0)
                {
                    // Rare race: version matched at SELECT but changed before the UPDATE.
                    // Re-read the live version so the 409 carries the actual current version (AC-1).
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    var freshVersion = await conn.ExecuteScalarAsync<int?>(
                        new CommandDefinition(
                            "SELECT version FROM custom_dataset WHERE id = @id",
                            new { id = datasetId },
                            commandTimeout: 5,
                            cancellationToken: CancellationToken.None))
                        .ConfigureAwait(false);
                    return new UpdateDatasetResult(UpdateDatasetOutcome.ConcurrencyConflict,
                        CurrentVersion: freshVersion ?? current.Version);
                }

                // VIEW DDL (§2) — driven by the query-type transition (see Step E).
                if (wasView && newIsView)
                {
                    // view → view: rename first if the name changed, then REPLACE the
                    // definition when the query changed (or always, when not a rename).
                    if (isRename)
                    {
                        await viewManager.RenameAsync(conn, tx, current.DatasetName, effectiveNewName, ct)
                            .ConfigureAwait(false);
                        if (queryChanged)
                        {
                            await viewManager.ReplaceAsync(conn, tx, resolvedNewName!, effectiveViewQuery, ct)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await viewManager.ReplaceAsync(conn, tx, resolvedNewName!, effectiveViewQuery, ct)
                            .ConfigureAwait(false);
                    }
                }
                else if (wasView)
                {
                    // view → query: drop the existing VIEW (still under the current name).
                    await viewManager.DropAsync(conn, tx, current.DatasetName, ct).ConfigureAwait(false);
                }
                else if (newIsView)
                {
                    // query → view: create a fresh VIEW under the new name.
                    await viewManager.CreateAsync(conn, tx, resolvedNewName!, effectiveViewQuery, ct)
                        .ConfigureAwait(false);
                }
                // else query → query: record only, no VIEW DDL.

                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                succeeded = true;
                LogViewUpdated(logger, effectiveNewName, isRename);
            }
            catch (OperationCanceledException)
            {
                // Don't write audit log on request cancellation.
                throw;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation
                && pg.ConstraintName == "idx_custom_dataset_dataset_name")
            {
                // 23505 from the UPDATE hitting idx_custom_dataset_dataset_name — roll back
                // and return immediately, writing NO audit entry (AC-7, mirrors Story 8.4).
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                LogNameConflict(logger, effectiveNewName);
                return new UpdateDatasetResult(UpdateDatasetOutcome.NameConflict);
            }
            catch (NpgsqlException ex)
            {
                // VIEW DDL syntax/semantic failure — roll back and record the message for
                // the INVALID_QUERY detail (AC-8). Audit failure row written below (AC-11).
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                pgErrorDetail = ex.Message;
                LogViewUpdateFailed(logger, ex, effectiveNewName);
            }
            finally
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }

            // Step G — audit log via EF, after the Dapper transaction settled.
            var previousValues = JsonSerializer.Serialize(new
            {
                dataset_name = current.DatasetName,
                is_custom_query = current.IsCustomQuery,
                query_type = current.QueryType,
                query = current.Query,
                builder_state = current.BuilderState,
                version = current.Version,
            });
            var newValues = succeeded
                ? JsonSerializer.Serialize(new
                {
                    dataset_name = effectiveNewName,
                    is_custom_query = effectiveIsCustomQuery,
                    query_type = effectiveQueryType,
                    query = string.IsNullOrWhiteSpace(effectiveNewQuery) ? null : effectiveNewQuery,
                    builder_state = effectiveBuilderState,
                    version = request.Version + 1,
                })
                : null;

            db.DatasetAuditLog.Add(new DatasetAuditLogEntry
            {
                DatasetName = effectiveNewName,
                Operation = "UPDATE",
                ActorId = actorId,
                ActorName = actorName,
                PreviousValues = previousValues,
                NewValues = newValues,
                Ddl = primaryDdl,
                Succeeded = succeeded,
                CorrelationId = correlationId,
                Timestamp = now,
            });
            try
            {
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // audit write is best-effort; never fail the request after the main tx settled
            catch (Exception auditEx)
#pragma warning restore CA1031
            {
                // Audit write failed after the main transaction settled — log, don't surface.
                LogAuditWriteFailed(logger, auditEx, effectiveNewName);
            }

            // Step H — result.
            if (succeeded)
            {
                var dto = new DatasetDto(
                    Id: datasetId,
                    DatasetName: effectiveNewName,
                    IsCustomQuery: effectiveIsCustomQuery,
                    Query: string.IsNullOrWhiteSpace(effectiveNewQuery) ? null : effectiveNewQuery,
                    BuilderState: effectiveBuilderState,
                    Version: request.Version + 1,
                    CreatedAt: current.CreatedAt,
                    CreatedBy: current.CreatedBy,
                    QueryType: effectiveQueryType);
                return new UpdateDatasetResult(UpdateDatasetOutcome.Success, Dataset: dto);
            }

            return new UpdateDatasetResult(UpdateDatasetOutcome.InvalidQuery, ErrorDetail: pgErrorDetail);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Story 8.6 (FR-58 / AR-59) — delete an existing Dataset. The row DELETE and the
    // backing VIEW DROP execute inside a single NpgsqlTransaction; the audit log is
    // then written via EF. DELETE is not version-guarded (§3): it deletes by id alone.
    public async Task<DeleteDatasetResult> DeleteAsync(
        Guid datasetId,
        Guid actorId,
        string? correlationId,
        CancellationToken ct)
    {
        // Resolve the actor's display name for the audit log (null if the user vanished).
        var actorName = await db.Users
            .Where(u => u.Id == actorId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var succeeded = false;

        // The connection is shared by the initial SELECT (read-committed) and the
        // subsequent transaction (DELETE + DROP VIEW). Opened once; disposed in the
        // outer finally even on the early-return NotFound path below (§1).
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            // Step B — load the current row (column aliases: this project does NOT set
            // Dapper's MatchNamesWithUnderscores, so snake_case must be aliased).
            const string selectSql = """
                SELECT id              AS "Id",
                       dataset_name    AS "DatasetName",
                       is_custom_query AS "IsCustomQuery",
                       query_type      AS "QueryType",
                       query           AS "Query",
                       builder_state   AS "BuilderState",
                       version         AS "Version",
                       created_at      AS "CreatedAt",
                       created_by      AS "CreatedBy"
                FROM custom_dataset
                WHERE id = @id
                """;
            var current = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
                new CommandDefinition(selectSql, new { id = datasetId },
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);
            if (current is null)
            {
                return new DeleteDatasetResult(DeleteDatasetOutcome.NotFound);
            }

            // Step C — pre-build the DROP DDL so the audit log records it even on failure.
            // A "query"-type dataset has no backing VIEW, so there is nothing to drop.
            var hasView = current.QueryType == DatasetQueryTypes.View;
            var dropDdl = hasView ? DatasetViewManager.BuildDropViewDdl(current.DatasetName) : null;
            var now = DateTimeOffset.UtcNow;

            // Step D — DELETE row + DROP VIEW inside one transaction (AR-59).
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                const string deleteSql = """
                    DELETE FROM custom_dataset WHERE id = @id
                    """;
                var rowsAffected = await conn.ExecuteAsync(new CommandDefinition(
                    deleteSql,
                    new { id = datasetId },
                    transaction: tx,
                    commandTimeout: 5,
                    cancellationToken: ct)).ConfigureAwait(false);
                if (rowsAffected == 0)
                {
                    // Row was deleted concurrently between the outer SELECT and this DELETE.
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new DeleteDatasetResult(DeleteDatasetOutcome.NotFound);
                }

                if (hasView)
                    await viewManager.DropAsync(conn, tx, current.DatasetName, ct).ConfigureAwait(false);

                // Once committing, do not allow host shutdown to cancel mid-flight.
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                succeeded = true;
                // LogViewDropped is emitted inside DatasetViewManager.DropAsync — do not re-log here.
            }
            catch (OperationCanceledException)
            {
                // Don't write audit log on request cancellation.
                throw;
            }
            catch (NpgsqlException ex)
            {
                // DROP VIEW IF EXISTS essentially never fails (§2) — a failure here means
                // a catastrophic DB issue. Roll back (row survives) and fall through to
                // record a failed audit row, then return DdlFailure → 500.
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                LogViewDropFailed(logger, ex, current.DatasetName);
            }
            finally
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }

            // Step E — audit log via EF, after the Dapper transaction settled. Record the
            // deleted row's values as previous_values; new_values is null (record is gone).
            var previousValues = JsonSerializer.Serialize(new
            {
                dataset_name = current.DatasetName,
                is_custom_query = current.IsCustomQuery,
                query_type = current.QueryType,
                query = current.Query,
                builder_state = current.BuilderState,
                version = current.Version,
            });

            db.DatasetAuditLog.Add(new DatasetAuditLogEntry
            {
                DatasetName = current.DatasetName,
                Operation = "DELETE",
                ActorId = actorId,
                ActorName = actorName,
                PreviousValues = previousValues,
                NewValues = null,
                Ddl = dropDdl,
                Succeeded = succeeded,
                CorrelationId = correlationId,
                Timestamp = now,
            });
            try
            {
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // audit write is best-effort; never fail the request after the main tx settled
            catch (Exception auditEx)
#pragma warning restore CA1031
            {
                // Audit write failed after the main transaction settled — log, don't surface.
                LogAuditWriteFailed(logger, auditEx, current.DatasetName);
            }

            // Step F — result. On the NpgsqlException path the row still exists, so a 404
            // would be misleading; surface DdlFailure → 500 instead (§2).
            return new DeleteDatasetResult(
                succeeded ? DeleteDatasetOutcome.Success : DeleteDatasetOutcome.DdlFailure);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Story 8.7 (FR-62 / AR-65) — paginated list of datasets, newest first. Auth-only
    // (the endpoint does not require dataset-management). LEFT JOIN to users resolves the
    // creator's display name (null when the user row was deleted — SET NULL FK). Inputs are
    // clamped in the service so the bounds hold regardless of how the service is invoked.
    // `search` filters by dataset_name (case-insensitive contains); `sort` is a whitelisted
    // single-column "field:dir" spec (mirrors the admin users list) — both flow into the
    // count and list queries so they apply across all pages, not just the current one.
    public async Task<PagedResult<DatasetSummaryDto>> ListAsync(
        int page,
        int pageSize,
        string? search,
        string? sort,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Parameterized ILIKE filter on dataset_name. The %term% is bound as a parameter,
        // so it is injection-safe; literal % / _ in the term act as wildcards (mirrors the
        // admin users search, which makes the same trade-off).
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var searchTerm = hasSearch ? $"%{search!.Trim()}%" : null;
        var whereSql = hasSearch ? "WHERE cd.dataset_name ILIKE @search" : string.Empty;

        // Two awaits (count + data) share one connection; dispose in finally (mirrors §1).
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var countSql = $"SELECT COUNT(*) FROM custom_dataset cd {whereSql}";
            var total = await conn.ExecuteScalarAsync<long>(
                new CommandDefinition(countSql, new { search = searchTerm },
                    commandTimeout: 5, cancellationToken: ct))
                .ConfigureAwait(false);

            // ORDER BY is built from a fixed whitelist (never interpolates user input), so
            // an unknown/malformed sort falls back to newest-first (the legacy default).
            var listSql = $"""
                SELECT cd.id              AS "Id",
                       cd.dataset_name    AS "DatasetName",
                       cd.is_custom_query AS "IsCustomQuery",
                       cd.query_type      AS "QueryType",
                       cd.created_at      AS "CreatedAt",
                       cd.updated_at      AS "UpdatedAt",
                       u.display_name     AS "CreatedByName"
                FROM custom_dataset cd
                LEFT JOIN users u ON u.id = cd.created_by
                {whereSql}
                ORDER BY {BuildOrderByClause(sort)}
                LIMIT @limit OFFSET @offset
                """;
            // (long) cast prevents int overflow on the offset for very large page numbers
            // (page is only lower-bounded by Math.Max above); a wrapped negative OFFSET would
            // make Postgres throw 22023 → 500 on attacker-controlled ?page=.
            var rows = await conn.QueryAsync<ListDatasetRow>(
                new CommandDefinition(listSql,
                    new { search = searchTerm, limit = pageSize, offset = (long)(page - 1) * pageSize },
                    commandTimeout: 5, cancellationToken: ct))
                .ConfigureAwait(false);

            var data = rows
                .Select(r => new DatasetSummaryDto(
                    r.Id, r.DatasetName, r.IsCustomQuery,
                    r.CreatedAt,          // DateTime → DateTimeOffset (implicit, UTC)
                    r.UpdatedAt,          // DateTime? → DateTimeOffset?
                    r.CreatedByName,
                    r.QueryType))
                .ToList();

            return new PagedResult<DatasetSummaryDto>(data, total, page, pageSize);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Whitelisted single-column ORDER BY for the dataset list ("field:dir"). The column
    // keys mirror the frontend SortHeader colKeys; cd.id DESC is the stable tiebreaker.
    // Unknown/empty/malformed sort falls back to newest-first (created_at DESC), the
    // legacy default. Returns a fixed SQL fragment — user input never reaches the string.
    private static string BuildOrderByClause(string? sort)
    {
        var field = string.Empty;
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

        var dir = desc ? "DESC" : "ASC";
        return field switch
        {
            "name" => $"cd.dataset_name {dir}, cd.id DESC",
            "mode" => $"cd.is_custom_query {dir}, cd.id DESC",
            // NULLS LAST keeps never-updated rows out of the way when sorting by updated_at.
            "updatedAt" => $"cd.updated_at {dir} NULLS LAST, cd.id DESC",
            "createdAt" => $"cd.created_at {dir}, cd.id DESC",
            _ => "cd.created_at DESC, cd.id DESC",
        };
    }

    // Story 8.7 (FR-62 / AR-65) — fetch a single dataset's full DTO (incl. query,
    // builder_state, version). Returns null → 404 when no row matches. Only one query, but
    // the connection is disposed via try/finally with an explicit ConfigureAwait(false) on
    // DisposeAsync (this project enforces CA2007; a bare `await using` would flag it — §1).
    public async Task<DatasetDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            const string selectSql = """
                SELECT id              AS "Id",
                       dataset_name    AS "DatasetName",
                       is_custom_query AS "IsCustomQuery",
                       query_type      AS "QueryType",
                       query           AS "Query",
                       builder_state   AS "BuilderState",
                       version         AS "Version",
                       created_at      AS "CreatedAt",
                       created_by      AS "CreatedBy"
                FROM custom_dataset
                WHERE id = @id
                """;
            var row = await conn.QuerySingleOrDefaultAsync<CurrentDatasetRow>(
                new CommandDefinition(selectSql, new { id },
                    commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            if (row is null)
                return null;

            return new DatasetDto(
                Id: row.Id,
                DatasetName: row.DatasetName,
                IsCustomQuery: row.IsCustomQuery,
                Query: row.Query,
                BuilderState: row.BuilderState,
                Version: row.Version,
                CreatedAt: row.CreatedAt,   // DateTime → DateTimeOffset implicit
                CreatedBy: row.CreatedBy,
                QueryType: row.QueryType);
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Dapper projection of a custom_dataset list row (§5). CreatedAt/UpdatedAt are
    // DateTime/DateTime? (not DateTimeOffset): Npgsql materializes timestamptz as
    // DateTime(Kind=Utc), and Dapper's constructor matcher requires the exact reader type.
    // UpdatedAt is null until the row is first updated. Both convert implicitly to the DTO's
    // DateTimeOffset/DateTimeOffset? (UTC → offset zero) at the mapping site.
    private sealed record ListDatasetRow(
        Guid Id,
        string DatasetName,
        bool IsCustomQuery,
        string QueryType,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        string? CreatedByName);

    // Dapper projection of the current custom_dataset row (§5). Constructor params
    // match the SELECT column aliases above. CreatedAt is DateTime (not DateTimeOffset):
    // Npgsql materializes timestamptz as DateTime(Kind=Utc), and Dapper's constructor
    // matcher requires the exact reader type. It converts implicitly to the DTO's
    // DateTimeOffset (UTC → offset zero) at the return site.
    private sealed record CurrentDatasetRow(
        Guid Id,
        string DatasetName,
        bool IsCustomQuery,
        string QueryType,
        string? Query,
        string? BuilderState,
        int Version,
        DateTime CreatedAt,
        Guid? CreatedBy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated VIEW datasets.\"{Name}\" (rename: {IsRename})")]
    private static partial void LogViewUpdated(ILogger logger, string name, bool isRename);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UPDATE VIEW failed for dataset {Name}")]
    private static partial void LogViewUpdateFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dataset name conflict: {Name}")]
    private static partial void LogNameConflict(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CREATE VIEW failed for dataset {Name}")]
    private static partial void LogViewCreateFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Audit log write failed for dataset {Name}; resource was already created or rolled back")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DROP VIEW failed for dataset {Name}")]
    private static partial void LogViewDropFailed(ILogger logger, Exception ex, string name);
}

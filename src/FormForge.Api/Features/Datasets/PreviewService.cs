using FormForge.Api.Features.Datasets.Dtos;
using FormForge.Api.Infrastructure.Persistence;
using Npgsql;

namespace FormForge.Api.Features.Datasets;

// Story 11.3 (FR-72 / AR-63) — executes a read-only, LIMIT-10 preview of a dataset query
// without persisting anything. Resolves SQL from either the raw custom query (re-validated
// SELECT-only) or builder_state (re-derived via DatasetSqlGenerator, parameterized variant),
// then runs it on the least-privileged `formforge_preview` pool inside a transaction scoped
// by `SET LOCAL statement_timeout` (Dev Notes §2). A statement-timeout cancel (SqlState
// 57014) surfaces as Timeout; any other PostgreSQL error surfaces as SqlError with the raw
// message (no stack traces).
internal interface IPreviewService
{
    Task<PreviewServiceResult> ExecuteAsync(PreviewRequest request, CancellationToken ct = default);
}

// MissingParameters — a parameterized "query"-type preview was requested but one or more
// {_placeholder} tokens had no value in queryParameters. The endpoint maps it to 422 and
// echoes the missing names so the client can prompt for them.
internal enum PreviewOutcome { Success, Timeout, SqlError, BuilderStateInvalid, MissingParameters }

internal sealed record PreviewServiceResult(
    PreviewOutcome Outcome,
    PreviewResultDto? Data = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? MissingParameters = null);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class PreviewService(
    IPreviewConnectionFactory connectionFactory,
    IDatasetAllowlist allowlist,
    IConfiguration configuration) : IPreviewService
{
    // CA2100: the executed SQL is never raw client input concatenated with values — it is
    // either SELECT-only-validated (custom mode) or server-generated with parameterized
    // filter values (builder mode), and the statement_timeout value is operator config, not
    // request data. Identifiers are double-quoted by DatasetSqlGenerator.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
        Justification = "SQL is SELECT-only-validated or server-generated; values are parameterized.")]
    public async Task<PreviewServiceResult> ExecuteAsync(
        PreviewRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Step 1: Resolve SQL + parameters
        string sql;
        IReadOnlyList<object?> parameters = [];
        // Parameterized {_placeholder} values are bound as PostgreSQL `unknown`-typed
        // parameters so they coerce contextually exactly like an inline literal would
        // ('uuid'::unknown → uuid, '23'::unknown → integer). A plain text parameter would
        // NOT coerce (uuid = text has no operator), changing the user's intended semantics.
        // The builder-mode path keeps its already-typed positional binding (false here).
        var bindParametersAsUnknown = false;

        if (request.IsCustomQuery)
        {
            // Strip trailing whitespace and semicolons before wrapping — a trailing ';'
            // would produce `SELECT * FROM (SELECT 1;) AS _preview LIMIT 10` which is a
            // Postgres parse error. Strip first so SqlSelectEnforcer sees the clean query.
            var query = (request.Query ?? string.Empty).TrimEnd().TrimEnd(';').TrimEnd();

            if (DatasetParameterResolver.HasPlaceholders(query))
            {
                // Parameterized "query"-type dataset — bind {_placeholder} tokens as positional
                // parameters before validating/executing, so values are never interpolated into
                // the SQL text. The substituted SQL (with $N) still parses as a SelectStmt, so
                // SELECT-only enforcement runs unchanged on the rewritten query.
                if (!DatasetParameterResolver.TryParseParameters(
                        request.QueryParameters, out var values, out var parseError))
                    return new PreviewServiceResult(PreviewOutcome.SqlError, ErrorMessage: parseError);

                var resolution = DatasetParameterResolver.Resolve(query, values);
                if (resolution.MissingParameters.Count > 0)
                    return new PreviewServiceResult(PreviewOutcome.MissingParameters,
                        ErrorMessage: "Missing values for parameter(s): "
                            + string.Join(", ", resolution.MissingParameters),
                        MissingParameters: resolution.MissingParameters);

                var validation = SqlSelectEnforcer.Validate(resolution.Sql);
                if (!validation.IsValid)
                    return new PreviewServiceResult(PreviewOutcome.SqlError,
                        ErrorMessage: validation.ErrorMessage);

                sql = resolution.Sql;
                parameters = resolution.Parameters;
                bindParametersAsUnknown = true;
            }
            else
            {
                var validation = SqlSelectEnforcer.Validate(query);
                if (!validation.IsValid)
                    return new PreviewServiceResult(PreviewOutcome.SqlError,
                        ErrorMessage: validation.ErrorMessage);
                sql = query;
            }
        }
        else
        {
            var bsDto = BuilderStateSerializer.Deserialize(request.BuilderState);
            if (bsDto is null)
                return new PreviewServiceResult(PreviewOutcome.BuilderStateInvalid,
                    ErrorMessage: "builder_state could not be parsed.");

            var generated = DatasetSqlGenerator.Generate(bsDto, allowlist);
            if (generated.HasErrors)
                return new PreviewServiceResult(PreviewOutcome.BuilderStateInvalid,
                    ErrorMessage: string.Join("; ", generated.Errors));

            if (string.IsNullOrEmpty(generated.ParameterizedSql))
                return new PreviewServiceResult(PreviewOutcome.BuilderStateInvalid,
                    ErrorMessage: "Generator produced no SQL.");

            sql = generated.ParameterizedSql;
            parameters = generated.Parameters;
        }

        // Step 2: Execute inside a transaction to scope SET LOCAL statement_timeout.
        // Connection/transaction are disposed via try/finally with an explicit
        // ConfigureAwait(false) on DisposeAsync — this project enforces CA2007, so a bare
        // `await using` would flag (mirrors DatasetService.GetByIdAsync). Commands and the
        // reader use a synchronous `using` (their Dispose() is not awaited, so CA2007 is moot).
        // Clamp timeout to a positive integer; fall back to 5 on misconfigured or zero values.
        var rawTimeout = configuration["DatasetManager:PreviewTimeoutSeconds"];
        var timeoutSeconds = int.TryParse(rawTimeout, out var ts) && ts > 0 ? rawTimeout! : "5";

        // Optional extra WHERE clause (parameterized-query feature). Preview is dataset-
        // management-only — a caller who can reach it can already author the entire SELECT —
        // so applying the condition as a raw predicate adds no privilege beyond what they have.
        // At true runtime the condition is built server-side from filters, never taken raw.
        var condition = request.Condition?.Trim();
        var whereClause = string.IsNullOrEmpty(condition) ? string.Empty : $" WHERE {condition}";
        try
        {
            var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    using (var timeoutCmd = conn.CreateCommand())
                    {
                        timeoutCmd.Transaction = tx;
                        timeoutCmd.CommandText = $"SET LOCAL statement_timeout = '{timeoutSeconds}s'";
                        await timeoutCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    List<string> columns;
                    var rows = new List<IReadOnlyList<object?>>();
                    using (var previewCmd = conn.CreateCommand())
                    {
                        previewCmd.Transaction = tx;
                        previewCmd.CommandText =
                            $"SELECT * FROM ({sql}) AS _preview{whereClause} LIMIT 10";

                        for (int i = 0; i < parameters.Count; i++)
                        {
                            if (bindParametersAsUnknown)
                            {
                                // `unknown`-typed param: value is the literal's string form so
                                // PostgreSQL coerces it to the comparison column's type.
                                var raw = parameters[i];
                                previewCmd.Parameters.Add(new NpgsqlParameter
                                {
                                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown,
                                    Value = raw is null
                                        ? DBNull.Value
                                        : System.Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)
                                          ?? (object)DBNull.Value,
                                });
                            }
                            else
                            {
                                previewCmd.Parameters.AddWithValue(parameters[i] ?? DBNull.Value);
                            }
                        }

                        using var reader = await previewCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                        columns = Enumerable.Range(0, reader.FieldCount)
                            .Select(reader.GetName)
                            .ToList();

                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            var values = new object[reader.FieldCount];
                            reader.GetValues(values);
                            rows.Add(values.Select(v => v is DBNull ? null : v).ToArray<object?>());
                        }
                    }

                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return new PreviewServiceResult(PreviewOutcome.Success,
                        Data: new PreviewResultDto(columns, rows));
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
        }
        catch (NpgsqlException ex) when (ex.SqlState == "57014")
        {
            return new PreviewServiceResult(PreviewOutcome.Timeout,
                ErrorMessage: "Preview query exceeded the time limit." +
                              " Simplify the query or add filters.");
        }
        catch (NpgsqlException ex)
        {
            return new PreviewServiceResult(PreviewOutcome.SqlError,
                ErrorMessage: ex.Message);
        }
    }
}

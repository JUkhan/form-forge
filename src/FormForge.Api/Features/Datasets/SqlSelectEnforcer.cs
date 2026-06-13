using PgSqlParser;

namespace FormForge.Api.Features.Datasets;

// Story 8.8 (FR-60 / AR-61) — checkpoint (a): SELECT-only enforcement for
// Custom Query create/update, before any VIEW DDL executes. Checkpoints (b)
// and (c) — builder_state SQL and preview — are wired in Stories 11.x.
//
// Parsing is delegated to pgsqlparser (libpg_query protobuf binding, namespace
// PgSqlParser). NOTE: the story spec named a non-existent "PgQuery.NET 2.1.2";
// pgsqlparser is the real substitute. Its Parser.Parse returns a Result<ParseResult?>
// (.IsSuccess / .Value / .Error) and does NOT throw on a parse failure, so we branch
// on IsSuccess rather than catching an exception. The enforcement logic is unchanged:
// exactly one statement whose root Node is a SelectStmt (CTEs included).
internal static class SqlSelectEnforcer
{
    internal static SqlValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SqlValidationResult.Empty;

        var result = Parser.Parse(sql);

        // A syntactically invalid query yields IsSuccess == false (Error populated).
        if (!result.IsSuccess || result.Value is null)
            return SqlValidationResult.InvalidQuery("SQL could not be parsed.");

        var parseResult = result.Value;

        // A bare ";" or whitespace parses successfully with zero statements — reject it
        // as unparsable (there is no SELECT to run).
        if (parseResult.Stmts.Count == 0)
            return SqlValidationResult.InvalidQuery("SQL could not be parsed.");

        if (parseResult.Stmts.Count > 1)
            return SqlValidationResult.InvalidQuery("Only a single SELECT statement is permitted.");

        var stmt = parseResult.Stmts[0].Stmt;
        var selectStmt = stmt?.SelectStmt;

        if (selectStmt is null)
            return SqlValidationResult.InvalidQuery("Only SELECT statements are permitted.");

        // SELECT INTO (e.g. "SELECT id INTO newtable FROM foo") parses as SelectStmt
        // with IntoClause populated — reject it like DML.
        if (selectStmt.IntoClause is not null)
            return SqlValidationResult.InvalidQuery("Only SELECT statements are permitted.");

        // Writable CTEs (WITH x AS (INSERT/UPDATE/DELETE … RETURNING) SELECT …) parse as
        // SelectStmt at the root but mutate data — reject any CTE whose body is not itself
        // a SelectStmt.
        if (selectStmt.WithClause is not null)
        {
            foreach (var cteNode in selectStmt.WithClause.Ctes)
            {
                if (cteNode.CommonTableExpr?.Ctequery?.SelectStmt is null)
                    return SqlValidationResult.InvalidQuery("Only SELECT statements are permitted.");
            }
        }

        return SqlValidationResult.Valid;
    }
}

internal sealed record SqlValidationResult(bool IsValid, string? ErrorMessage)
{
    internal static readonly SqlValidationResult Valid = new(true, null);
    internal static readonly SqlValidationResult Empty = new(false, "SQL query cannot be empty.");

    internal static SqlValidationResult InvalidQuery(string message) => new(false, message);
}

namespace FormForge.Api.Features.Designer;

// Curated subset of PostgreSQL 17 reserved and unreserved-reserved keywords —
// the ones that would break DDL if used as a table or column name. Sourced from
// `SELECT word FROM pg_get_keywords() WHERE catcode IN ('R','U')` and refreshed
// per major PG version. Also blocks identifiers that collide with the system
// columns every dynamic table receives (Story 5.x).
internal static class PgReservedKeywords
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc",
        "asymmetric", "authorization", "binary", "both", "case", "cast",
        "check", "collate", "collation", "column", "concurrently", "constraint",
        "create", "cross", "current_catalog", "current_date", "current_role",
        "current_schema", "current_time", "current_timestamp", "current_user",
        "default", "deferrable", "desc", "distinct", "do", "else", "end",
        "except", "false", "fetch", "for", "foreign", "freeze", "from",
        "full", "grant", "group", "having", "ilike", "in", "initially",
        "inner", "intersect", "into", "is", "isnull", "join", "lateral",
        "leading", "left", "like", "limit", "localtime", "localtimestamp",
        "natural", "not", "notnull", "null", "offset", "on", "only", "or",
        "order", "outer", "overlaps", "placing", "primary", "references",
        "returning", "right", "role", "select", "session_user", "similar", "some",
        "symmetric", "table", "tablesample", "then", "to", "trailing",
        "true", "union", "unique", "user", "using", "variadic", "verbose",
        "when", "where", "window", "with",
        // System-column names PostgreSQL adds to every table (per docs §5.5).
        // Story 5.x DDL will fail/collide if a designer uses any of these.
        "oid", "tableoid", "xmin", "xmax", "cmin", "cmax", "ctid",
        // Names that collide with system columns added to every dynamic table.
        "id", "created_at", "created_by", "updated_at", "updated_by",
        "is_deleted", "cascade_event_id",
        // Reserved by the GET single-record response schema (Story 6.2):
        // ?include=children injects a top-level "children" key into the
        // DynamicRecord response dict. A user fieldKey named "children" would
        // be silently overwritten by that merge.
        "children",
    };

    // PostgreSQL reserves the `pg_*` namespace for itself (system catalogs and
    // any future extensions). Reject the prefix wholesale so Story 5.x DDL can
    // never collide with a current or future system object.
    public static bool IsReserved(string identifier) =>
        Keywords.Contains(identifier)
        || identifier.StartsWith("pg_", StringComparison.Ordinal);
}

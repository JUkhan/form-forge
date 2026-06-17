namespace FormForge.Api.Domain.Entities;

// Story 8.1 (FR-55, AR-57) — persistent Dataset definition. A Dataset is either a
// hand-written custom query (IsCustomQuery = true, SQL in Query) or a visually
// composed Query Builder query (BuilderState JSONB, populated by later stories).
// This is the backing store for the Dataset Manager; the generated SQL is later
// materialized as a VIEW in the `datasets` schema. All column mapping is fluent in
// FormForgeDbContext (this project maps POCOs via OnModelCreating, no annotations).
internal sealed class CustomDataset
{
    public Guid Id { get; set; }

    // UNIQUE, NOT NULL. Stored as TEXT; the 63-byte PostgreSQL identifier limit is
    // enforced by name validation in a later story (8.3), not by the column type.
    public string DatasetName { get; set; } = string.Empty;

    public bool IsCustomQuery { get; set; } = true;

    // Dataset Manager parameterized-query feature — orthogonal to IsCustomQuery (which
    // distinguishes Custom Query vs visual Query Builder). QueryType distinguishes how the
    // dataset is materialized and used:
    //   "view"  — the generated SQL is materialized as a VIEW in the `datasets` schema
    //             (the original behaviour; Custom Query OR Query Builder).
    //   "query" — the SQL is stored as a record only (NO backing VIEW). When the SQL
    //             contains {_placeholder} tokens it is a *parameterized* query, usable only
    //             by the designer's Dataset palette element. See DatasetQueryTypes.
    // Stored as TEXT; defaults to "view" so every pre-existing row keeps its VIEW semantics.
    public string QueryType { get; set; } = DatasetQueryTypes.View;

    public string? Query { get; set; }

    // jsonb — Query Builder state; null for custom-query datasets. Populated by Epic 10/11.
    public string? BuilderState { get; set; }

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    // Optional FK navigations to users(id); cleared (SetNull) if the user is deleted.
    public User? CreatedByUser { get; set; }
    public User? UpdatedByUser { get; set; }
}

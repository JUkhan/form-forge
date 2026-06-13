namespace FormForge.Api.Domain.Entities;

internal sealed class ComponentSchemaVersion
{
    public Guid Id { get; set; }
    public string DesignerId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Status { get; set; } = "Draft";   // "Draft" | "Published" | "Archived"

    // JSONB stored as a string so Npgsql passes raw JSON through transparently;
    // the service layer parses/serializes via JsonNode rather than relying on
    // EF Core's JsonElement mapping (which requires UseJsonColumns + adds double
    // serialization risk).
    public string? RootElement { get; set; }

    // Optional per-version auth filter. When set, it names a user fieldKey
    // (== the dynamic table column) that holds the owning user's id. The record
    // list endpoint then scopes results to rows where that column equals the
    // requesting user's id, and create stamps the column with the creator's id.
    // Null/empty means no auth filter — the table behaves as before. Admin sets
    // this on demand from the Component Library, per designer version.
    public string? AuthFilterFieldKey { get; set; }

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public ComponentSchema Schema { get; set; } = null!;
    public User? Creator { get; set; }
}

namespace FormForge.Api.Domain.Entities;

internal sealed class ComponentSchema
{
    public string DesignerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // FR-54 / Decision 1.8 — component mode lives on the root designer entity,
    // NOT on individual versions. "CRUD" provisions a table; "VIEW" is display-only.
    // Immutable for the life of the component. Backfilled to "CRUD" for pre-3.11 rows.
    public string Mode { get; set; } = "CRUD";

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public List<ComponentSchemaVersion> Versions { get; set; } = [];
    public User? Creator { get; set; }
}

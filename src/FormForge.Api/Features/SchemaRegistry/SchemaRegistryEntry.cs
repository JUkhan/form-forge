namespace FormForge.Api.Features.SchemaRegistry;

// Story 5.3 — cached schema snapshot keyed by (designerId, version). Populated
// by DdlEmitter after a successful provision so Epic 6 CRUD can skip the
// pg_attribute round-trip on every request. ChildRepeaterDesignerIds is recorded
// here for Story 5.5's cascade walk (CycleDetector + child-table provisioning).
internal sealed record SchemaRegistryEntry(
    string DesignerId,
    int Version,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> ChildRepeaterDesignerIds,
    DateTimeOffset CachedAt)
{
    // Story B-1-4-followup — subquery-backed table columns (Designer-backed Dropdown
    // labels, Repeater row counts). Init-only with an empty default so the many
    // existing construction sites that don't supply it keep compiling and behave as
    // before (no derived columns). Entry-construction sites that feed the record-list
    // SELECT set this from RootElementParser.ParseWithDerived.
    public IReadOnlyList<DerivedColumn> DerivedColumns { get; init; } = [];
}

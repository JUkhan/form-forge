namespace FormForge.Api.Features.SchemaRegistry;

// Story 5.3 — in-memory cache of provisioned schemas, keyed by (designerId, version).
// Populated by DdlEmitter after a successful CREATE/ALTER; consumed by Epic 6
// CRUD code so SELECT/INSERT statements can be built without a pg_attribute
// round-trip on every request.
internal interface ISchemaRegistry
{
    void Populate(SchemaRegistryEntry entry);

    SchemaRegistryEntry? TryGet(string designerId, int version);

    // Story 5.6 — remove all cached entries for a designer (every version). Called
    // by SchemaDriftService after a DROP COLUMN so the next Epic 6 CRUD request
    // re-fetches the live schema from PostgreSQL instead of using a stale entry
    // that still references the dropped column.
    void InvalidateDesigner(string designerId);
}

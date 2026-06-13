namespace FormForge.Api.Features.Provisioning.Dtos;

// Admin "Table Provisioned" tab — provision a CRUD Designer's physical table
// directly, without binding it to a menu item. A table is named after its
// designerId; "provisioned" means that physical table exists in the public
// schema. Status is derived (no dedicated status table): table existence +
// the latest schema_audit_log row for the designer.

// One CRUD Designer row in the tab. PublishedVersions lists every Published
// version (descending) the admin may provision/sync to. LastProvisionedVersion /
// LastOperation / LastProvisionedAt come from the most recent CREATE|ALTER audit
// row, or are null when the table has never been provisioned.
internal sealed record TableProvisioningItem(
    string DesignerId,
    string DisplayName,
    string TableName,
    bool IsProvisioned,
    IReadOnlyList<int> PublishedVersions,
    int? LatestVersion,
    int? LastProvisionedVersion,
    string? LastOperation,
    DateTimeOffset? LastProvisionedAt);

// POST body — the Published version to create/sync the table to.
internal sealed record ProvisionTableRequest(int? Version);

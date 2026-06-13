namespace FormForge.Api.Features.Designer.Dtos;

// Admin UNIQUE-constraint management for provisioned CRUD tables.
// A "provisioned designer" is a CRUD-mode Designer whose physical table (named
// after its designerId) exists — whether it was provisioned via a menu binding
// or directly from the Table Provisioning tab (menu-less). The admin picks one
// of these, then adds/drops UNIQUE constraints on its user-authored columns.

// One row in the designer picker. MenuNames lists every menu that binds this
// designer (a designer can back more than one menu item) and is empty for a
// menu-less provisioned table.
internal sealed record ProvisionedDesignerItem(
    string DesignerId,
    string DisplayName,
    int? BoundVersion,
    IReadOnlyList<string> MenuNames);

internal sealed record ProvisionedDesignersResponse(
    IReadOnlyList<ProvisionedDesignerItem> Designers);

// A column that is eligible to take part in a UNIQUE constraint — user-authored
// fields only (system columns id/created_at/… and parent FK columns are excluded
// because constraining them is either meaningless or would break the platform).
internal sealed record ConstrainableColumn(
    string ColumnName,
    string PgDataType);

// An existing UNIQUE constraint on the table. Columns are ordered as declared.
internal sealed record UniqueConstraintInfo(
    string Name,
    IReadOnlyList<string> Columns);

// GET payload — everything the UI needs to render the page for one designer in a
// single round-trip: the table name, the columns it can constrain, and the
// constraints already present.
internal sealed record UniqueConstraintsResponse(
    string DesignerId,
    string TableName,
    IReadOnlyList<ConstrainableColumn> Columns,
    IReadOnlyList<UniqueConstraintInfo> Constraints);

// POST body — the ordered list of columns the new UNIQUE constraint spans.
// One column = a single-column unique; many = a composite unique.
internal sealed record AddUniqueConstraintRequest(
    IReadOnlyList<string>? Columns);

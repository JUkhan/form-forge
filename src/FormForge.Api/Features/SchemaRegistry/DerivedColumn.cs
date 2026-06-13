namespace FormForge.Api.Features.SchemaRegistry;

// Story B-1-4-followup — a "virtual" table column whose value is not stored on the
// record's own table but pulled from a referenced designer/dataset at SELECT time via
// a correlated scalar subquery. Three kinds today:
//   DropdownLabel — a Designer-backed Dropdown stores the referenced row's UUID id;
//                   the list shows the human label instead, pulled from the target
//                   table's LabelColumn WHERE id = this row's LocalFkColumn.
//   DatasetDropdownLabel — a Dataset-backed Dropdown stores the selected ValueColumn
//                   value; the list shows the human label pulled from the dataset's
//                   backing VIEW (datasets."<ReferencedDesignerId>") WHERE
//                   ValueColumn = this row's LocalFkColumn. ReferencedDesignerId is the
//                   dataset's VIEW name (optionsDatasetId now stores the name, not a
//                   GUID — so no id→name lookup is needed, mirroring optionsDesignerId).
//   RepeaterCount — a Repeater's rows live in a child table; the list shows how many
//                   live child rows reference this parent row.
// ResultAlias is the column name the subquery is aliased to (and the key the row
// dictionary carries back to the client). It is produced by RootElementParser.
// BuildDerivedAlias, which the frontend mirrors exactly so it can read the value.
internal sealed record DerivedColumn(
    string Kind,                 // "DropdownLabel" | "DatasetDropdownLabel" | "RepeaterCount"
    string ResultAlias,          // {ref}_{labelField} | {ref}_row_count (truncated to 63)
    string ReferencedDesignerId, // optionsDesignerId | optionsDatasetId (VIEW name) | rowDesignerId
    string? LabelColumn,         // labelField — (Dataset)DropdownLabel only
    string? LocalFkColumn,       // the Dropdown's own fieldKey — (Dataset)DropdownLabel only
    string? ValueColumn = null)  // the dataset VIEW's join column (valueField) — DatasetDropdownLabel only
{
    internal const string DropdownLabelKind = "DropdownLabel";
    internal const string DatasetDropdownLabelKind = "DatasetDropdownLabel";
    internal const string RepeaterCountKind = "RepeaterCount";
}

using System.Text.Json;
using FormForge.Api.Features.Designer;

namespace FormForge.Api.Features.SchemaRegistry;

// Story 5.3 — walks the Designer's RootElement JSON tree and extracts column
// definitions. DFS traversal; structural containers (Stack, Row, Tabs) are
// entered but not themselves emitted. Repeater/RepeaterField are skipped here
// (the Repeater's child rows belong to a separate child table provisioned by
// Story 5.5). Duplicate fieldKeys are silently dropped — first occurrence wins.
internal static class RootElementParser
{
    private const int MaxDepth = 64;

    // PostgreSQL truncates result-column labels to NAMEDATALEN (63 bytes). Derived
    // column aliases are ASCII-only (SafeIdentifier parts joined by '_'), so byte
    // length == char length. Mirror Postgres' truncation here so the alias we write
    // in SQL, the name Npgsql returns, and the key the frontend recomputes all match.
    private const int MaxAliasLength = 63;

    public static IReadOnlyList<ColumnDefinition> Parse(string? rootElementJson)
    {
        var (columns, _, _) = ParseWithDerived(rootElementJson);
        return columns;
    }

    // Returns both the column list AND the child Repeater designerIds (for the
    // SchemaRegistryEntry; Story 5.5 will use the latter for the cascade walk).
    public static (IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<string> ChildRepeaterIds)
        ParseFull(string? rootElementJson)
    {
        var (columns, repeaterIds, _) = ParseWithDerived(rootElementJson);
        return (columns, repeaterIds);
    }

    // Story B-1-4-followup — superset of ParseFull that also returns the derived
    // (subquery-backed) table columns: Designer-backed Dropdown labels and Repeater
    // row counts. Entry-construction sites use this so cached SchemaRegistryEntry
    // rows carry their derived columns; the cheaper ParseFull/Parse delegate here.
    public static (IReadOnlyList<ColumnDefinition> Columns,
                   IReadOnlyList<string> ChildRepeaterIds,
                   IReadOnlyList<DerivedColumn> DerivedColumns)
        ParseWithDerived(string? rootElementJson)
    {
        if (string.IsNullOrWhiteSpace(rootElementJson))
            return ([], [], []);

        using var doc = JsonDocument.Parse(rootElementJson);
        var root = doc.RootElement;
        var columns = new List<ColumnDefinition>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var repeaterIds = new List<string>();
        var derived = new List<DerivedColumn>();

        WalkElement(root, columns, seenKeys, repeaterIds, derived, 0);
        return (columns.AsReadOnly(), repeaterIds.AsReadOnly(), derived.AsReadOnly());
    }

    // TreeView self-reference collector. A TreeView component declares a node-template
    // designer via `rowDesignerId`; the referenced table is provisioned with a
    // self-FK (parent_<table>_id REFERENCES <table>(id) ON DELETE CASCADE) so it can
    // hold an adjacency-list tree. Returned SEPARATELY from ParseFull's child-Repeater
    // ids: a TreeView is a standalone tree of an external table, NOT a child of THIS
    // designer's record — so it must not become a parent_<thisDesigner>_id edge, nor
    // feed the soft-delete cascade graph of this designer. DdlEmitter consumes this to
    // emit the self-FK; nothing else does, so the extra walk runs only at provision time.
    public static IReadOnlyList<string> CollectTreeViewSelfRefIds(string? rootElementJson)
    {
        if (string.IsNullOrWhiteSpace(rootElementJson)) return [];
        using var doc = JsonDocument.Parse(rootElementJson);
        var ids = new List<string>();
        WalkForTreeViews(doc.RootElement, ids, 0);
        return ids.AsReadOnly();
    }

    private static void WalkForTreeViews(JsonElement element, List<string> ids, int depth)
    {
        if (depth > MaxDepth) return;
        if (element.ValueKind != JsonValueKind.Object) return;
        if (!element.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString() ?? string.Empty;

        if (string.Equals(type, "TreeView", StringComparison.Ordinal))
        {
            if (element.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object &&
                props.TryGetProperty("rowDesignerId", out var rowId))
            {
                var id = rowId.GetString();
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            // The node template belongs to the referenced table — do not recurse.
            return;
        }

        if (element.TryGetProperty("children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                WalkForTreeViews(child, ids, depth + 1);
        }
    }

    // Builds the result-column alias for a derived column and applies Postgres'
    // 63-char truncation. The frontend (useDesignerFieldKeys.buildDerivedAlias)
    // MUST apply the identical rule so it reads the same key from the row.
    internal static string BuildDerivedAlias(string referencedId, string suffix)
    {
        var combined = $"{referencedId}_{suffix}";
        return combined.Length <= MaxAliasLength ? combined : combined[..MaxAliasLength];
    }

    private static void WalkElement(
        JsonElement element,
        List<ColumnDefinition> columns,
        HashSet<string> seenKeys,
        List<string> repeaterIds,
        List<DerivedColumn> derived,
        int depth)
    {
        if (depth > MaxDepth) return;
        if (element.ValueKind != JsonValueKind.Object) return;
        if (!element.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString() ?? string.Empty;

        // Collect child Repeater designerIds for Story 5.5 cascade provisioning.
        // The Repeater itself produces no column; do NOT recurse into its children
        // (they belong to the child table, provisioned by Story 5.5).
        if (string.Equals(type, "Repeater", StringComparison.Ordinal))
        {
            if (element.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object &&
                props.TryGetProperty("rowDesignerId", out var rowId))
            {
                var id = rowId.GetString();
                if (!string.IsNullOrEmpty(id)) repeaterIds.Add(id);

                // Story B-1-4-followup — a Repeater shown in the table renders the
                // count of its live child rows, fetched via a correlated subquery
                // against the child table (named after rowDesignerId). Gate on the
                // authored showInTable (absent → visible) and re-validate the id as
                // a SafeIdentifier so it is safe to interpolate downstream.
                if (IsShownInTable(props)
                    && !string.IsNullOrEmpty(id)
                    && SafeIdentifier.TryCreate(id, out _, out _))
                {
                    derived.Add(new DerivedColumn(
                        Kind: DerivedColumn.RepeaterCountKind,
                        ResultAlias: BuildDerivedAlias(id, "row_count"),
                        ReferencedDesignerId: id,
                        LabelColumn: null,
                        LocalFkColumn: null));
                }
            }
            return;
        }

        // A TreeView renders a standalone self-referencing tree of ANOTHER designer's
        // table (its node template, rowDesignerId). It contributes ONE column to
        // THIS designer's table — its fieldKey, which holds the view/select-mode
        // selection (comma-separated node ids), like a multi-select. Its node-template
        // children belong to the referenced table, so do NOT recurse into them (any
        // input dropped there is meant for the referenced table, not this one). The
        // self-FK on the referenced table is provisioned separately via
        // CollectTreeViewSelfRefIds (DdlEmitter), NOT as a child edge of this designer.
        if (string.Equals(type, "TreeView", StringComparison.Ordinal))
        {
            if (element.TryGetProperty("properties", out var tvProps) &&
                tvProps.ValueKind == JsonValueKind.Object &&
                tvProps.TryGetProperty("fieldKey", out var tvFieldKeyProp))
            {
                var tvFieldKey = tvFieldKeyProp.GetString();
                if (!string.IsNullOrWhiteSpace(tvFieldKey)
                    && SafeIdentifier.TryCreate(tvFieldKey, out _, out _)
                    && seenKeys.Add(tvFieldKey))
                {
                    // The component-type default is TEXT, but an explicit per-field
                    // "pgType" property (authored via the Designer's PgType component)
                    // takes precedence — e.g. a single-select tree keyed by uuid stores
                    // a uuid, not free text. Validate + canonicalize through SafePgType so
                    // only an allowlisted, DDL-safe type string reaches ColumnDefinition;
                    // absent or malformed values fall back to the TEXT default (mirrors
                    // the normal column-extraction path below).
                    var tvPgType = ComponentTypeMapper.MapToPgType(type) ?? "TEXT";
                    if (tvProps.TryGetProperty("pgType", out var tvPgTypeProp) &&
                        tvPgTypeProp.ValueKind == JsonValueKind.String &&
                        SafePgType.TryCreate(tvPgTypeProp.GetString(), out var tvSafePgType, out _))
                    {
                        tvPgType = tvSafePgType!.Value;
                    }

                    columns.Add(new ColumnDefinition(
                        ColumnName: tvFieldKey,
                        PgType: tvPgType,
                        ComponentType: type,
                        IsImage: false));
                }
            }
            return;
        }

        // A DatasetComponent is a standalone data-view; its children are filter-form
        // inputs scoped to that view, NOT columns of this designer's table. Skip the
        // whole subtree so neither it nor its filter inputs are provisioned (mirrors
        // the Repeater early-return above).
        if (string.Equals(type, "DatasetComponent", StringComparison.Ordinal))
        {
            return;
        }

        // A SingleRecord embeds another (CRUD) designer's form and reads/writes a record
        // in THAT designer's table — it contributes no column to this designer's table.
        if (string.Equals(type, "SingleRecord", StringComparison.Ordinal))
        {
            return;
        }

        // Recurse into structural containers BEFORE emitting the current element.
        // The traversal order is documentation-only — duplicate fieldKey resolution
        // uses HashSet.Add which is order-sensitive, so we recurse first to match
        // the design contract (first occurrence in DFS order wins).
        if (element.TryGetProperty("children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                WalkElement(child, columns, seenKeys, repeaterIds, derived, depth + 1);
        }

        // Extract the column for this element (if it maps to a PG type).
        var pgType = ComponentTypeMapper.MapToPgType(type);
        if (pgType is null) return;   // structural or UI-only

        if (!element.TryGetProperty("properties", out var properties)) return;
        if (properties.ValueKind != JsonValueKind.Object) return;   // guard against "properties": null

        // A Dropdown bound to a Designer source stores the referenced row's UUID id,
        // not free text — re-map TEXT → UUID so the column type, parameter binding,
        // and equality filtering all treat it as a uuid (mirrors the parent-FK path).
        var optionsSource = properties.TryGetProperty("optionsSource", out var optionsSourceProp)
            && optionsSourceProp.ValueKind == JsonValueKind.String
                ? optionsSourceProp.GetString()
                : null;
        var remappedPgType = ComponentTypeMapper.MapToPgType(type, optionsSource);
        if (remappedPgType is not null) pgType = remappedPgType;   // null only for structural types (already returned)

        // Story B — an explicit per-field "pgType" property (authored via the
        // Designer's PgType component) takes precedence over the component-type
        // default. Validate + canonicalize through SafePgType so only an allowlisted,
        // DDL-safe type string ever reaches ColumnDefinition.PgType. Absent OR
        // malformed pgType falls back to the ComponentTypeMapper default above (which
        // still applies the designer-dropdown → uuid rule), so legacy designs authored
        // before this property keep provisioning. Bind-time validation surfaces a 422
        // for malformed authored values; this fallback is defence-in-depth.
        if (properties.TryGetProperty("pgType", out var pgTypeProp) &&
            pgTypeProp.ValueKind == JsonValueKind.String &&
            SafePgType.TryCreate(pgTypeProp.GetString(), out var safePgType, out _))
        {
            pgType = safePgType!.Value;
        }

        if (!properties.TryGetProperty("fieldKey", out var fieldKeyProp)) return;
        var fieldKey = fieldKeyProp.GetString();
        if (string.IsNullOrWhiteSpace(fieldKey)) return;

        // Defence-in-depth — FieldKeyValidator already rejects unsafe keys at save
        // time, but re-validate here so a hand-edited row in the DB cannot produce
        // unsafe SQL identifiers downstream.
        if (!SafeIdentifier.TryCreate(fieldKey, out _, out _)) return;

        // First-occurrence wins on duplicate fieldKey.
        if (!seenKeys.Add(fieldKey)) return;

        columns.Add(new ColumnDefinition(
            ColumnName: fieldKey,
            PgType: pgType,
            ComponentType: type,
            IsImage: ComponentTypeMapper.IsImageType(type)));

        // Story B-1-4-followup — a Designer-backed Dropdown stores the referenced
        // row's UUID, so a raw column would show an opaque id. When it is shown in
        // the table, add a derived column that pulls the human label from the target
        // table (optionsDesignerId) via a correlated subquery. Static dropdowns need
        // nothing — their own column already holds the displayable value.
        if (string.Equals(type, "Dropdown", StringComparison.Ordinal)
            && string.Equals(optionsSource, "designer", StringComparison.OrdinalIgnoreCase)
            && IsShownInTable(properties))
        {
            var optionsDesignerId = StringProp(properties, "optionsDesignerId");
            var labelField = StringProp(properties, "labelField");
            if (!string.IsNullOrWhiteSpace(optionsDesignerId)
                && !string.IsNullOrWhiteSpace(labelField)
                && SafeIdentifier.TryCreate(optionsDesignerId, out _, out _)
                && SafeIdentifier.TryCreate(labelField, out _, out _))
            {
                derived.Add(new DerivedColumn(
                    Kind: DerivedColumn.DropdownLabelKind,
                    ResultAlias: BuildDerivedAlias(optionsDesignerId!, labelField!),
                    ReferencedDesignerId: optionsDesignerId!,
                    LabelColumn: labelField,
                    LocalFkColumn: fieldKey));
            }
        }

        // A Dataset-backed Dropdown stores the selected value (its valueField value),
        // so a raw column shows an opaque value. When shown in the table, add a derived
        // column that pulls the human label from the dataset's backing VIEW
        // (datasets."<optionsDatasetId>") via a correlated subquery joined on valueField.
        // optionsDatasetId now holds the dataset's VIEW NAME (not a GUID), exactly like
        // optionsDesignerId holds a table name — so the SELECT needs no id→name lookup.
        else if (string.Equals(type, "Dropdown", StringComparison.Ordinal)
            && string.Equals(optionsSource, "dataset", StringComparison.OrdinalIgnoreCase)
            && IsShownInTable(properties))
        {
            var optionsDatasetId = StringProp(properties, "optionsDatasetId");
            var labelField = StringProp(properties, "labelField");
            var valueField = StringProp(properties, "valueField");
            if (!string.IsNullOrWhiteSpace(optionsDatasetId)
                && !string.IsNullOrWhiteSpace(labelField)
                && !string.IsNullOrWhiteSpace(valueField)
                && SafeIdentifier.TryCreate(optionsDatasetId, out _, out _)
                && SafeIdentifier.TryCreate(labelField, out _, out _)
                && SafeIdentifier.TryCreate(valueField, out _, out _))
            {
                derived.Add(new DerivedColumn(
                    Kind: DerivedColumn.DatasetDropdownLabelKind,
                    ResultAlias: BuildDerivedAlias(optionsDatasetId!, labelField!),
                    ReferencedDesignerId: optionsDatasetId!,
                    LabelColumn: labelField,
                    LocalFkColumn: fieldKey,
                    ValueColumn: valueField));
            }
        }
    }

    // showInTable defaults to true when absent or non-boolean — a field authored
    // before this property existed keeps showing as a column (back-compat).
    private static bool IsShownInTable(JsonElement properties)
    {
        if (!properties.TryGetProperty("showInTable", out var prop)) return true;
        return prop.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            _ => true,
        };
    }

    private static string? StringProp(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}

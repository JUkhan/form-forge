namespace FormForge.Api.Features.SchemaRegistry;

// Story 5.3 / Decision 1.2 — complete 14-component PG type mapping.
// Returns null when the component type produces no column (structural / UI-only).
// Unknown / future component types fall back to JSONB so forward-compat designs
// don't crash the provisioner; the SPA's IconPickerSection still gates what
// users can author so this branch is defence-in-depth.
internal static class ComponentTypeMapper
{
    // Structural and UI-only: produce no column.
    // Story 5.4 added "Repeater Field" (SPA space-format) alongside shorthand "RepeaterField"
    // so production data flowing through FieldKeyValidator.InputBearingTypes resolves to null
    // rather than the JSONB fallback.
    private static readonly HashSet<string> NoColumnTypes = new(StringComparer.Ordinal)
    {
        "Stack", "Row", "Tabs",
        "Label", "Button",
        // Image is a static display component (renders its `src` property), not a data field —
        // no column. (File uploads, which DO store a MinIO object key, use the "File" type.)
        "Image",
        "Repeater", "RepeaterField",
        "Repeater Field",
        // A DatasetComponent renders a standalone dataset data-view (toolbar + table +
        // optional filter sub-form). It is never a record column, and its filter-input
        // children are view-local (RootElementParser skips the whole subtree).
        "DatasetComponent",
        // A SingleRecord renders ANOTHER (CRUD) designer's form inline and reads/writes a
        // single per-user record in THAT designer's table. It owns no column on the host
        // designer's table and has no children of its own.
        "SingleRecord",
    };

    // A Dropdown whose "Options source" property is "Designer" stores the referenced
    // row's UUID id (not free text), so its column must be UUID — not the default
    // TEXT. This overload re-maps that one case; every other component (and every
    // static-option Dropdown) defers to the type-only mapping below.
    // optionsSource is the raw Designer-JSON property string ("static" | "designer");
    // null / absent / anything else keeps the default mapping.
    public static string? MapToPgType(string componentType, string? optionsSource) =>
        IsDesignerBackedDropdown(componentType, optionsSource)
            ? "UUID"
            : MapToPgType(componentType);

    private static bool IsDesignerBackedDropdown(string componentType, string? optionsSource) =>
        string.Equals(componentType, "Dropdown", StringComparison.Ordinal)
        && string.Equals(optionsSource, "designer", StringComparison.OrdinalIgnoreCase);

    public static string? MapToPgType(string componentType) => componentType switch
    {
        // Shorthand (architecture spec / unit tests / integration tests up to Story 5.3).
        "TextInput"      => "TEXT",
        "TextArea"       => "TEXT",
        "Dropdown"       => "TEXT",      // static option list; Designer-backed → UUID (see overload)
        "ColorPicker"    => "TEXT",
        // "Image" intentionally omitted — it is a static display component (no column); it
        // resolves to null via the NoColumnTypes branch below.
        "File"           => "TEXT",      // MinIO object key (string)
        "NumberInput"    => "NUMERIC",   // avoids float drift (not FLOAT8)
        "Checkbox"       => "BOOLEAN",
        "DateTimePicker" => "TIMESTAMPTZ",
        // Story 5.4 SPA-format bridge mappings — these are the strings the React SPA
        // actually stores in RootElement JSON (mirrors FieldKeyValidator.InputBearingTypes).
        // Both formats must work side-by-side; do not remove the shorthand cases.
        "Text Input"      => "TEXT",
        "Text Area"       => "TEXT",
        "Color Picker"    => "TEXT",
        "Number Input"    => "NUMERIC",
        "DateTime Picker" => "TIMESTAMPTZ",
        // A TreeView stores the view/select-mode selection (comma-separated node ids)
        // in its OWN column on the host table — like a multi-select. Its node template
        // (rowDesignerId) is a SEPARATE self-referencing table; RootElementParser emits
        // this one TEXT column and does not recurse into the template.
        "TreeView"        => "TEXT",
        _ when NoColumnTypes.Contains(componentType) => null,
        _                => "JSONB",     // forward-compatibility fallback for unknown types
    };

    public static bool IsImageType(string componentType) =>
        string.Equals(componentType, "Image", StringComparison.Ordinal);
}

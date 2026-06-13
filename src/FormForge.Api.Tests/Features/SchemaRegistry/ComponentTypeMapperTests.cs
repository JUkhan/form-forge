using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Tests.Features.SchemaRegistry;

// Story 5.4 — locks the Decision 1.2 mapping AND the new SPA-format bridge
// mappings added in this story. Both formats must continue to work
// side-by-side until the SPA migrates to a single canonical format.
public sealed class ComponentTypeMapperTests
{
    [Theory]
    [InlineData("TextInput",       "TEXT")]
    [InlineData("TextArea",        "TEXT")]
    [InlineData("Dropdown",        "TEXT")]
    [InlineData("ColorPicker",     "TEXT")]
    [InlineData("File",            "TEXT")]   // upload field → MinIO object key
    [InlineData("NumberInput",     "NUMERIC")]
    [InlineData("Checkbox",        "BOOLEAN")]
    [InlineData("DateTimePicker",  "TIMESTAMPTZ")]
    // SPA-format bridge mappings (Story 5.4)
    [InlineData("Text Input",      "TEXT")]
    [InlineData("Text Area",       "TEXT")]
    [InlineData("Color Picker",    "TEXT")]
    [InlineData("Number Input",    "NUMERIC")]
    [InlineData("DateTime Picker", "TIMESTAMPTZ")]
    // TreeView stores its view/select-mode selection (comma-separated ids) in a TEXT column.
    [InlineData("TreeView",        "TEXT")]
    public void MapToPgType_InputComponents_ReturnsExpectedPgType(
        string componentType, string expectedPgType)
        => Assert.Equal(expectedPgType, ComponentTypeMapper.MapToPgType(componentType));

    [Theory]
    [InlineData("Stack")]
    [InlineData("Row")]
    [InlineData("Tabs")]
    [InlineData("Label")]
    [InlineData("Button")]
    [InlineData("Image")]            // static display component → no column
    [InlineData("Repeater")]
    [InlineData("RepeaterField")]
    [InlineData("Repeater Field")]   // SPA format (Story 5.4)
    public void MapToPgType_StructuralTypes_ReturnsNull(string componentType)
        => Assert.Null(ComponentTypeMapper.MapToPgType(componentType));

    [Fact]
    public void MapToPgType_UnknownType_ReturnsJsonb()
        => Assert.Equal("JSONB", ComponentTypeMapper.MapToPgType("FutureWidget"));

    [Fact]
    public void MapToPgType_DesignerBackedDropdown_ReturnsUuid()
        => Assert.Equal("UUID", ComponentTypeMapper.MapToPgType("Dropdown", "designer"));

    [Fact]
    public void MapToPgType_DesignerBackedDropdown_IsCaseInsensitive()
        => Assert.Equal("UUID", ComponentTypeMapper.MapToPgType("Dropdown", "Designer"));

    [Theory]
    [InlineData("static")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("dynamic")]   // removed legacy value — falls back to TEXT
    public void MapToPgType_NonDesignerDropdown_StaysText(string? optionsSource)
        => Assert.Equal("TEXT", ComponentTypeMapper.MapToPgType("Dropdown", optionsSource));

    [Fact]
    public void MapToPgType_OptionsSourceIgnoredForNonDropdown()
    {
        // "designer" optionsSource on a non-Dropdown component is meaningless and
        // must not alter the type-only mapping.
        Assert.Equal("NUMERIC", ComponentTypeMapper.MapToPgType("NumberInput", "designer"));
        Assert.Equal("TEXT", ComponentTypeMapper.MapToPgType("TextInput", "designer"));
    }
}

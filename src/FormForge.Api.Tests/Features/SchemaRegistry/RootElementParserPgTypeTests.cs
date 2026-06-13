using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Tests.Features.SchemaRegistry;

// Story B — the explicit authored pgType property precedence + fallback rules.
public sealed class RootElementParserPgTypeTests
{
    private static string Field(string type, string fieldKey, string? pgType)
    {
        var pg = pgType is null ? "" : $", \"pgType\": \"{pgType}\"";
        return $$"""
            { "id": "root", "type": "Stack", "properties": {}, "children": [
              { "id": "f1", "type": "{{type}}", "properties": { "fieldKey": "{{fieldKey}}"{{pg}} } }
            ]}
            """;
    }

    [Fact]
    public void ExplicitValidPgType_WinsOverComponentDefault()
    {
        // NumberInput defaults to NUMERIC; explicit "integer" must win.
        var columns = RootElementParser.Parse(Field("Number Input", "qty", "integer"));
        Assert.Single(columns);
        Assert.Equal("integer", columns[0].PgType);
    }

    [Fact]
    public void ExplicitParametrizedPgType_IsCanonicalized()
    {
        var columns = RootElementParser.Parse(Field("Text Input", "code", "VARCHAR(64)"));
        Assert.Equal("varchar(64)", columns[0].PgType);
    }

    [Fact]
    public void MalformedPgType_FallsBackToComponentDefault()
    {
        // "bogus" is not allowlisted → fall back to the TextInput default (TEXT),
        // never emit the unsafe string into ColumnDefinition.PgType.
        var columns = RootElementParser.Parse(Field("Text Input", "name", "bogus"));
        Assert.Equal("TEXT", columns[0].PgType);
    }

    [Fact]
    public void InjectionPgType_FallsBackToComponentDefault()
    {
        var columns = RootElementParser.Parse(Field("Text Input", "name", "text); DROP TABLE x;--"));
        Assert.Equal("TEXT", columns[0].PgType);
    }

    [Fact]
    public void AbsentPgType_UsesComponentDefault()
    {
        var columns = RootElementParser.Parse(Field("Number Input", "qty", null));
        Assert.Equal("NUMERIC", columns[0].PgType);
    }

    [Fact]
    public void DesignerDropdownWithoutPgType_DefaultsToUuid()
    {
        const string json = """
            { "id": "root", "type": "Stack", "properties": {}, "children": [
              { "id": "f1", "type": "Dropdown",
                "properties": { "fieldKey": "factory", "optionsSource": "designer" } }
            ]}
            """;
        var columns = RootElementParser.Parse(json);
        Assert.Equal("UUID", columns[0].PgType);
    }

    [Fact]
    public void ExplicitPgType_OverridesDesignerDropdownUuidDefault()
    {
        const string json = """
            { "id": "root", "type": "Stack", "properties": {}, "children": [
              { "id": "f1", "type": "Dropdown",
                "properties": { "fieldKey": "factory", "optionsSource": "designer", "pgType": "varchar(20)" } }
            ]}
            """;
        var columns = RootElementParser.Parse(json);
        Assert.Equal("varchar(20)", columns[0].PgType);
    }
}

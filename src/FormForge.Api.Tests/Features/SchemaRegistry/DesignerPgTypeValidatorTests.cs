using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Tests.Features.SchemaRegistry;

// Story B — bind-time pgType validation: report the first malformed authored
// pgType so the bind endpoint can 422 instead of silently falling back.
public sealed class DesignerPgTypeValidatorTests
{
    [Fact]
    public void AllValid_ReturnsNull()
    {
        const string json = """
            { "type": "Stack", "children": [
              { "type": "Text Input", "properties": { "fieldKey": "name", "pgType": "varchar(80)" } },
              { "type": "Number Input", "properties": { "fieldKey": "qty", "pgType": "numeric(10,2)" } }
            ]}
            """;
        Assert.Null(DesignerPgTypeValidator.FindFirstInvalid(json));
    }

    [Fact]
    public void AbsentPgType_IsAllowed()
    {
        const string json = """
            { "type": "Stack", "children": [
              { "type": "Text Input", "properties": { "fieldKey": "name" } }
            ]}
            """;
        Assert.Null(DesignerPgTypeValidator.FindFirstInvalid(json));
    }

    [Fact]
    public void MalformedPgType_IsReportedWithFieldKey()
    {
        const string json = """
            { "type": "Stack", "children": [
              { "type": "Text Input", "properties": { "fieldKey": "name", "pgType": "varchar" } },
              { "type": "Number Input", "properties": { "fieldKey": "qty", "pgType": "numeric(5,9)" } }
            ]}
            """;
        var error = DesignerPgTypeValidator.FindFirstInvalid(json);
        Assert.NotNull(error);
        Assert.Equal("name", error!.FieldKey);          // first occurrence in DFS order
        Assert.Equal("varchar", error.PgType);
    }

    [Fact]
    public void NullOrEmptyJson_ReturnsNull()
    {
        Assert.Null(DesignerPgTypeValidator.FindFirstInvalid(null));
        Assert.Null(DesignerPgTypeValidator.FindFirstInvalid(""));
        Assert.Null(DesignerPgTypeValidator.FindFirstInvalid("   "));
    }
}

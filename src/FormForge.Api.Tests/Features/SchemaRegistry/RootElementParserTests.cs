using System.Text.Json;
using FormForge.Api.Features.SchemaRegistry;

namespace FormForge.Api.Tests.Features.SchemaRegistry;

public class RootElementParserTests
{
    [Fact]
    public void Parse_EmptyStack_ReturnsNoColumns()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Empty(columns);
    }

    [Fact]
    public void Parse_TextInputChild_ReturnsTextColumn()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"f1","type":"TextInput","properties":{"fieldKey":"full_name"},"children":[]}
            ]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Single(columns);
        Assert.Equal("full_name", columns[0].ColumnName);
        Assert.Equal("TEXT", columns[0].PgType);
    }

    [Fact]
    public void Parse_MultipleFieldTypes_MapsCorrectly()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"f1","type":"TextInput","properties":{"fieldKey":"name"},"children":[]},
                {"id":"f2","type":"NumberInput","properties":{"fieldKey":"age"},"children":[]},
                {"id":"f3","type":"Checkbox","properties":{"fieldKey":"active"},"children":[]},
                {"id":"f4","type":"DateTimePicker","properties":{"fieldKey":"due_at"},"children":[]}
            ]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Equal(4, columns.Count);
        var map = columns.ToDictionary(c => c.ColumnName, c => c.PgType, StringComparer.Ordinal);
        Assert.Equal("TEXT", map["name"]);
        Assert.Equal("NUMERIC", map["age"]);
        Assert.Equal("BOOLEAN", map["active"]);
        Assert.Equal("TIMESTAMPTZ", map["due_at"]);
    }

    [Fact]
    public void Parse_DuplicateFieldKeys_FirstOccurrenceWins()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"f1","type":"TextInput","properties":{"fieldKey":"dup"},"children":[]},
                {"id":"f2","type":"NumberInput","properties":{"fieldKey":"dup"},"children":[]}
            ]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Single(columns);
        // The TextInput child is walked first; the NumberInput duplicate is dropped.
        Assert.Equal("TEXT", columns[0].PgType);
    }

    [Fact]
    public void Parse_StructuralAndUiOnly_AreSkipped()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"r1","type":"Row","properties":{},"children":[
                    {"id":"l1","type":"Label","properties":{"label":"Hi"},"children":[]},
                    {"id":"b1","type":"Button","properties":{"label":"OK"},"children":[]}
                ]}
            ]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Empty(columns);
    }

    [Fact]
    public void Parse_RepeaterCollectsChildIdButProducesNoColumn()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"rep1","type":"Repeater","properties":{"rowDesignerId":"line_item"},"children":[]}
            ]}
            """;

        var (columns, children) = RootElementParser.ParseFull(json);

        Assert.Empty(columns);
        Assert.Single(children);
        Assert.Equal("line_item", children[0]);
    }

    [Fact]
    public void Parse_TreeView_EmitsTextColumnForFieldKey_AndIsNotAChildRepeater()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"tv1","type":"TreeView","properties":{"fieldKey":"selected_nodes","rowDesignerId":"org_unit"},"children":[
                    {"id":"f1","type":"TextInput","properties":{"fieldKey":"should_not_leak"},"children":[]}
                ]}
            ]}
            """;

        var (columns, children) = RootElementParser.ParseFull(json);

        // The TreeView contributes ONE TEXT column (its own fieldKey) to THIS table…
        var col = Assert.Single(columns);
        Assert.Equal("selected_nodes", col.ColumnName);
        Assert.Equal("TEXT", col.PgType);
        Assert.Equal("TreeView", col.ComponentType);
        // …and is NOT a child-Repeater edge of this designer (no parent_<this>_id on org_unit).
        Assert.Empty(children);
        // The node-template subtree belongs to org_unit — its fields must NOT leak here.
        Assert.DoesNotContain(columns, c => c.ColumnName == "should_not_leak");
    }

    [Fact]
    public void CollectTreeViewSelfRefIds_ReturnsNodeTemplateDesignerIds()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"tv1","type":"TreeView","properties":{"fieldKey":"a","rowDesignerId":"org_unit"},"children":[]},
                {"id":"tv2","type":"TreeView","properties":{"fieldKey":"b","rowDesignerId":"category"},"children":[]}
            ]}
            """;

        var ids = RootElementParser.CollectTreeViewSelfRefIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Contains("org_unit", ids);
        Assert.Contains("category", ids);
    }

    [Fact]
    public void CollectTreeViewSelfRefIds_NoTreeViews_ReturnsEmpty()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"rep1","type":"Repeater","properties":{"rowDesignerId":"line_item"},"children":[]}
            ]}
            """;

        Assert.Empty(RootElementParser.CollectTreeViewSelfRefIds(json));
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(RootElementParser.Parse(null));
        Assert.Empty(RootElementParser.Parse(""));
        Assert.Empty(RootElementParser.Parse("  "));
    }

    [Fact]
    public void Parse_DesignerBackedDropdown_ReturnsUuidColumn()
    {
        const string json = """
            {
              "type": "Stack",
              "children": [
                {
                  "type": "Dropdown",
                  "properties": { "fieldKey": "factory", "optionsSource": "designer" }
                }
              ]
            }
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Single(columns);
        Assert.Equal("factory", columns[0].ColumnName);
        Assert.Equal("UUID", columns[0].PgType);
    }

    [Fact]
    public void Parse_StaticDropdown_ReturnsTextColumn()
    {
        const string json = """
            {
              "type": "Stack",
              "children": [
                {
                  "type": "Dropdown",
                  "properties": { "fieldKey": "status", "optionsSource": "static" }
                },
                {
                  "type": "Dropdown",
                  "properties": { "fieldKey": "priority" }
                }
              ]
            }
            """;

        var columns = RootElementParser.Parse(json);

        var map = columns.ToDictionary(c => c.ColumnName, c => c.PgType, StringComparer.Ordinal);
        Assert.Equal("TEXT", map["status"]);
        Assert.Equal("TEXT", map["priority"]);   // absent optionsSource → default TEXT
    }

    [Fact]
    public void Parse_UnknownType_FallsBackToJsonb()
    {
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"f1","type":"FutureWidget","properties":{"fieldKey":"custom"},"children":[]}
            ]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Single(columns);
        Assert.Equal("JSONB", columns[0].PgType);
    }

    [Fact]
    public void Parse_FieldKeyFailsSafeIdentifier_IsSkipped()
    {
        // Uppercase letter — rejected by SafeIdentifier regex.
        const string json = """
            {"id":"root","type":"Stack","properties":{},"children":[
                {"id":"f1","type":"TextInput","properties":{"fieldKey":"BadKey"},"children":[]}
            ]}
            """;

        var columns = RootElementParser.Parse(json);

        Assert.Empty(columns);
    }

    // ---- Story B-1-4-followup: derived table columns ----

    [Fact]
    public void ParseWithDerived_DesignerBackedDropdown_EmitsLabelDerivedColumn()
    {
        const string json = """
            {"type":"Stack","children":[
                {"type":"Dropdown","properties":{
                    "fieldKey":"factory","optionsSource":"designer",
                    "optionsDesignerId":"factory","labelField":"name"}}
            ]}
            """;

        var (columns, _, derived) = RootElementParser.ParseWithDerived(json);

        Assert.Single(columns);                       // the UUID FK column itself
        var d = Assert.Single(derived);
        Assert.Equal(DerivedColumn.DropdownLabelKind, d.Kind);
        Assert.Equal("factory_name", d.ResultAlias);
        Assert.Equal("factory", d.ReferencedDesignerId);
        Assert.Equal("name", d.LabelColumn);
        Assert.Equal("factory", d.LocalFkColumn);
    }

    [Fact]
    public void ParseWithDerived_DatasetBackedDropdown_EmitsDatasetLabelDerivedColumn()
    {
        // optionsDatasetId holds the dataset's VIEW NAME (not a GUID), mirroring how a
        // Designer-backed dropdown stores a table name. pgType is authored uuid here.
        const string json = """
            {"type":"Stack","children":[
                {"type":"Dropdown","properties":{
                    "fieldKey":"user_id","pgType":"uuid","optionsSource":"dataset",
                    "optionsDatasetId":"app_users",
                    "labelField":"users_display_name","valueField":"users_id"}}
            ]}
            """;

        var (columns, _, derived) = RootElementParser.ParseWithDerived(json);

        var col = Assert.Single(columns);             // the FK column itself
        Assert.Equal("user_id", col.ColumnName);
        var d = Assert.Single(derived);
        Assert.Equal(DerivedColumn.DatasetDropdownLabelKind, d.Kind);
        Assert.Equal("app_users_users_display_name", d.ResultAlias);
        Assert.Equal("app_users", d.ReferencedDesignerId);   // = the VIEW name
        Assert.Equal("users_display_name", d.LabelColumn);
        Assert.Equal("users_id", d.ValueColumn);
        Assert.Equal("user_id", d.LocalFkColumn);
    }

    [Fact]
    public void ParseWithDerived_DatasetDropdownMissingValueField_EmitsNoDerivedColumn()
    {
        const string json = """
            {"type":"Stack","children":[
                {"type":"Dropdown","properties":{
                    "fieldKey":"user_id","optionsSource":"dataset",
                    "optionsDatasetId":"app_users","labelField":"users_display_name"}}
            ]}
            """;

        var (_, _, derived) = RootElementParser.ParseWithDerived(json);

        Assert.Empty(derived);
    }

    [Fact]
    public void ParseWithDerived_StaticDropdown_EmitsNoDerivedColumn()
    {
        const string json = """
            {"type":"Stack","children":[
                {"type":"Dropdown","properties":{"fieldKey":"status","optionsSource":"static"}}
            ]}
            """;

        var (_, _, derived) = RootElementParser.ParseWithDerived(json);

        Assert.Empty(derived);
    }

    [Fact]
    public void ParseWithDerived_Repeater_EmitsRowCountDerivedColumn()
    {
        const string json = """
            {"type":"Stack","children":[
                {"type":"Repeater","properties":{"fieldKey":"lines","rowDesignerId":"line_item"}}
            ]}
            """;

        var (columns, childIds, derived) = RootElementParser.ParseWithDerived(json);

        Assert.Empty(columns);                        // Repeater produces no real column
        Assert.Equal("line_item", Assert.Single(childIds));
        var d = Assert.Single(derived);
        Assert.Equal(DerivedColumn.RepeaterCountKind, d.Kind);
        Assert.Equal("line_item_row_count", d.ResultAlias);
        Assert.Equal("line_item", d.ReferencedDesignerId);
    }

    [Fact]
    public void ParseWithDerived_ShowInTableFalse_SuppressesDerivedColumn()
    {
        const string json = """
            {"type":"Stack","children":[
                {"type":"Dropdown","properties":{
                    "fieldKey":"factory","optionsSource":"designer",
                    "optionsDesignerId":"factory","labelField":"name","showInTable":false}},
                {"type":"Repeater","properties":{
                    "fieldKey":"lines","rowDesignerId":"line_item","showInTable":false}}
            ]}
            """;

        var (_, childIds, derived) = RootElementParser.ParseWithDerived(json);

        Assert.Empty(derived);
        // The child id is still collected for cascade provisioning regardless of showInTable.
        Assert.Equal("line_item", Assert.Single(childIds));
    }

    [Fact]
    public void ParseWithDerived_DesignerDropdownMissingLabelField_EmitsNoDerivedColumn()
    {
        const string json = """
            {"type":"Stack","children":[
                {"type":"Dropdown","properties":{
                    "fieldKey":"factory","optionsSource":"designer","optionsDesignerId":"factory"}}
            ]}
            """;

        var (_, _, derived) = RootElementParser.ParseWithDerived(json);

        Assert.Empty(derived);
    }

    [Fact]
    public void BuildDerivedAlias_OverLongCombination_TruncatesTo63Chars()
    {
        var longId = new string('a', 60);
        var alias = RootElementParser.BuildDerivedAlias(longId, "row_count");

        Assert.Equal(63, alias.Length);
        Assert.Equal((longId + "_row_count")[..63], alias);
    }
}

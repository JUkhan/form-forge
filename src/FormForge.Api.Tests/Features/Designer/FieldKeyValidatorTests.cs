using System.Text.Json.Nodes;
using FormForge.Api.Features.Designer;

namespace FormForge.Api.Tests.Features.Designer;

// Pure in-memory tests for FieldKeyValidator — no DB fixture needed because
// the validator walks JsonNode trees synchronously and never touches EF.
// Mirrors the SafeIdentifier rules but collapses InvalidPattern + ReservedKeyword
// under the single FIELD_KEY_INVALID code (see Dev Notes on the v1 asymmetry).
public sealed class FieldKeyValidatorTests
{
    [Fact]
    public void Validate_NullRoot_ReturnsValid()
    {
        var result = FieldKeyValidator.Validate(null);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ComponentMissingFieldKey_ReturnsFieldKeyMissing()
    {
        // properties object present but no fieldKey member — exercises the
        // IsNullOrWhiteSpace branch in Walk(). A whitespace-only key would
        // also flow here (intentional, since "  " is not a localizable error).
        var root = JsonNode.Parse("""
            {
              "type": "Root",
              "id": "root-1",
              "children": [
                { "type": "Text Input", "id": "tx-1", "properties": {} }
              ]
            }
            """);

        var result = FieldKeyValidator.Validate(root);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors);
        Assert.Equal("FIELD_KEY_MISSING", err.Code);
        Assert.Equal("tx-1", err.ElementId);
        Assert.Equal("Text Input", err.ElementType);
        Assert.Null(err.FieldKey);
    }

    [Fact]
    public void Validate_ComponentInvalidFieldKey_ReturnsFieldKeyInvalid()
    {
        var root = JsonNode.Parse("""
            {
              "type": "Root",
              "id": "root-1",
              "children": [
                { "type": "Text Input", "id": "tx-1", "properties": { "fieldKey": "Has-Dash" } }
              ]
            }
            """);

        var result = FieldKeyValidator.Validate(root);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors);
        Assert.Equal("FIELD_KEY_INVALID", err.Code);
        Assert.Equal("Has-Dash", err.FieldKey);
    }

    [Fact]
    public void Validate_ComponentReservedKeyword_ReturnsFieldKeyInvalid()
    {
        // Reserved keywords collapse under FIELD_KEY_INVALID rather than getting
        // their own FIELD_KEY_RESERVED_KEYWORD code — intentional for v1 per Dev
        // Notes: the inline designer error shows SafeIdentifier's message verbatim
        // (which already contains the word "reserved"), so the SPA does not need
        // a distinct code to localize. If a future story needs the distinction
        // it would migrate FieldKeyValidator to the 4-param SafeIdentifier overload.
        var root = JsonNode.Parse("""
            {
              "type": "Root",
              "id": "root-1",
              "children": [
                { "type": "Text Input", "id": "tx-1", "properties": { "fieldKey": "select" } }
              ]
            }
            """);

        var result = FieldKeyValidator.Validate(root);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors);
        Assert.Equal("FIELD_KEY_INVALID", err.Code);
        Assert.Equal("select", err.FieldKey);
        Assert.Contains("reserved", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DuplicateFieldKeys_ReturnsFieldKeyCollision()
    {
        // First sibling claims the key successfully; second flags the collision
        // and the error message includes BOTH ids so the SPA can point the admin
        // at the conflicting pair without an extra round-trip.
        var root = JsonNode.Parse("""
            {
              "type": "Root",
              "id": "root-1",
              "children": [
                { "type": "Text Input", "id": "tx-1", "properties": { "fieldKey": "name" } },
                { "type": "Text Input", "id": "tx-2", "properties": { "fieldKey": "name" } }
              ]
            }
            """);

        var result = FieldKeyValidator.Validate(root);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors);
        Assert.Equal("FIELD_KEY_COLLISION", err.Code);
        Assert.Equal("tx-2", err.ElementId);
        Assert.Equal("name", err.FieldKey);
        Assert.Contains("tx-1", err.Message, StringComparison.Ordinal);
        Assert.Contains("tx-2", err.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ValidSchema_ReturnsValid()
    {
        var root = JsonNode.Parse("""
            {
              "type": "Root",
              "id": "root-1",
              "children": [
                { "type": "Text Input", "id": "tx-1", "properties": { "fieldKey": "first_name" } },
                { "type": "Number Input", "id": "num-1", "properties": { "fieldKey": "age" } }
              ]
            }
            """);

        var result = FieldKeyValidator.Validate(root);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}

using System.Text.Json.Nodes;

namespace FormForge.Api.Features.Designer;

// Walks a designer RootElement tree and validates every input-bearing component's
// `properties.fieldKey`. Reuses SafeIdentifier for the regex + reserved-keyword
// check so the fieldKey rules can never drift from the designerId rules. Returns
// ALL violations (missing, invalid, collision) in one pass — the SPA renders the
// full list inline so an admin can fix every error before retrying the save.
internal static class FieldKeyValidator
{
    // Past any legitimate designer tree by orders of magnitude. Guards the
    // recursive Walk against adversarial inputs that would otherwise blow
    // the call stack (uncatchable StackOverflowException → process exit).
    private const int MaxDepth = 64;

    // Exact type strings stored in the canvas JSON. These match the
    // PropertyInspector switch cases (component-type strings with spaces);
    // NOT the architecture table's shorthand ("TextInput", etc.).
    // Note: `Repeater` IS in this set — its fieldKey names the one-to-many
    // relation and is required. `Repeater Field` is NOT — it's a tabular display
    // column that points at a row-form field via `fieldName`, so it has no
    // fieldKey of its own. (RootElementParser still emits no parent column for a
    // Repeater; the key is validated for format/uniqueness, not provisioned.)
    private static readonly HashSet<string> InputBearingTypes = new(StringComparer.Ordinal)
    {
        "Text Input",
        "TextArea",
        "Text Area",    // SPA space-format — both must be accepted (Story 5.4 bridge)
        "Number Input",
        "Checkbox",
        "Dropdown",
        "DateTime Picker",
        "Color Picker",
        // NOTE: "Image" is NOT here — it is a static display component (renders its `src`
        // property, like a Label renders `text`), with no fieldKey and no column. Uploads
        // use the "File" component. Keep in sync with the SPA's INPUT_BEARING_TYPES.
        "File",
        "Repeater",
        // TreeView binds its selected node ids (comma-separated) to fieldKey in view
        // mode, so a SQL-safe fieldKey is required — exactly like Repeater. Keep in sync
        // with the SPA's INPUT_BEARING_TYPES.
        "TreeView",
    };

    public static FieldKeyValidationResult Validate(JsonNode? root)
    {
        var errors = new List<FieldKeyValidationError>();
        if (root is null)
        {
            return new FieldKeyValidationResult(errors);
        }

        // fieldKey -> first element id that claimed it. Ordinal comparison
        // matches the backend's PG identifier semantics (case-sensitive).
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, errors, seen, depth: 0);
        return new FieldKeyValidationResult(errors);
    }

    private static void Walk(
        JsonNode node,
        List<FieldKeyValidationError> errors,
        Dictionary<string, string> seen,
        int depth)
    {
        if (depth >= MaxDepth)
        {
            errors.Add(new FieldKeyValidationError(
                Code: "TREE_TOO_DEEP",
                ElementId: "(deep)",
                ElementType: "(deep)",
                FieldKey: null,
                Message: $"Component tree exceeds maximum depth of {MaxDepth}."));
            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        // Defensive extraction: `type` / `id` are stored as strings by the
        // designer SPA, but a malformed POST can send them as numbers, booleans,
        // arrays, or objects — `JsonNode.GetValue<string>()` throws on those.
        // Treat anything that isn't a JSON string as missing and let the
        // input-bearing check below short-circuit naturally.
        var type = obj["type"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var t) => t,
            _ => null,
        };
        var id = (obj["id"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => null,
        }) ?? "(no-id)";

        if (type is not null && InputBearingTypes.Contains(type))
        {
            var properties = obj["properties"] as JsonObject;
            var rawKey = properties?["fieldKey"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => null,
            };

            // IsNullOrWhiteSpace (not IsNullOrEmpty): whitespace-only keys
            // would otherwise fall through to the regex path and surface as
            // `invalid field key: "   "` — misleading. Whitespace is "missing".
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                errors.Add(new FieldKeyValidationError(
                    Code: "FIELD_KEY_MISSING",
                    ElementId: id,
                    ElementType: type,
                    FieldKey: null,
                    Message: $"{type} (id: {id}) is missing a field key."));
            }
            else if (!SafeIdentifier.TryCreate(rawKey, out _, out var detail))
            {
                errors.Add(new FieldKeyValidationError(
                    Code: "FIELD_KEY_INVALID",
                    ElementId: id,
                    ElementType: type,
                    FieldKey: rawKey,
                    Message: detail ?? $"{type} (id: {id}) has an invalid field key."));
            }
            else if (seen.TryGetValue(rawKey, out var firstId))
            {
                errors.Add(new FieldKeyValidationError(
                    Code: "FIELD_KEY_COLLISION",
                    ElementId: id,
                    ElementType: type,
                    FieldKey: rawKey,
                    Message: $"Field key \"{rawKey}\" is used by multiple components ({firstId} and {id})."));
            }
            else
            {
                seen[rawKey] = id;
            }
        }

        if (obj["children"] is JsonArray children)
        {
            foreach (var child in children)
            {
                if (child is not null)
                {
                    Walk(child, errors, seen, depth + 1);
                }
            }
        }
    }
}

internal sealed record FieldKeyValidationError(
    string Code,
    string ElementId,
    string ElementType,
    string? FieldKey,
    string Message);

internal sealed record FieldKeyValidationResult(IReadOnlyList<FieldKeyValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

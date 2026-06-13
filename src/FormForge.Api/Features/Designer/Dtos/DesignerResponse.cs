using System.Text.Json.Nodes;

namespace FormForge.Api.Features.Designer.Dtos;

// Shape MUST match ComponentSchemaDto in web/src/types/designer.ts exactly.
// Used for both "get latest" and "get specific version" responses.
//
// PublishedAt semantics: timestamp of the most recent publish for THIS version
// (the one identified by LatestVersion). Set on Publish, preserved on Archive —
// an Archived version's PublishedAt reflects when it WAS last published, as
// history. Consumers that need "is currently published?" MUST filter on
// Status == "Published", never on PublishedAt != null.
internal sealed record DesignerResponse(
    string DesignerId,
    string DisplayName,
    string Mode,    // "CRUD" | "VIEW" — FR-54
    string Status,
    int LatestVersion,
    JsonNode? RootElement,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? PublishedAt,
    // Optional per-version auth filter fieldKey (null when none configured for
    // the version identified by LatestVersion). Surfaced so the renderer can hide
    // the matching form field and the Component Library can show the current value.
    string? AuthFilterFieldKey = null,
    IReadOnlyList<DesignerVersionSummary>? Versions = null);

internal sealed record DesignerVersionSummary(
    int Version,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt);

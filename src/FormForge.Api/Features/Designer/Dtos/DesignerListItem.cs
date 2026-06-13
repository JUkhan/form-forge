namespace FormForge.Api.Features.Designer.Dtos;

// Shape MUST match ComponentSchemaListItem in web/src/types/designer.ts exactly.
internal sealed record DesignerListItem(
    string DesignerId,
    string DisplayName,
    string Mode,    // "CRUD" | "VIEW" — FR-54
    string Status,
    int LatestVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? CreatorDisplayName);

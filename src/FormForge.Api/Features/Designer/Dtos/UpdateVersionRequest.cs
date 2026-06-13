using System.Text.Json.Nodes;

namespace FormForge.Api.Features.Designer.Dtos;

// In-place edit of an existing version: overwrites the version's RootElement and
// the schema-level DisplayName. Distinct from SaveVersionRequest, which creates a
// brand-new Draft snapshot. DisplayName lives on ComponentSchema (not the version),
// so a save here is also how a rename gets persisted.
internal sealed record UpdateVersionRequest(JsonNode? RootElement, string DisplayName);

using System.Text.Json.Nodes;

namespace FormForge.Api.Features.Designer.Dtos;

internal sealed record SaveVersionRequest(JsonNode? RootElement);

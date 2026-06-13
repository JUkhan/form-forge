using System.Text.Json;

namespace FormForge.Api.Features.Menus.Dtos;

internal sealed record UpdateMenuRequest(
    string Name,
    int Order,
    JsonElement? Icon,
    bool IsActive);

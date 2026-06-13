using System.Text.Json;

namespace FormForge.Api.Features.Menus.Dtos;

internal sealed record CreateMenuRequest(
    string Name,
    int Order,
    JsonElement? Icon,
    bool IsActive = true,
    Guid? ParentId = null);

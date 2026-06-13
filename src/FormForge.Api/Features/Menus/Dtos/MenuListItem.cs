namespace FormForge.Api.Features.Menus.Dtos;

internal sealed record MenuListItem(
    Guid Id,
    string Name,
    int Order,
    bool IsActive,
    Guid? ParentId,
    DateTimeOffset CreatedAt);

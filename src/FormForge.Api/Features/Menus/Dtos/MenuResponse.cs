using System.Text.Json;

namespace FormForge.Api.Features.Menus.Dtos;

internal sealed record MenuResponse(
    Guid Id,
    string Name,
    int Order,
    JsonElement? Icon,
    bool IsActive,
    Guid? ParentId,
    IReadOnlyList<Guid> AllowedRoleIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    // Story 5.2 — Designer binding fields. All four are null when the menu item is
    // an unbound section header. ProvisioningStatus transitions Pending → Success | Error
    // asynchronously after PUT /api/admin/menus/{id}/binding returns 202; the admin SPA
    // polls GET /api/admin/menus/{id} to observe the transition.
    string? DesignerId,
    int? BoundVersion,
    string? ProvisioningStatus,   // null | "Pending" | "Success" | "Error"
    string? ProvisioningError,    // null unless ProvisioningStatus == "Error"
    // Custom route path — mutually exclusive with the Designer binding above. When
    // non-null, DesignerId and the provisioning fields are all null.
    string? RoutePath);

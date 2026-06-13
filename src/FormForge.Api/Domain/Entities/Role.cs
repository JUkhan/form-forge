namespace FormForge.Api.Domain.Entities;

internal sealed class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;           // stored lowercase-normalized
    public string? Description { get; set; }
    public bool IsSystem { get; set; }                         // platform-admin and viewer; cannot be mutated or deleted
    public bool CanManageDatasets { get; set; }                // Story 8.1 (AR-58) — Dataset Manager RBAC; DB default false
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<RolePermission> Permissions { get; set; } = [];
    public ICollection<UserRole> UserRoles { get; set; } = [];
}

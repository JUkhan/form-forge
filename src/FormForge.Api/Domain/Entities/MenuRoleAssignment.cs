namespace FormForge.Api.Domain.Entities;

internal sealed class MenuRoleAssignment
{
    public Guid MenuId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Menu Menu { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

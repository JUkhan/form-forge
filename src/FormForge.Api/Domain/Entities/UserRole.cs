namespace FormForge.Api.Domain.Entities;

internal sealed class UserRole
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

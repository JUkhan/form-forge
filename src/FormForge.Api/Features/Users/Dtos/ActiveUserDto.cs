namespace FormForge.Api.Features.Users.Dtos;

// A single active user surfaced by GET /api/users/active — the minimal identity
// fields (id, email, display name) a caller needs to populate a people picker.
// Intentionally excludes status/role/audit fields: this is a directory lookup,
// not the admin user-management projection (see UserListItem).
internal sealed record ActiveUserDto(
    Guid Id,
    string Email,
    string DisplayName);

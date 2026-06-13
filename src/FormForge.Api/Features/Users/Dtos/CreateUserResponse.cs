namespace FormForge.Api.Features.Users.Dtos;

// Story 2.10 — flat superset of UserDetailResponse plus a non-fatal Warnings
// array (e.g. the welcome email could not be sent). Flat rather than a nested
// { user, warnings } wrapper so the existing SPA keeps reading user fields
// directly; the warnings array is purely additive. (AC-3.)
internal sealed record CreateUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<UserRoleItem> Roles,
    IReadOnlyList<string> Warnings);

namespace FormForge.Api.Features.Users.Dtos;

// Both fields optional. The validator enforces "at least one non-null" so an
// empty PUT body returns 422 instead of silently bumping UpdatedAt with no
// effective change.
internal sealed record UpdateUserRequest(
    string? DisplayName,
    string? NewPassword);

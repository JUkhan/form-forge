namespace FormForge.Api.Features.Users.Dtos;

// Nullable fields so the validator can yield a clean 422 instead of the handler
// NRE'ing on a missing property (positional records default to null when the JSON
// key is omitted). Same pattern as AssignRolesRequest.
internal sealed record CreateUserRequest(
    string? Email,
    string? DisplayName,
    string? TemporaryPassword);

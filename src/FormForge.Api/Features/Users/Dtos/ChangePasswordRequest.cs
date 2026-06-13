namespace FormForge.Api.Features.Users.Dtos;

internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

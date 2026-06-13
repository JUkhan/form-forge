namespace FormForge.Api.Features.Auth.Dtos;

// Story 2.11 — body for POST /api/auth/forgot-password.
internal sealed record ForgotPasswordRequest(string Email);

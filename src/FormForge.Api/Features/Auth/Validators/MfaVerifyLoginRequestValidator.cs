using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using FormForge.Api.Features.Auth.Dtos;

namespace FormForge.Api.Features.Auth.Validators;

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated via DI")]
internal sealed class MfaVerifyLoginRequestValidator : AbstractValidator<MfaVerifyLoginRequest>
{
    public MfaVerifyLoginRequestValidator()
    {
        RuleFor(x => x.MfaSessionToken)
            .NotEmpty()
            .MaximumLength(64);

        // Story 2.14 (review) — narrow Code to the only two legitimate shapes: exactly
        // 6 decimal digits (TOTP) OR exactly 8 alphanumerics (backup code). Any other
        // input previously fell through to the backup-code branch and was bcrypt-verified
        // against every unused backup code (~8-10 × ~250 ms) — a mild DoS-amplification
        // vector. Rejecting the malformed shapes here (422) caps the work per request.
        RuleFor(x => x.Code)
            .NotEmpty()
            .Matches(@"^([0-9]{6}|[a-zA-Z0-9]{8})$")
            .WithMessage("Code must be a 6-digit authenticator code or an 8-character backup code.");
    }
}

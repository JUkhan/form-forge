using FluentValidation;
using FormForge.Api.Features.Auth.Dtos;

namespace FormForge.Api.Features.Auth.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<ResetPasswordRequest>.")]
internal sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty()
            .MaximumLength(128);

        // BCrypt silently truncates input to 72 bytes — cap NewPassword at 72 so a
        // long passphrase surfaces a validation error rather than silent truncation.
        // Minimum 8 enforces AC-6's length floor server-side.
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(72);
    }
}

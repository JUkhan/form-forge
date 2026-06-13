using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<ChangePasswordRequest>.")]
internal sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        // CurrentPassword: verifying an existing credential — no MinimumLength.
        // Cap at the BCrypt 72-byte ceiling so an over-long input surfaces a
        // validation error rather than silent truncation inside Verify().
        RuleFor(x => x.CurrentPassword)
            .NotEmpty()
            .MaximumLength(72);

        // NewPassword: enforce AC-2's 8-char floor server-side; cap at BCrypt's
        // 72-byte UTF-8 limit (input is silently truncated beyond that).
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(72);
    }
}

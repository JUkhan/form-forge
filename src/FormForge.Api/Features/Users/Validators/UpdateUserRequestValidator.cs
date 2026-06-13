using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<UpdateUserRequest>.")]
internal sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        // At least one updatable field must be present so an empty body fails fast
        // as 422 rather than silently touching UpdatedAt with no effective change.
        RuleFor(x => x)
            .Must(r => r.DisplayName is not null || r.NewPassword is not null)
            .WithMessage("At least one of displayName or newPassword must be provided.")
            .WithName("body");

        // When the caller opts in to changing a field, whitespace-only input must
        // be rejected — otherwise "   " would replace the stored DisplayName and
        // "        " (8 spaces) would replace the password hash.
        // (Story 2.8 review patches P8 + P9.)
        RuleFor(x => x.DisplayName)
            .Must(s => !string.IsNullOrWhiteSpace(s))
                .WithMessage("Display name cannot be whitespace.")
            .MaximumLength(200)
            .When(x => x.DisplayName is not null);

        RuleFor(x => x.NewPassword)
            .Must(s => !string.IsNullOrWhiteSpace(s))
                .WithMessage("Password cannot be whitespace.")
            .MinimumLength(8)
            .When(x => x.NewPassword is not null);
    }
}

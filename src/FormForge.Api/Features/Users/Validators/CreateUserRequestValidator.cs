using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<CreateUserRequest>.")]
internal sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        // .NotEmpty() rejects null and "" but accepts whitespace-only — pair it
        // with an explicit IsNullOrWhiteSpace guard so "   " fails fast with 422
        // instead of trimming to "" inside the handler. (Story 2.8 review P8.)
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .Must(s => !string.IsNullOrWhiteSpace(s))
                .WithMessage("Display name cannot be whitespace.")
            .MaximumLength(200);

        // Same whitespace guard for passwords — 8 spaces passes MinimumLength(8)
        // and would otherwise become the user's password hash. (Story 2.8 review P9.)
        RuleFor(x => x.TemporaryPassword)
            .NotEmpty()
            .Must(s => !string.IsNullOrWhiteSpace(s))
                .WithMessage("Password cannot be whitespace.")
            .MinimumLength(8);
    }
}

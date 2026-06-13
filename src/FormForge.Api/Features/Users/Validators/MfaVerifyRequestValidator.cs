using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[SuppressMessage("Performance", "CA1812", Justification = "Instantiated via DI")]
internal sealed class MfaVerifyRequestValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .Length(6)
            .Matches(@"^\d{6}$").WithMessage("Code must be exactly 6 digits.");
    }
}

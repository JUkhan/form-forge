using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<BindMenuDesignerRequest>.")]
internal sealed class BindMenuDesignerRequestValidator : AbstractValidator<BindMenuDesignerRequest>
{
    // Mirrors SafeIdentifier's regex (Decision 1.1 / AR-4): lowercase letter or
    // underscore start, then ≤62 lowercase letters/digits/underscores. The semantic
    // VERSION_NOT_PUBLISHED check happens in MenuService (not here) because it needs
    // a DB lookup and would produce a richer error code than FluentValidation's
    // generic ValidationProblemDetails 422.
    public BindMenuDesignerRequestValidator()
    {
        RuleFor(x => x.DesignerId)
            .NotNull().NotEmpty()
            .MaximumLength(63)
            .Matches(@"^[a-z_][a-z0-9_]{0,62}$")
            .WithMessage("designerId must match ^[a-z_][a-z0-9_]{0,62}$ (per AR-4)");

        RuleFor(x => x.Version)
            .NotNull()
            .GreaterThan(0);
    }
}

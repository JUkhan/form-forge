using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<ToggleMenuActiveRequest>.")]
internal sealed class ToggleMenuActiveRequestValidator : AbstractValidator<ToggleMenuActiveRequest>
{
    public ToggleMenuActiveRequestValidator()
    {
        RuleFor(x => x.IsActive)
            .NotNull()
            .WithMessage("isActive must be provided.");
    }
}

using System.Text.Json;
using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

internal sealed class UpdateMenuRequestValidator : AbstractValidator<UpdateMenuRequest>
{
    public UpdateMenuRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Order).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Icon)
            .Must(IconValidation.IsValidShape)
            .WithMessage(IconValidation.InvalidIconMessage)
            .When(x => x.Icon.HasValue && x.Icon.Value.ValueKind != JsonValueKind.Null);
    }
}

using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<ReorderMenusRequest>.")]
internal sealed class ReorderMenusRequestValidator : AbstractValidator<ReorderMenusRequest>
{
    // Same cap as AssignMenuRolesRequestValidator: bounds the Distinct() check
    // and keeps any future ANY(...) parameter list well under Postgres' ~65,535
    // cap. MaxItems check runs BEFORE Distinct so a pathologically large payload
    // is rejected before allocating a same-size HashSet (Story 4.4 review P6).
    private const int MaxItems = 256;

    public ReorderMenusRequestValidator()
    {
        RuleFor(x => x.Items)
            .NotNull()
            .WithMessage("items must be provided (use [] for a no-op).");

        RuleFor(x => x.Items)
            .Must(items => items == null || items.Count <= MaxItems)
            .WithMessage($"items cannot contain more than {MaxItems} entries.");

        RuleFor(x => x.Items)
            .Must(items => items == null || items.Select(i => i.Id).Distinct().Count() == items.Count)
            .When(x => x.Items == null || x.Items.Count <= MaxItems)
            .WithMessage("Duplicate item ids are not allowed.");

        RuleFor(x => x.Items)
            .Must(items => items == null || items.All(i => i.Id != Guid.Empty))
            .When(x => x.Items == null || x.Items.Count <= MaxItems)
            .WithMessage("Item ids must not be Guid.Empty.");

        RuleFor(x => x.Items)
            .Must(items => items == null || items.All(i => i.Order >= 0))
            .When(x => x.Items == null || x.Items.Count <= MaxItems)
            .WithMessage("Item orders must be non-negative.");
    }
}

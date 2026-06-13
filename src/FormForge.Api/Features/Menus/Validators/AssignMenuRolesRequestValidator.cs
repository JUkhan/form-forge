using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<AssignMenuRolesRequest>.")]
internal sealed class AssignMenuRolesRequestValidator : AbstractValidator<AssignMenuRolesRequest>
{
    // Same cap as AssignRolesRequestValidator: bounds the Distinct() check and
    // keeps the future ANY(...) parameter list well under Postgres' ~65,535 cap.
    private const int MaxRoleIds = 256;

    public AssignMenuRolesRequestValidator()
    {
        RuleFor(x => x.RoleIds)
            .NotNull()
            .WithMessage("roleIds must be provided (use [] for empty).");

        // MaxRoleIds check runs BEFORE Distinct so a pathologically large payload
        // is rejected before allocating a same-size HashSet. (Story 4.4 bmad
        // review P6.) The .When guard on the Distinct rule short-circuits in the
        // same case so we never walk the list at all.
        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.Count <= MaxRoleIds)
            .WithMessage($"roleIds cannot contain more than {MaxRoleIds} entries.");

        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.Distinct().Count() == ids.Count)
            .When(x => x.RoleIds == null || x.RoleIds.Count <= MaxRoleIds)
            .WithMessage("Duplicate roleId values are not allowed.");

        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.All(id => id != Guid.Empty))
            .When(x => x.RoleIds == null || x.RoleIds.Count <= MaxRoleIds)
            .WithMessage("roleIds entries must not be Guid.Empty.");
    }
}

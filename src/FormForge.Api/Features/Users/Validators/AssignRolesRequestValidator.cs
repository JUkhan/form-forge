using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<AssignRolesRequest>.")]
internal sealed class AssignRolesRequestValidator : AbstractValidator<AssignRolesRequest>
{
    // Upper bound on roleIds to avoid pushing Postgres near its ~65,535-parameter
    // cap on a single ANY(...) query, and to bound the cost of the validator's own
    // O(N) Distinct check. The role catalog is admin-managed and realistically holds
    // tens of entries; 256 is generous. (Story 2.5 review.)
    private const int MaxRoleIds = 256;

    public AssignRolesRequestValidator()
    {
        RuleFor(x => x.RoleIds)
            .NotNull()
            .WithMessage("roleIds must be provided (use [] for empty).");

        // Whole-collection rule (not RuleForEach): we care about set-uniqueness,
        // not per-element validity. One Must() on the collection produces a single
        // error rather than N per duplicate occurrence.
        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Duplicate roleId values are not allowed.");

        // Reject Guid.Empty per element so the request fails as a validation error
        // (semantically malformed) rather than as a ROLES_NOT_FOUND (semantically
        // "doesn't exist"). (Story 2.5 review.)
        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.All(id => id != Guid.Empty))
            .WithMessage("roleIds entries must not be Guid.Empty.");

        RuleFor(x => x.RoleIds)
            .Must(ids => ids == null || ids.Count <= MaxRoleIds)
            .WithMessage($"roleIds cannot contain more than {MaxRoleIds} entries.");
    }
}

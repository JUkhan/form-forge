using FluentValidation;
using FormForge.Api.Features.Roles.Dtos;

namespace FormForge.Api.Features.Roles.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<CreateRoleRequest>.")]
internal sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    // Reject more than this many permissions in a single request — prevents a
    // single oversized payload from churning row-by-row INSERTs and bounds the
    // duplicate-detection scan. (Story 2.4 review.)
    private const int MaxPermissionsPerRole = 200;

    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100)
            .Matches(@"^[a-z][a-z0-9-]{0,98}[a-z0-9]$|^[a-z]$")
                .WithMessage("Role name must be lowercase alphanumeric with hyphens, 1–100 characters, no leading/trailing hyphen.")
            .Must(name => string.IsNullOrEmpty(name) || !name.Contains("--", StringComparison.Ordinal))
                .WithMessage("Role name cannot contain consecutive hyphens.");

        RuleFor(x => x.Description)
            .MaximumLength(500)
            .When(x => x.Description is not null);

        // Permissions must be present (positional record defaults to null on missing
        // JSON property, which crashed the service with NRE), bounded in size, and
        // free of duplicate resourceIds (the DB unique index uq_role_permissions_role_resource
        // would otherwise surface as a 500 instead of 422). (Story 2.4 review.)
        RuleFor(x => x.Permissions)
            .NotNull()
            .Must(p => p is null || p.Count <= MaxPermissionsPerRole)
                .WithMessage($"Too many permissions in one request (max {MaxPermissionsPerRole}).")
            .Must(p => p is null
                       || p.Select(x => x.ResourceId.Trim().ToLowerInvariant())
                            .Distinct(StringComparer.Ordinal)
                            .Count() == p.Count)
                .WithMessage("Duplicate resourceId in permissions.");

        RuleForEach(x => x.Permissions).ChildRules(p =>
        {
            p.RuleFor(x => x.ResourceId)
             .NotEmpty()
             .MaximumLength(63)
             .Matches(@"^[a-z][a-z0-9_]{0,61}[a-z0-9]$|^[a-z]$")
             .WithMessage("ResourceId must be a valid snake_case identifier (max 63 chars).");
        });
    }
}

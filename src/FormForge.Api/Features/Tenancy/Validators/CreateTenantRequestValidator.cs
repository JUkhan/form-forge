using FluentValidation;
using FormForge.Api.Features.Tenancy.Dtos;

namespace FormForge.Api.Features.Tenancy.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<CreateTenantRequest>.")]
internal sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        // Format/reserved-keyword rules for schemaName live in SafeIdentifier, called
        // directly inside TenantEndpoints.CreateTenantHandler — same precedent as
        // DesignerService.CreateAsync's designerId handling — so every rejection reuses
        // Story 12.2's exact validation (this story's "do not reimplement it" boundary)
        // and carries the matching TENANT_SCHEMA_NAME_INVALID / _RESERVED code.
        // FluentValidation only guards presence/length here so an empty body still
        // surfaces the standard 422 envelope.
        RuleFor(x => x.SchemaName)
            .NotEmpty()
            .MaximumLength(63);
    }
}

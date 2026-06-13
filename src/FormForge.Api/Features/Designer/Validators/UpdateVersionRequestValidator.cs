using System.Text.Json.Nodes;
using FluentValidation;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<UpdateVersionRequest>.")]
internal sealed class UpdateVersionRequestValidator : AbstractValidator<UpdateVersionRequest>
{
    public UpdateVersionRequestValidator()
    {
        // RootElement: same structural shape as SaveVersionRequest — fieldKey-level
        // rules stay in FieldKeyValidator so their 422s carry the FIELD_KEY_INVALID
        // envelope; here we only require a JSON object tree.
        RuleFor(r => r.RootElement)
            .NotNull()
            .Must(r => r is JsonObject)
            .When(r => r.RootElement is not null)
            .WithMessage("RootElement must be a JSON object.");

        // DisplayName: same rules CreateDesignerRequestValidator enforces, since a
        // save here is the only path that persists a rename.
        RuleFor(r => r.DisplayName)
            .NotEmpty()
            .MaximumLength(200)
            .Must(name => name is null || name.All(c => !char.IsControl(c)))
                .WithMessage("displayName must not contain control characters.");
    }
}

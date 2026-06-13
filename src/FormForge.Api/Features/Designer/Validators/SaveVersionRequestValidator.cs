using System.Text.Json.Nodes;
using FluentValidation;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<SaveVersionRequest>.")]
internal sealed class SaveVersionRequestValidator : AbstractValidator<SaveVersionRequest>
{
    public SaveVersionRequestValidator()
    {
        // fieldKey-level rules (missing, regex, reserved-keyword, collision) live in
        // FieldKeyValidator inside DesignerService so EVERY 422 from those carries
        // the FIELD_KEY_INVALID / FIELD_KEY_COLLISION envelope. FluentValidation
        // here only enforces structural shape: a tree must be supplied AND it must
        // be a JSON object (not a bare string / number / array, which would parse
        // cleanly as a JsonNode but persist as junk and crash the SPA on render).
        RuleFor(r => r.RootElement)
            .NotNull()
            .Must(r => r is JsonObject)
            .When(r => r.RootElement is not null)
            .WithMessage("RootElement must be a JSON object.");
    }
}

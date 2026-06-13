using FluentValidation;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<CreateDesignerRequest>.")]
internal sealed class CreateDesignerRequestValidator : AbstractValidator<CreateDesignerRequest>
{
    public CreateDesignerRequestValidator()
    {
        // All identifier rules (empty, length, charset, reserved keyword) live in
        // SafeIdentifier inside DesignerService so EVERY 422 for designerId carries
        // the AC-2 `code: "IDENTIFIER_INVALID"` envelope. FluentValidation's generic
        // ValidationProblem body would drop that code if any identifier rule fired
        // here.
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .MaximumLength(200)
            .Must(name => name is null || name.All(c => !char.IsControl(c)))
                .WithMessage("displayName must not contain control characters.");

        // FR-54 AC-1 — mode is required and must be CRUD or VIEW. FluentValidation's
        // generic 422 body is sufficient here (no special error code needed — unlike
        // designerId, whose IDENTIFIER_INVALID code lives in the service layer).
        RuleFor(x => x.Mode)
            .NotEmpty().WithMessage("mode is required.")
            .Must(m => m == "CRUD" || m == "VIEW")
                .WithMessage("mode must be 'CRUD' or 'VIEW'.");
    }
}

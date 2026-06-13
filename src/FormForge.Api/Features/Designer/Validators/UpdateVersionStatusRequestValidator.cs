using FluentValidation;
using FormForge.Api.Features.Designer.Dtos;

namespace FormForge.Api.Features.Designer.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<UpdateVersionStatusRequest>.")]
internal sealed class UpdateVersionStatusRequestValidator : AbstractValidator<UpdateVersionStatusRequest>
{
    // Draft is recognized here so the handler can return AC-5's specific
    // STATUS_INVALID envelope; the handler pre-checks Draft and returns 422
    // before reaching the service. Unknown values ("foo") still fail the
    // Must rule and return the generic VALIDATION_FAILED envelope.
    private static readonly string[] RecognizedStatuses = ["Draft", "Published", "Archived"];

    public UpdateVersionStatusRequestValidator()
    {
        RuleFor(r => r.Status)
            .NotEmpty()
            .Must(s => RecognizedStatuses.Contains(s, StringComparer.Ordinal))
            .WithMessage("Status must be 'Draft', 'Published', or 'Archived'.");
    }
}

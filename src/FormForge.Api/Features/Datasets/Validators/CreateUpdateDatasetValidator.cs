using FluentValidation;
using FormForge.Api.Features.Datasets.Dtos;

namespace FormForge.Api.Features.Datasets.Validators;

// Story 8.3: validates dataset_name on create.
// Stories 8.4–8.8 will extend this with query/builder_state rules.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<CreateDatasetRequest>.")]
internal sealed class CreateDatasetRequestValidator : AbstractValidator<CreateDatasetRequest>
{
    public CreateDatasetRequestValidator()
    {
        RuleFor(x => x.DatasetName)
            .SetValidator(new DatasetNameValidator());
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<UpdateDatasetRequest>.")]
internal sealed class UpdateDatasetRequestValidator : AbstractValidator<UpdateDatasetRequest>
{
    public UpdateDatasetRequestValidator()
    {
        // dataset_name is optional on update — only validate it when present.
        When(x => x.DatasetName is not null, () =>
        {
            RuleFor(x => x.DatasetName!)
                .SetValidator(new DatasetNameValidator());
        });
    }
}

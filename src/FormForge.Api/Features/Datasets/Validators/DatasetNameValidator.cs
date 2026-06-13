using FluentValidation;
using FormForge.Api.Domain.ValueTypes;

namespace FormForge.Api.Features.Datasets.Validators;

// Standalone validator for the dataset_name field. Used as a child validator via
// SetValidator() in the create/update request validators, and directly in tests.
// Delegates to DatasetName.TryCreate so the regex/keyword/denylist rules live in
// exactly one place (the value type).
internal sealed class DatasetNameValidator : AbstractValidator<string>
{
    public DatasetNameValidator()
    {
        RuleFor(x => x)
            .Custom((name, ctx) =>
            {
                if (!DatasetName.TryCreate(name, out _, out _, out var error))
                    ctx.AddFailure(error ?? "Invalid dataset name.");
            });
    }
}

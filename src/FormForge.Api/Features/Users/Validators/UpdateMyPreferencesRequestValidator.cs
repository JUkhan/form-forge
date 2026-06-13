using FluentValidation;
using FormForge.Api.Features.Users.Dtos;

namespace FormForge.Api.Features.Users.Validators;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI as IValidator<UpdateMyPreferencesRequest>.")]
internal sealed class UpdateMyPreferencesRequestValidator : AbstractValidator<UpdateMyPreferencesRequest>
{
    private static readonly HashSet<string> AllowedThemes = new(StringComparer.Ordinal)
    {
        "default-light",
        "slate-dark",
        "solarized",
    };

    public UpdateMyPreferencesRequestValidator()
    {
        RuleFor(x => x.ThemePreference)
            .Must(t => t is null || AllowedThemes.Contains(t))
                .WithMessage("themePreference must be one of: default-light, slate-dark, solarized.");
    }
}

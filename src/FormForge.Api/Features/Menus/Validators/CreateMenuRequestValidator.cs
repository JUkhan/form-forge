using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using FormForge.Api.Features.Menus.Dtos;

namespace FormForge.Api.Features.Menus.Validators;

internal sealed class CreateMenuRequestValidator : AbstractValidator<CreateMenuRequest>
{
    public CreateMenuRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Order).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Icon)
            .Must(IconValidation.IsValidShape)
            .WithMessage(IconValidation.InvalidIconMessage)
            .When(x => x.Icon.HasValue && x.Icon.Value.ValueKind != JsonValueKind.Null);
    }
}

// Shared icon-shape validator used by both Create and Update menu validators.
internal static class IconValidation
{
    internal const string InvalidIconMessage =
        "Invalid icon: type must be 'lucide' or 'minio'; lucide name must be a valid lucide-react icon; "
        + "minio objectKey must be a valid upload path.";

    // P3: moved here from CreateMenuRequestValidator so UpdateMenuRequestValidator no longer
    // depends on a sibling class through a ForTests-named property.
    // P4: tightened from [a-f0-9\-]{32,36}\.[a-z]+ to exactly 32 hex chars (N format) + png|jpg.
    // SVG uploads are rejected at the endpoint (P9), so svg is excluded from the pattern.
    private static readonly Regex MinioKeyPattern = new(
        @"^menus/icons/[a-f0-9]{32}\.(?:png|jpg)$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    internal static bool IsValidShape(JsonElement? icon)
    {
        if (!icon.HasValue || icon.Value.ValueKind != JsonValueKind.Object) return false;
        if (!icon.Value.TryGetProperty("type", out var typeProp)) return false;
        var type = typeProp.GetString();
        if (type == "lucide")
        {
            if (!icon.Value.TryGetProperty("name", out var nameProp)) return false;
            var name = nameProp.GetString();
            return name is not null && LucideIconRegistry.IsValid(name);
        }
        if (type == "minio")
        {
            if (!icon.Value.TryGetProperty("objectKey", out var keyProp)) return false;
            var key = keyProp.GetString();
            return key is not null && MinioKeyPattern.IsMatch(key);
        }
        return false;
    }
}

using System.Collections.Frozen;

namespace FormForge.Api.Features.Menus;

internal static class LucideIconRegistry
{
    private static readonly FrozenSet<string> _names = LoadNames();

    public static bool IsValid(string name) => _names.Contains(name);

    private static FrozenSet<string> LoadNames()
    {
        var assembly = typeof(LucideIconRegistry).Assembly;
        const string resourceName = "FormForge.Api.Features.Menus.lucide-icon-names.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                "Run generate-icons.mjs and ensure EmbeddedResource is set in .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToFrozenSet(StringComparer.Ordinal);
    }
}

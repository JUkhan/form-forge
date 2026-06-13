using System.Globalization;
using System.Text.Json;

namespace FormForge.Api.Features.DynamicCrud.Export;

// Coerces an Npgsql-returned cell value into a human-readable string. Shared
// across all three writers so CSV, XLSX, and PDF render the same row data the
// same way — no per-format drift on null handling, timestamp format, or JSONB
// stringification.
internal static class ExportValueFormatter
{
    public static string Format(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D", CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            // JSONB columns surface as JsonElement when Npgsql is configured that
            // way; everything else falls through to ToString.
            JsonElement je => je.GetRawText(),
            IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}

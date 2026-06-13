using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace FormForge.Api.Features.DynamicCrud.Export;

internal sealed class CsvExportWriter : IRecordExportWriter
{
    public string ContentType => "text/csv; charset=utf-8";
    public string FileExtension => "csv";

    public async Task WriteAsync(
        Stream output,
        string schemaTitle,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<IDictionary<string, object?>> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(columnNames);
        ArgumentNullException.ThrowIfNull(rows);

        // UTF-8 *with BOM*: Excel can't auto-detect UTF-8 from a bare byte
        // stream and renders non-ASCII chars (é, ñ, …) as mojibake without
        // it. CsvHelper does not write the BOM itself; the StreamWriter does.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var writer = new StreamWriter(output, encoding, leaveOpen: true);
        await using var _ = writer.ConfigureAwait(false);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // RFC-4180 default: quote only when needed. CsvHelper auto-quotes
            // values containing comma/quote/newline.
            HasHeaderRecord = true,
        };
        using var csv = new CsvWriter(writer, config);

        foreach (var col in columnNames)
        {
            csv.WriteField(col);
        }
        await csv.NextRecordAsync().ConfigureAwait(false);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var col in columnNames)
            {
                row.TryGetValue(col, out var v);
                csv.WriteField(ExportValueFormatter.Format(v));
            }
            await csv.NextRecordAsync().ConfigureAwait(false);
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}

using ClosedXML.Excel;

namespace FormForge.Api.Features.DynamicCrud.Export;

internal sealed class XlsxExportWriter : IRecordExportWriter
{
    public string ContentType =>
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public string FileExtension => "xlsx";

    public Task WriteAsync(
        Stream output,
        string schemaTitle,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<IDictionary<string, object?>> rows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(columnNames);
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SanitizeSheetName(schemaTitle));

        // Header row — bold + frozen so it stays visible while scrolling.
        for (int c = 0; c < columnNames.Count; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = columnNames[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        sheet.SheetView.FreezeRows(1);

        for (int r = 0; r < rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[r];
            for (int c = 0; c < columnNames.Count; c++)
            {
                row.TryGetValue(columnNames[c], out var v);
                sheet.Cell(r + 2, c + 1).Value = ExportValueFormatter.Format(v);
            }
        }

        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 200));

        workbook.SaveAs(output);
        return Task.CompletedTask;
    }

    // Excel sheet names are bounded to 31 chars, cannot contain : \ / ? * [ ],
    // and cannot be empty. Anything else and SaveAs throws at write time.
    private static string SanitizeSheetName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Records";
        var span = raw.AsSpan();
        var buf = new char[Math.Min(span.Length, 31)];
        int j = 0;
        for (int i = 0; i < span.Length && j < buf.Length; i++)
        {
            var ch = span[i];
            if (ch is ':' or '\\' or '/' or '?' or '*' or '[' or ']') continue;
            buf[j++] = ch;
        }
        return j == 0 ? "Records" : new string(buf, 0, j);
    }
}

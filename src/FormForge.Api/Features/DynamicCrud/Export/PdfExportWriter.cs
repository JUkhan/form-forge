using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FormForge.Api.Features.DynamicCrud.Export;

internal sealed class PdfExportWriter : IRecordExportWriter
{
    public string ContentType => "application/pdf";
    public string FileExtension => "pdf";

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

        var title = string.IsNullOrWhiteSpace(schemaTitle) ? "Records" : schemaTitle;
        var generatedAt = DateTimeOffset.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                // Landscape A4 — wide tables read better than portrait once you
                // pass ~4 columns. Margins kept tight so the table breathes.
                page.Size(PageSizes.A4.Landscape());
                page.Margin(20);
                page.DefaultTextStyle(t => t.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(14).Bold();
                    col.Item().Text($"Exported {generatedAt} • {rows.Count} rows")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingTop(8).Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        for (int i = 0; i < columnNames.Count; i++)
                            cd.RelativeColumn();
                    });

                    // Header row — repeats on every page so multi-page exports
                    // remain readable.
                    table.Header(h =>
                    {
                        foreach (var col in columnNames)
                        {
                            h.Cell().Background(Colors.Grey.Lighten3)
                                .Padding(4)
                                .Text(col).Bold();
                        }
                    });

                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var col in columnNames)
                        {
                            row.TryGetValue(col, out var v);
                            table.Cell()
                                .BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1)
                                .Padding(4)
                                .Text(ExportValueFormatter.Format(v));
                        }
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Darken1);
                    t.TotalPages().FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });
        }).GeneratePdf(output);

        return Task.CompletedTask;
    }
}

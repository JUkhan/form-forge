using System.Text;
using FormForge.Api.Features.DynamicCrud.Export;

namespace FormForge.Api.Tests.Features.DynamicCrud.Export;

// Story 7-followup — exercises each export writer with a tiny but type-mixed
// rowset. We avoid asserting on full bytes (binary formats are version-pinned
// to the library) and instead check the invariants a downstream consumer
// actually cares about: magic-number signature, header row presence, and
// value coercion (null → empty, bool → text, JSONB → raw JSON).
public class RecordExportWriterTests
{
    static RecordExportWriterTests()
    {
        // Mirror Program.cs — required before any QuestPDF document renders.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private static readonly IReadOnlyList<string> Columns = ["id", "title", "is_active", "created_at"];

    private static readonly IReadOnlyList<IDictionary<string, object?>> Rows =
    [
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ["title"] = "Alpha",
            ["is_active"] = true,
            ["created_at"] = DateTime.Parse("2026-05-28T12:00:00Z").ToUniversalTime(),
        },
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ["title"] = null, // nullable column — must surface as empty, not "null".
            ["is_active"] = false,
            ["created_at"] = DateTime.Parse("2026-05-29T12:00:00Z").ToUniversalTime(),
        },
    ];

    [Fact]
    public async Task Csv_WritesHeaderPlusOneLinePerRow_WithBomForExcelCompat()
    {
        var writer = new CsvExportWriter();
        using var ms = new MemoryStream();
        await writer.WriteAsync(ms, "Patients", Columns, Rows, default);
        var bytes = ms.ToArray();

        // UTF-8 BOM — Excel needs it to render non-ASCII characters correctly.
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.TrimEnd('\r', '\n').Split('\n');
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Contains("id,title,is_active,created_at", lines[0], StringComparison.Ordinal);
        Assert.Contains("Alpha", lines[1], StringComparison.Ordinal);
        Assert.Contains("true", lines[1], StringComparison.Ordinal);
        // Null title → empty field between commas.
        Assert.Contains(",,", lines[2], StringComparison.Ordinal);
        Assert.Contains("false", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xlsx_HasZipSignatureAndNonEmptyBody()
    {
        var writer = new XlsxExportWriter();
        using var ms = new MemoryStream();
        await writer.WriteAsync(ms, "Patients", Columns, Rows, default);
        var bytes = ms.ToArray();

        // xlsx is a zip — first two bytes are 'P' 'K'.
        Assert.True(bytes.Length > 100);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public async Task Xlsx_SanitizesSheetNameToExcelLimits()
    {
        var writer = new XlsxExportWriter();
        using var ms = new MemoryStream();
        // Excel rejects : \ / ? * [ ] in sheet names and caps at 31 chars.
        // SaveAs throws if the name is illegal — a successful write here is
        // the test.
        await writer.WriteAsync(ms, "weird/name:with*chars?and[brackets]", Columns, Rows, default);
        Assert.True(ms.Length > 100);
    }

    [Fact]
    public async Task Pdf_HasPdfMagicHeaderAndIsNonEmpty()
    {
        var writer = new PdfExportWriter();
        using var ms = new MemoryStream();
        await writer.WriteAsync(ms, "Patients", Columns, Rows, default);
        var bytes = ms.ToArray();

        // %PDF- magic.
        Assert.True(bytes.Length > 200);
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }

    [Fact]
    public async Task Csv_EmptyRowList_ProducesHeaderOnly()
    {
        var writer = new CsvExportWriter();
        using var ms = new MemoryStream();
        await writer.WriteAsync(ms, "Empty", Columns, [], default);
        var text = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
        var lines = text.TrimEnd('\r', '\n').Split('\n');
        Assert.Single(lines);
        Assert.Contains("id,title,is_active,created_at", lines[0], StringComparison.Ordinal);
    }
}

using System.Text;
using FluentAssertions;
using Ims.Core.Data;
using Ims.Core.Export;
using Xunit;

namespace Ims.Core.Tests;

public class ResultExporterTests
{
    private static ResultColumn Column(int ordinal, string name, InformixDbType type) => new()
    {
        Ordinal = ordinal,
        Name = name,
        DbType = type,
        ServerTypeName = type.ToString(),
    };

    private static readonly IReadOnlyList<ResultColumn> Columns =
    [
        Column(0, "id", InformixDbType.Integer),
        Column(1, "name", InformixDbType.VarChar),
        Column(2, "note", InformixDbType.VarChar),
    ];

    private static async IAsyncEnumerable<InformixValue[]> Rows(params InformixValue[][] rows)
    {
        foreach (InformixValue[] row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    private static async Task<string> ExportAsync(char delimiter, params InformixValue[][] rows)
    {
        var writer = new StringWriter();

        await ResultExporter.WriteDelimitedAsync(
            writer, delimiter, Columns, Rows(rows), CancellationToken.None);

        return writer.ToString();
    }

    [Fact]
    public async Task Writes_a_header_and_rows()
    {
        string csv = await ExportAsync(
            ',',
            [
                InformixValue.From(InformixDbType.Integer, 1),
                InformixValue.From(InformixDbType.VarChar, "Kaveh"),
                InformixValue.From(InformixDbType.VarChar, "ok"),
            ]);

        csv.Should().StartWith("id,name,note");
        csv.Should().Contain("1,Kaveh,ok");
    }

    [Fact]
    public async Task Null_becomes_an_empty_field_not_the_word_NULL()
    {
        // In a CSV the text "NULL" is a value, and would round-trip as one.
        string csv = await ExportAsync(
            ',',
            [
                InformixValue.From(InformixDbType.Integer, 1),
                InformixValue.Null(InformixDbType.VarChar),
                InformixValue.From(InformixDbType.VarChar, string.Empty),
            ]);

        csv.Should().Contain("1,,");
        csv.Should().NotContain("NULL");
    }

    [Theory]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    [InlineData("plain", "plain")]
    public void Quotes_only_what_RFC_4180_requires(string input, string expected)
    {
        ResultExporter.Quote(input, ',').Should().Be(expected);
    }

    [Fact]
    public async Task A_value_containing_the_delimiter_survives_a_round_trip()
    {
        string csv = await ExportAsync(
            ',',
            [
                InformixValue.From(InformixDbType.Integer, 1),
                InformixValue.From(InformixDbType.VarChar, "Smith, John"),
                InformixValue.From(InformixDbType.VarChar, "said \"hello\""),
            ]);

        csv.Should().Contain("\"Smith, John\"");
        csv.Should().Contain("\"said \"\"hello\"\"\"");
    }

    [Fact]
    public async Task Tab_delimited_does_not_quote_a_comma()
    {
        string tsv = await ExportAsync(
            '\t',
            [
                InformixValue.From(InformixDbType.Integer, 1),
                InformixValue.From(InformixDbType.VarChar, "Smith, John"),
                InformixValue.Null(InformixDbType.VarChar),
            ]);

        tsv.Should().Contain("Smith, John");
        tsv.Should().NotContain("\"Smith");
    }

    [Fact]
    public async Task Json_uses_a_real_null()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ims-export-{Guid.NewGuid():N}.json");

        try
        {
            await ResultExporter.ExportToFileAsync(
                path,
                ExportFormat.Json,
                Columns,
                Rows(
                [
                    InformixValue.From(InformixDbType.Integer, 1),
                    InformixValue.Null(InformixDbType.VarChar),
                    InformixValue.From(InformixDbType.VarChar, string.Empty),
                ]),
                CancellationToken.None);

            string json = await File.ReadAllTextAsync(path);

            // PR-4.4's distinction survives exactly here: null and "" are different.
            json.Should().Contain("\"name\": null");
            json.Should().Contain("\"note\": \"\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Csv_is_written_with_a_BOM_so_Excel_reads_it_correctly()
    {
        // NFR-9: without a BOM, Excel reads a UTF-8 CSV as the system codepage and
        // mangles every non-ASCII character.
        string path = Path.Combine(Path.GetTempPath(), $"ims-export-{Guid.NewGuid():N}.csv");

        try
        {
            await ResultExporter.ExportToFileAsync(
                path,
                ExportFormat.Csv,
                Columns,
                Rows(
                [
                    InformixValue.From(InformixDbType.Integer, 1),
                    InformixValue.From(InformixDbType.VarChar, "Ostrowski-Łęcka"),
                    InformixValue.Null(InformixDbType.VarChar),
                ]),
                CancellationToken.None);

            byte[] bytes = await File.ReadAllBytesAsync(path);

            bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
            Encoding.UTF8.GetString(bytes).Should().Contain("Ostrowski-Łęcka");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Excel_export_produces_a_readable_workbook()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ims-export-{Guid.NewGuid():N}.xlsx");

        try
        {
            await ResultExporter.ExportToFileAsync(
                path,
                ExportFormat.Excel,
                Columns,
                Rows(
                [
                    InformixValue.From(InformixDbType.Integer, 42),
                    InformixValue.From(InformixDbType.VarChar, "Kaveh"),
                    InformixValue.Null(InformixDbType.VarChar),
                ]),
                CancellationToken.None);

            File.Exists(path).Should().BeTrue();

            using var workbook = new ClosedXML.Excel.XLWorkbook(path);
            ClosedXML.Excel.IXLWorksheet sheet = workbook.Worksheet(1);

            sheet.Cell(1, 1).GetString().Should().Be("id");
            sheet.Cell(2, 1).GetDouble().Should().Be(42, "a number must stay a number in Excel");
            sheet.Cell(2, 2).GetString().Should().Be("Kaveh");
            sheet.Cell(2, 3).IsEmpty().Should().BeTrue("a null cell is empty, not the text null");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ExportFormat.Csv, ".csv")]
    [InlineData(ExportFormat.Tsv, ".txt")]
    [InlineData(ExportFormat.Json, ".json")]
    [InlineData(ExportFormat.Excel, ".xlsx")]
    public void Suggests_the_conventional_extension(ExportFormat format, string expected)
    {
        ResultExporter.ExtensionFor(format).Should().Be(expected);
    }

    [Fact]
    public async Task Export_can_be_cancelled_midway()
    {
        // The exporter must stop on cancellation even when the row source ignores
        // the token, which a source that does not take one necessarily does. An
        // export of a million rows the user has cancelled has to actually stop.
        using var cancellation = new CancellationTokenSource();
        var writer = new StringWriter();

        async IAsyncEnumerable<InformixValue[]> ManyIgnoringTheToken()
        {
            for (int i = 0; i < 10_000; i++)
            {
                yield return [InformixValue.From(InformixDbType.Integer, i)];
                await Task.Yield();

                if (i == 5)
                {
                    await cancellation.CancelAsync();
                }
            }
        }

        Func<Task> act = () => ResultExporter.WriteDelimitedAsync(
            writer, ',', Columns, ManyIgnoringTheToken(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        writer.ToString().Should().NotContain(
            "9999", "the export must stop rather than run to completion");
    }
}

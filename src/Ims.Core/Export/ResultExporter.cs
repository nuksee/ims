using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ims.Core.Data;

namespace Ims.Core.Export;

/// <summary>The formats PR-4.6 requires.</summary>
public enum ExportFormat
{
    /// <summary>Comma-separated, RFC 4180 quoting.</summary>
    Csv,

    /// <summary>Tab-delimited text.</summary>
    Tsv,

    /// <summary>An array of objects, one per row.</summary>
    Json,

    /// <summary>An .xlsx workbook.</summary>
    Excel,
}

/// <summary>
/// Writes a result set out (PR-4.6).
/// </summary>
/// <remarks>
/// <para>
/// Consumes the same streaming contract the grid does, so exporting a million rows
/// costs no more memory than displaying them (PR-4.2). The one exception is Excel,
/// which the format itself makes impossible to stream — noted where it happens.
/// </para>
/// <para>
/// NULL is written as an empty field rather than the literal text "NULL", because
/// in a CSV that text is a value and would round-trip as one. PR-4.4 is about the
/// distinction surviving; in a delimited file the empty field is the honest
/// representation, and JSON has a real null to use.
/// </para>
/// </remarks>
public static class ResultExporter
{
    /// <summary>The extension conventionally used for a format.</summary>
    public static string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Tsv => ".txt",
        ExportFormat.Json => ".json",
        ExportFormat.Excel => ".xlsx",
        _ => ".txt",
    };

    /// <summary>Exports to a file.</summary>
    public static async Task ExportToFileAsync(
        string path,
        ExportFormat format,
        IReadOnlyList<ResultColumn> columns,
        IAsyncEnumerable<InformixValue[]> rows,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (format == ExportFormat.Excel)
        {
            await ExportExcelAsync(path, columns, rows, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        // UTF-8 with a BOM: Excel misreads a BOM-less UTF-8 CSV as the system
        // codepage, which mangles every non-ASCII character (NFR-9).
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        switch (format)
        {
            case ExportFormat.Csv:
                await WriteDelimitedAsync(writer, ',', columns, rows, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Tsv:
                await WriteDelimitedAsync(writer, '\t', columns, rows, cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Json:
                await WriteJsonAsync(writer, columns, rows, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported format.");
        }
    }

    /// <summary>Exports to a writer. Used by copy-to-clipboard as well as by files.</summary>
    public static Task WriteDelimitedAsync(
        TextWriter writer,
        char delimiter,
        IReadOnlyList<ResultColumn> columns,
        IAsyncEnumerable<InformixValue[]> rows,
        CancellationToken cancellationToken) =>
        WriteDelimitedCoreAsync(writer, delimiter, columns, rows, cancellationToken);

    private static async Task WriteDelimitedCoreAsync(
        TextWriter writer,
        char delimiter,
        IReadOnlyList<ResultColumn> columns,
        IAsyncEnumerable<InformixValue[]> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        await writer.WriteLineAsync(
            string.Join(delimiter, columns.Select(c => Quote(c.Name, delimiter))))
            .ConfigureAwait(false);

        await foreach (InformixValue[] row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Checked here as well as passed to the source. WithCancellation only
            // reaches a source that accepts the token, and an export the user has
            // cancelled must stop regardless of who produced the rows.
            cancellationToken.ThrowIfCancellationRequested();

            var builder = new StringBuilder();

            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(delimiter);
                }

                builder.Append(Quote(row[i].ToDisplayString(nullRepresentation: string.Empty), delimiter));
            }

            await writer.WriteLineAsync(builder.ToString()).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        TextWriter writer,
        IReadOnlyList<ResultColumn> columns,
        IAsyncEnumerable<InformixValue[]> rows,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync("[").ConfigureAwait(false);

        var first = true;

        await foreach (InformixValue[] row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!first)
            {
                await writer.WriteLineAsync(",").ConfigureAwait(false);
            }

            first = false;

            var builder = new StringBuilder("  {");

            for (int i = 0; i < row.Length && i < columns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(JsonEncode(columns[i].Name)).Append(": ");

                // JSON has a real null, so PR-4.4's distinction survives exactly here.
                builder.Append(row[i].IsNull
                    ? "null"
                    : JsonEncode(row[i].ToDisplayString(nullRepresentation: string.Empty)));
            }

            builder.Append('}');

            await writer.WriteAsync(builder.ToString()).ConfigureAwait(false);
        }

        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("]").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an .xlsx workbook.
    /// </summary>
    /// <remarks>
    /// The one export that cannot stream: the format requires the whole sheet in
    /// memory before it can be zipped. Rows are capped at Excel's own limit, and
    /// the caller is told when that happens rather than silently losing data.
    /// </remarks>
    private static async Task ExportExcelAsync(
        string path,
        IReadOnlyList<ResultColumn> columns,
        IAsyncEnumerable<InformixValue[]> rows,
        CancellationToken cancellationToken)
    {
        const int excelRowLimit = 1_048_575; // one reserved for the header

        using var workbook = new ClosedXML.Excel.XLWorkbook();
        ClosedXML.Excel.IXLWorksheet sheet = workbook.AddWorksheet("Results");

        for (int i = 0; i < columns.Count; i++)
        {
            sheet.Cell(1, i + 1).Value = columns[i].Name;
        }

        sheet.Row(1).Style.Font.Bold = true;

        int rowNumber = 1;
        var truncated = false;

        await foreach (InformixValue[] row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (rowNumber > excelRowLimit)
            {
                truncated = true;
                break;
            }

            rowNumber++;

            for (int i = 0; i < row.Length && i < columns.Count; i++)
            {
                // A null cell is left genuinely empty, not filled with the text "null".
                if (row[i].IsNull)
                {
                    continue;
                }

                ClosedXML.Excel.IXLCell cell = sheet.Cell(rowNumber, i + 1);

                switch (row[i].Value)
                {
                    case decimal or double or float or int or long or short:
                        cell.Value = Convert.ToDouble(row[i].Value, CultureInfo.InvariantCulture);
                        break;
                    case bool boolean:
                        cell.Value = boolean;
                        break;
                    default:
                        cell.Value = row[i].ToDisplayString(nullRepresentation: string.Empty);
                        break;
                }
            }
        }

        sheet.Columns().AdjustToContents();

        if (truncated)
        {
            sheet.Cell(1, columns.Count + 2).Value =
                $"Truncated at Excel's limit of {excelRowLimit:N0} rows.";
        }

        workbook.SaveAs(path);
    }

    /// <summary>RFC 4180 quoting: quote when the value contains a delimiter, quote or newline.</summary>
    internal static string Quote(string value, char delimiter)
    {
        bool needsQuoting = value.Contains(delimiter, StringComparison.Ordinal)
                            || value.Contains('"', StringComparison.Ordinal)
                            || value.Contains('\n', StringComparison.Ordinal)
                            || value.Contains('\r', StringComparison.Ordinal);

        if (!needsQuoting)
        {
            return value;
        }

        return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }

    private static string JsonEncode(string value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

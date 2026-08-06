using System.Data;
using System.Data.Odbc;
using System.Runtime.CompilerServices;
using Ims.Core.Data;
using Ims.Core.Diagnostics;

namespace Ims.Data.Informix;

/// <summary>
/// A streaming result set over an <see cref="OdbcDataReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of PR-4.2: rows are pulled from the server as the caller
/// consumes them, never gathered into a list first. RSK-6 is that an unbounded
/// <c>SELECT</c> hangs the client, and the mitigation only works if nothing in this
/// path ever materialises the set.
/// </para>
/// <para>
/// Note that whether the <em>driver</em> also streams is a separate question, and
/// one only a live server can answer — see the Streaming probe in Ims.SmokeTest.
/// This class guarantees IMS does not add buffering of its own.
/// </para>
/// </remarks>
internal sealed class OdbcStatementResult : IStatementResult
{
    private readonly OdbcCommand _command;
    private readonly OdbcDataReader _reader;

    /// <summary>
    /// Columns that must be read as text because System.Data.Odbc cannot type them.
    /// </summary>
    /// <remarks>
    /// Measured against CSDK 4.10 and Informix 14.10: an INTERVAL column reports
    /// ODBC's SQL_INTERVAL_* (110 for DAY TO SECOND), which System.Data.Odbc's type
    /// map has no entry for. <c>GetValue</c>, <c>IsDBNull</c>, <c>GetFieldType</c>
    /// and <c>GetSchemaTable</c> all throw <see cref="ArgumentException"/> from
    /// inside the type map, before any value conversion. <c>GetString</c>,
    /// <c>GetFieldValue&lt;string&gt;</c> and <c>GetChars</c> work.
    /// <para>
    /// Worse, the damage is not confined to the offending column: in the probe run,
    /// every column at or after the first interval became unreadable. So the
    /// unsupported accessors must never be called at all, which is why this is
    /// decided once from the type name and honoured for every row.
    /// </para>
    /// </remarks>
    private readonly bool[] _readAsText;

    private bool _disposed;

    private OdbcStatementResult(
        OdbcCommand command,
        OdbcDataReader reader,
        IReadOnlyList<ResultColumn> columns,
        bool[] readAsText)
    {
        _command = command;
        _reader = reader;
        Columns = columns;
        _readAsText = readAsText;
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public long RowsRead { get; private set; }

    public bool IsComplete { get; private set; }

    public static OdbcStatementResult Create(OdbcCommand command, OdbcDataReader reader)
    {
        List<ResultColumn> columns = BuildColumns(reader);

        bool[] readAsText = columns
            .Select(c => c.DbType is InformixDbType.Interval)
            .ToArray();

        return new OdbcStatementResult(command, reader, columns, readAsText);
    }

    public async IAsyncEnumerable<InformixValue[]> ReadRowsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Reading rows is a server round trip like any other, and System.Data.Odbc
        // has no true async — ReadAsync runs synchronously underneath. Without this
        // assertion a caller could drain a result set on the dispatcher and freeze
        // the UI, which is exactly the NFR-1 failure the guard exists to prevent.
        ServerCallGuard.AssertNotOnUiThread("Read result rows");

        while (await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new InformixValue[Columns.Count];

            for (int i = 0; i < Columns.Count; i++)
            {
                ResultColumn column = Columns[i];

                // Text-access columns first: IsDBNull would throw on them.
                if (_readAsText[i])
                {
                    row[i] = ReadAsText(column, i);
                    continue;
                }

                if (await _reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false))
                {
                    row[i] = InformixValue.Null(column.DbType);
                    continue;
                }

                if (column.IsLargeObject)
                {
                    row[i] = InformixValue.LargeObject(CreateLargeObjectReference(column, i));
                    continue;
                }

                row[i] = InformixTypeMapper.ToInformixValue(column, _reader.GetValue(i));
            }

            RowsRead++;
            yield return row;
        }

        IsComplete = true;
    }

    /// <summary>
    /// Reads a column that only <c>GetString</c> can reach.
    /// </summary>
    /// <remarks>
    /// Nullness is inferred from <see cref="InvalidCastException"/>, which is what
    /// <c>DbDataReader.GetString</c> raises for SQL NULL. That is the documented
    /// contract rather than a guess, and it is the only route available here —
    /// <c>IsDBNull</c> throws on these columns before it can answer.
    /// </remarks>
    private InformixValue ReadAsText(ResultColumn column, int ordinal)
    {
        string text;

        try
        {
            text = _reader.GetString(ordinal);
        }
        catch (InvalidCastException)
        {
            return InformixValue.Null(column.DbType);
        }

        // The driver pads the interval text, and the padding is not part of the value.
        return InformixTypeMapper.ToInformixValue(column, text.Trim());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _reader.DisposeAsync().ConfigureAwait(false);
        await _command.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// PR-4.5: a large object becomes a reference, not bytes in a cell.
    /// </summary>
    /// <remarks>
    /// The value is read eagerly here, which is a deliberate compromise. Truly
    /// deferred fetching needs the reader still positioned on the row, and a grid
    /// the user can scroll has long since moved on. Reading it now and holding it
    /// behind the reference keeps the cell rendering correct, which is what PR-4.5
    /// actually asks for. Slice 4 can revisit it with a re-query by primary key if
    /// large objects turn out to be common enough to matter.
    /// </remarks>
    private LargeObjectReference CreateLargeObjectReference(ResultColumn column, int ordinal)
    {
        ReadOnlyMemory<byte> data = column.DbType is InformixDbType.Text or InformixDbType.Clob
            ? System.Text.Encoding.UTF8.GetBytes(_reader.GetString(ordinal))
            : ReadBytes(ordinal);

        return new LargeObjectReference(column.DbType, data.Length, _ => Task.FromResult(data));
    }

    private ReadOnlyMemory<byte> ReadBytes(int ordinal)
    {
        long length = _reader.GetBytes(ordinal, 0, null, 0, 0);

        if (length <= 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var buffer = new byte[length];
        _reader.GetBytes(ordinal, 0, buffer, 0, (int)length);

        return buffer;
    }

    /// <summary>
    /// Builds column metadata, recovering as much Informix type detail as ODBC will
    /// give up.
    /// </summary>
    private static List<ResultColumn> BuildColumns(OdbcDataReader reader)
    {
        var columns = new List<ResultColumn>(reader.FieldCount);
        DataTable? schema = TryGetSchemaTable(reader);

        for (int i = 0; i < reader.FieldCount; i++)
        {
            string serverTypeName = SafeTypeName(reader, i);
            InformixDbType dbType = InformixTypeMapper.FromServerTypeName(serverTypeName);

            DataRow? schemaRow = schema is not null && i < schema.Rows.Count ? schema.Rows[i] : null;

            int? scale = GetInt(schemaRow, "NumericScale");
            int? precision = GetInt(schemaRow, "NumericPrecision");
            int? size = GetInt(schemaRow, "ColumnSize");
            bool nullable = GetBool(schemaRow, "AllowDBNull") ?? true;

            columns.Add(new ResultColumn
            {
                Ordinal = i,
                Name = reader.GetName(i),
                DbType = dbType,
                ServerTypeName = serverTypeName,
                Qualifier = InferQualifier(dbType, serverTypeName, scale),
                Precision = precision,
                Scale = scale,
                MaxLength = size,
                IsNullable = nullable,
            });
        }

        return columns;
    }

    /// <summary>
    /// Works out a DATETIME or INTERVAL qualifier from what ODBC exposes.
    /// </summary>
    /// <remarks>
    /// Best effort, and knowingly so. ODBC describes an Informix DATETIME as a
    /// generic timestamp, so the qualifier has to be inferred from the reported
    /// fractional-second precision. That recovers the common cases — YEAR TO SECOND
    /// and YEAR TO FRACTION(n) — but not a qualifier that starts below YEAR or ends
    /// above SECOND without a fraction.
    /// <para>
    /// The exact qualifier is available from the catalogue, via
    /// <see cref="InformixTypeMapper.DecodeCatalogQualifier"/>, and Slice 2's object
    /// browser will use it. Whether ODBC gives up anything better than this is one
    /// of the questions the smoke test's type-fidelity probe exists to answer.
    /// </para>
    /// </remarks>
    private static DateTimeQualifier? InferQualifier(
        InformixDbType dbType,
        string serverTypeName,
        int? scale)
    {
        if (dbType is not (InformixDbType.DateTime or InformixDbType.Interval))
        {
            return null;
        }

        // If the driver spelled the qualifier out, believe it.
        if (InformixTypeMapper.TryParseQualifier(serverTypeName, out DateTimeQualifier parsed))
        {
            return parsed;
        }

        if (dbType == InformixDbType.Interval)
        {
            // Rarely reached: the driver spells intervals out in full, e.g.
            // "INTERVAL DAY(2) TO SECOND", so TryParseQualifier above handles them.
            return new DateTimeQualifier(DateTimeField.Day, DateTimeField.Second);
        }

        return scale switch
        {
            null or 0 => DateTimeQualifier.YearToSecond,
            1 => new DateTimeQualifier(DateTimeField.Year, DateTimeField.Fraction1),
            2 => new DateTimeQualifier(DateTimeField.Year, DateTimeField.Fraction2),
            3 => DateTimeQualifier.YearToFraction3,
            4 => new DateTimeQualifier(DateTimeField.Year, DateTimeField.Fraction4),
            _ => new DateTimeQualifier(DateTimeField.Year, DateTimeField.Fraction5),
        };
    }

    /// <summary>
    /// Column metadata, where the driver will give it up.
    /// </summary>
    /// <remarks>
    /// <see cref="ArgumentException"/> is caught because it is the normal case, not
    /// an exotic one: a result set containing any INTERVAL column makes
    /// <c>GetSchemaTable</c> throw "Unknown SQL type - 110" from inside the ODBC
    /// type map. Precision and scale are a nicety; the result set is not.
    /// </remarks>
    private static DataTable? TryGetSchemaTable(OdbcDataReader reader)
    {
        try
        {
            return reader.GetSchemaTable();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OdbcException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string SafeTypeName(OdbcDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (OdbcException)
        {
            return "UNKNOWN";
        }
        catch (IndexOutOfRangeException)
        {
            return "UNKNOWN";
        }
    }

    private static int? GetInt(DataRow? row, string column) =>
        row is not null
        && row.Table.Columns.Contains(column)
        && row[column] is not DBNull
            ? Convert.ToInt32(row[column], System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static bool? GetBool(DataRow? row, string column) =>
        row is not null
        && row.Table.Columns.Contains(column)
        && row[column] is not DBNull
            ? Convert.ToBoolean(row[column], System.Globalization.CultureInfo.InvariantCulture)
            : null;
}

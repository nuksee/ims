using Ims.Core.Data;

namespace Ims.Data.Informix;

/// <summary>
/// A result set already read into memory, up to a bounded number of rows.
/// </summary>
/// <remarks>
/// <para>
/// Exists because of a constraint that only showed up against a real server: a
/// single ODBC connection can have one open cursor at a time. A script with two
/// <c>SELECT</c>s therefore cannot keep both streaming — the second statement
/// cannot execute until the first reader is closed.
/// </para>
/// <para>
/// PR-4.7 (keep several result sets) and PR-4.2 (stream, never materialise) pull
/// against each other here, and PR-4.2 is the Must. The resolution: every
/// row-returning statement except the last in a script is read into memory up to
/// <see cref="MaximumBufferedRows"/> and its cursor closed, so the script can
/// continue. The last one streams as normal.
/// </para>
/// <para>
/// The cap is what keeps PR-4.2's promise intact: an unbounded <c>SELECT</c> in the
/// middle of a script degrades to a truncated result rather than exhausting memory.
/// Truncation is reported, never silent.
/// </para>
/// </remarks>
internal sealed class BufferedStatementResult : IStatementResult
{
    /// <summary>
    /// How many rows an intermediate result keeps. Generous enough to be useful,
    /// bounded enough that a runaway statement cannot exhaust memory.
    /// </summary>
    public const int MaximumBufferedRows = 10_000;

    private readonly List<InformixValue[]> _rows;

    private BufferedStatementResult(
        IReadOnlyList<ResultColumn> columns,
        List<InformixValue[]> rows,
        bool wasTruncated)
    {
        Columns = columns;
        _rows = rows;
        WasTruncated = wasTruncated;
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public long RowsRead => _rows.Count;

    public bool IsComplete => true;

    public bool WasTruncated { get; }

    /// <summary>
    /// Drains <paramref name="source"/> up to the cap, then disposes it — releasing
    /// the cursor so the next statement in the script can run.
    /// </summary>
    public static async Task<BufferedStatementResult> CreateAsync(
        IStatementResult source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rows = new List<InformixValue[]>();
        var truncated = false;

        try
        {
            await foreach (InformixValue[] row in source
                .ReadRowsAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (rows.Count >= MaximumBufferedRows)
                {
                    truncated = true;
                    break;
                }

                rows.Add(row);
            }

            return new BufferedStatementResult(source.Columns, rows, truncated);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<InformixValue[]> ReadRowsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        foreach (InformixValue[] row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _rows.Clear();
        return ValueTask.CompletedTask;
    }
}

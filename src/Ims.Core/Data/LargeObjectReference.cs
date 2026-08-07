namespace Ims.Core.Data;

/// <summary>
/// A handle to a BYTE, TEXT, BLOB or CLOB value that has not been fetched.
/// </summary>
/// <remarks>
/// <para>
/// PR-4.5 requires large objects to appear as "a viewable value, not raw bytes
/// in a cell". PR-4.2 requires results to stream rather than materialise. Both
/// point the same way: a result row carries a reference and a size, and the
/// bytes are fetched only when the user opens the value.
/// </para>
/// <para>
/// Fetching is therefore an explicit, cancellable act — which also keeps it
/// inside PR-6.2, since retrieving a large object is a server round trip the
/// user asked for.
/// </para>
/// </remarks>
public sealed class LargeObjectReference
{
    private readonly Func<CancellationToken, Task<ReadOnlyMemory<byte>>> _fetch;

    public LargeObjectReference(
        InformixDbType dbType,
        long? sizeInBytes,
        Func<CancellationToken, Task<ReadOnlyMemory<byte>>> fetch)
    {
        DbType = dbType;
        SizeInBytes = sizeInBytes;
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
    }

    public InformixDbType DbType { get; }

    /// <summary>Size where the server reported one; null when it did not.</summary>
    public long? SizeInBytes { get; }

    /// <summary>True for TEXT and CLOB, which the viewer should render as text.</summary>
    public bool IsCharacterData => DbType is InformixDbType.Text or InformixDbType.Clob;

    /// <summary>
    /// What the grid cell shows in place of the value — never the bytes themselves.
    /// </summary>
    public string Placeholder =>
        SizeInBytes is { } size
            ? $"<{DbType.ToString().ToUpperInvariant()}, {FormatSize(size)}>"
            : $"<{DbType.ToString().ToUpperInvariant()}>";

    /// <summary>Fetches the value. Called only when the user opens it.</summary>
    public Task<ReadOnlyMemory<byte>> FetchAsync(CancellationToken cancellationToken) =>
        _fetch(cancellationToken);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}

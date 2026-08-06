using Ims.Core.Catalog;

namespace Ims.Core.Completion;

/// <summary>
/// What completion is allowed to know about the schema, and how fast.
/// </summary>
/// <remarks>
/// <para>
/// Every member is synchronous and returns immediately, because this is called
/// between one keystroke and the next. A completion list that waits on a server
/// round trip is a caret that stutters, and NFR-1 and PR-8.5 do not carve out an
/// exception for typing — they are mostly <em>about</em> typing.
/// </para>
/// <para>
/// So the contract is deliberately weak: these return what is cached, and nothing
/// else. <see cref="RequestColumns"/> is how the caller says "I wanted those" — the
/// answer arrives for the next keystroke, not this one.
/// </para>
/// </remarks>
public interface ICatalogSnapshot
{
    /// <summary>Objects that have been loaded so far. Possibly empty, never null.</summary>
    IReadOnlyList<SchemaObject> Objects { get; }

    /// <summary>Owners seen so far.</summary>
    IReadOnlyList<string> Owners { get; }

    /// <summary>The columns of a table, if they are cached. Empty if they are not.</summary>
    IReadOnlyList<string> ColumnsOf(string name, string? owner);

    /// <summary>
    /// Asks for a table's columns to be fetched in the background.
    /// </summary>
    /// <remarks>
    /// Returns at once. Calling it repeatedly for the same table is expected and must
    /// be cheap — PR-6.4 makes one fetch per table the budget, not one per keystroke.
    /// </remarks>
    void RequestColumns(string name, string? owner);
}

/// <summary>An empty snapshot, for an editor with no connection behind it.</summary>
public sealed class EmptyCatalogSnapshot : ICatalogSnapshot
{
    public static EmptyCatalogSnapshot Instance { get; } = new();

    public IReadOnlyList<SchemaObject> Objects => [];

    public IReadOnlyList<string> Owners => [];

    public IReadOnlyList<string> ColumnsOf(string name, string? owner) => [];

    public void RequestColumns(string name, string? owner)
    {
    }
}

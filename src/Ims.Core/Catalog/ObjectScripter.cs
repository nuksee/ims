namespace Ims.Core.Catalog;

/// <summary>The outcome of a scripting request (PR-2.6).</summary>
/// <param name="Sql">The script, or empty when <paramref name="Unsupported"/> is set.</param>
/// <param name="Unsupported">
/// Why nothing could be produced, in words for the user. Null on success.
/// </param>
/// <param name="QueriesUsed">The catalogue queries the script was built from (PR-8.2).</param>
public sealed record ScriptResult(
    string Sql,
    string? Unsupported = null,
    IReadOnlyList<string>? QueriesUsed = null);

/// <summary>
/// Fetches whatever an object needs to be scripted, and scripts it (PR-2.6).
/// </summary>
/// <remarks>
/// Split from <see cref="DdlScripter"/> so that the text generation stays pure and
/// testable without a server, and the I/O stays in one small place. It lives in
/// <c>Ims.Core</c> rather than the WPF layer because nothing here is about Windows,
/// and NFR-5 asks that a later cross-platform client not have to reimplement it.
/// </remarks>
public sealed class ObjectScripter(ICatalogReader catalog)
{
    private readonly ICatalogReader _catalog = catalog
        ?? throw new ArgumentNullException(nameof(catalog));

    public async Task<ScriptResult> ScriptAsync(
        SchemaObject schemaObject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schemaObject);

        switch (schemaObject.Kind)
        {
            case SchemaObjectKind.Table:
            {
                TableDetail detail = await _catalog
                    .GetTableDetailAsync(schemaObject.TabId, cancellationToken)
                    .ConfigureAwait(false);

                return new ScriptResult(DdlScripter.ScriptTable(detail), null, detail.QueriesUsed);
            }

            case SchemaObjectKind.View:
            {
                CatalogResult<string> text = await _catalog
                    .GetViewSourceAsync(schemaObject.TabId, cancellationToken)
                    .ConfigureAwait(false);

                return new ScriptResult(
                    DdlScripter.ScriptView(schemaObject, text.Items), null, [text.Sql]);
            }

            case SchemaObjectKind.Procedure or SchemaObjectKind.Function:
            {
                // A routine node carries procid in TabId — the two catalogues use
                // different identifiers for the same tree position.
                CatalogResult<string> source = await _catalog
                    .GetRoutineSourceAsync(schemaObject.TabId, cancellationToken)
                    .ConfigureAwait(false);

                return new ScriptResult(
                    DdlScripter.ScriptRoutine(schemaObject, source.Items), null, [source.Sql]);
            }

            case SchemaObjectKind.Index:
            {
                // An index node carries the tabid of the table it is on, so the whole
                // table's detail is what identifies it. One extra round trip buys the
                // key order and the direction of each column, which sysindexes alone
                // would give as bare part numbers.
                TableDetail detail = await _catalog
                    .GetTableDetailAsync(schemaObject.TabId, cancellationToken)
                    .ConfigureAwait(false);

                IndexDetail? index = detail.Indexes.FirstOrDefault(i =>
                    string.Equals(i.Name, schemaObject.Name, StringComparison.OrdinalIgnoreCase));

                return index is null
                    ? new ScriptResult(
                        string.Empty,
                        $"{schemaObject.Name} was not found on {detail.Object.Name}. "
                        + "The tree may be showing a stale listing — refresh it and try again.")
                    : new ScriptResult(
                        DdlScripter.ScriptIndex(detail.Object, index), null, detail.QueriesUsed);
            }

            default:
                // Named rather than silently producing nothing. PR-8.4: IMS says what
                // it cannot do instead of leaving the user to infer it from an empty tab.
                return new ScriptResult(
                    string.Empty,
                    $"IMS does not script a {Describe(schemaObject.Kind)} yet. "
                    + "Tables, views, indexes, procedures and functions are supported.");
        }
    }

    /// <summary>True when <see cref="ScriptAsync"/> would produce a script.</summary>
    public static bool CanScript(SchemaObjectKind kind) =>
        kind is SchemaObjectKind.Table or SchemaObjectKind.View or SchemaObjectKind.Index
             or SchemaObjectKind.Procedure or SchemaObjectKind.Function;

    private static string Describe(SchemaObjectKind kind) => kind switch
    {
        SchemaObjectKind.Synonym or SchemaObjectKind.PrivateSynonym => "synonym",
        SchemaObjectKind.Sequence => "sequence",
        SchemaObjectKind.UserDefinedType => "user-defined type",
        SchemaObjectKind.Constraint => "constraint",
        SchemaObjectKind.Trigger => "trigger",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

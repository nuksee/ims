namespace Ims.Core.Completion;

/// <summary>
/// The Informix language, as completion offers it (PR-3.2).
/// </summary>
/// <remarks>
/// <para>
/// The detail text is the point of this file, not the word list. A generic SQL tool
/// can offer <c>MATCHES</c>; only one that knows Informix can say that its wildcards
/// are <c>*</c> and <c>?</c> rather than <c>%</c> and <c>_</c>, which is the thing
/// that stops the next hour being wasted. PR-8.3 — "IMS should teach the platform"
/// — is a requirement, and this is where most of it is paid.
/// </para>
/// <para>
/// Entries with nothing Informix-specific to say carry no detail. An explanation
/// that only restates the keyword trains people to stop reading the column.
/// </para>
/// </remarks>
public static class InformixVocabulary
{
    private static CompletionItem Keyword(string text, string? detail = null) =>
        new(text, CompletionKind.Keyword, detail);

    private static CompletionItem Function(string text, string? detail = null) =>
        new(text, CompletionKind.BuiltInFunction, detail);

    private static CompletionItem Type(string text, string? detail = null) =>
        new(text, CompletionKind.DataType, detail);

    /// <summary>SQL and SPL keywords.</summary>
    public static IReadOnlyList<CompletionItem> Keywords { get; } =
    [
        Keyword("SELECT"),
        Keyword("FROM"),
        Keyword("WHERE"),
        Keyword("GROUP BY"),
        Keyword("HAVING"),
        Keyword("ORDER BY"),
        Keyword("ASC"),
        Keyword("DESC"),
        Keyword("DISTINCT"),
        Keyword("UNION"),
        Keyword("UNION ALL"),
        Keyword("INTERSECT"),

        Keyword("FIRST", "Informix's row limit: SELECT FIRST 10 … — no LIMIT clause"),
        Keyword("SKIP", "Rows to discard first: SELECT SKIP 20 FIRST 10 …"),
        Keyword("MATCHES", "Pattern match with * and ? — not LIKE's % and _"),
        Keyword("LIKE", "Pattern match with % and _. MATCHES is the Informix form"),
        Keyword("OUTER", "Informix's older outer-join form: FROM a, OUTER(b)"),

        Keyword("JOIN"),
        Keyword("INNER JOIN"),
        Keyword("LEFT OUTER JOIN"),
        Keyword("RIGHT OUTER JOIN"),
        Keyword("FULL OUTER JOIN"),
        Keyword("ON"),
        Keyword("AS"),
        Keyword("AND"),
        Keyword("OR"),
        Keyword("NOT"),
        Keyword("IN"),
        Keyword("EXISTS"),
        Keyword("BETWEEN"),
        Keyword("IS NULL"),
        Keyword("IS NOT NULL"),
        Keyword("CASE"),
        Keyword("WHEN"),
        Keyword("THEN"),
        Keyword("ELSE"),
        Keyword("END"),

        Keyword("INSERT INTO"),
        Keyword("VALUES"),
        Keyword("UPDATE"),
        Keyword("SET"),
        Keyword("DELETE FROM"),
        Keyword("MERGE"),

        Keyword("INTO TEMP", "Informix's temp table: SELECT … INTO TEMP t WITH NO LOG"),
        Keyword("WITH NO LOG", "Keeps a temp table out of the logical log"),

        Keyword("CREATE TABLE"),
        Keyword("CREATE INDEX"),
        Keyword("CREATE VIEW"),
        Keyword("CREATE PROCEDURE"),
        Keyword("CREATE FUNCTION"),
        Keyword("ALTER TABLE"),
        Keyword("DROP TABLE"),
        Keyword("PRIMARY KEY"),
        Keyword("FOREIGN KEY"),
        Keyword("REFERENCES"),
        Keyword("CONSTRAINT"),
        Keyword("DEFAULT"),
        Keyword("NOT NULL"),
        Keyword("UNIQUE"),
        Keyword("CHECK"),

        Keyword("EXTENT SIZE", "First extent in kilobytes: … EXTENT SIZE 64 NEXT SIZE 64"),
        Keyword("NEXT SIZE", "Every extent after the first, in kilobytes"),
        Keyword("LOCK MODE ROW", "Row-level locking. The table default is page"),
        Keyword("LOCK MODE PAGE"),
        Keyword("FRAGMENT BY", "Spreads a table over dbspaces: … FRAGMENT BY ROUND ROBIN IN dbs1, dbs2"),
        Keyword("IN DBSPACE"),

        Keyword("SET ISOLATION TO", "DIRTY READ, COMMITTED READ, CURSOR STABILITY or REPEATABLE READ"),
        Keyword("SET LOCK MODE TO WAIT", "Wait for a lock rather than failing with -107"),
        Keyword("SET LOCK MODE TO NOT WAIT"),
        Keyword("SET EXPLAIN ON", "Writes the query plan to sqexplain.out on the server"),
        Keyword("UPDATE STATISTICS", "LOW, MEDIUM or HIGH. The optimiser is only as good as these"),
        Keyword("BEGIN WORK"),
        Keyword("COMMIT WORK"),
        Keyword("ROLLBACK WORK"),

        Keyword("DEFINE", "SPL: declares a local variable"),
        Keyword("LET", "SPL: assigns — LET x = 1"),
        Keyword("FOREACH", "SPL: iterates a cursor"),
        Keyword("RETURNING", "SPL: names what a procedure gives back"),
        Keyword("RETURN"),
        Keyword("ON EXCEPTION", "SPL: handles an error by SQLCODE"),
        Keyword("RAISE EXCEPTION"),
        Keyword("END PROCEDURE", "Terminates an SPL body — a semicolon does not"),
        Keyword("END FUNCTION"),
        Keyword("EXECUTE PROCEDURE"),
        Keyword("EXECUTE FUNCTION"),
    ];

    /// <summary>Built-in functions.</summary>
    public static IReadOnlyList<CompletionItem> Functions { get; } =
    [
        Function("COUNT"),
        Function("SUM"),
        Function("AVG"),
        Function("MIN"),
        Function("MAX"),

        Function("TODAY", "The current date, as a DATE"),
        Function("CURRENT", "The current datetime. CURRENT YEAR TO SECOND to fix the qualifier"),
        Function("EXTEND", "Re-qualifies a datetime: EXTEND(col, YEAR TO DAY)"),
        Function("MDY", "Builds a DATE from month, day, year"),
        Function("WEEKDAY", "0 is Sunday"),
        Function("DAY"),
        Function("MONTH"),
        Function("YEAR"),
        Function("DATE"),
        Function("UNITS", "Interval literal: 1 UNITS DAY"),

        Function("NVL", "NVL(a, b) — b when a is null"),
        Function("DECODE", "DECODE(x, w1, r1, w2, r2, default)"),
        Function("COALESCE"),
        Function("NULLIF"),
        Function("CASE"),

        Function("LENGTH", "Ignores trailing blanks on a CHAR. CHAR_LENGTH does not"),
        Function("CHAR_LENGTH"),
        Function("OCTET_LENGTH"),
        Function("TRIM", "TRIM(LEADING '0' FROM col)"),
        Function("SUBSTR", "SUBSTR(col, start, length) — 1-based"),
        Function("SUBSTRING", "ANSI form: SUBSTRING(col FROM 2 FOR 3)"),
        Function("UPPER"),
        Function("LOWER"),
        Function("INITCAP"),
        Function("REPLACE"),
        Function("LPAD"),
        Function("RPAD"),
        Function("TO_CHAR"),
        Function("TO_DATE"),
        Function("TO_NUMBER"),
        Function("CAST", "CAST(x AS INTEGER). Informix also accepts x::INTEGER"),
        Function("HEX"),

        Function("ABS"),
        Function("MOD"),
        Function("POW"),
        Function("ROUND"),
        Function("TRUNC"),
        Function("SQRT"),

        Function("USER", "The connected user"),
        Function("DBSERVERNAME", "The instance this session is on"),
        Function("SITENAME", "Same as DBSERVERNAME"),
        Function(
            "DBINFO",
            "DBINFO('sqlca.sqlerrd1') for the last SERIAL, DBINFO('sessionid') for this session"),

        Function("ROW_NUMBER"),
        Function("RANK"),
        Function("LAG"),
        Function("LEAD"),
        Function("OVER"),
    ];

    /// <summary>Data types, for DDL and CAST.</summary>
    public static IReadOnlyList<CompletionItem> DataTypes { get; } =
    [
        Type("CHAR"),
        Type("VARCHAR", "Up to 255 bytes. LVARCHAR for more"),
        Type("LVARCHAR", "Variable length up to 32 kilobytes"),
        Type("NCHAR"),
        Type("NVARCHAR"),
        Type("SMALLINT"),
        Type("INTEGER"),
        Type("BIGINT"),
        Type("INT8", "The older 8-byte integer. BIGINT is the current one"),
        Type("SERIAL", "Auto-incrementing INTEGER. Never null"),
        Type("SERIAL8"),
        Type("BIGSERIAL"),
        Type("DECIMAL", "DECIMAL(p,s). Without a scale it is floating decimal"),
        Type("MONEY", "DECIMAL with a currency presentation; MONEY(16,2) is usual"),
        Type("FLOAT"),
        Type("SMALLFLOAT"),
        Type("DATE", "A day, with no time. DATETIME if you need one"),
        Type("DATETIME", "Meaningless without a qualifier: DATETIME YEAR TO SECOND"),
        Type("INTERVAL", "Either YEAR TO MONTH or DAY TO FRACTION — the two do not mix"),
        Type("BOOLEAN", "Stores 't' or 'f'"),
        Type("BYTE", "Simple large object, stored in a blobspace"),
        Type("TEXT", "Simple large object for character data"),
        Type("BLOB", "Smart large object, stored in an sbspace"),
        Type("CLOB"),
        Type("SET"),
        Type("MULTISET"),
        Type("LIST"),
    ];

    /// <summary>Everything, for when the caret gives no better clue.</summary>
    public static IReadOnlyList<CompletionItem> All { get; } =
        [.. Keywords, .. Functions, .. DataTypes];
}

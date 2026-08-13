using FluentAssertions;
using Ims.Data.Informix.Catalog;
using Xunit;

namespace Ims.Data.Informix.Tests;

/// <summary>
/// RSK-5 and PR-6.4 — every session query is bounded before it is sent, and none of them
/// selects a type this driver cannot read.
/// </summary>
/// <remarks>
/// Assertions on the SQL text, like <see cref="CatalogQueryCompositionTests"/>. Nothing here
/// opens a socket: the queries are constants, so their shape is checkable without a server —
/// which matters more here than for the catalogue, because these particular queries could not
/// be verified against one before they were written.
/// </remarks>
public class SessionQueryCompositionTests
{
    private static readonly (string Name, string Sql)[] EveryQuery =
    [
        (nameof(SessionQueries.SessionList), SessionQueries.SessionList),
        (nameof(SessionQueries.SessionListMinimal), SessionQueries.SessionListMinimal),
        (nameof(SessionQueries.SessionCount), SessionQueries.SessionCount),
        (nameof(SessionQueries.CurrentSql), SessionQueries.CurrentSql),
        (nameof(SessionQueries.LocksHeld), SessionQueries.LocksHeld),
        (nameof(SessionQueries.LockWaits), SessionQueries.LockWaits),
        (nameof(SessionQueries.LockContention), SessionQueries.LockContention),
        (nameof(SessionQueries.SessionResources), SessionQueries.SessionResources),
        (nameof(SessionQueries.ServerState), SessionQueries.ServerState),
        (nameof(SessionQueries.Profile), SessionQueries.Profile),
        (nameof(SessionQueries.LastCheckpoint), SessionQueries.LastCheckpoint),
    ];

    [Fact]
    public void Every_row_returning_query_is_capped_before_it_is_sent()
    {
        // RSK-5: bounded before it is sent, not merely cancelled once running. OdbcCommand
        // .Cancel() does not reach this server (measured 2026-08-06), so a token stops IMS
        // waiting and the statement runs on — and the test database shares a server with
        // production (DEP-2), so there is no margin for a runaway.
        string[] uncapped = EveryQuery
            .Where(q => !q.Sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase))
            .Where(q => !q.Sql.Contains("MAX(", StringComparison.OrdinalIgnoreCase))
            .Where(q => !q.Sql.Contains("FIRST ", StringComparison.OrdinalIgnoreCase))
            .Select(q => q.Name)
            .ToArray();

        uncapped.Should().BeEmpty(
            "because every session query must carry FIRST n or be an aggregate that returns one row");
    }

    [Fact]
    public void No_session_query_selects_an_interval()
    {
        // System.Data.Odbc has no type-map entry for SQL_INTERVAL_*, so GetValue, IsDBNull,
        // GetFieldType and GetSchemaTable all throw on one — and every column at or after
        // the first INTERVAL becomes unreadable with it. Durations are therefore always an
        // epoch integer converted client-side, never asked of the server.
        foreach ((string name, string sql) in EveryQuery)
        {
            sql.Should().NotContain("INTERVAL", $"because {name} would be unreadable");
        }
    }

    [Fact]
    public void Any_cast_targets_a_readable_type()
    {
        // The other half of the rule above: where a value has to be converted server-side,
        // it becomes CHAR and is parsed as text.
        foreach ((string name, string sql) in EveryQuery.Where(q =>
            q.Sql.Contains("CAST(", StringComparison.OrdinalIgnoreCase)))
        {
            sql.Should().Contain("AS CHAR", $"because {name}'s cast must land on a type ODBC maps");
        }
    }

    [Fact]
    public void Every_per_session_query_is_a_keyed_lookup()
    {
        // PR-6.4: negligible on a production instance. A per-session read that scanned would
        // be the one query in this slice that is not.
        SessionQueries.CurrentSql.Should().Contain("WHERE sqx_sessionid = ?");
        SessionQueries.LocksHeld.Should().Contain("WHERE owner = ?");
        SessionQueries.SessionResources.Should().Contain("WHERE sid = ?");
    }

    [Fact]
    public void The_session_list_leads_with_the_columns_that_are_confirmed()
    {
        // The Slice 0 smoke test read sid and username from syssessions against 14.10, which
        // is what answered Q-1. Uncertain columns go last so a surprise type costs one
        // column rather than the whole tail.
        int sid = SessionQueries.SessionList.IndexOf("sid", StringComparison.Ordinal);
        int user = SessionQueries.SessionList.IndexOf("username", StringComparison.Ordinal);
        int connected = SessionQueries.SessionList.IndexOf("connected", StringComparison.Ordinal);

        sid.Should().BeLessThan(user);
        user.Should().BeLessThan(connected, "because connected is the least certain of the three");
    }

    [Fact]
    public void Keeps_ORDER_BY_last()
    {
        foreach ((string name, string sql) in EveryQuery.Where(q =>
            q.Sql.Contains("ORDER BY", StringComparison.Ordinal)))
        {
            int order = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
            sql[order..].Should().NotContain("WHERE", $"because {name} would not parse");
        }
    }

    [Fact]
    public void The_lock_wait_query_asks_only_for_locks_somebody_is_queued_behind()
    {
        // syslocks.waiter names the waiting session, so one scan answers PR-5.3. The
        // predicate is what keeps it to the rows that matter: on a healthy instance almost
        // every lock has nobody behind it.
        SessionQueries.LockWaits.Should().Contain("waiter IS NOT NULL");
        SessionQueries.LockWaits.Should().NotContain("syslocks h",
            "because the self-join it replaced timed out against 14.10 on 2026-08-13");
    }

    [Fact]
    public void The_contention_fallback_excludes_a_session_contending_with_itself()
    {
        // Without this the self-join reports every lock as contending with itself, and every
        // session would appear blocked by itself.
        SessionQueries.LockContention.Should().Contain("w.owner <> h.owner");
    }

    [Fact]
    public void The_contention_fallback_is_capped_harder_than_anything_else()
    {
        // It is quadratic over an unindexed pseudo-table and measured to time out at ten
        // seconds, so the cap is the only thing bounding it — and a cancel would not reach the
        // server to stop it (RSK-5).
        SessionQueries.ContentionCap.Should().BeLessThan(SessionQueries.LockCap);
        SessionQueries.LockContention.Should()
            .Contain($"FIRST {SessionQueries.ContentionCap}");
    }

    [Fact]
    public void The_timeout_is_shorter_than_the_catalogue_s()
    {
        // A monitor refresh that has not answered in ten seconds has already failed what it
        // was for (NFR-1) — and since the cancel does not reach the server, the timeout is
        // the only thing that actually ends the statement.
        SessionQueries.TimeoutSeconds.Should().BeLessThan(60);
        SessionQueries.TimeoutSeconds.Should().BePositive();
    }
}

/// <summary>
/// PR-5.1, PR-5.3 and PR-8.2 — the pure translators between the server's codes and words.
/// </summary>
/// <remarks>
/// These test the <em>mechanism</em>, not the labels. Whether syssessions.state arrives as a
/// bitmask or a string could not be confirmed against a live server, so the mapping tables are
/// provisional — but the rules around them are not. When someone verifies against a real
/// server they should be changing a table, not a design.
/// </remarks>
public class SessionTranslationTests
{
    [Fact]
    public void An_unrecognised_state_is_never_reported_as_running()
    {
        // The most important assertion in this file. A confident wrong "Running" on a session
        // that is actually blocked would defeat the entire view, and silently: the user would
        // look at the one screen built to tell them they are blocked and be reassured.
        string described = InformixCatalogReader.DescribeSessionState("64");

        described.Should().NotBe("Running");
        described.Should().Contain("64", "because the server's own code is preserved (PR-8.2)");
    }

    [Theory]
    [InlineData("0", "Running")]
    [InlineData("4", "Waiting on a lock")]
    [InlineData("16", "Waiting on a transaction")]
    public void Translates_the_state_codes_it_knows(string raw, string expected) =>
        InformixCatalogReader.DescribeSessionState(raw).Should().Be(expected);

    [Fact]
    public void Reports_every_wait_a_combined_state_carries()
    {
        // The codes are bits, so a session can be waiting on more than one thing.
        InformixCatalogReader.DescribeSessionState("6")
            .Should().Be("Waiting on a condition, a lock");
    }

    [Fact]
    public void Prefers_the_server_s_own_word_over_a_code()
    {
        // If the view hands back text, it is more specific than anything IMS would infer, and
        // PR-8.2's habit is to prefer the server's vocabulary.
        InformixCatalogReader.DescribeSessionState("cond wait").Should().Be("cond wait");
    }

    [Fact]
    public void Trims_a_padded_state()
    {
        // Every CHAR column out of sysmaster arrives padded. An untrimmed idxtype once
        // reported every index in the database as non-unique, which is why this is a test and
        // not a convention.
        InformixCatalogReader.DescribeSessionState("  4   ").Should().Be("Waiting on a lock");
    }

    [Fact]
    public void An_absent_state_is_unknown_rather_than_a_guess()
    {
        InformixCatalogReader.DescribeSessionState(null).Should().Be("Unknown");
        InformixCatalogReader.DescribeSessionState("   ").Should().Be("Unknown");
    }

    [Theory]
    [InlineData("S", "Shared")]
    [InlineData("X", "Exclusive")]
    [InlineData(" x ", "Exclusive")]
    [InlineData("SIX", "Shared with intent exclusive")]
    public void Translates_the_lock_modes_it_knows(string raw, string expected) =>
        InformixCatalogReader.DescribeLockType(raw).Should().Be(expected);

    [Fact]
    public void An_unrecognised_lock_mode_keeps_the_server_s_code()
    {
        InformixCatalogReader.DescribeLockType("ZZ").Should().Be("Unknown (ZZ)");
    }

    [Fact]
    public void Two_shared_locks_do_not_block_each_other()
    {
        // The rule that turns contention into blocking. Two readers on one row coexist
        // perfectly well, and reporting that as a block would fill the view with noise.
        InformixCatalogReader.AreIncompatibleLocks("S", "S").Should().BeFalse();
    }

    [Theory]
    [InlineData("X", "S")]
    [InlineData("S", "X")]
    [InlineData("X", "X")]
    [InlineData("U", "S")]
    public void An_exclusive_lock_blocks(string holder, string waiter) =>
        InformixCatalogReader.AreIncompatibleLocks(holder, waiter).Should().BeTrue();

    [Theory]
    [InlineData("ZZ", "X")]
    [InlineData("X", "ZZ")]
    [InlineData(null, "X")]
    public void An_unrecognised_mode_does_not_claim_a_block(string? holder, string? waiter)
    {
        // Deliberately biased this way. Being wrong here downgrades the answer to contention;
        // being wrong the other way names a blocker IMS cannot justify, and someone might
        // interrupt a colleague's work on the strength of it.
        InformixCatalogReader.AreIncompatibleLocks(holder, waiter).Should().BeFalse();
    }

    [Fact]
    public void A_zero_epoch_is_unknown_rather_than_1970()
    {
        // A server that has recorded no timestamp reports zero, and "connected since 1 January
        // 1970" is the kind of visible absurdity that costs a user their trust in every other
        // number on the screen.
        InformixCatalogReader.FromUnixSeconds(0).Should().BeNull();
        InformixCatalogReader.FromUnixSeconds(-1).Should().BeNull();
        InformixCatalogReader.FromUnixSeconds(null).Should().BeNull();
    }

    [Fact]
    public void Converts_a_real_epoch_to_a_moment()
    {
        // This is how the whole slice avoids the INTERVAL trap: durations are computed from a
        // number rather than asked of the server.
        DateTimeOffset? converted = InformixCatalogReader.FromUnixSeconds(1_754_000_000);

        converted.Should().NotBeNull();
        converted!.Value.ToUniversalTime().Year.Should().Be(2025);
    }

    [Fact]
    public void A_cache_ratio_with_no_reads_is_unknown_rather_than_zero()
    {
        // A freshly booted instance has done no reads. Reporting 0% efficiency for that is a
        // confident wrong answer where "unknown" is the true one — and it would look alarming.
        InformixCatalogReader.ComputeCacheRatio(0, 0).Should().BeNull();
        InformixCatalogReader.ComputeCacheRatio(null, 5).Should().BeNull();
        InformixCatalogReader.ComputeCacheRatio(100, null).Should().BeNull();
    }

    [Fact]
    public void Computes_a_cache_ratio()
    {
        // 1000 logical reads, 20 of which went to disk: 98%.
        InformixCatalogReader.ComputeCacheRatio(1000, 20).Should().BeApproximately(98.0, 0.001);
    }

    [Fact]
    public void Rejects_more_physical_reads_than_logical_ones()
    {
        // Impossible, so the counters are not what IMS assumed. A negative efficiency on
        // screen would be worse than an admitted gap.
        InformixCatalogReader.ComputeCacheRatio(10, 50).Should().BeNull();
    }

    [Fact]
    public void An_unrecognised_server_mode_keeps_its_code()
    {
        InformixCatalogReader.DescribeServerMode(99).Should().Be("Unknown (99)");
        InformixCatalogReader.DescribeServerMode(5).Should().Be("Online");
        InformixCatalogReader.DescribeServerMode(null).Should().BeNull();
    }
}

# Slice 3 — Observe: the session monitor

## Context

IMS is a WPF Informix client. Slices 0–2 (connect, query, browse) are built; Slice 3 is
the largest block of unstarted work and the last Must-carrying slice. Its purpose, from
PRD §4: U1 "needs to know when their own session is blocked and by what", and U2 wants to
see session activity "without learning `onstat` first".

Q-1 — the question that gated this slice entirely — was answered against 14.10 on
2026-08-06: an ordinary developer account read `sysmaster:syssessions`. So this slice
serves the primary user, not only the DBA, and its priority stands as the PRD wrote it.

**Done when** (PRD §5): you can see who is connected, what each session is running, and —
given two sessions where one blocks the other — identify the blocker from the UI alone.
Refresh is manual by default.

Requirements: PR-5.1–PR-5.5 (Must), PR-5.6/PR-5.7 (Should). **PR-5.8, terminate a session,
is excluded by DEC-2 — do not build it.** It would be the first administrative write, and
it pulls in a confirmation framework and an audit store (DEC-8).

### Decisions already taken

1. **Share the existing connection.** The monitor reuses the `SerializedCatalogReader`
   already serving the tree and completion. PR-6.4 asks IMS not to add a session per
   instance. Accepted cost: a refresh queues behind a tree expansion.
2. **A tab, one per instance**, following `ObjectDetailTabViewModel`. Closing stops
   polling; deselecting pauses it.
3. **PR-5.6 is in scope**, as a header strip.
4. **Verification** = pure unit tests + a bounded smoke probe. The two-session lock-wait
   acceptance test stays a documented open item: DEP-2 is unmet, `testdb` sits on the
   production server, and there is nowhere safe to arrange a real lock wait.

### Two facts that shape everything below

- **`OdbcCommand.Cancel()` does not reach this server** (measured 2026-08-06;
  `SQL_ATTR_ASYNC_ENABLE` returns `HYC00 -11097`). A refresh token stops IMS *waiting* and
  nothing else. Every session query must therefore be bounded **before it is sent** —
  `FIRST n` plus a short `CommandTimeout` — not cancelled once running (RSK-5, PR-6.4).
- **The INTERVAL trap** ([OdbcStatementResult.cs](src/Ims.Data.Informix/OdbcStatementResult.cs)):
  `System.Data.Odbc` has no type-map entry for `SQL_INTERVAL_*`, so `GetValue`, `IsDBNull`,
  `GetFieldType` and `GetSchemaTable` all throw — and **every column at or after the first
  INTERVAL becomes unreadable**. `QueryAsync`'s helpers all route through `GetValue`/
  `IsDBNull`, so they are not safe on one. Durations are the natural shape of half this
  slice's data, so this is the sharpest risk in it.

---

## Step 0 — Branch

```
git switch -c slice-3-observe
```

---

## Step 1 — Fix the two-way tab host (do this first; nothing is visible until it lands)

[MainWindow.xaml.cs:343](src/Ims.App/MainWindow.xaml.cs#L343) `ShowHostForSelectedTab` is
`bool detail = SelectedTab is ObjectDetailTabViewModel`. A third tab kind falls to the else
branch and renders the editor — and [BindEditorToSelectedTab](src/Ims.App/MainWindow.xaml.cs#L302)
sets `Editor.IsEnabled = false` there, so the user would get a greyed-out empty editor
where the monitor should be.

Replace the boolean with a per-kind lookup, keeping the row-height logic untouched. **Invert
the results-area test** from `detail ? hide : show` to `isEditor ? show : hide`, so the
editor is the special case that gets a results pane and a fourth tab kind fails safe.

Add a `SessionMonitorHost` `ContentControl` as a third sibling in row 2 of the right-hand
grid, `AutomationProperties.Name="Session monitor"` (NFR-8), alongside `EditorHost` and
`ObjectDetailHost`.

`BindEditorToSelectedTab` needs no change — its else branch already does the right thing.

**Do not** name any property on the new tab VM `CanExecute`, `IsExecuting`, `Session`,
`SelectedResult`, `TargetLabel`, `CancelNotice`, `Results` or `Outcomes`. Toolbar bindings
on `SelectedTab.*` rely on `FallbackValue=False` and a property-name miss; a collision
would light up Execute and Commit on a monitor tab.

---

## Step 2 — The queries: `src\Ims.Data.Informix\Catalog\SessionQueries.cs`

`internal static`, same shape as [CatalogQueries.cs](src/Ims.Data.Informix/Catalog/CatalogQueries.cs)
— `public const string` for fixed queries, static composers where a predicate varies.
Predicates compose into the SQL text (Informix cannot type a bare `?`, and PR-8.2 wants the
shown SQL to be the SQL that ran); real values go through `?` parameters.

```csharp
public const int RowCap = 500;        // RSK-5: bounded before it is sent
public const int TimeoutSeconds = 10; // shorter than the catalogue's 60 (NFR-1)
```

### Two invariants, stated in a file-level `<remarks>` and enforced by a test

1. **No session query selects an INTERVAL column.** Durations are either an epoch/count
   INTEGER converted client-side, or `CAST(expr AS CHAR(n))` parsed as text.
2. **Any column whose type is uncertain goes last in the select list**, so a surprise
   INTERVAL costs one column instead of the whole tail.

### Confidence, stated honestly — I cannot verify against a live server

Everything marked **[UNSURE]** gets its own `try`/`catch (OdbcException)` sub-read, so its
absence costs one section and never the pane. This is
[GetTableDetailAsync](src/Ims.Data.Informix/Catalog/InformixCatalogReader.cs)'s pattern
applied where it is most needed.

| Query | Source | Confidence |
|---|---|---|
| Session list | `sysmaster:syssessions` — `sid`, `username`, `hostname`, `pid`, `feprogram`, `state`, `connected` | `sid`/`username` **CONFIDENT** (the smoke test read them); `hostname` LIKELY; `feprogram` and `state` **UNSURE**; `connected` LIKELY an epoch INTEGER |
| Session count | `SELECT COUNT(*) FROM sysmaster:syssessions` | **CONFIDENT** — so the count is true even when the list is capped |
| Current SQL | `sysmaster:syssqlcurses` keyed on sid | **UNSURE** — the `sqx_` vs `sqc_` column prefix is a coin flip |
| Locks held | `sysmaster:syslocks` — `owner`, `dbsname`, `tabname`, `rowidlk`, `keynum`, `type` | LIKELY. `owner` may be a pid rather than a sid — a silent mis-join risk |
| Lock waits | `syslocks` self-join on resource identity (`dbsname`+`tabname`+`rowidlk`+`keynum`, `w.owner <> h.owner`) | LIKELY as *contention*; see below |
| Resources | `sysmaster:sysrstcb` — `memtotal`, `memused` | **UNSURE** on every column name |
| Mode | `sysmaster:sysshmvals.sh_mode` | LIKELY; codes UNSURE |
| Uptime | `sysshmvals` boot time as an **epoch INTEGER** | **UNSURE** on the column name |
| Buffer efficiency | `sysmaster:sysprofile` name/value pairs (`dskreads`, `bufreads`, `dskwrits`, `bufwrits`) | LIKELY. No INTERVAL risk |
| Checkpoint recency | `sysmaster:syscheckpoint` as an epoch INTEGER | **UNSURE** it exists under that name |

**Deliberately not used:** the `sysscblst`/`sysrstcb`/`systcblst` join for the master list.
It is the classic `onstat -g ses` reconstruction and the least stable shape in `sysmaster`.
Building on `syssessions` — the one object with a measured success against 14.10 — puts
PR-5.1's core on proven ground. Add the join later only if `feprogram` proves absent.
`sysseswts` and `sysptprof` are also out: the first is a prime INTERVAL suspect, the second
is per-partition and does not answer PR-5.2 anyway.

**Per-session temp space stays an open item.** Deriving it needs a `systabnames`/`sysptnhdr`
shape I am not confident about, and claiming a number IMS cannot derive would violate
PR-8.4. Temp space appears at the *instance* level in the header strip instead.

### The blocker — three graceful tiers, not one guess

PR-5.3 is the acceptance-critical requirement and the least certain query. The self-join
finds sessions *contending on a resource*, which is a superset of "A is blocked by B" — two
compatible shared locks match it too. So lock-type compatibility is decided client-side by
a pure function, and the result is graded:

- **`BlockerIdentified`** — a waiter/holder pair with incompatible types. "Blocked by
  session 4821 (informix)". This is what §5 needs.
- **`ContentionOnly`** — pairs found, compatibility undecidable. "Contending with session
  4821 on `stores:orders`" — honestly weaker wording.
- **`Unknown`** — the query failed or `syslocks` has no waiter concept. "This server does
  not expose lock waits to IMS." A stated absence, not an error; the pane still shows locks
  held, state and resources.

This is `StatisticsCurrency`'s discipline applied to the blocker. **A wrong blocker is worse
than an admitted absence, because someone might act on it.**

---

## Step 3 — The abstraction: `src\Ims.Core\Monitoring\`

### `ISessionMonitor` — a second interface on the same objects

The one-cursor rule decides this. A separate `SerializedSessionMonitor` with its own
semaphore would be *two gates over one cursor* — precisely the bug
[SerializedCatalogReader](src/Ims.Core/Catalog/SerializedCatalogReader.cs) exists to
prevent. And merging into `ICatalogReader` would force `CatalogCache` and every test double
to grow methods with no meaning for them — a session is not schema.

So: `ISessionMonitor` is its own interface, and **both `InformixCatalogReader` and
`SerializedCatalogReader` implement it** — the latter forwarding through its existing
`_gate` via the existing `RunAsync`. One connection, one gate, two capabilities.

```csharp
public interface ISessionMonitor
{
    Task<SessionSnapshot> GetSessionsAsync(CancellationToken cancellationToken);
    Task<SessionDetail> GetSessionDetailAsync(int sid, CancellationToken cancellationToken);
    Task<InstanceIndicators> GetInstanceIndicatorsAsync(CancellationToken cancellationToken);
    bool? SysMasterReadable { get; }   // null until first read
}
```

`SerializedCatalogReader`'s constructor keeps taking `ICatalogReader` — tightening it to
require both would break `CatalogCache` and every existing test. It tests `_inner is
ISessionMonitor` and returns `SessionSnapshot.Unavailable(...)` when not.

Implementation goes in a new partial, `InformixCatalogReader.Sessions.cs` — the existing
file is already 1114 lines. Same type, same connection, same `QueryAsync` primitive, but
reviewable on its own. **`QueryAsync` needs an optional timeout parameter** (it hardcodes
`CommandTimeout = 60`); default it to 60 so Slice 2 is untouched.

### `ServerQuery` — the PR-8.3 home that does not exist yet

`CatalogResult<T>(Items, Sql)` is a single-query shape and PR-8.3 needs a *second* string
per query. Rather than refactor a working Slice 2 type, add the parallel:

```csharp
public sealed record ServerQuery(
    string Purpose, string Sql, string OnstatEquivalent,
    ServerQueryOutcome Outcome = ServerQueryOutcome.Succeeded, string? Message = null);

public enum ServerQueryOutcome { Succeeded, Failed, NotAttempted }
```

`TableDetail.QueriesUsed`'s `List<string>` accumulator becomes a `List<ServerQuery>` one
here. `Outcome` is on it because **a section that failed is part of what IMS asked** —
hiding the query that did not work leaves the user unable to see why a section is empty
(NFR-4). The onstat strings come from PRD §12's parity map: `onstat -g ses`, `-g sql`,
`-g act`, `-g lok`, `-K`.

### Records

`sealed record` with `required init` properties, matching `TableDetail`'s style. Raw
catalogue codes are always preserved alongside the friendly value, per the
`RawColType`/`RawLockLevel` precedent.

- **`SessionSnapshot`** — `Sessions`, `Waits`, `Fidelity` (`LockWaitFidelity`),
  `TotalSessionCount`, `IsCapped`, `ReadAt`, `Queries`, `UnavailableReason`. A static
  `Unavailable(why)` factory.
- **`SessionInfo`** — `Sid`, `UserName`, `HostName`, `Application`, `ProcessId`,
  `ConnectedAt`, `State`, `RawState`, `IsMine`, `IsSystem`.
- **`LockWaitEdge`** — `WaiterSid`, `HolderSid`, `Resource`, `WaiterLockType`,
  `HolderLockType`.
- **`SessionDetail`** — `Sid`, `Session`, `CurrentSql`, `CurrentSqlTruncated`, `LocksHeld`,
  `Waits`, `Resources`, `Queries`.
- **`InstanceIndicators`** — every field nullable, because each is a Should:
  `VersionBanner`, `Mode`, `RawMode`, `Uptime`, `SessionCount`, `ReadCachePercent`,
  `WriteCachePercent`, `LastCheckpoint`, `Queries`.

### Pure translators — the tested surface

`internal static` on the `.Sessions.cs` partial, following `DescribeLockLevel`/`DecodeDecimal`:

```csharp
internal static string DescribeSessionState(string? raw);
internal static string DescribeLockType(string? raw);
internal static string DescribeServerMode(int? raw);
internal static DateTimeOffset? FromUnixSeconds(long? epoch);
internal static double? ComputeCacheRatio(long? logical, long? physical);
internal static bool AreIncompatibleLocks(string? holder, string? waiter);
```

`state` is read via `GetString` (which works whether the server sends an integer or a char)
then `.TrimEnd()`ed — **every CHAR column from `sysmaster` needs trimming**; an untrimmed
`idxtype` once reported every index non-unique. Unrecognised codes render as
`"Unknown (7)"`, never as `"Running"` — a confident wrong "Running" on a blocked session
would break the §5 acceptance test silently.

`FromUnixSeconds` is how the slice dodges the INTERVAL trap, so it is tested for null, a
normal value, and **zero — which must yield null, not 1970**; "connected since 1970" is a
visible absurdity.

### `LockWaitChain` — pure, in `Ims.Core`, for PR-5.7

```csharp
public static class LockWaitChain
{
    public static IReadOnlyList<IReadOnlyList<int>> Resolve(IEnumerable<LockWaitEdge> edges);
    public static int? BlockerOf(int sid, IEnumerable<LockWaitEdge> edges);
}
```

In `Ims.Core` for a decisive reason: **`Ims.Core.Tests` references only `Ims.Core`**, so
this is the only place a pure algorithm gets tested by the existing net9.0 project. Must be
cycle-safe with a visited set, not recursion depth — a real deadlock produces A→B→A and
must render as a cycle rather than hang.

---

## Step 4 — PR-5.5: the refresh policy, in `Ims.Core` so it is testable

**No `Ims.App.Tests` project.** `MainViewModel` touches `App.Current.Dispatcher` and
`MessageBox`, so `Ims.App` is not unit-testable without refactoring outside this slice's
scope. Instead the decision — "may I query right now?" — is a pure function of state and
lives in `src\Ims.Core\Monitoring\RefreshPolicy.cs`, where `Ims.Core.Tests` reaches it
today with no new csproj to get past `DependencyPolicyTests`.

The `DispatcherTimer` stays in the view model as a dumb tick source that *asks* the policy.

```csharp
public enum RefreshMode { Manual, Interval }   // Manual is the PR-5.5 default

public sealed class RefreshPolicy
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);
    public static readonly IReadOnlyList<TimeSpan> OfferedIntervals = /* 5s 10s 30s 60s 5m */;

    public RefreshPolicy(Func<DateTimeOffset>? now = null);   // injected clock, no test waits

    public RefreshMode Mode { get; }              // Manual by default
    public TimeSpan Interval { get; }             // 30s
    public bool IsViewOpen { get; }
    public bool IsViewSelected { get; }
    public bool IsPolling => IsViewOpen && IsViewSelected && Mode == RefreshMode.Interval;

    public void ViewOpened(); public void ViewClosed();       // ViewClosed is terminal
    public void ViewSelected(); public void ViewDeselected();
    public void SetManual(); public void SetInterval(TimeSpan);  // clamps to the minimum
    public void RecordQuery();
    public bool ShouldRefreshNow();      // never true when the view is not watching
    public bool CanRefreshOnDemand();    // true while open, whatever the mode
}
```

`ViewClosed` is **terminal**: a queued refresh resolving after the tab closes must not
issue. That is the literal reading of "never query a server while the view is closed".

### Observing deselection

Use the toolkit's generated **two-parameter** hook on `MainViewModel`, not the code-behind:

```csharp
partial void OnSelectedTabChanged(ITabViewModel? oldValue, ITabViewModel? newValue)
```

It fires for programmatic selection too — opening a monitor tab selects it in code and the
poll has to start — whereas `OnTabChanged`'s `SelectionChanged` handler guards those out
with its `ReferenceEquals(e.OriginalSource, TabHeaders)` test. Pure addition; touches no
existing behaviour.

`CloseTabAsync` already awaits `DisposeAsync`, so `SessionMonitorTabViewModel.DisposeAsync`
is where `ViewClosed()` + timer stop + CTS cancel go — **the close path needs no change**.

**One real leak to fix:** `MainViewModel.ShutdownAsync` iterates `EditorTabs` only, so a
monitor tab's timer would survive shutdown. Add a second explicit loop over
`Tabs.OfType<SessionMonitorTabViewModel>()` — a smaller diff than disturbing the intricate
autosave logic in the first loop.

---

## Step 5 — The UI

`SessionMonitorTabViewModel(ConnectionDescriptor descriptor, ISessionMonitor monitor,
string? currentUser)` : `ObservableObject, ITabViewModel`. `Title => $"Sessions — {descriptor.ServerName}"`.
`Selected()` / `Deselected()` / `DisposeAsync()` drive the policy. Opened by an
`OpenSessionMonitorTab(...)` factory on `MainViewModel` following
[OpenObjectDetailTab](src/Ims.App/ViewModels/MainViewModel.cs#L370): dedupe by
`Descriptor.Id` (one monitor per instance) → select existing if found → else construct,
`Tabs.Add`, `SelectedTab =`, fire-and-forget `_ = tab.LoadAsync(...)`.

A `DataTemplate` in `MainWindow.xaml`'s `Window.Resources`, three bands:

1. **Header strip (PR-5.6)** — version, mode, uptime, session count, buffer efficiency,
   checkpoint recency. Each omitted, or shown as "Unknown", when its query failed.
2. **Session list** — a static `DataGrid`, following the object-detail template's grids
   ([MainWindow.xaml:66-141](src/Ims.App/MainWindow.xaml#L66-L141)):
   `AutoGenerateColumns="False"`, explicit `DataGridTextColumn`s, `IsReadOnly="True"`,
   `HeadersVisibility="Column"`, `Consolas 12`, `ClipboardCopyMode="IncludeHeader"`,
   `CanUserSortColumns="True"`, row virtualisation with recycling. **Not** the dynamic
   `RebuildResultColumns`/`ResultSetViewModel` paging machinery — that is for unknown-shape
   cursors and the session list has fixed columns and a bounded row count.
3. **Detail pane** for the selected session — current SQL, locks held, locks awaited with
   the blocker named, resources, and the PR-5.7 chain when more than two sessions are
   involved.

### PR-5.4 — sort and filter, and "highlight my sessions"

Sort is free: `CanUserSortColumns="True"` with correct `SortMemberPath` on explicit columns.

Filter is **client-side** — the app's first `ICollectionView`. The tree's filter is
server-side because 20,000+ objects are exactly what it is trying not to fetch; that
reasoning **inverts** for a session list of tens of rows, and re-querying `sysmaster` per
keystroke would violate PR-6.4 and PR-5.5. Reuse the tree's
`UpdateSourceTrigger=PropertyChanged, Delay=400` TextBox idiom and the
`IncludeSystemObjects` CheckBox shape for a "hide system sessions" toggle.

"Highlight the user's own sessions" must not be colour-alone (NFR-8). Follow the
`EnvironmentLabel` precedent — a **word or glyph in a column** (`SessionInfo.IsMine` → a
"YOU" marker), with a `DataGrid.RowStyle` `DataTrigger` background as strictly secondary
decoration. Same discipline as italic-`(null)`.

### PR-8.2 / PR-8.3 — never hide the server, and name the command

- An `Expander` "Queries behind this pane" — a read-only Consolas 11 `TextBox`, exactly the
  detail tab's pattern, but rendering `ServerQuery` triples: purpose, SQL, and **the
  `onstat` equivalent beside it**. Failed sections show their query and why it failed.
- Per-band onstat labels: `onstat -g ses` on the list, `-g sql` on current SQL, `-g lok` on
  locks. This is the first UI home for an onstat command — today the only instance is prose
  in `EditorTabViewModel.CancelNotice`.
- A context-menu "Show the query…" opening the SQL in a new editor tab, matching the tree's
  affordance so U3 can run and edit it.

### NFR-8 and NFR-11

`AutomationProperties.Name` on every new control — it is on ~30 controls today and treated
as mandatory. A `RoutedUICommand` for the monitor in the block at
[MainWindow.xaml.cs:32-74](src/Ims.App/MainWindow.xaml.cs#L32-L74) with an inline
`KeyGesture`, bound in the ctor. A new top-level `_Sessions` menu between `_Connection` and
`_Results` (S and O mnemonics are both free), plus a toolbar button in the instance-scoped
run beside Connect/Disconnect — `<ic:SymbolIcon Symbol="Pulse" />`, size from the implicit
style, never a per-icon `FontSize`.

New `InformixConcepts` constants (session, lock, latch, checkpoint, buffer efficiency, LRU,
temp space, sid vs pid) at the bottom of
[TableDetailViewModel.cs](src/Ims.App/ViewModels/TableDetailViewModel.cs#L174), wired
**both** ways as the existing six are: `{x:Static vm:InformixConcepts.Lock}` as a tooltip
on the term *and* an in-pane glossary `Expander`. (`Serial` is declared but unwired today —
an existing harmless precedent not to repeat.)

---

## Step 6 — Capability wiring (NFR-4, PR-6.1)

`InformixCapability.SysMasterReadable` and `SessionLockDetail` are declared in
[InformixServerInfo.cs](src/Ims.Core/Data/InformixServerInfo.cs#L47) and never populated.

**Probe on first monitor open, not at connect.** `ReadServerInfoAsync`'s empty capability
set carries a comment that the emptiness is deliberate under PR-6.2 — "a capability nobody
has asked about yet is one IMS has no business probing for". Honour that: opening the
monitor *is* the documented user action that licenses the probe.

Use the `_hasStatisticsTimestamp` tri-state pattern — a `private bool?` field, lazy
try-once, on `OdbcException` set false, log at Information, return a first-class `Unknown`.
Nothing branches on a version number.

When `sysmaster` is unreadable the tab opens and states it plainly: "This account cannot
read `sysmaster`, so IMS cannot show sessions. `onstat -g ses` at the command line needs
the same access." Not an error dialog — under PR-6.1 an ordinary user may legitimately lack
it, and IMS grants no capability the user does not already hold.

---

## Step 7 — Tests

Following the house conventions: flat files, one per subject; file-scoped namespace;
snake_case sentence method names; `[Theory]`/`[InlineData]` for mapping tables;
`.Should().BeEmpty("because …")` carrying the requirement text. **Every test carries a
comment naming the PRD requirement it defends** — the strongest convention in the repo.

`tests\Ims.Core.Tests\` (net9.0, references `Ims.Core` only):

| Class | Defends |
|---|---|
| `RefreshPolicyTests` | PR-5.5 — Manual is the default; `ShouldRefreshNow` is false when closed, false when deselected, false before the interval elapses; `ViewClosed` is terminal; `SetInterval` clamps below the minimum; `CanRefreshOnDemand` is true in Manual mode but false once closed |
| `LockWaitChainTests` | PR-5.7 — a two-session pair yields no chain ("more than two"); A→B→C resolves ordered; **a cycle A→B→A terminates and renders as a cycle**; a session blocked by two holders; an edge naming a sid absent from the list must not throw |
| `SessionSnapshotTests` | PR-5.3/NFR-4 — `Unavailable` carries a reason and `Unknown` fidelity; a capped snapshot reports the true `TotalSessionCount` |

`tests\Ims.Data.Informix.Tests\` (net9.0-windows, where `CatalogQueryCompositionTests` lives):

| Class | Defends |
|---|---|
| `SessionQueryCompositionTests` | RSK-5/PR-6.4 — every query carries `FIRST n`; every per-session query is a keyed lookup, never a scan; `ORDER BY` stays last. **`No_session_query_selects_an_interval`** — no query contains `INTERVAL` outside a comment, and any `CAST` targets `CHAR`. Crude, same grain as `Keeps_ORDER_BY_last`, and it catches the careless case |
| `SessionTranslationTests` | PR-5.1/PR-8.2 — tests the *mechanism*, not the provisional labels: an unrecognised state surfaces as `Unknown (n)` and never as `Running`; the raw code is always preserved; padded CHAR input is trimmed; `FromUnixSeconds(0)` is null not 1970; `ComputeCacheRatio` with a zero denominator is Unknown, not NaN; `AreIncompatibleLocks` is false for S/S and true for X/anything |

When someone verifies against a real server they change a mapping table, not a design.

### Smoke probe

Add `SessionMonitorAsync` to [Probes.cs](tools/Ims.SmokeTest/Probes.cs) behind a new
`--probe-sessions` flag (a bool `case` in `SmokeTestOptions.Parse`). Bounded: `FIRST 5`,
`CommandTimeout = 30`, via the existing `ScalarAsync` helper. It probes each uncertain
object in turn — `syssessions` columns, `syslocks`, `sysprofile`, `sysshmvals`,
`syscheckpoint` — reporting `Inconclusive` where a human must read the answer.

**Register in three places** — the sequence in `RunAllAsync`, the `if (connection is null)`
skip block that hand-writes a `Skip` for every probe so the report is complete when connect
fails, and the `PROBES` block in `PrintUsage()`.

This probe is how the **[UNSURE]** table in Step 2 gets resolved. Run it first, against a
non-production instance, and let it decide the column names before the reader is finished.

---

## Step 8 — Update `docs\IMPLEMENTATION-TODO.md`

Follow its established voice: measured facts with dates, `[~]` for partly met, honest notes
on what is unverified. Expected end state for §"Slice 3 — Observe":

- `[x]` PR-5.1, PR-5.4, PR-5.5, PR-8.2/8.3
- `[~]` PR-5.2 — locks and current SQL built; **per-session temp space and resource
  counters unverified**, `sysrstcb` column names unconfirmed
- `[~]` PR-5.3 — built with three fidelity tiers; **which tier a real 14.10 server yields
  is unmeasured**
- `[~]` PR-5.6 — built; individual indicators degrade to Unknown where the query failed
- `[x]` PR-5.7 — resolver built and unit-tested; unexercised against real data
- `[ ]` **Exit check: two sessions, one blocking the other, identified from the UI alone.**
  Needs an instance where a lock wait can be arranged safely — DEP-2 is unmet, `testdb`
  sits on the production server. This is the §5 acceptance bar and it stays open.

---

## Verification

Ordered so each step leaves the build green.

1. **`dotnet build -c Release`** after every step. `TreatWarningsAsErrors` is on, so this is
   a real gate.
2. **`dotnet test`** — 273 tests pass today; the new ones must join them. No test opens a
   socket.
3. `DependencyPolicyTests` must stay green: any new package goes in
   `Directory.Packages.props` with **no inline `Version=`**.
4. **Run the app** with no database: the shell renders, the `_Sessions` menu item and
   toolbar button appear, and opening a monitor tab with no connection reports the absence
   rather than throwing. Confirms Step 1's host fix — switch editor → object detail →
   monitor → editor and check the results pane and splitter appear only for the editor, and
   that a dragged splitter position survives the round trip.
5. **Keyboard-only pass** (NFR-8): reach the monitor, sort a column, filter, select a
   session, and read the onstat label without touching the mouse.
6. **Against 14.10** (`demo_srv`), in this order:
   - `Ims.SmokeTest --probe-sessions` **first** — it resolves the [UNSURE] column names
     before the reader depends on them.
   - Open the monitor as an ordinary developer account. Confirm own session highlighted,
     manual refresh only, and that closing the tab stops all querying (check the log).
   - Leave the monitor on an interval, select another tab, and confirm from the log that
     **no query is issued while it is not selected** — the PR-5.5 clause most easily
     half-implemented.
7. **Not verifiable now, and left documented:** the two-session lock-wait test. DEP-2 is
   unmet and there is nowhere safe to arrange a real lock wait.

## What could go wrong

- **The column names are wrong.** Most likely `feprogram`, the `syssqlcurses` prefix, and
  every `sysrstcb` name. Mitigated by per-section isolation and by running the probe first
  — but PR-5.2 may land as a documented `[~]` rather than a pass.
- **An unexpected INTERVAL** kills a column and everything after it. Mitigated by the two
  invariants, the composition test, and putting uncertain columns last.
- **`syslocks.owner` may be a pid, not a sid** — a silent mis-join that would name the wrong
  blocker. This is the single worst failure mode in the slice, which is why the fidelity
  tiers exist and why `BlockerIdentified` should not be claimed until the probe confirms it.
- **A refresh queues behind a tree expansion** by design. If it becomes annoying in daily
  use, the fix is a decision about PR-6.4, not a code change to make first.

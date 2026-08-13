# IMS — Implementation To-Do

Derived from [PRD-Informix-Management-Studio.md](PRD-Informix-Management-Studio.md) v0.3.
Ordered by the four slices in PRD §5. Every task carries the requirement ID it satisfies;
tasks with no ID are marked **[infra]** and are not traceable to the PRD.

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · **M/S/C** = PRD priority.

---

## Slice 0 — Foundation **[infra]** — *complete except where noted*

Not in the PRD. Prerequisite for everything below; keep it thin — DEC-12 says every slice
must be usable alone, and this one isn't, so it should be measured in days not weeks.

- [x] Solution scaffold: `Ims.App` (WPF), `Ims.Core` (domain/abstractions), `Ims.Data.Informix` (provider), `tools/Ims.SmokeTest`, two test projects — DEC-3
- [x] Pin target framework: `net9.0-windows`, SDK 9.0.311 via `global.json`. `Ims.Core` stays plain `net9.0` with no Windows dependency — NFR-5
- [x] **Provider decision made.** The CSDK's bundled `IBM.Data.Informix.dll` ships only for .NET Framework 2.0 (`bin\netf20`) and cannot load in .NET 9, so DEC-4 resolves to its **ODBC** branch: `System.Data.Odbc` over the registered `IBM INFORMIX ODBC DRIVER (64-bit)` (CSDK 4.10.FC1DE), keeping the native SQLI protocol — DEC-3, DEC-4
- [x] Async/threading model: every provider call async and cancellable; `ServerCallGuard` throws if a round trip is attempted on the dispatcher thread — NFR-1, PR-8.5
- [x] Local logging via `FileLoggerProvider`, wrapped in `RedactingLoggerProvider` so PR-6.3 holds at one boundary rather than at every call site — NFR-10, PR-6.3
- [x] `DependencyPolicyTests` fails the build if a telemetry package or a redistributed IBM client library enters the graph — PR-6.5, DEC-10
- [x] CI: GitHub Actions, `windows-latest`, Release build + test on push. No CI job connects to a server
- [x] **Run `Ims.SmokeTest` against non-prod 14.10** — run against `demo_srv`, 14.10; results below. 12.10 descoped (DEC-5) — DEP-2, RSK-9
- [ ] Secure DEP-3 (a realistic-size schema for NFR-2)

### Answered by the smoke test — against `demo_srv`, Informix **14.10**

| Question | Requirement | Answer |
|---|---|---|
| Can an ordinary developer read `sysmaster`? | **Q-1** | ✅ **Yes.** `sysmaster:syssessions` read as a normal developer account. **Slice 3 is unblocked and serves the primary user (U1), not only U2/U3.** AS-3 holds |
| Does ODBC connect over the CSDK at all? | PR-1.1, DEC-4 | ✅ Yes, 506 ms. The `Database` keyword must be *present*, even empty |
| Does DATETIME keep its qualifier? | PR-4.5 | ✅ Yes — `GetDataTypeName` returns `DATETIME YEAR TO FRACTION(3)` in full |
| Can INTERVAL be read at all? | PR-4.5 | ⚠️ **Only as text.** See the constraint below |
| Is SQLCODE retrievable? | PR-3.6 | ✅ Yes. ISAM reporting still unproven — `-206` has no ISAM error; needs a lock conflict or constraint violation |
| Is `SECURITY=ssl` the keyword for encryption? | PR-1.10 | ❌ **No.** The driver ignores unknown keywords silently, so it would have faked encryption. Now throws instead |
| How do DECIMAL, MONEY and LVARCHAR arrive? | PR-4.5 | ✅ **Cleanly**, measured 2026-08-06 — `DECIMAL`→`Decimal`, `MONEY`→`Decimal` (mapped `Money`), `LVARCHAR`→`String`. An earlier run reported all three unreadable; that was the INTERVAL poisoning below, reproduced by a probe that listed them after an INTERVAL column, not a property of these types |
| Does the driver stream or buffer? | PR-4.2, RSK-6 | ✅ **Streams.** 20,000 join rows in 695 ms, first row after 44 ms, managed heap flat. Bounded run — says nothing about NFR-2's million |
| Is an empty `Database=` accepted? | PR-1.1 | ⚠️ **By the driver, yes; by this server, no** — `-354`. Naming a database succeeds. See the note in `InformixOdbcConnectionString` |

### The INTERVAL constraint — measured, and it shapes the code

`System.Data.Odbc` has no type-map entry for ODBC's `SQL_INTERVAL_*` types (110 =
`DAY TO SECOND`) and throws `ArgumentException` from inside `TypeMap` *before* any value
conversion:

| Accessor | On an INTERVAL column |
|---|---|
| `GetName`, `GetDataTypeName` | ✅ works — and the type name carries the full qualifier |
| `GetString`, `GetFieldValue<string>`, `GetChars` | ✅ works — returns e.g. `"  5 12:30:45"`, padded |
| `GetValue`, `IsDBNull`, `GetFieldType`, `GetSchemaTable` | ❌ throws |

Worse, the damage is not confined to the offending column: **every column at or after the
first INTERVAL became unreadable**, including `DECIMAL` and `LVARCHAR` ones. So the
unsupported accessors must never be called at all. `OdbcStatementResult` therefore decides
once, from the type name, which columns are text-access, and `GetSchemaTable` failure is
treated as normal rather than exceptional.

**DEC-4's ODBC branch survives this** — PR-4.5 is reachable — but it is the closest thing to
a reason to reopen it that has come up, and it is worth remembering if another type turns out
to be equally unmapped.

### Still blocked on a live server

- [x] ~~Run against a **12.10** instance~~ — **descoped by the owner, 2026-08-06, and 12.10 is now
  out of v1 scope entirely.** DEC-5, NFR-4, DEP-2, RSK-9 and the README have been updated to say
  14.10 only. This withdraws RSK-9's stated mitigation ("test both from Slice 1 onward, not at the
  end"), so NFR-4's capability detection carries that risk alone: nothing may branch on a version
  number, and any catalogue feature absent in 12.10 must degrade rather than fail. 12.10 is untested
  and unsupported — not refused; restoring it is a testing exercise, not a code change
> **DEP-2 is only half met, and it shapes how these are run.** The test database `testdb` sits on
> the *same server as production* — there is no separate instance to be reckless on. So the
> smoke test now has two load tiers, and on this estate the bounded one is the only one that
> should ever run:
>
> - `--include-light-load` — streaming and cancellation with every statement capped
>   server-side by `FIRST` and a 30s `CommandTimeout`. The work is bounded *before* it is sent.
> - `--recheck-cancellation` — the two synchronous cancellation probes are **off by default**
>   now that they have answered. Each spends a 30-second cross join to reconfirm a known
>   failure, which is a poor trade against a shared instance. Pass this after a driver or
>   server upgrade.
> - `--include-load` — the original unbounded form: a four-way cross join with
>   `CommandTimeout = 0`. If `Cancel()` does not land, nothing stops it. **Do not run this
>   against the production server**; it needs an instance of its own (RSK-5, PR-6.4).

- [~] Cancellation — PR-3.5. **Half answered, and the half that failed is the important one.**
  Measured 2026-08-06 against 14.10 once the statement was finally slow enough (`ORDER BY` over
  a three-way join; the two earlier attempts used `COUNT(*)` over `FIRST n`, which the optimiser
  short-circuits, so they proved nothing):

  | | |
  |---|---|
  | Session survives | ✅ Yes — usable immediately afterwards |
  | `OdbcCommand.Cancel()` stops the statement | ❌ **No.** Called at 2s; the statement ran to the 30s `CommandTimeout` and ended with `-11094 Timeout expired`, 31s after the cancel |

  So the token does **not** reach the server, and PR-3.5's "cancel reaches the server via
  `OdbcCommand.Cancel`, not just the await" is **not met** — §1b marks that item done on the
  strength of the code path existing, which this disproves. A user pressing Alt+Break would see
  the UI return while the statement kept running.

  **The sort was not the cause — it is the driver.** Both workloads were run on 2026-08-06,
  one made slow by `ORDER BY` and one by scanning a cross join with a filter that cannot be
  pushed below the join. They failed identically:

  | Workload | Result |
  |---|---|
  | `ORDER BY` over a 3-way join | Ran 32,247 ms to the timeout. `Cancel()` ignored |
  | 3-way join, `a.tabid + b.tabid + c.tabid < 0` | Ran 32,118 ms to the timeout. `Cancel()` ignored |

  Both ended on `-11094 Timeout expired`, ~30s after `Cancel()` was called, and in both the
  session was usable immediately afterwards. Sorting has nothing to do with it: **`Cancel()`
  does not reach this server at all.** PR-3.5's "the token reaches the server via
  `OdbcCommand.Cancel`" is unmet, and no amount of statement-shaping will change it.

  **Both single-connection routes are now closed.**

  1. ~~`SQL_ATTR_ASYNC_ENABLE`~~ — **ruled out, measured 2026-08-06.** `System.Data.Odbc`
     executes synchronously, and `SQLCancel` against a synchronous handle is documented to
     take effect only in limited states, which fitted what was observed. The driver refuses
     the attribute outright: `SQLSetConnectAttr` returns `-1` with
     `HYC00, native -11097: Optional feature not implemented`. Asynchronous execution is not
     available over this driver at all, so it cannot be what makes `Cancel()` work. The spike
     (`AsyncCancelSpike`) is kept — it is one cheap statement and re-answers the question after
     a CSDK upgrade
  2. **The CSDK's own interrupt settings** — the remaining unexamined idea, and a thin one.
     `INFORMIXCONTIME`/`INFORMIXCONRETRY` govern connection attempts, not running statements.
     Worth one read of the CSDK's connection-attribute list for an interrupt option before
     accepting (3), but do not expect much
  3. **A second connection issuing an administrative cancel** — now the only route that is
     known to be able to work, and it costs the extra session PR-6.4 asks IMS not to add.
     **This is a decision for the owner, not a default.** See below

### What PR-3.5 costs now — a decision, not a task

Every cheap route is closed, so the options are all uncomfortable. None should be picked by
whoever next opens the file; this needs the owner.

| Option | What the user gets | What it costs |
|---|---|---|
| **A. Second connection, administrative cancel** | Cancel works as PR-3.5 specifies | Breaks PR-6.4 (a second session per instance) and strains PR-6.2 (IMS issues a statement the user did not type). Needs the user's own privileges to cancel their own session — unverified |
| **B. Remove the cancel gesture** | An honest UI: no button that lies | PR-3.5 unmet and visible. RSK-1 suffers — a runaway statement means killing the tab or the app |
| **C. Keep it, tell the truth** | Alt+Break stops IMS waiting; a message says the statement continues server-side and names `onmode -z` | Cheapest by far, honest, and leaves the statement running. PR-3.5 still unmet |
| **D. Do nothing** | A cancel button that silently does nothing | Not acceptable. This is today's behaviour and it misleads |

**Recommendation: C now, A only if daily use proves it necessary.** C is small, removes the
lie, and keeps PR-6.2/PR-6.4 intact; it also matches PR-8.2's habit of naming the `onstat`/
`onmode` equivalent rather than hiding the server. A is a real design with a privilege question
behind it (can an ordinary developer cancel their own session?) that is itself unmeasured — and
Q-1 already showed that assumption is worth testing rather than believing.

Whichever is chosen, **D must not ship**. The gesture currently returns control while the
statement runs on, and that is worse than having no cancel at all
- [x] Streaming: does the driver stream or buffer? — PR-4.2, RSK-6. **It streams.** 20,000 rows
  in 1090 ms, first row after 69 ms, managed heap flat. Bounded run, so this is the driver's
  behaviour at that size and says nothing about NFR-2's million rows
- [x] Instance-level connection (empty `Database=`) is **refused** by this server with `-354`,
  where the recorded 4.10 finding expected a real connection attempt. A database name is
  required in practice — see the Slice 1 note below
- [ ] ISAM error reporting, via a lock conflict or constraint violation — PR-3.6
- [ ] NULL INTERVAL: IMS infers null from `InvalidCastException`; the probe now measures this — PR-4.4
- [ ] NFR-2 scale, 20,000+ objects and 1,000,000+ rows — DEP-3 unmet

---

## Slice 1 — Connect and query

> **Acceptance (PRD §5):** register a connection, open an editor, run a multi-statement
> script, cancel a long-running one without killing the app, sortable result grid, CSV
> export, find yesterday's statement in history — against 14.10 (12.10 descoped, DEC-5). Unsaved
> editor content survives killing the process.
>
> This is the real deliverable (RSK-1). If it doesn't stop you reaching for `dbaccess`, it isn't done.

### 1a. Connection management

- [x] **M** Connection model + dialog: server name, host, port, protocol, matching `sqlhosts` semantics — PR-1.1
- [x] **M** Saved instance list: grouped by environment (Production / UAT / Development), searchable. Flat list, no hierarchy — PR-1.2, DEC-7
- [x] **M** Auth mode per connection: Local and LDAP/PAM — PR-1.3, DEC-6
- [x] **M** Windows Credential Manager integration. `ConnectionDescriptor` has no password field, so PR-1.4 holds by construction — PR-1.4, DEC-9
- [x] **M** Environment indicator: a word (`PRODUCTION`/`UAT`/`DEV`) on every list row and in the status bar; colour is secondary — PR-1.5, NFR-8
- [x] **M** Multiple concurrent connections; every tab shows its target instance — PR-1.6
- [x] **M** Dropped-connection detection → clear message → editor content kept — PR-1.7 *(the detection path is untested against a real drop)*
- [x] **M** CSDK check at startup, reported as a prerequisite failure — PR-1.8, NFR-6
- [x] **S** Import `sqlhosts` — both the registry and the file, not merged — PR-1.9
- [ ] **S** Encrypted connections — PR-1.10 *(`SECURITY=ssl` is emitted but unverified; flagged in code)*
- [ ] **C** SSH tunnel / bastion — PR-1.11 *(not started)*

### 1b. SQL editor

- [x] **M** Tabbed editor with Informix SQL + SPL highlighting (AvalonEdit; all three comment forms) — PR-3.1
- [x] **M** Context-aware completion — PR-3.2. Built in Slice 2 as planned, over the shared `CatalogCache`. See "How completion decides what to offer" below
- [x] **M** Execute whole script or selection only — PR-3.3
- [x] **M** Multi-statement execution, each outcome in sequence, failing statement identified by index and offset — PR-3.4
- [ ] **M** Cancel — PR-3.5, RSK-6. **Not met.** The code calls `OdbcCommand.Cancel` rather
  than only abandoning the await, but measured against 14.10 on 2026-08-06 that call does not
  reach the server: two statements, one slow by sorting and one by scanning, both ran on to
  their 30s timeout ~30s after the cancel. The session survives, so PR-3.5's second half holds
  and only the first fails. **The gesture is worse than absent** — Alt+Break returns control
  while the statement keeps running, and the user has no way to tell. `SQL_ATTR_ASYNC_ENABLE`
  was the cheap hope and is ruled out: the driver does not implement it. See Slice 0's
  "What PR-3.5 costs now" — this needs a decision before it needs code
- [x] **M** Error surface: SQLCODE + ISAM + explanation, ISAM winning where both exist — PR-3.6
- [x] **M** Transaction state in the status bar at all times; explicit commit/rollback — PR-3.7
- [x] **M** Warn before `UPDATE`/`DELETE` with no `WHERE`; literals and comments stripped first — PR-3.8, RSK-7
- [x] **M** Crash-safe autosave, recovered at next start — PR-3.9, NFR-3
- [x] **M** Keyboard: F5 execute, Ctrl+Enter selection, Alt+Break cancel, Ctrl+N new tab — PR-3.10, PR-8.1
- [x] **M** Open / save / reopen `.sql` files — PR-3.11
- [x] **M** Local searchable query history with target, timing, row count, outcome — PR-3.12, DEC-8
- [ ] **S** SQL formatter — PR-3.13 *(Slice 4)*
- [ ] **C** Named snippets — PR-3.14

#### How completion decides what to offer (PR-3.2)

`CompletionContext.Analyse` reads the caret's surroundings; `CompletionEngine.Suggest`
turns that into a list. Both are pure and synchronous, because this runs between one
keystroke and the next and NFR-1 does not carve out an exception for typing.

| Where the caret is | What it offers |
|---|---|
| After `FROM`, `JOIN`, `INTO`, `UPDATE` | Tables, views, synonyms, owners |
| In `SELECT`, `WHERE`, `ON`, `SET`, `GROUP BY`, `HAVING`, `ORDER BY` | Columns of the tables in scope first, then aliases, functions, objects, keywords |
| After `alias.` | That table's columns — an alias shadows a table of the same name |
| After `owner.` | Everything that owner owns. Checked *before* the table match, or `informix.` would answer with a table named `informix` |
| Anywhere else | The Informix language, then object names |

Decisions worth keeping:

- **Tables are collected from the whole statement, not just before the caret.**
  `SELECT ▮ FROM customer` is the order people type in.
- **Only the statement the caret is in.** A caret in the third statement of a script
  is not offered the first statement's tables.
- **Two auto-triggers, deliberately not three.** A dot always opens the list; a letter
  opens it only where a table name belongs. Everywhere else waits for Ctrl+Space
  (PR-8.1 — SSMS's gesture). A window that appears on every letter is one people turn off.
- **The cache never blocks.** `ICatalogSnapshot` returns what is cached and
  `RequestColumns` fetches the rest in the background, so the answer arrives for the
  next keystroke rather than stalling this one.
- **One reader, shared.** `SerializedCatalogReader` puts the tree and the completion
  cache behind one semaphore on one connection. An Informix connection has one cursor,
  so they would otherwise close each other's results — and a second session per
  instance is the cost PR-6.4 asks IMS not to add.
- **The detail text is the point** (PR-8.3). `MATCHES` says its wildcards are `*` and
  `?`, not `%` and `_`; `LENGTH` says it ignores trailing blanks; `INTERVAL` says its
  two classes do not mix. Entries with nothing Informix-specific to say carry no
  detail, so the column stays worth reading.

### 1c. Result grid

- [x] **M** Per-column sort and copy of cell/row/selection — PR-4.1 *(in-grid filter not built; see below)*
- [x] **M** Streaming + paged reads, 500 rows at a time, with "fetch more" — PR-4.2, NFR-2, RSK-6
- [x] **M** Row count and elapsed time per statement — PR-4.3
- [x] **M** `NULL` renders as italic `(null)` — a shape difference, not a colour one — PR-4.4, NFR-8
- [x] **M** Informix type rendering through `InformixValue`; large objects as a placeholder, never bytes in a cell — PR-4.5
- [x] **M** Export to CSV, tab-delimited, JSON and Excel — PR-4.6
- [x] **S** Concurrent result sets kept per tab — PR-4.7 *(came free with the design)*
- [ ] **S** Generate `INSERT` statements from a result set — PR-4.8 *(Slice 4)*
- [ ] **S** Vertical single-row view — PR-4.9 *(Slice 4)*
- [ ] **M** In-grid filter — part of PR-4.1, **not built**

### 1d. Slice 1 exit checks

Verified on the development workstation, with no database:

- [x] Release build clean with `TreatWarningsAsErrors`
- [x] 273 tests pass; no test opens a socket
- [x] The app starts, renders the shell, and logs the detected Client SDK
- [x] No credential or result data reaches the log — redaction enforced at the boundary and tested — PR-6.3

Still open, because each needs a live server:

- [ ] The full §5 acceptance script against **14.10** — RSK-9 *(12.10 descoped, DEC-5)*
- [ ] A 1,000,000+ row result set stays responsive — NFR-2 *(DEP-3 also unmet)*
- [ ] 200 ms input acknowledgement under real load — NFR-1, PR-8.5
- [ ] Cancel a long-running statement and keep the session — PR-3.5. **The session is kept; the
  statement is not cancelled.** Confirmed against both a sorting and a scanning workload,
  2026-08-06 — see §1b. **Pilot blocker.** RSK-1's premise is that IMS stops you reaching for
  `dbaccess`; a runaway statement you cannot stop is precisely when you would reach for it, and
  a cancel button that lies is worse than none
- [ ] Kill the process mid-edit against a live session and confirm recovery — PR-3.9 *(the autosave itself is tested)*

---

## Slice 2 — Browse

> **Acceptance (PRD §5):** expand a database with thousands of objects without stalling,
> find a table by partial name, read columns/indexes/constraints/storage/fragmentation
> without writing a query, script its DDL at `dbschema` fidelity.

- [x] **M** Catalogue query layer over `systables` et al., with capability detection rather than version branching — NFR-4, DEC-5
- [~] **M** Object tree — PR-2.1. **Verified against 14.10:** tables, views, synonyms, sequences, procedures, functions and indexes all list correctly. **User-defined types are not shown** — the `sysxtdtypes` query was the one listing query that failed, and the owner descoped it on 2026-08-06 rather than spend time diagnosing it. PR-2.1 names UDTs, so this Must is *partly* met. Constraints and triggers appear in the table detail tab rather than as tree folders
- [x] **M** Strictly on-demand loading of children; virtualised tree — PR-2.2, NFR-1, NFR-2
- [x] **M** Filter by object name; owner filter supported by the reader but not yet surfaced in the UI — PR-2.3
- [x] **M** Table detail: columns + types + nullability, indexes, constraints, triggers, owner, estimated row count, dbspace, lock mode, extent sizing, fragmentation strategy — PR-2.4. **Verified against 14.10** on 2026-08-06: defaults, `sysxtdtypes`-resolved types (`LVARCHAR`, `BOOLEAN`, a UDT), index uniqueness, statistics and lock mode all read correctly. **Moved on 2026-08-10** out of the results area and into the tab strip, one tab per object: the results area is where a *statement's* output goes, and a single shared pane could only ever show one object and retargeted itself as the tree selection moved. `ObjectDetailTabViewModel` wraps the unchanged `TableDetailViewModel`; the old `IsDetailVisible` gate went with it, since a tab that does not follow the selection needs no gate to keep metadata queries negligible (PR-6.4)
- [x] **M** Statistics currency indicator (current vs stale) — PR-2.5. `ustlowts` is probed once and remembered; absent, IMS reports Unknown rather than guessing
- [~] **M** DDL scripting into a new editor tab, at `dbschema` fidelity — PR-2.6. Built for tables, views, indexes, procedures and functions. **The acceptance test is not run:** `dbschema` ships with the *server*, not the CSDK, so it is not on the development machine — see "Open verification" below
- [x] **M** Subtree refresh without rebuilding the whole tree — PR-2.7
- [x] **M** Show the underlying catalogue query behind a tree node — PR-8.2, PR-8.3 *(detail pane still to come)*
- [x] **M** In-context explanation of Informix concepts surfaced in the detail tab (dbspace, extent, fragmentation) — NFR-11. `InformixConcepts` in the detail view model
- [x] **S** Tree shortcuts — PR-2.8. `SELECT` first 100 rows, copy qualified name, and "Script as CREATE"
- [ ] **S** Dependencies and dependents — PR-2.9 *(Slice 4)*
- [ ] **S** Whole-database re-runnable schema script — PR-2.10 *(Slice 4)*
- [ ] Exit check: 20,000+ object database, no stall — NFR-2

### PR-2.6 — what the scripter covers, and what it does not

Scripted: **tables** (columns, types, defaults, nullability, primary/unique/foreign/check
constraints, standalone indexes, dbspace, fragmentation, extent sizing, lock mode),
**views** (from `sysviews.viewtext`, the server's own words), **indexes**,
**procedures** and **functions** (from `sysprocbody`).

Not scripted, and the menu item greys out rather than producing an empty tab:
synonyms, sequences and user-defined types.

Deliberate differences from `dbschema`, each one written into `DdlScripter`'s remarks:

| Difference | Why |
|---|---|
| Two leading `--` provenance lines | A script with no provenance is one someone will read as the server's own words |
| No `{ TABLE … row size = n }` banner | It reports storage arithmetic IMS does not compute — PR-8.4 rules out presenting an inference as a fact |
| No `grant`/`revoke` | IMS does not read `systabauth`. The comment says so rather than leaving it to be discovered |
| Triggers listed in a trailing comment, not scripted | Their text is in `systrigbody`, which IMS does not read |
| `serial` without `not null` | `dbschema` writes the words; Informix rejects them on some paths. A script that runs beats a byte-exact diff |

**Open verification.** PR-2.6's own acceptance test is a diff against real `dbschema`
output. `dbschema` ships with the Informix *server*, not the CSDK, so it is not on this
machine and the diff needs someone with server access:

```
dbschema -d <db> -t <table>
```

against the same table IMS scripts. The unit tests pin the output to the format
transcribed from `dbschema`'s published shape, so the diff is against a deliberate
baseline rather than whatever the code happened to emit — but a transcription is not a
comparison, and this stays `[~]` until the comparison is run.

---

## Slice 3 — Observe

> **Q-1 is answered: unblocked.** The smoke test read `sysmaster:syssessions` as an
> ordinary developer account against 14.10, so AS-3 holds and this slice serves U1 —
> the primary user — not only U2 and U3. Its priority stands as written; no
> re-prioritisation against §8 is needed.

- [x] **Q-1 answered:** ordinary developers can read `sysmaster` — Q-1, AS-3, DEP-4
- [x] **M** Session list: id, user, originating host, application, connection time, state — PR-5.1.
  Built on `sysmaster:syssessions`, the one object here with a measured success against 14.10.
  Current SQL moved to the detail pane rather than the list — see the note below
- [~] **M** Selected-session detail: locks held and awaited, resource consumption, temp space — PR-5.2.
  Locks built. **Current SQL is refused by this estate** and **the resource counters and
  per-session temp space are not met** — both measured 2026-08-13, see below
- [ ] **M** Blocked-session identification, with the blocker named — PR-5.3. **Not met on this
  estate.** Every read of `sysmaster:syslocks` times out at the 10s cap — the self-join *and* a
  plain single scan, measured 2026-08-13. The resolver, fidelity grading and chain logic are built
  and tested; what is missing is a source that answers in time. See below
- [x] **M** Sort/filter the session list; highlight the user's own sessions — PR-5.4. Sort is the
  grid's own; the filter is the app's first `ICollectionView`, client-side. "YOU" is a word in a
  column, with the row tint strictly secondary (NFR-8)
- [x] **M** Manual refresh by default; optional user-chosen interval; **no query at all while the
  view is closed** — PR-5.5, PR-6.4. The decision lives in `RefreshPolicy` in `Ims.Core`, not in
  the view model, so all three clauses are unit-tested. Deselecting the tab suspends as surely as
  closing it, and `ViewClosed` is terminal
- [x] **M** Name the equivalent `onstat` command on every view (`-g ses`, `-g sql`, `-g lok`) and
  expose the raw output — PR-8.2, PR-8.3. `ServerQuery` carries purpose, SQL and the `onstat`
  equivalent together, so the pair cannot drift; failed sections show their query and why
- [~] **S** Instance indicators: version, mode, uptime, session count, buffer efficiency,
  checkpoint recency — PR-5.6. Built as a header strip. Each degrades to "Unknown" independently
- [x] **S** Lock-wait dependency chain for >2 sessions — PR-5.7. `LockWaitChain` in `Ims.Core`,
  pure and cycle-safe. **Unexercised against real data**
- [ ] ~~Terminate a session~~ — **excluded**, PR-5.8 / DEC-2. Do not build. It pulls in a
  confirmation framework and an audit store (DEC-8)
- [ ] Exit check: two sessions, one blocking the other, identified from the UI alone — §5.
  **Still open, and it is the acceptance bar.** See below

### What is unverified, and why — read this before trusting the monitor

Slice 3 was written without a live server to check against. The consequence is concentrated in
one place: **`sysmaster` column names.** `syssessions.sid` and `.username` are confirmed — the
Slice 0 smoke test read them as an ordinary developer, which is what answered Q-1 — and
everything else is an educated guess.

So the reader follows `GetTableDetailAsync`'s shape at the smallest useful granularity: every
uncertain read is wrapped on its own, and a missing column costs one section of one pane rather
than the view. Two invariants in `SessionQueries.cs` back it up, both enforced by tests:

1. **Every query is bounded before it is sent** — `FIRST n` plus a 10s timeout. `Cancel()` does
   not reach this server, so a token stops IMS waiting and the statement runs on (RSK-5).
2. **No query selects an INTERVAL**, because `System.Data.Odbc` cannot read one and every column
   at or after it dies too. Durations are epoch integers converted client-side. Uncertain columns
   go **last** in the select list so a surprise type costs one column, not the tail.

**Run `Ims.SmokeTest --probe-sessions` before relying on any of this.** It checks each uncertain
object and column in turn and reports which this server actually exposes — bounded by `FIRST`, so
it is safe on a shared instance. It is the intended way to settle the guesses; update
`SessionQueries.cs` with the real names and re-run.

### Measured against 14.10.FC10W2X7 on 2026-08-13 — three findings, two of them corrections

The monitor was run against the UAT instance (`pronto_net/t01`) as an ordinary account. The
degradation held — every failure below was an Information-level log and a named pane section,
never a crash — but three guesses were wrong and the code has been changed to match:

| What | Server said | What changed |
|---|---|---|
| **`syslocks` — any read of it** | `HYT00 Timeout expired`, at the 10s cap, for the self-join *and* for a plain single scan | See below. **This is the finding that matters** |
| **`sysrstcb.dbnum`** | `42S22: Column (dbnum) not found` | **Removed from the select list.** It took the two memory columns with it — one absent name costs everything selected alongside it — so that query now asks for as little as it can |
| **`syssqlcurses`** | `42000: No SELECT permission` | **Nothing to change.** The table exists and the column names may well be right; an ordinary account simply cannot read it. So PR-5.1's "current SQL" is a *privilege* limit on this estate, not a naming error, and no amount of query fixing will reach it. Granting `SELECT` on `syssqlcurses` would — that is a DBA decision, and PR-6.1 says IMS must not work around it |

#### `syslocks` cannot be read inside a monitor's budget on this estate — and that is PR-5.3

The first diagnosis was wrong and is worth recording as such. The self-join timed out, so it was
replaced with a single scan of `syslocks.waiter` on the theory that the join was quadratic over an
unindexed pseudo-table. **The single scan then timed out too** (measured 16:44, and the fallback
join behind it at 16:45). So the join was never the problem: **`syslocks` is expensive to
materialise at all here.** It is synthesised from shared memory across every lock in the instance,
and on a busy server that costs more than ten seconds regardless of what the predicate asks for.

Three consequences, all now in the code:

1. **A timeout no longer costs double.** The fallback reads the same pseudo-table, so once
   `syslocks` has timed out it cannot do better — trying it anyway spent 20+ seconds to reach the
   same `Unknown`. `IsTimeoutState` tells a timeout (`HYT00`/`HY008`) apart from a shape problem
   (`42S22`), and only the latter is worth a fallback. The skipped query still appears in the
   PR-8.2 list, marked `NotAttempted` with the reason.
2. **The UI distinguishes "too slow" from "not there."** They point at different remedies — a
   timeout says reach for `onstat -g lok`, an absence says this server has nothing to give — and
   calling a timeout "does not expose" would be a small lie about the server that sends someone
   hunting a permission that was never the problem.
3. **Raising the timeout is not the fix.** Ten seconds is already generous for a monitor
   (NFR-1), and `Cancel()` does not reach this server, so a longer cap means a longer *unstoppable*
   statement holding the connection the object tree shares (PR-6.4). The honest position is that
   PR-5.3 is not reachable through `syslocks` on a busy instance, and `onstat -g lok` is.

**So PR-5.3 is unmet on this estate, and the reason is performance rather than shape.** The
resolver, the fidelity grading and the chain logic are all built and tested and would work the
moment a readable source appears — a quieter instance, or a narrower lock view. What is not
established is that one exists here.

Still unconfirmed: `syssessions.feprogram`, `.state` and `.connected`, and whether
`syslocks.waiter` exists at all — the timeout means even that much is unknown, since the statement
never got far enough to complain about a column. `--probe-sessions` asks for it in isolation,
which is the cheapest question that could settle it.

**How PR-5.3 works when its source answers.** The primary read is `syslocks.waiter` — the session
the server has already queued behind a lock, so no lock-mode test is applied on that path:
Informix decided the modes conflict before it made anyone wait. The self-join fallback is
different, and its rows are only ever *contention*, because two sessions on one resource may both
hold compatible locks and block nothing. Either way the result is graded rather than asserted:

| Fidelity | What the UI says |
|---|---|
| `BlockerIdentified` | "N session(s) blocked", with the blocker named in the row |
| `ContentionOnly` | "Sessions are contending on the same rows; IMS cannot tell which is waiting" |
| `Unknown` | "This server does not expose lock waits to IMS" |

That grading is not hedging. **The worst remaining failure mode is `syslocks.owner` being a
process id rather than a session id** — it is read as the holder's session id on both paths, so if
it is a pid, IMS names the wrong blocker silently, and someone might interrupt a colleague's work
on its word. Nothing measured so far settles it: a wrong number here looks exactly like a right
one. It is why an unrecognised lock mode is treated as *not* conflicting on the fallback path,
downgrading the answer rather than inventing a blocker, and why the two-session exit check below
still matters even though the algorithm is well covered.

**Per-session temp space is deliberately absent**, not forgotten. Deriving it needs partition
detail IMS does not read, and PR-8.4 rules out presenting an inference as a fact. Temp space is
explained at the instance level instead (`InformixConcepts.TempSpace`).

**Current SQL is in the detail pane, not the list.** PR-5.1 names it among the list columns, so
this is a conscious reading of the requirement: the statement text is the largest thing this
slice fetches, and pulling it for 500 rows on every refresh is the one query here that would not
be negligible (PR-6.4). It is also the least certain table, so keeping it out of the list means a
wrong column name costs a pane section rather than the session list. If daily use wants it in the
list, fetch it as a second bounded read over the returned sids — never as a join, which would
take the list down with it.

**The §5 acceptance test cannot be run here.** It needs two sessions with one blocking the other,
and DEP-2 is unmet: `testdb` sits on the production server, so there is nowhere safe to arrange a
lock wait. `LockWaitChainTests` covers the shapes a live server would not reliably produce anyway
— a three-deep chain, a fork, a genuine deadlock cycle, a session stranded behind one — but a
test of the algorithm is not a test of the query feeding it. **This stays open.**

Also fixed on the way through, both real: `ShowHostForSelectedTab` was a two-way boolean that
rendered a greyed-out empty editor for any third tab kind, and `ShutdownAsync` iterated
`EditorTabs` only — so a monitor's refresh timer would have survived shutdown, leaving a closing
IMS still querying a production instance.

---

## Slice 4 — Refine

No fixed contents by design — this is where scope shrinks. Pull items in only if daily use
has proven they're missed: PR-1.10, PR-2.9, PR-2.10, PR-3.13, PR-4.8, PR-4.9.

### UX polish **[infra]**

Small affordances with no PRD requirement behind them. They are recorded here rather than in
PRD §8 because §8 is the pressure valve for *capabilities* (RSK-2), and sizing these as
deferred scope would overstate them. None is a Must; none should displace a Must.

- [x] Middle-click a tab header to close it — a second affordance over the existing ✕, never a
  replacement, so NFR-8 keeps a route that needs no three-button mouse. Hooked via
  `ItemContainerStyle` on the whole `TabItem` rather than the item template, because aiming at
  the label is not how anyone middle-clicks. It goes through the same `CloseTabAsync` path, so
  PR-3.9's autosave still applies. Two faults surfaced while building it, both from closing a
  tab that is *not* the one on screen — the ordinary case for this gesture and one the ✕ made
  rare: unsaved text in the visible editor was not flushed to its own tab first, and selection
  jumped to the last tab instead of staying put. Both fixed
- [x] **Query toolbar above the tab strip** — the discoverability half of PR-8.1. Every query
  action was reachable only from the menu bar or by knowing its gesture, Execute included. The
  buttons drive the existing routed commands, so there is one definition per action and the
  menu and toolbar cannot drift. Notes worth keeping:
  - **A `Border` + `DockPanel`, not WPF's `ToolBar`.** `ToolBar` replaces each child `Button`'s
    template with its own chrome, which fights the inline padding used everywhere else in this
    window. Worse, it moves buttons it cannot fit into an overflow popup — and this toolbar sits
    in a column whose width the user drags, so Execute would vanish mid-drag and
    `AutomationProperties` on an overflowed item is unreachable, taking NFR-8 with it
  - **The button run is in a hidden-bar `ScrollViewer`**, the same trick as the tab strip below
    and for the same reason. A `DockPanel` measures at its full desired width whatever space is
    on offer, so without it the run pushed the trailing History/Help group clean off the window
    — measured 260px past the right edge, both buttons enabled and undrawable. `ClipToBounds`
    alone does not fix it: it clips the drawing, not the measure
  - **`EditorTabViewModel.CanExecute` was computed and bound to nothing** until this. Execute on
    a disconnected tab was a silent no-op. The Query menu now binds the same property, so the
    two agree
  - **`ClearResultsCommand` is new**, and exposes `ClearResultsAsync` — written for PR-4.7 and
    never reachable by a user since. It disposes each result, so it returns cursors as well as
    screen space
  - **Icons: `FluentIcons.Wpf` (MIT)**, Microsoft's fluentui-system-icons. A glyph font, so it
    scales with DPI and takes its colour from the button. MIT keeps the DEC-10 open-source
    option intact. Note the casing — the older `FluentIcons.WPF` is deprecated on nuget.org.
    Commit and Roll back keep text labels beside their icons: confusing those two is not
    recoverable and no pair of icons tells them apart
  - **Deferred, with reasons.** SSMS's *database dropdown*: `ConnectionDescriptor.Database` is
    fixed for the life of a connection, there is no enumeration, and switching would mean
    cloning the descriptor and reconnecting — silently dropping any open transaction. A
    read-only target label stands in its place. *Parse* and both *execution-plan* buttons: there
    is no SQL parser here (`Ims.Core.Sql` is lexical only) and PR-6.2 says IMS sends no
    statement the user did not type. *Format SQL* is PR-3.13, still deferred
- [ ] **Switch the bottom pane to Messages when a statement fails** — arguably a **PR-3.4
  refinement** rather than polish: PR-3.4 requires "indicating clearly which statement failed",
  and today the failure is written into `Outcomes` (the Messages list) while the pane stays on
  Results, so the user has to know to go looking for it. `ExecuteAsync` already counts `failed`
  but it is a local, so this needs a bound signal — e.g. an `AnyFailed` property on
  `EditorTabViewModel`, with `ResultsArea` selecting the Messages `TabItem` in response.
  **Decide the partial-failure case first:** when statement 2 of 5 fails but the others returned
  rows, switching away steals a pane the user is about to read. Recommend switching only when no
  result set was produced, and otherwise leaving the pane alone — the status bar already says
  "N of M statement(s) failed", so the failure is not silent either way. Auto-switching
  unconditionally is the version most likely to become annoying in daily use
- [ ] **Close the tab from the keyboard (Ctrl+W or Ctrl+F4)** — arguably a **PR-3.10 gap**, not
  polish: PR-3.10 requires full keyboard operation of "execute, cancel, new tab, switch tab",
  and closing is absent from that list *and* from the gestures registered in `MainWindow.xaml.cs`.
  So a tab can currently only be closed with the mouse, which also puts NFR-8's keyboard
  operability at risk. Worth deciding whether PR-3.10 should name it — if so this leaves
  Slice 4 and becomes an **M** in §1b

---

## Cross-cutting — verify continuously, not at the end

- [ ] **PR-6.1** No capability beyond the user's own Informix privileges. No shared/service credential
- [ ] **PR-6.2** No statement sent that the user didn't type or explicitly request via a documented action. Audit every generated query against this
- [ ] **PR-6.4** Metadata and session queries kept negligible on a production instance
- [ ] **NFR-3** No unhandled termination across a full working day of real use
- [ ] **NFR-7** Install and update without per-user local administrator rights
- [ ] **NFR-8** Keyboard operability, screen-reader labelling, no colour-alone state
- [ ] **NFR-9** Informix locales, code sets, collations handled correctly. Localisable, shipped English
- [ ] **NFR-11** Short getting-started note
- [ ] **DEC-10** No CSDK redistribution — require a separately installed client
- [ ] **RSK-2** Anything new goes into §8 first and stays there
- [ ] **RSK-5** Iterate metadata queries against non-prod only, never production

---

## Open questions to resolve

| ID | Question | Needed by |
|---|---|---|
| ~~Q-1~~ | ~~Can ordinary developers read `sysmaster`?~~ | **Closed.** Yes — measured against 14.10. Slice 3 unblocked |
| Q-2 | Strictly agentless, or is an optional local agent acceptable later? | Before Tier 1 |
| Q-3 | Which §8 Tier 1 item comes first? | After Slice 3 — answer from real use |

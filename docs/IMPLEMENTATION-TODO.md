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
> - `--include-load` — the original unbounded form: a four-way cross join with
>   `CommandTimeout = 0`. If `Cancel()` does not land, nothing stops it. **Do not run this
>   against the production server**; it needs an instance of its own (RSK-5, PR-6.4).

- [ ] Cancellation: does `OdbcCommand.Cancel()` leave the session usable? — PR-3.5. **Attempted
  2026-08-06, inconclusive:** the bounded statement finished before the two-second cancel, so
  `Cancel()` was never exercised. `systables` on this instance is small enough that a three-way
  join is not slow. The cap is now a four-way join under `FIRST 5000000` — retry with
  `--include-light-load`
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
- [x] **M** Cancel: the token reaches the server via `OdbcCommand.Cancel`, not just the await — PR-3.5, RSK-6 *(unverified against a server)*
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
- [ ] Cancel a long-running statement and keep the session — PR-3.5
- [ ] Kill the process mid-edit against a live session and confirm recovery — PR-3.9 *(the autosave itself is tested)*

---

## Slice 2 — Browse

> **Acceptance (PRD §5):** expand a database with thousands of objects without stalling,
> find a table by partial name, read columns/indexes/constraints/storage/fragmentation
> without writing a query, script its DDL at `dbschema` fidelity.

- [x] **M** Catalogue query layer over `systables` et al., with capability detection rather than version branching — NFR-4, DEC-5
- [~] **M** Object tree — PR-2.1. **Verified against 14.10:** tables, views, synonyms, sequences, procedures, functions and indexes all list correctly. **User-defined types are not shown** — the `sysxtdtypes` query was the one listing query that failed, and the owner descoped it on 2026-08-06 rather than spend time diagnosing it. PR-2.1 names UDTs, so this Must is *partly* met. Constraints and triggers appear in the table detail pane rather than as tree folders
- [x] **M** Strictly on-demand loading of children; virtualised tree — PR-2.2, NFR-1, NFR-2
- [x] **M** Filter by object name; owner filter supported by the reader but not yet surfaced in the UI — PR-2.3
- [x] **M** Table detail pane: columns + types + nullability, indexes, constraints, triggers, owner, estimated row count, dbspace, lock mode, extent sizing, fragmentation strategy — PR-2.4. **Verified against 14.10** on 2026-08-06: defaults, `sysxtdtypes`-resolved types (`LVARCHAR`, `BOOLEAN`, a UDT), index uniqueness, statistics and lock mode all read correctly
- [x] **M** Statistics currency indicator (current vs stale) — PR-2.5. `ustlowts` is probed once and remembered; absent, IMS reports Unknown rather than guessing
- [~] **M** DDL scripting into a new editor tab, at `dbschema` fidelity — PR-2.6. Built for tables, views, indexes, procedures and functions. **The acceptance test is not run:** `dbschema` ships with the *server*, not the CSDK, so it is not on the development machine — see "Open verification" below
- [x] **M** Subtree refresh without rebuilding the whole tree — PR-2.7
- [x] **M** Show the underlying catalogue query behind a tree node — PR-8.2, PR-8.3 *(detail pane still to come)*
- [x] **M** In-context explanation of Informix concepts surfaced in the detail pane (dbspace, extent, fragmentation) — NFR-11. `InformixConcepts` in the detail view model
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
- [ ] **M** Session list: id, user, originating host, application, connection time, state, current/recent SQL — PR-5.1
- [ ] **M** Selected-session detail: locks held and awaited, resource consumption, temp space — PR-5.2
- [ ] **M** Blocked-session identification, with the blocker named — PR-5.3
- [ ] **M** Sort/filter the session list; highlight the user's own sessions — PR-5.4
- [ ] **M** Manual refresh by default; optional user-chosen interval; **no query at all while the view is closed** — PR-5.5, PR-6.4
- [ ] **M** Name the equivalent `onstat` command on every view (`-g ses`, `-g sql`, `-g lok`) and expose the raw output — PR-8.2, PR-8.3
- [ ] **S** Instance indicators needing no privileged access: version, mode, uptime, session count, buffer efficiency, checkpoint recency — PR-5.6
- [ ] **S** Lock-wait dependency chain for >2 sessions — PR-5.7
- [ ] ~~Terminate a session~~ — **excluded**, PR-5.8 / DEC-2. Do not build. It pulls in a confirmation framework and an audit store (DEC-8)
- [ ] Exit check: two sessions, one blocking the other, identified from the UI alone — §5

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

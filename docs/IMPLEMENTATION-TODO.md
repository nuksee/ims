# IMS — Implementation To-Do

Derived from [PRD-Informix-Management-Studio.md](PRD-Informix-Management-Studio.md) v0.3.
Ordered by the four slices in PRD §5. Every task carries the requirement ID it satisfies;
tasks with no ID are marked **[infra]** and are not traceable to the PRD.

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · **M/S/C** = PRD priority.

---

## Slice 0 — Foundation **[infra]**

Not in the PRD. Prerequisite for everything below; keep it thin — DEC-12 says every slice
must be usable alone, and this one isn't, so it should be measured in days not weeks.

- [ ] Solution scaffold: `Ims.App` (WPF), `Ims.Core` (domain/services), `Ims.Data` (provider access), `Ims.Tests` — DEC-3
- [ ] Pin target framework and confirm it is installed on target workstations — NFR-5
- [ ] **Spike: Informix provider choice.** Prove `IBM.Data.Db2` / `IBM.Data.Informix` vs ODBC against a live 12.10 *and* 14.10 instance before committing. Decide on: cancellation support, `DATETIME`/`INTERVAL`/`MONEY` fidelity, streaming reads, error-code detail — DEC-4, DEC-5, RSK-9
- [ ] Decide async/threading model up front: no server call on the UI thread, ever — NFR-1, PR-8.5
- [ ] Structured local logging with a credential/result-data redaction filter baked in from day one — NFR-10, PR-6.3
- [ ] Confirm no telemetry package enters the dependency graph; add a check — PR-6.5
- [ ] CI build + test on push
- [ ] Secure DEP-2 (non-prod 12.10 and 14.10 instances) and DEP-3 (a realistic-size schema for NFR-2)

---

## Slice 1 — Connect and query

> **Acceptance (PRD §5):** register a connection, open an editor, run a multi-statement
> script, cancel a long-running one without killing the app, sortable result grid, CSV
> export, find yesterday's statement in history — against both 12.10 and 14.10. Unsaved
> editor content survives killing the process.
>
> This is the real deliverable (RSK-1). If it doesn't stop you reaching for `dbaccess`, it isn't done.

### 1a. Connection management

- [ ] **M** Connection model + dialog: server name, host, port, protocol, matching `sqlhosts` semantics — PR-1.1
- [ ] **M** Saved instance list: grouped by environment (Production / UAT / Development), searchable by name. Flat list, no hierarchy — PR-1.2, DEC-7
- [ ] **M** Auth mode per connection: Local and LDAP/PAM — PR-1.3, DEC-6
- [ ] **M** Windows Credential Manager integration. No credential ever touches a config file — PR-1.4, DEC-9
- [ ] **M** Environment indicator: persistent, unmistakable, **not colour-alone** — PR-1.5, NFR-8
- [ ] **M** Multiple concurrent connections in one workspace; every editor and pane shows its target instance unambiguously — PR-1.6
- [ ] **M** Dropped-connection detection → clear message → reconnect with editor content intact — PR-1.7
- [ ] **M** CSDK presence/config check at startup, reported as a prerequisite failure, not a connection failure — PR-1.8, NFR-6
- [ ] **S** Import `sqlhosts` to populate the instance list — PR-1.9 *(may slip to Slice 4)*
- [ ] **S** Encrypted connections where the server supports them — PR-1.10 *(may slip to Slice 4)*
- [ ] **C** SSH tunnel / bastion — PR-1.11 *(only if effectively free)*

### 1b. SQL editor

- [ ] **M** Tabbed editor with Informix SQL + SPL syntax highlighting (evaluate AvalonEdit) — PR-3.1
- [ ] **M** Context-aware completion: schema objects, columns, Informix built-ins — PR-3.2 *(largest single item in the slice; the object-metadata cache it needs is shared with Slice 2)*
- [ ] **M** Execute whole script or selection only — PR-3.3
- [ ] **M** Multi-statement execution: statement splitter, per-statement result/error in sequence, failing statement clearly identified — PR-3.4
- [ ] **M** Cancel a running statement without killing the session or the app — PR-3.5, RSK-6 *(verify the provider actually supports this in the Slice 0 spike)*
- [ ] **M** Error surface: Informix error code + ISAM error + plain-language explanation — PR-3.6
- [ ] **M** Transaction state always visible; explicit commit/rollback when autocommit is off — PR-3.7
- [ ] **M** Warn before `UPDATE`/`DELETE` with no `WHERE` — PR-3.8, RSK-7
- [ ] **M** Crash-safe autosave of unsaved editor content — PR-3.9, NFR-3
- [ ] **M** Full keyboard operation: execute, cancel, new tab, switch tab. Follow SSMS bindings — PR-3.10, PR-8.1, NFR-8
- [ ] **M** Open / save / reopen `.sql` files — PR-3.11
- [ ] **M** Local query history: statement, target instance, timing, row count, outcome; searchable — PR-3.12, DEC-8
- [ ] **S** SQL formatter — PR-3.13 *(Slice 4)*
- [ ] **C** Named snippets — PR-3.14

### 1c. Result grid

- [ ] **M** Per-column sort, in-grid filter, copy cell / row / selection — PR-4.1
- [ ] **M** Streaming + paged reads. An unbounded `SELECT` must degrade, not exhaust memory — PR-4.2, NFR-2, RSK-6
- [ ] **M** Row count and elapsed time per statement — PR-4.3
- [ ] **M** `NULL` visually distinct from empty string and from zero — PR-4.4
- [ ] **M** Informix type rendering: `DATETIME` with qualifier, `INTERVAL`, `DECIMAL`, `MONEY`, `BOOLEAN`; `BYTE`/`TEXT`/smart LOBs as a viewable value, never raw bytes in a cell — PR-4.5
- [ ] **M** Export result set or selection to CSV, delimited text, JSON, Excel — PR-4.6
- [ ] **S** Keep concurrent result sets from several statements/tabs — PR-4.7 *(Slice 4)*
- [ ] **S** Generate `INSERT` statements from a result set — PR-4.8 *(Slice 4)*
- [ ] **S** Vertical single-row view — PR-4.9 *(Slice 4)*

### 1d. Slice 1 exit checks

- [ ] Run the full §5 acceptance script against 12.10 **and** 14.10 — RSK-9
- [ ] Verify 1,000,000+ row result set stays responsive — NFR-2
- [ ] Verify 200 ms input acknowledgement on every interactive action — NFR-1, PR-8.5
- [ ] Kill the process mid-edit; confirm content recovers — PR-3.9
- [ ] Audit logs for leaked credentials or result data — PR-6.3

---

## Slice 2 — Browse

> **Acceptance (PRD §5):** expand a database with thousands of objects without stalling,
> find a table by partial name, read columns/indexes/constraints/storage/fragmentation
> without writing a query, script its DDL at `dbschema` fidelity.

- [ ] **M** Catalogue query layer over `systables` et al., with capability detection rather than version branching — NFR-4, DEC-5
- [ ] **M** Object tree: databases → tables, views, synonyms, indexes, constraints, triggers, procedures, functions, sequences, UDTs — PR-2.1
- [ ] **M** Strictly on-demand loading of children and detail; virtualised tree — PR-2.2, NFR-1, NFR-2
- [ ] **M** Filter/search by object name, type, owner — PR-2.3
- [ ] **M** Table detail pane: columns + types + nullability, indexes, constraints, triggers, owner, estimated row count, dbspace, lock mode, extent sizing, fragmentation strategy — PR-2.4
- [ ] **M** Statistics currency indicator (current vs stale) — PR-2.5
- [ ] **M** DDL scripting into a new editor tab, at `dbschema` fidelity. Diff against real `dbschema` output as the test — PR-2.6
- [ ] **M** Subtree refresh without rebuilding the whole tree — PR-2.7
- [ ] **M** Show the underlying catalogue query behind every structured view — PR-8.2, PR-8.3
- [ ] **M** In-context explanation of Informix concepts surfaced in the detail pane (dbspace, extent, fragmentation) — NFR-11
- [ ] **S** Tree shortcuts: `SELECT` first *n* rows, script object, copy qualified name — PR-2.8
- [ ] **S** Dependencies and dependents — PR-2.9 *(Slice 4)*
- [ ] **S** Whole-database re-runnable schema script — PR-2.10 *(Slice 4)*
- [ ] Exit check: 20,000+ object database, no stall — NFR-2

---

## Slice 3 — Observe

> **Gated on Q-1.** Before writing any of this: connect as an *unprivileged* developer
> account and run the `sysmaster` session queries. If they fail and no role can be granted,
> this slice serves only U2/U3 — stop and re-prioritise against §8 (AS-3, DEP-4).

- [ ] **BLOCKER** Answer Q-1: can ordinary developers read `sysmaster`? Can a role be granted? — Q-1, AS-3
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
has proven they're missed: PR-1.9, PR-1.10, PR-2.9, PR-2.10, PR-3.13, PR-4.7, PR-4.8, PR-4.9.

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
| Q-1 | Can ordinary developers read `sysmaster`? | **Before Slice 3** — cheap to test, blocks the whole slice |
| Q-2 | Strictly agentless, or is an optional local agent acceptable later? | Before Tier 1 |
| Q-3 | Which §8 Tier 1 item comes first? | After Slice 3 — answer from real use |

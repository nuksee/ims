# Product Requirements Document
## Informix Management Studio (IMS) — Version 1

| Field | Value |
|---|---|
| Product | Informix Management Studio (IMS) |
| Document | Product Requirements Document, v1 scope |
| Version | 0.3 |
| Date | 2026-08-05 |
| Owner | Kaveh Shahbazi |
| Status | Working document — revise as decisions change |

### Revision history

| Version | Date | Change |
|---|---|---|
| 0.1 | 2026-08-05 | Initial draft as a BRD — full three-phase programme |
| 0.2 | 2026-08-05 | Rescoped to a minimal v1 |
| 0.3 | 2026-08-05 | Reframed as a PRD. Solo, part-time context applied: approval ceremony and success metrics removed, personas reduced to real users, delivery restructured into vertical slices with per-slice acceptance criteria. Six open questions closed. |

> **Note on document type.** v0.1–0.2 were written as a BRD. That was the wrong instrument: a BRD exists to secure funding from business stakeholders, and this project has none — it is solo work on sanctioned time. This is a PRD, whose reader is the author some months from now, deciding what to build next and what was already settled. [§3 Decisions](#3-decisions) is retained with rationale for exactly that reason.

---

## 1. Overview

IMS is a Windows desktop application for working with IBM Informix — the tool SQL Server Management Studio is for SQL Server. One window in which to connect to a server, browse its objects, write and run SQL, and see what the server is doing.

Informix has no equivalent. Day-to-day work is split across `dbaccess` for SQL, `dbschema` for object definitions, the `onstat` family for diagnostics, and generic third-party clients that treat Informix as a lowest-common-denominator JDBC target. The practical effects: writing and iterating on SQL is slower than the same work in any modern IDE; finding a table's columns or indexes needs either memorised catalogue knowledge or a CLI invocation; and answering "is my query blocked, and by what?" requires someone fluent in `onstat`.

**v1 is deliberately small.** Four capabilities: connection management, object browser, SQL editor with a usable result grid, and a read-only session monitor. IMS performs no administrative changes of its own — every statement it sends is one the user typed ([DEC-2](#3-decisions)). This keeps the build achievable part-time, and makes it safe to point at production and safe to hand to a colleague on day one.

Everything else — execution plan visualisation, storage and log health, backup, cluster monitoring, security administration — is in [§8](#8-deferred-scope) as a prioritised backlog, and explicitly not in v1.

---

## 2. Goals and non-goals

### 2.1 Goals

| ID | Goal |
|---|---|
| G-1 | Make writing and iterating on Informix SQL as pleasant as it is on other platforms |
| G-2 | Make schema discovery immediate — no catalogue queries, no `dbschema` |
| G-3 | Let a non-DBA answer basic "what is the server doing" questions unaided |
| G-4 | Be good enough that a colleague new to Informix can be productive with it |
| G-5 | Be safe enough to point at production without hesitation |

### 2.2 Non-goals

IMS v1 does **not**:

- Perform administrative changes. No dialog issues a configuration change, storage operation, backup, restore, mode change, session kill, or role transition ([DEC-2](#3-decisions)).
- Replace Informix's utilities. The CLI stays the tool for anything IMS doesn't cover, and IMS should teach it rather than hide it ([PR-8.3](#7-design-principles)).
- Monitor or alert. IMS shows live state while open; it never runs unattended, polls on a schedule, or raises alerts.
- Support any database other than Informix.
- Provide reporting, BI, or ETL capability.

There are no success metrics in this document. Adoption and time-saving targets are meaningless at this scale; per-slice acceptance criteria in [§5](#5-scope-and-delivery) serve the purpose instead.

---

## 3. Decisions

Settled deliberately. Each carries its rationale, because the rationale is the part that gets forgotten. Changing any of these changes the shape of v1.

| ID | Decision | Rationale |
|---|---|---|
| DEC-1 | **The primary user is the application developer** — the author, plus a small number of colleagues. The DBA is a secondary beneficiary. | The SQL-and-browse core covers the widest daily use. Admin depth is in §8 and only becomes worth building once v1 proves itself in real use. |
| DEC-2 | **IMS issues no administrative writes of its own.** Users may execute any SQL their Informix privileges permit, including DML and DDL. Gating is done by Informix, not by IMS. | The single most valuable constraint in this document. It removes the need for a confirmation framework, an IMS role model, and an audit store — and it is what makes IMS safe to hand to a colleague and safe to point at production. Developers keep their normal workflow because they keep their normal privileges. |
| DEC-3 | **Windows desktop application, .NET / WPF.** | Closest to the SSMS experience the intended users already know; best fit for the Informix .NET provider; Windows is the client platform in use. |
| DEC-4 | **IBM Informix .NET provider or ODBC, via the Informix Client SDK.** | Richest access to Informix-specific features and error detail. *Confirmed: CSDK is already installed on the target workstations, so this carries no deployment dependency.* |
| DEC-5 | **Informix 12.10 and 14.10 supported.** | The two versions actually in the estate. Detect capabilities rather than branching on version number, so a third version later costs little. |
| DEC-6 | **Local and LDAP/PAM-backed authentication, selectable per connection.** | The estate uses both. |
| DEC-7 | **Designed for under 10 instances.** | A grouped, searchable flat list suffices. No folder hierarchy, no tagging, no inventory sync. |
| DEC-8 | **No tamper-proof audit trail.** Local, user-visible query history only. | Follows directly from DEC-2 — IMS takes no privileged action of its own, so there is nothing IMS-specific to audit. Informix's own logging remains the record. Revisit the moment any §8 admin capability lands. |
| DEC-9 | **Credentials stored in Windows Credential Manager.** | *Confirmed acceptable.* Never in a plain-text or user-readable config file. |
| DEC-10 | **Internal use now; keep the option of a product or open-source release open.** | Costs almost nothing to preserve. The one real constraint it imposes: do not assume redistribution rights for IBM client libraries — IMS requires a separately installed CSDK rather than bundling it. |
| DEC-11 | **Solo build, on sanctioned work time. No fixed delivery date.** | Scope is the fixed variable; schedule is not. This is why DEC-12 exists. |
| DEC-12 | **Ship in vertical slices, each independently usable.** | A part-time solo build is at real risk of producing something 80% finished and unusable. Each slice must be worth using on its own, so that stalling at any point still leaves a working tool. |

---

## 4. Users

Three real people-shaped roles, not market segments.

**U1 — The developer (primary; the author and most colleagues).** Writes application SQL and SPL against Informix. Comfortable in SSMS or a JetBrains IDE. Wants to find a table, read its definition, write a query, see results, and iterate without leaving one window or remembering what `systables` looks like. Needs to know when their own session is blocked and by what.

**U2 — The generalist DBA colleague (secondary).** Runs SQL Server or PostgreSQL, has inherited some Informix. Uses IMS to explore an unfamiliar schema and see session activity without learning `onstat` first. Their deeper needs are all in §8.

**U3 — The Informix veteran (secondary — and the one who will judge it hardest).** Faster in the CLI than in any GUI, and will not be won over by a tool that pretends to replace `onstat`. Earns their occasional use by being genuinely faster for schema browsing and multi-statement SQL, by always exposing the raw output behind any structured view ([PR-8.2](#7-design-principles)), and by never sending a statement they did not type.

---

## 5. Scope and delivery

v1 is [§6](#6-requirements) in full. It is built and released as four slices, each usable on its own (DEC-12). Acceptance criteria below are the bar for calling a slice finished — not aspirations.

### Slice 1 — Connect and query

Connection management, SQL editor, execution and cancellation, result grid, query history. No object tree yet; you type SQL.

**Done when:** you can register a connection, open an editor, run a multi-statement script, cancel a long-running one without killing the app, see results in a sortable grid, export them to CSV, and find yesterday's statement in history — against both a 12.10 and a 14.10 server. Unsaved editor content survives killing the process.

> This slice alone should be enough to stop using `dbaccess` for ad-hoc SQL. If it isn't, something in [§7](#7-design-principles) is being violated — most likely PR-8.5.

### Slice 2 — Browse

Object tree, object detail, DDL scripting, tree-to-editor shortcuts.

**Done when:** you can expand a database with thousands of objects without the UI stalling, find a table by partial name, read its columns, indexes, constraints, storage placement and fragmentation strategy without writing a query, and script its DDL into an editor tab at `dbschema` fidelity.

### Slice 3 — Observe

Session monitor.

**Done when:** you can see who is connected, what each session is running, and — given two sessions where one blocks the other — identify the blocker from the UI alone. Refresh is manual by default.

> **Gated on [Q-1](#10-open-questions).** If ordinary developers cannot read `sysmaster`, this slice serves only U2 and U3, and its priority should be reconsidered against §8.

### Slice 4 — Refine

The Should-priority items across §6: SQL formatting, whole-database schema scripting, dependency display, `INSERT` generation, multiple concurrent result sets, single-row view, `sqlhosts` import, encrypted connections.

**Done when:** whichever of these have earned their place through actual daily use are in. This slice has no fixed contents — it is explicitly the place where scope may shrink.

---

## 6. Requirements

**M** = Must (v1 is not v1 without it) · **S** = Should (include if it does not delay the slice) · **C** = Could (only if effectively free) · **W** = Won't in v1

### 6.1 Connection management — Slice 1

| ID | Requirement | Pri |
|---|---|---|
| PR-1.1 | Connect by server name, host, port and protocol, consistent with `sqlhosts` semantics | M |
| PR-1.2 | Saved instance list, groupable by environment (Production / UAT / Development) and searchable by name | M |
| PR-1.3 | Local and LDAP/PAM authentication, selected per connection (DEC-6) | M |
| PR-1.4 | Credentials held only in Windows Credential Manager (DEC-9) | M |
| PR-1.5 | Indicate a connection's environment persistently and unmistakably, so a production connection cannot be mistaken for a non-production one at a glance | M |
| PR-1.6 | Several concurrent connections in one workspace, with unambiguous indication of which instance each editor and pane targets | M |
| PR-1.7 | Detect a dropped connection, say so clearly, and reconnect without losing editor content | M |
| PR-1.8 | Report a missing or misconfigured Client SDK clearly at startup, not as a connection failure | M |
| PR-1.9 | Import an existing `sqlhosts` file to populate the instance list | S |
| PR-1.10 | Encrypted connections where the server is configured for them | S |
| PR-1.11 | Connect through an SSH tunnel or bastion | C |

### 6.2 Object browser — Slice 2

| ID | Requirement | Pri |
|---|---|---|
| PR-2.1 | Navigable tree: databases, and within each — tables, views, synonyms, indexes, constraints, triggers, procedures, functions, sequences, user-defined types | M |
| PR-2.2 | Load children and detail strictly on demand, so expanding a large database never stalls the UI | M |
| PR-2.3 | Filter and search by object name, type and owner | M |
| PR-2.4 | For a table, show: columns with types and nullability, indexes, constraints, triggers, owner, estimated row count, dbspace placement, lock mode, extent sizing, fragmentation strategy | M |
| PR-2.5 | Indicate whether an object's statistics are current or stale | M |
| PR-2.6 | Script an object's DDL into a new editor tab, at `dbschema` fidelity | M |
| PR-2.7 | Refresh a subtree without collapsing and rebuilding the whole tree | M |
| PR-2.8 | Tree shortcuts: `SELECT` first *n* rows, script object, copy qualified name | S |
| PR-2.9 | Show an object's dependencies and dependents | S |
| PR-2.10 | Generate a complete, re-runnable schema script for a whole database | S |

### 6.3 SQL editor — Slice 1

| ID | Requirement | Pri |
|---|---|---|
| PR-3.1 | Tabbed editor with syntax highlighting for Informix SQL and SPL | M |
| PR-3.2 | Context-aware completion for schema objects, columns, and Informix built-in functions and syntax | M |
| PR-3.3 | Execute the whole script, or only the selected text | M |
| PR-3.4 | Execute a multi-statement script, presenting each result or error in sequence and indicating clearly which statement failed | M |
| PR-3.5 | Cancel a running statement without terminating the session or the application | M |
| PR-3.6 | Report errors with the Informix error code, the ISAM error where present, and a plain-language explanation | M |
| PR-3.7 | Show transaction state at all times; require explicit commit or rollback when autocommit is off | M |
| PR-3.8 | Warn before executing an `UPDATE` or `DELETE` with no `WHERE` clause | M |
| PR-3.9 | Preserve unsaved editor content across an unexpected termination | M |
| PR-3.10 | Full keyboard operation of execute, cancel, new tab, switch tab | M |
| PR-3.11 | Open, save and re-open `.sql` files | M |
| PR-3.12 | Local searchable history of executed statements, with target instance, timing, row count and outcome (DEC-8) | M |
| PR-3.13 | Format SQL to a consistent style | S |
| PR-3.14 | Named snippets for frequently used statements | C |

### 6.4 Result grid — Slice 1

| ID | Requirement | Pri |
|---|---|---|
| PR-4.1 | Per-column sort, in-grid filter, and copy of cell, row or selection | M |
| PR-4.2 | Stream and page large result sets rather than materialising them, so an unbounded `SELECT` degrades gracefully instead of exhausting memory | M |
| PR-4.3 | Report row count and elapsed time for every statement | M |
| PR-4.4 | Display `NULL` distinguishably from empty string and from zero | M |
| PR-4.5 | Render Informix types correctly: `DATETIME` with its qualifier, `INTERVAL`, `DECIMAL`, `MONEY`, `BOOLEAN`, and `BYTE`/`TEXT` and smart large objects — the last as a viewable value, not raw bytes in a cell | M |
| PR-4.6 | Export the result set or a selection to CSV, delimited text, JSON and Excel | M |
| PR-4.7 | Keep results from several statements or tabs concurrently, without discarding earlier ones | S |
| PR-4.8 | Generate `INSERT` statements from a result set | S |
| PR-4.9 | Vertical single-row view for wide tables | S |

### 6.5 Session monitor — Slice 3

| ID | Requirement | Pri |
|---|---|---|
| PR-5.1 | List sessions with id, user, originating host and application, connection time, state, and current or most recent SQL | M |
| PR-5.2 | For a selected session: locks held and awaited, resource consumption, temporary space usage | M |
| PR-5.3 | Identify blocked sessions and the session blocking them | M |
| PR-5.4 | Sort and filter the session list; highlight the user's own sessions | M |
| PR-5.5 | Refresh only on explicit action or at a user-chosen interval, defaulting to manual — and never query a server while the view is closed | M |
| PR-5.6 | Instance indicators needing no privileged access: version, mode, uptime, session count, buffer efficiency, checkpoint recency | S |
| PR-5.7 | Present lock waits as a dependency chain where more than two sessions are involved | S |
| PR-5.8 | Terminate a session — *excluded by DEC-2; see [§8](#8-deferred-scope)* | W |

### 6.6 Safety

| ID | Requirement | Pri |
|---|---|---|
| PR-6.1 | Grant no capability the user does not already hold through their Informix privileges. No privilege escalation, no shared or service credential | M |
| PR-6.2 | Send no statement to any server that the user did not type or explicitly request through a documented action | M |
| PR-6.3 | Never write credentials, tokens or result-set data into application logs | M |
| PR-6.4 | Keep metadata and session queries light enough to be negligible on a production instance, with user-controlled frequency (PR-5.5) | M |
| PR-6.5 | Emit no telemetry | M |

### 6.7 Non-functional

| ID | Category | Requirement |
|---|---|---|
| NFR-1 | Responsiveness | Interactive actions acknowledge input within 200 ms. The UI never blocks on server or network work. See PR-8.5 — this is a functional requirement, not polish |
| NFR-2 | Scale | Usable against a database of 20,000+ objects and a result set of 1,000,000+ rows |
| NFR-3 | Stability | No unhandled termination across a full working day of real use; unsaved editor content survives one if it happens (PR-3.9) |
| NFR-4 | Compatibility | Informix 12.10 and 14.10 (DEC-5). Where a capability is unavailable, degrade gracefully with a clear explanation rather than failing opaquely |
| NFR-5 | Platform | Windows 10 and 11, 64-bit. Don't gratuitously preclude a later cross-platform client (DEC-10) |
| NFR-6 | Prerequisites | Informix Client SDK required, not bundled (DEC-4, DEC-10) |
| NFR-7 | Deployment | Installable and updatable without per-user local administrator rights |
| NFR-8 | Accessibility | Full keyboard operability, screen-reader labelling, and no reliance on colour alone for state — including the environment indicator in PR-1.5 |
| NFR-9 | Internationalisation | Correct handling of Informix locales, code sets and collations. Localisable; shipped in English |
| NFR-10 | Diagnostics | Log application errors locally in a form useful for debugging, subject to PR-6.3 |
| NFR-11 | Documentation | A short getting-started note, and in-context explanation of the Informix concepts surfaced in PR-2.4 — U2 will not know what a dbspace is |

---

## 7. Design principles

Not requirements — the standards to judge the result against.

| ID | Principle |
|---|---|
| PR-8.1 | **Familiar before novel.** Where SSMS has an established convention for something IMS does, follow it. The users' existing muscle memory is an asset, not a constraint. |
| PR-8.2 | **Never hide the server.** Any structured view must offer the underlying raw output — the `onstat` text, the catalogue query, the generated DDL — on demand. This is what earns U3's trust. |
| PR-8.3 | **Teach the CLI, don't replace it.** Where a view corresponds to a command, name the command. Users should leave IMS more capable at the command line, not less. |
| PR-8.4 | **Do less, completely.** A half-implemented capability is worse than an absent one, because it invites reliance it can't bear. Anything not fully working belongs in §8. |
| PR-8.5 | **Fast, or it won't get used.** The tool competes with a terminal. Perceived slowness is a defect. |

---

## 8. Deferred scope

Excluded by DEC-2 or by scope discipline. Kept as the backlog. Which Tier 1 item comes first is deliberately undecided — see [Q-3](#10-open-questions).

### Tier 1 — the plausible next thing

| Item | Why not in v1 | Note |
|---|---|---|
| Graphical execution plan viewer | Not a v1 must-have | The largest gap versus SSMS and the strongest differentiator. Turns `SET EXPLAIN` text into something readable |
| Storage and log health — dbspaces, chunks, free space, logical log usage and backup status | Read-only, but a DBA need rather than a developer one | Compatible with DEC-2. Addresses U2's most acute pain |
| Session termination (PR-5.8) | First administrative write | Breaks DEC-2 and pulls in a confirmation framework and audit (DEC-8). Small to build, disproportionate in consequence |
| Backup status and RPO warning | Read-only, DBA concern | Compatible with DEC-2 |
| Server message log viewer | Needs host file access, unreachable over SQL | Depends on [Q-2](#10-open-questions) |
| Tamper-proof audit trail | DEC-8 | Becomes mandatory the moment any administrative write is introduced |

### Tier 2 — later

Actual-versus-estimated plan comparison; workload analysis and top-resource statements; deadlock graph beyond PR-5.7; HDR/RSS/SDS cluster topology and lag; Enterprise Replication visibility; effective-privilege resolution; guided schema management dialogs; `UPDATE STATISTICS` management; schema comparison and sync scripting; in-grid data editing; bulk import and `dbexport`/`dbimport`; `onconfig` and `sqlhosts` management; scheduler integration; shared script library.

### Tier 3 — speculative

SPL debugging with breakpoints; performance baselining and historical comparison; index and statistics advice; backup and restore orchestration; role transition and mode control; cross-instance script execution; single sign-on; cross-platform client; TimeSeries, spatial, Warehouse Accelerator and JSON wire-listener support.

---

## 9. Assumptions, dependencies, risks

### 9.1 Assumptions

| ID | Assumption | If wrong |
|---|---|---|
| AS-1 | Instances are reachable directly from developer workstations | Needs a gateway or agent; changes the architecture |
| AS-2 | Users already hold Informix credentials appropriate to their work | Privilege provisioning becomes a dependency |
| AS-3 | `sysmaster` is readable by the intended users, or a suitable role can be granted | Slice 3 serves only U2/U3 — see Q-1 |
| AS-4 | Windows 10/11 throughout | Cross-platform moves from Tier 3 into v1 |

*Closed since v0.2:* CSDK availability (confirmed installed), Credential Manager acceptability (confirmed), version matrix (12.10 and 14.10), development instance availability (work instances).

### 9.2 Dependencies

| ID | Dependency |
|---|---|
| DEP-1 | IBM Informix Client SDK and .NET/ODBC provider — installed, but note the DEC-10 non-redistribution constraint |
| DEP-2 | A non-production instance at each of 12.10 and 14.10 for development and testing |
| DEP-3 | A schema of realistic size to test NFR-2 against |
| DEP-4 | `sysmaster` read access for Slice 3 (AS-3) |

### 9.3 Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| RSK-1 | Part-time solo build stalls partway, leaving nothing usable | High | High | DEC-12 vertical slices — each is worth using alone. Slice 1 is the one that matters most; treat it as the real deliverable |
| RSK-2 | Scope creeps from §8 into v1 and v1 never ships | High | High | §8 is the pressure valve. Anything new goes there first and stays there |
| RSK-3 | Not actually better than `dbaccess` or DBeaver, so it doesn't get used — including by you | Medium | High | NFR-1 and PR-8.5 are functional requirements. Slice 1's acceptance bar is explicitly "stop reaching for `dbaccess`" |
| RSK-4 | Once colleagues rely on it, you are a one-person support desk, and a bus factor of one | Medium | Medium | Keep DEC-2 (nothing IMS does is load-bearing for operations — the CLI still works). Be explicit with colleagues that it is unsupported |
| RSK-5 | Developing against work instances causes an unintended impact | Low | High | PR-6.2 and PR-6.4. Do exploratory work against a non-production instance (DEP-2), and never against production while iterating on metadata queries |
| RSK-6 | An unbounded `SELECT` hangs the client | Medium | Medium | PR-4.2 streaming and paging, PR-3.5 cancellation. Both are Slice 1 |
| RSK-7 | A user runs a destructive statement unintentionally | Medium | Medium | PR-3.8 and PR-1.5. Note this risk is no worse than `dbaccess` today, and Informix privileges remain the real control (DEC-2) |
| RSK-8 | U3 dismisses it and that shapes colleagues' view | Medium | Low | PR-8.2 and PR-8.3. Make no claim that v1 replaces the CLI, because it doesn't |
| RSK-9 | Version differences between 12.10 and 14.10 cost more than expected | Low | Medium | Capability detection, not version branching (NFR-4). Test both from Slice 1 onward, not at the end |

---

## 10. Open questions

Six of the original nine are closed and recorded in [§3](#3-decisions) and §9.1. These remain:

| ID | Question | Needed by |
|---|---|---|
| Q-1 | Can ordinary developers read `sysmaster`, or is it DBA-only? If restricted, can a role be granted? **Test this before starting Slice 3** — it determines whether that slice serves the primary user at all, and it is cheap to answer: connect as an unprivileged user and run the session queries. | Before Slice 3 |
| Q-2 | Must IMS stay strictly agentless, or is an optional local agent acceptable later? This bounds several Tier 1 items — notably the message log viewer and anything touching physical chunk state. | Before Tier 1 |
| Q-3 | Which §8 Tier 1 item comes first? Deliberately deferred until v1 has had real use — the honest answer is whichever you find yourself missing most. | After Slice 3 |

---

## 11. Glossary

| Term | Definition |
|---|---|
| Chunk | The unit of physical storage in a dbspace; a file or raw device |
| CSDK | Informix Client Software Development Kit — the client libraries and drivers |
| `dbaccess` | Informix's bundled interactive SQL and schema utility |
| Dbspace | A logical storage container made of one or more chunks |
| `dbschema` | Utility that generates DDL for database objects |
| Extent | A contiguous disk allocation within a dbspace, assigned to a table or index |
| Fragmentation | Informix's partitioning mechanism, distributing a table or index across dbspaces |
| HDR / RSS / SDS | Informix's high-availability secondary types: replicated pair, remote standalone, shared disk |
| Logical log | The transaction log used for recovery and replication |
| `onstat` | The primary read-only diagnostic utility, exposing shared-memory structures |
| RPO | Recovery point objective |
| Sbspace | Storage space for smart large objects |
| `SET EXPLAIN` | Statement causing the optimiser to write its query plan to a file |
| SPL | Stored Procedure Language — Informix's procedural SQL extension |
| `sqlhosts` | Connectivity configuration mapping server names to network endpoints |
| SSMS | SQL Server Management Studio — the reference product for this concept |
| `sysmaster` | System database exposing instance and session state through SQL |

---

## 12. Appendix — SSMS parity map

Where v1 lands against what users will expect from SSMS, and where the rest is held.

| SSMS capability | Informix today | IMS v1 | Reference |
|---|---|---|---|
| Query Editor with IntelliSense | `dbaccess` | ✅ Slice 1 | §6.3 |
| Results grid with export | `dbaccess` output | ✅ Slice 1 | §6.4 |
| Registered Servers | `sqlhosts` file | ✅ Slice 1 | §6.1 |
| Object Explorer | `dbschema`, catalogue queries | ✅ Slice 2 | §6.2 |
| Generate Scripts | `dbschema` | ✅ Slice 2 | PR-2.6, PR-2.10 |
| Activity Monitor | `onstat -g ses` / `-g sql` / `-g act` | ◐ Slice 3, read-only — no kill | §6.5, PR-5.8 |
| Import / Export wizard | `dbexport`, `dbimport`, load/unload | ◐ Export only | PR-4.6 |
| Graphical execution plan | `SET EXPLAIN` text file | ⛔ Tier 1 | §8 |
| Data files / filegroups | Dbspaces, chunks, `onspaces` | ⛔ Tier 1 | §8 |
| Error log viewer | Server message log file | ⛔ Tier 1 — needs host access | §8, Q-2 |
| Backup / Restore | `onbar`, `ontape` | ⛔ Tier 1 / 3 | §8 |
| Deadlock graph | `onstat -g lok`, `-K` | ◐ Partial (PR-5.7); full in Tier 2 | §8 |
| Security folder | `GRANT` / `REVOKE`, catalogue queries | ⛔ Tier 2 | §8 |
| Table designer | Hand-written DDL | ⛔ Tier 2 — DDL by hand in v1 | DEC-2 |
| Edit Top 200 Rows | None | ⛔ Tier 2 | §8 |
| Maintenance Plans / Agent jobs | Informix scheduler, cron | ⛔ Tier 2 | §8 |
| Server configuration properties | `onconfig`, `onmode` | ⛔ Tier 2 | §8 |
| Always On dashboard | `onstat -g dri` / `-g rss` / `-g sds` | ⛔ Tier 2 | §8 |
| Schema compare | None | ⛔ Tier 2 | §8 |
| Query Store | None | ⛔ Tier 3 | §8 |
| T-SQL debugger | None | ⛔ Tier 3 | §8 |
| Tuning Advisor | None | ⛔ Tier 3 | §8 |

**Capabilities with no Informix counterpart at all** — IMS's strongest long-term differentiators: graphical plan visualisation, deadlock graphs, workload history, schema comparison, in-grid data editing. None are in v1; the first is the leading Tier 1 candidate.

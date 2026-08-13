# Changelog

Every release of IMS so far is a pilot build: `vX.Y.Z-pilot`, handed to a small
number of people who have been told what follows. The minor goes up when a build
adds capability, the patch when it repairs one.

The annotated git tag is the primary record — `git show v0.2.0-pilot` — and each
tag repeats the standing caveats in full so that a build can be handed over with
nothing else attached. This file is the shorter view across all of them.

Standing caveats apply to every version below unless a release says otherwise:
cancel does not stop the statement, 14.10 only, scale unmeasured, encrypted
connections unverified, PR-6.2 unaudited. They are spelled out under
[Known limits](#known-limits).

## v0.3.0-pilot — 2026-08-13

A session monitor. The minor goes up because this adds capability — but read
[what it cannot do on a busy instance](#the-session-monitor-on-a-busy-instance)
before promising it to anyone, because on the estate it was built against the
part people will want most does not work.

### Added

- **A session monitor**, one tab per instance, on the Sessions menu, the toolbar
  and <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>S</kbd>. It lists who is connected with
  their session id, user, originating host, application, state and connection
  time; sorts on any column; filters on any of them at once; and marks your own
  sessions with the word `YOU` rather than a colour. Sessions the server owns can
  be hidden.
- **Blocked sessions name their blocker in the row that is waiting**, and where
  more than two sessions are involved the whole chain is drawn — read right to
  left, so the session at the head is the one holding everyone up. The chain
  survives a genuine deadlock: a cycle is reported as a loop rather than
  hanging, because the server may not have detected it when IMS read the locks.
- **Refresh is manual by default**, with an optional interval no shorter than
  five seconds. Nothing is queried while the tab is closed **or while it is
  behind another tab** — a monitor polling a production instance nobody is
  looking at is exactly the load PR-6.4 rules out.
- **Instance indicators**: mode, uptime, session count, read and write cache
  efficiency, and checkpoint recency. Each is read by its own query, so one
  absent object costs one figure and reads `Unknown` rather than blank — which
  would be indistinguishable from zero.
- **Every query is on show, with the `onstat` command that answers the same
  question beside it** — `-g ses`, `-g sql`, `-g lok` — and that includes the
  queries that failed, with the server's reason, and the ones IMS decided not to
  send. Take the block to an editor tab with one button and run it yourself.
- **Elapsed time, while you wait and after.** A count-up beside the reading
  notice, and the duration kept on the timestamp afterwards. A count-up rather
  than a progress bar: IMS does not know how far along the server is, and cannot
  stop it either.
- **In-context explanations** for session, lock, latch, checkpoint, buffer
  efficiency, temp space, and session id versus process id (NFR-11).

### Changed

- **Session detail is read when you ask, not when you select.** Selecting a row
  used to issue three queries, so arrowing down the list issued three per
  keypress — the opposite of what refresh-on-explicit-action means, and the list
  itself already behaved correctly while the detail pane did not. Load it with
  the button or by double-clicking the row.
- **A failed read is remembered for the life of the connection.** A refused
  table or a query that timed out will not answer differently on the next
  refresh, and re-asking was most of why the view took about a minute to appear.

### Fixed

- **A third kind of tab rendered as a greyed-out empty editor.** The window
  chose between exactly two documents, so anything else fell through to the
  editor. Each kind is now tested for on its own, and the results pane belongs
  to the editor rather than being hidden for one exception — so a fourth kind
  will fail safe.
- **A monitor's refresh timer outlived the window.** Shutdown disposed editor
  tabs only, which would have left a closing IMS querying a production instance.

### The session monitor on a busy instance

Measured against 14.10.FC10W2X7 on 2026-08-13, as an ordinary developer account:

- **Blocked-session identification does not work here.** Every read of
  `sysmaster:syslocks` exceeds a ten-second cap on this instance — a self-join
  and a plain single scan alike. `syslocks` is synthesised from shared memory
  across every lock in the server, so on a busy one it costs more than the whole
  budget whatever the query asks for. The resolver, the grading and the chain
  logic are built and unit-tested and would work the moment a source answers in
  time; none of that makes the feature available to you today. `onstat -g lok`
  reads them directly, and the UI says so rather than claiming the server does
  not expose them.
- **The current statement per session is refused** — no `SELECT` permission on
  `sysmaster:syssqlcurses`. That is a privilege, not a bug, and IMS will not
  work around it. Ask a DBA for the grant, or use `onstat -g sql`.
- **Per-session resource counters are switched off.** Two guessed `sysrstcb`
  column names were rejected one per run, so the read waits for real names
  rather than guessing a third time. Run the smoke test with `--probe-sessions`
  to establish them.
- **Per-session temporary space is not reported at all.** Deriving it needs
  partition detail IMS does not read, and a number it cannot justify is worse
  than an admitted gap. Temporary space is explained at the instance level.

So the honest summary: the session **list** works, sorting and filtering work,
and the machinery behind blocking works — but on a busy instance the blocking
answer is `Unknown`, and that is the half a developer opens this for.

### Not built, and why

- **No session termination.** DEC-2 says IMS issues no administrative writes of
  its own. It is small to build and disproportionate in consequence: it pulls in
  a confirmation framework and an audit store, and it is what makes IMS safe to
  point at production.
- **No automated tests over the monitor's window.** The 525 tests cover the
  refresh policy, the chain resolver, the query shapes and the translators —
  none of them touches a control or a binding.

### Still open

- **The acceptance check has not been run:** two sessions, one blocking the
  other, identified from the UI alone. There is nowhere safe to arrange a lock
  wait, because the test database shares a server with production (DEP-2).

## v0.2.0-pilot — 2026-08-10

A query toolbar, and objects in their own tabs. The minor goes up because this
adds capability rather than repairing the last build.

### Added

- **A query toolbar above the tab strip.** Every query action was previously
  reachable only from the menu bar or by knowing its gesture — Execute included,
  the one people reach for first. The toolbar carries the connection target,
  connect and disconnect, new/open/save, Execute, Cancel, run-selection, commit
  and roll back, complete word, clear results, export to CSV, copy, history and
  help. Each button drives a command that already existed, so the menu and the
  toolbar cannot disagree about what an action does.
- **Execute becomes Cancel in place** while a statement runs, in the same spot,
  so the strip does not shuffle under the cursor at the moment you want to click
  it. Anything needing a connection stays greyed out until there is one.
- **Objects open in the tab strip**, one tab per object, beside the query tabs
  rather than in the pane below. An object's detail is a document, not output of
  the statement in the editor, and it was competing for the results pane with
  the rows the user had just asked for. A detail tab gives the results row back
  while it is showing, and reopening an object returns to its tab rather than
  opening a second one.
- **Connect and disconnect from a tree node's right-click menu.**
- **Clear results**, on the toolbar and the Results menu.
- **A public landing page**, linked from the README.

### Fixed

- **Execute did nothing, quietly, on a tab with no connection.** The view model
  had computed whether it could run since the beginning and nothing was bound to
  the answer. The toolbar and the Query menu both bind it now, so the action is
  visibly unavailable rather than silently inert.
- **Right-click now selects the node it lands on** rather than acting on
  whatever was selected before — which was a way to disconnect the wrong
  instance.
- **The bottom pane shows Messages rather than Results** when a run produced no
  rows, so a failure is not left behind a tab the user has to know to click.
- **Clearing the results was unreachable.** Written for the streaming work and
  never wired to anything a user could press. Results accumulate until the next
  run replaces them, so on a long session this returns server cursors as well as
  screen space.
- **The help file listed Ctrl+W for closing a tab.** There is no such binding
  and there never was. The shortcut tables now match the gestures actually
  registered, and Ctrl+Enter, Ctrl+O, Ctrl+S and Ctrl+Space — all of which
  worked and none of which were written down — are in them.

### Not built, and why

- **No database switcher.** SSMS puts one in this toolbar. A connection's
  database is fixed when it is opened, so switching would mean reconnecting
  underneath you and dropping any open transaction without saying so. Open a
  second connection instead.
- **No Parse button and no execution plan.** There is no SQL parser in IMS, and
  PR-6.2 says IMS sends no statement the user did not type.

## v0.1.2-pilot — 2026-08-10

The tag for this one records only its subject line; the rest is reconstructed
from the commits.

### Added

- **User help**, opened from Help → Contents and <kbd>F1</kbd>.
- **Connect and disconnect from a right-click menu** rather than buttons.

### Changed

- **Relicensed under Apache-2.0**, and internal references removed.

### Fixed

- The window failed to load when opened from the connection context menu.

## v0.1.1-pilot — 2026-08-07

The build to actually hand to a pilot user. Supersedes v0.1.0-pilot, which was
tagged before the first real use with several tabs open and did not survive it.
Nothing was distributed from that tag.

### Fixed

- **Autosave was breeding tabs.** The key was derived from the tab title, and
  titles change — recovery itself appended " (recovered)" — so every launch
  orphaned a file and reopened it as an extra tab. Three real tabs had become
  twenty-one files. Tabs now carry a stable id, a reopened tab adopts the id it
  was saved under, and empty tabs are no longer written at all, which is why a
  session where nothing was typed used to "recover" work.
- **The tab strip clipped, then clipped again.** WPF wraps headers onto rows a
  fixed-height strip cannot show; replacing that with a scroller then let the
  scrollbar itself cover the headers. It scrolls by wheel and by selection now,
  with the bar hidden.

### Added

- **An application icon**, cropped to its artwork so it is not dwarfed in the
  taskbar.
- **About shows the version and the commit**, selectable and copyable, so a bug
  report can name the exact build. It also says when a build came from a tree
  with uncommitted changes, because then the commit describes nothing.

## v0.1.0-pilot — 2026-08-07

The first build meant to be run by someone other than its author. Connect to an
Informix instance, write and run SQL, read the results, browse the catalogue,
script DDL. Slices 1 and 2, for a small internal pilot.

Not distributed — superseded by v0.1.1-pilot before it reached anyone.

## Known limits

True of every build above. Read these before handing IMS to anyone.

- **Cancel does not stop the statement.** Measured against 14.10: the ODBC
  driver's cancel never reaches the server, on either a sorting or a scanning
  workload, and `SQL_ATTR_ASYNC_ENABLE` is refused outright (`HYC00 -11097`).
  <kbd>Alt</kbd>+<kbd>Break</kbd> frees the editor and IMS says plainly that the
  statement is still running, but it keeps consuming server CPU until it
  finishes. Stopping it needs `onstat -g ses` then `onmode -z <sid>`. On an
  instance shared with production this is the sharpest edge in the build.
- **14.10 only.** 12.10 was descoped on 2026-08-06 (DEC-5). Untested rather than
  refused — capability detection means it may work — but nothing about it has
  been verified.
- **Scale is unmeasured.** NFR-2 asks for 20,000+ objects and 1,000,000+ rows;
  DEP-3 was never met, so neither has been tried. The driver streams, measured
  at 20,000 rows, and that is the whole of what is known.
- **Encrypted connections are unverified** (PR-1.10). Do not claim them.
- **PR-6.2 has not been audited.** IMS issues background catalogue and
  completion queries the user did not type. Each is probably defensible; none
  has been reviewed against the requirement.
- **No automated tests over the window.** The test suite covers the core and the
  provider; nothing exercises a control, a binding or a command. The
  Execute-that-did-nothing fixed in v0.2.0 is exactly the fault that gap admits,
  and it survived three tags.
- **Lock waits are unreadable on a busy instance.** Every read of
  `sysmaster:syslocks` exceeded a ten-second cap against 14.10 on 2026-08-13, so
  blocked-session identification reports `Unknown` there. Raising the cap is not
  the answer: since cancel does not reach the server, a longer cap means a longer
  unstoppable statement holding the connection the object tree shares. Use
  `onstat -g lok`.
- **Missing:** the in-grid filter (PR-4.1), user-defined types in the object
  tree (PR-2.1), and — within the session monitor — the current statement per
  session (no `SELECT` on `syssqlcurses` for an ordinary account), per-session
  resource counters (`sysrstcb` column names unverified) and per-session
  temporary space (not derivable from what IMS reads).

Unsupported, provided as-is, and not a replacement for the Informix CLI.
Requires the IBM Informix Client SDK, installed separately (DEC-10, NFR-6). The
published folder carries its own .NET runtime, so it needs no administrator
rights.

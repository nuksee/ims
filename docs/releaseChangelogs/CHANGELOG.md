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
- **Missing:** the in-grid filter (PR-4.1), user-defined types in the object
  tree (PR-2.1), and the whole of Slice 3 — no session monitoring.

Unsupported, provided as-is, and not a replacement for the Informix CLI.
Requires the IBM Informix Client SDK, installed separately (DEC-10, NFR-6). The
published folder carries its own .NET runtime, so it needs no administrator
rights.

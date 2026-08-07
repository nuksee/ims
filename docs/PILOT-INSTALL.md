# IMS pilot — installing and what to expect

Informix Management Studio, pilot build `v0.1.0-pilot`. One window in which to connect to an
Informix server, write and run SQL, read results, and browse the schema.

This is a pilot, not a release. It is worth your time, and there are three things you need to
know before you point it at anything. They are at the top rather than the bottom on purpose.

---

## Read this first

### 1. Cancel does not stop your query

Press **Alt+Break** and IMS stops waiting — you get the editor back and the session stays
usable. **The statement keeps running on the server.**

This is a limitation of the Informix ODBC driver, not a bug we can fix in IMS; the cancel never
reaches the server. IMS tells you so in a banner rather than pretending, but the work continues
until it finishes on its own.

If you start something expensive by accident, stop it properly:

```
onstat -g ses                 # find your session id
onmode -z <sid>               # end that session
```

**This matters most because the test database shares a server with production.** A runaway
cross join costs everyone. If you are about to run something big, know how you would stop it
first.

### 2. Informix 14.10 only

12.10 is untested. IMS will probably connect, and nothing about that path has been verified, so
please do not use the pilot to reach one.

### 3. Nothing here has been tested at scale

The largest result set anyone has run through IMS is 20,000 rows. A million-row `SELECT`, or a
schema with tens of thousands of objects, is genuinely unknown territory. If you try it, that is
useful — just expect it to be the interesting kind of useful.

---

## What you need

| | |
|---|---|
| **Windows 10 or 11** | 64-bit |
| **IBM Informix Client SDK** | With the 64-bit ODBC driver registered. Developed against 4.10.FC1DE |

You do **not** need the .NET runtime — this build carries its own. You do **not** need local
administrator rights to run it.

The CSDK is not included, because IMS cannot redistribute IBM's client libraries. It is already
on most workstations here. If it is missing or only the 32-bit driver is registered, IMS says so
at startup and explains what to fix, rather than failing later as a puzzling connection error.

## Installing

1. Unzip anywhere you can write — `C:\Tools\IMS`, or your Desktop. No installer, no admin
   prompt, nothing written to `Program Files` or the registry.
2. Run **`Ims.exe`**.

That is the whole installation. To update, replace the folder. To uninstall, delete it — see
[What it leaves on your machine](#what-it-leaves-on-your-machine) for the two directories it
creates outside its own folder.

If Windows SmartScreen warns about an unrecognised publisher: the build is unsigned, which is
expected for a pilot. **Do not click through that warning on my say-so alone** — check with me
first that the copy you have is the one I sent.

## First run

1. **Add a connection** — Connection ▸ New connection… You need the server name, host, port and your
   Informix credentials, the same ones `dbaccess` uses. Mark the environment honestly:
   Production, UAT or Development is shown on every tab, and it is there to stop you running
   the right query against the wrong server.
2. Your password goes into **Windows Credential Manager**, never into a file.
3. **Connect**, then start typing. `Ctrl+Space` completes table and column names.
4. **F5** runs the script; **Ctrl+Enter** runs just what you have selected.

Worth knowing:

- Unsaved editor content survives a crash or a forced restart — it comes back on next launch.
- IMS warns before an `UPDATE` or `DELETE` with no `WHERE`.
- IMS can do nothing your own Informix privileges do not already allow. It sends no
  administrative commands of its own.

## What is missing

Not bugs — not built yet:

- **Session monitoring.** No way to see who is connected or what is blocking you. That is the
  next slice.
- **Filtering inside the result grid.** Sorting works; filtering does not.
- **User-defined types** are absent from the object tree. Everything else lists.
- **Encrypted connections** are unverified. Please assume they do not work.

## What it leaves on your machine

Two directories, and deleting the program folder does not remove them:

| Path | What |
|---|---|
| `%LOCALAPPDATA%\IMS\logs` | Log files. Credentials and query results are stripped before anything is written |
| `%LOCALAPPDATA%\IMS\autosave` | Unsaved editor content, so it survives a crash |
| `%APPDATA%\IMS\connections.json` | Your saved connections — no passwords; those are in Credential Manager |
| `%APPDATA%\IMS\history.jsonl` | Your query history |

Your passwords live in **Windows Credential Manager**, under entries starting
`IMS:Informix:`. Removing IMS does not remove them; delete them there if you want them gone.

Nothing is sent anywhere. There is no telemetry — that is enforced by a build check, not by
good intentions.

## Telling me about problems

Please include:

- What you were doing, and the SQL if you can share it
- The Informix server and database
- Anything in `%LOCALAPPDATA%\IMS\logs` from around the time

The single most useful thing you can report is **anything that made you go back to `dbaccess`**.
That is the bar this is being measured against, and it is the thing I cannot see from here.

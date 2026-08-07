# Informix Management Studio (IMS)

A Windows desktop application for working with IBM Informix — the tool SQL Server Management
Studio is for SQL Server. One window in which to connect to a server, browse its objects,
write and run SQL, and see what the server is doing.

Informix has no equivalent today: SQL goes through `dbaccess`, object definitions through
`dbschema`, diagnostics through the `onstat` family, and generic JDBC clients treat Informix as
a lowest-common-denominator target. IMS v1 covers four capabilities — connection management,
object browser, SQL editor with a usable result grid, and a read-only session monitor.

**IMS performs no administrative changes of its own.** Every statement it sends is one the user
typed or explicitly requested. Gating is done by Informix privileges, not by IMS. That single
constraint is what makes it safe to point at production and safe to hand to a colleague.

> **Status: early.** Slice 0 (foundation) is largely complete; Slice 1 (connect and query) is in
> progress. The application currently starts, detects the Client SDK, and reports a missing or
> misconfigured one — there is no connection UI yet. See
> [IMPLEMENTATION-TODO.md](docs/IMPLEMENTATION-TODO.md) for exactly what is and isn't built.
>
> Internal, unsupported, and not a replacement for the Informix CLI.

---

## Documentation

| Document | What it is |
|---|---|
| [PRD-Informix-Management-Studio.md](docs/PRD-Informix-Management-Studio.md) | Product requirements: scope, decisions and their rationale, requirement IDs, deferred backlog |
| [IMPLEMENTATION-TODO.md](docs/IMPLEMENTATION-TODO.md) | Task list per slice, each traced back to a PRD requirement ID |

Code comments cite requirement IDs (`PR-3.5`, `DEC-4`, `NFR-1`, …). They all resolve against the
PRD — if a piece of code looks over-constrained, the reason is there.

## Prerequisites

- **Windows 10 or 11, 64-bit**
- **.NET SDK 9.0.311** or a later 9.0 feature band (pinned in [global.json](global.json))
- **IBM Informix Client SDK**, with the `IBM INFORMIX ODBC DRIVER (64-bit)` registered.
  Developed against CSDK 4.10.FC1DE.

The CSDK is **required but not bundled** — IMS assumes no redistribution rights for IBM client
libraries, so it must be installed separately. A build and the full test suite run fine without
it; only actually talking to a server needs it.

## Build and test

```powershell
dotnet restore ims.sln
dotnet build ims.sln --configuration Release
dotnet test  ims.sln --configuration Release
```

Warnings are errors across every project, so a clean build is also the code-quality gate. No test
connects to an Informix instance.

## Running

```powershell
dotnet run --project src/Ims.App
```

If the Client SDK is missing or misconfigured, IMS shows a prerequisite window explaining what is
wrong and how to fix it — rather than failing later as an unexplained connection error. Logs are
written to `%LOCALAPPDATA%\IMS\logs`, with credentials and result data redacted at the logging
provider boundary.

## The smoke test

`tools/Ims.SmokeTest` is the Slice 0 provider spike. It answers, against a real instance, the
questions that cannot be settled on a developer workstation: whether a running statement can be
cancelled without losing the session, whether the ODBC driver streams or buffers a result set,
whether `DATETIME` arrives with enough information to recover its qualifier, whether SQLCODE and
the ISAM error are both retrievable, and whether an ordinary developer can read `sysmaster`.

```powershell
dotnet run --project tools/Ims.SmokeTest -- --help
dotnet run --project tools/Ims.SmokeTest -- --server ol_dev --host devhost --service 9088 --user me
```

It reads only, prints every statement it sends, and prompts for the password rather than taking it
on the command line. **Point it at a non-production instance only.** Probes that put real load on
the server (streaming, cancellation) are off unless you pass `--include-load`.

It has not yet been run against a live server, which is why several Slice 0 items are marked
delivered-but-unverified.

## Layout

```
src/
  Ims.App              WPF shell and composition root          (net9.0-windows)
  Ims.Core             Domain, abstractions, diagnostics       (net9.0, no Windows dependency)
  Ims.Data.Informix    ODBC provider layer over the CSDK       (net9.0-windows)
tests/
  Ims.Core.Tests
  Ims.Data.Informix.Tests
tools/
  Ims.SmokeTest        Slice 0 provider spike — needs a live server
docs/                  PRD and implementation to-do
```

`Ims.Core` deliberately stays free of any Windows dependency so a later cross-platform client is
not precluded. `Ims.App` supplies the UI-thread detector that `ServerCallGuard` uses to throw
when a server round trip is attempted on the dispatcher thread — the UI never blocks on network
work.

`DependencyPolicyTests` fails the build if a telemetry package or a redistributed IBM client
library enters the dependency graph.

## Technical decisions worth knowing

- **ODBC, not the .NET provider.** The CSDK's bundled `IBM.Data.Informix.dll` ships only for .NET
  Framework 2.0 (`bin\netf20`) and cannot load in .NET 9, so IMS uses `System.Data.Odbc` over the
  registered Informix ODBC driver. This still speaks the native SQLI protocol.
- **Informix 14.10 supported.** Tested against 14.10. 12.10 was descoped for v1 on
  2026-08-06: it is untested, not refused. Because IMS detects capabilities rather than branching on
  version number, a 12.10 server may work and should degrade rather than fail — but nothing about it
  has been verified, so treat it as unsupported.
- **Credentials live in Windows Credential Manager**, never in a config file.
- **No telemetry**, enforced by test rather than by intention.

## Contributing

Solo, part-time build. Work proceeds in vertical slices, each independently usable — if it stalls
partway, what exists should still be worth using. New ideas go into PRD §8 (deferred scope) first
and stay there.

## Licence

Not yet determined. Internal use for now; a product or open-source release is deliberately kept
possible, which is the reason for the no-CSDK-redistribution constraint above.

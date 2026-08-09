# Contributing to IMS

Thanks for looking. IMS is a solo, part-time build, so the honest expectation to set is that
issues get read quickly and pull requests get read slowly.

## Before opening a pull request

Open an issue first. Work proceeds in vertical slices against a written PRD, and a change that
does not fit the current slice is likely to sit unmerged however good it is — not because it is
unwelcome, but because merging it early makes the slice harder to finish. An issue costs you
five minutes and can save you an afternoon.

New feature ideas go into [PRD §8 (deferred scope)](docs/PRD-Informix-Management-Studio.md)
first. That is not a filing cabinet for saying no; it is how the scope of v1 stays small enough
to reach.

## The constraint that is not negotiable

**IMS performs no administrative changes of its own.** Every statement it sends is one the user
typed or explicitly requested, and what a user may do is gated by their Informix privileges
rather than by IMS. A change that has IMS issue a statement on its own initiative will be
declined regardless of its merit — that single constraint is what makes the tool safe to point
at a production server.

Related, and equally firm:

- **No telemetry.** Enforced by `DependencyPolicyTests`, not by good intentions.
- **No redistributed IBM client libraries.** IMS assumes no redistribution rights for the
  Client SDK, which is why it is a prerequisite rather than a bundled dependency.
- **No credentials outside Windows Credential Manager**, and none in logs.

## Building

```powershell
dotnet restore ims.sln
dotnet build ims.sln --configuration Release
dotnet test  ims.sln --configuration Release
```

Warnings are errors across every project, so a clean build is also the code-quality gate. The
full test suite runs without an Informix Client SDK and without a server — no test connects to
an instance, and none should start.

## Style

Match the surrounding code. Two conventions are worth stating because they are load-bearing:

- **Cite requirement IDs in comments** (`PR-3.5`, `DEC-4`, `NFR-1`, …) where a piece of code is
  shaped by a decision rather than by taste. They resolve against the PRD. If code looks
  over-constrained, the reason should be one grep away.
- **Comments explain why, not what.** The existing comments record measurements against a real
  server — what was tried, what happened, and on which date. If you measure something that
  contradicts one, say so in the comment rather than deleting it.

## Test data

Use documentation-range addresses (`192.0.2.0/24`, RFC 5737) and generic server names in tests
and docs. Do not commit real hostnames, addresses, database names, or server version strings.

## Licence

By contributing, you agree that your contributions are licensed under the Apache License 2.0,
as the project is.

# Site screenshots

| File | Used by | Shows |
|---|---|---|
| `Screenshot01.png` | the figure below the hero | Main window — connections, object tree, SQL editor, result grid, status bar |
| `ims-icon.png` | favicon | App icon |

`Screenshot01.png` is safe to publish: the connection is `UAT DB` on `127.0.0.1:9088`
(localhost, not a real hostname), the query is a generic `Select * From Customers`, and the
two identifying spots in the status bar are already redacted.

## Adding more

```powershell
dotnet run --project src/Ims.App
pwsh tools/capture-screenshots.ps1 -Name shot-editor
```

The script sizes the window to 1600×1000 so images crop consistently, and writes straight to
this folder. Guidelines that affect how the page reads:

- **1600 px wide or more.** The page renders images at up to ~1180 CSS px.
- **Light Windows theme.** Sits better on both the light and dark versions of the page.
- **Fill the grid.** A result grid with three rows undersells it; 20+ rows and enough columns
  to need horizontal space is what makes it look like a real tool. `Screenshot01.png` has a
  collapsed tree and an empty grid — a shot showing real work would be a straight upgrade.
- **Crop to the window.** No desktop, no taskbar, no other applications.

## Before committing any new one — not reversible

These images become public and get indexed. Deleting them later does not retract them.
They must not show real customer or production schema, real server names or hostnames,
usernames, or anything identifying a person — and that includes the saved-connection list,
the query tab names, the window title, and the status bar, not just the result grid.

Use a database created for the purpose with obviously fictional names.

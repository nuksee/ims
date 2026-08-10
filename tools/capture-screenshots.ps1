<#
.SYNOPSIS
  Capture a screenshot of the running IMS window for the public website.

.DESCRIPTION
  Finds the Ims process's main window, sizes it to a fixed 1600x1000 so every
  image on the site crops consistently, and writes a PNG to site/img/.

  Captures the window rectangle only — no desktop, no taskbar, no other
  application. It does NOT connect to anything or read any data; it photographs
  whatever is already on screen.

.PARAMETER Name
  Output basename. The site expects 'shot-editor' and 'shot-connect'.

.PARAMETER NoResize
  Capture at the window's current size instead of 1600x1000. Use for dialogs
  that should not be stretched.

.EXAMPLE
  pwsh tools/capture-screenshots.ps1 -Name shot-editor

.NOTES
  READ THIS BEFORE COMMITTING THE OUTPUT.

  These images become public and are indexed by search engines. Deleting them
  later does not retract them. A screenshot must not show real customer or
  production schema, real server names or hostnames, usernames, or anything
  identifying a person — and that includes the saved-connection list and the
  query tab names, not just the result grid.

  Use a connection and a database created for the purpose, with obviously
  fictional names. Check the connection list, the tab strip, the window title
  and any status bar before you commit.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Name,
  [switch]$NoResize
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$interop = @'
using System;
using System.Runtime.InteropServices;
public class ImsWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $interop -ReferencedAssemblies System.Runtime.InteropServices, System.Runtime

$proc = Get-Process -Name Ims -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

if ($null -eq $proc) {
  Write-Error "IMS is not running with a visible window. Start it first: dotnet run --project src/Ims.App"
}

$h = $proc.MainWindowHandle

if (-not $NoResize) {
  # 1600x1000 keeps every site image on the same grid. The page renders at up to
  # 940 CSS px, so 1600 stays crisp on a high-DPI display.
  [void][ImsWin]::MoveWindow($h, 60, 40, 1600, 1000, $true)
}

[void][ImsWin]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1200   # let the window repaint before the grab

$r = New-Object ImsWin+RECT
[void][ImsWin]::GetWindowRect($h, [ref]$r)
$w  = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
if ($w -le 0 -or $ht -le 0) { Write-Error "Window rectangle is empty; is IMS minimised?" }

$dir = Join-Path $PSScriptRoot '..\site\img'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$out = Join-Path (Resolve-Path $dir) "$Name.png"

$bmp = New-Object System.Drawing.Bitmap $w, $ht
try {
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
  } finally { $g.Dispose() }
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
} finally { $bmp.Dispose() }

Write-Output "Saved $out ($($w)x$($ht))"
Write-Output ""
Write-Output "Before committing: open it and confirm no real hostname, schema, or"
Write-Output "user name is visible — including the connection list and tab names."

<#
.SYNOPSIS
  A cross-agent lock for the oracle programs that are driven through real windows (BlastEm, Mesen): only one agent at a time may send keys to or capture a window of the same oracle,
  otherwise screenshots overlap and key presses land in the wrong window (this happened between the Genesis agents on 2026-10-04).

.DESCRIPTION
  Acquire before a batch of window-driving work, release when done (a `try { ... } finally { release }` in your own script is the safest shape).
  The lock is a file under C:\BrokenNes-tools\sega\locks (outside the repository). A lock older than -StaleMinutes is taken over (its owner is gone).
  Headless oracle runs that open no window (Mesen's --testRunner / Lua capture without a window, BlastEm launched with a private screenshot path and never focused) need no lock.

.EXAMPLE
  pwsh -File Workshop\Sega\oracle-lock.ps1 -Acquire blastem -Owner b3a -WaitMinutes 60
  ... drive BlastEm ...
  pwsh -File Workshop\Sega\oracle-lock.ps1 -Release blastem -Owner b3a
  pwsh -File Workshop\Sega\oracle-lock.ps1 -Status
#>
[CmdletBinding()]
param(
    [string]$Acquire,
    [string]$Release,
    [switch]$Status,
    [string]$Owner = $env:USERNAME,
    [int]$WaitMinutes = 30,
    [int]$StaleMinutes = 45,
    [string]$Dir = 'C:\BrokenNes-tools\sega\locks'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Dir | Out-Null
function LockPath($name) { Join-Path $Dir ($name.ToLowerInvariant() + '.lock') }
function ReadLock($path) { try { $j = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json; [pscustomobject]@{ Owner = $j.owner; At = [datetime]$j.at } } catch { $null } }

if ($Status) {
    Get-ChildItem $Dir -Filter *.lock -ErrorAction SilentlyContinue | ForEach-Object {
        $l = ReadLock $_.FullName
        "{0}: held by {1} since {2:HH:mm:ss} ({3:n0} min ago)" -f $_.BaseName, $l.Owner, $l.At, ((Get-Date) - $l.At).TotalMinutes
    }
    if (-not (Get-ChildItem $Dir -Filter *.lock -ErrorAction SilentlyContinue)) { 'no locks held' }
    return
}
if ($Release) {
    $p = LockPath $Release
    if (-not (Test-Path -LiteralPath $p)) { "not held: $Release"; return }
    $l = ReadLock $p
    if ($l -and $l.Owner -ne $Owner) { throw "lock '$Release' is held by '$($l.Owner)', not by '$Owner'" }
    Remove-Item -LiteralPath $p -Force
    "released $Release"
    return
}
if ($Acquire) {
    $p = LockPath $Acquire
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while ($true) {
        $l = if (Test-Path -LiteralPath $p) { ReadLock $p } else { $null }
        if ($null -eq $l -or ((Get-Date) - $l.At).TotalMinutes -gt $StaleMinutes -or $l.Owner -eq $Owner) {
            try {
                $fs = [IO.File]::Open($p, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                try {
                    $fs.SetLength(0)
                    $b = [Text.Encoding]::UTF8.GetBytes((@{ owner = $Owner; at = (Get-Date).ToString('o') } | ConvertTo-Json -Compress))
                    $fs.Write($b, 0, $b.Length)
                } finally { $fs.Dispose() }
                "acquired $Acquire for $Owner"
                return
            } catch { }   # raced with another agent: wait and retry
        }
        if ((Get-Date) -gt $deadline) { throw "gave up waiting for the '$Acquire' lock (held by $($l.Owner) since $($l.At))" }
        Start-Sleep -Seconds 10
    }
}
'usage: -Acquire <name> | -Release <name> | -Status   (names: blastem, mesen)'

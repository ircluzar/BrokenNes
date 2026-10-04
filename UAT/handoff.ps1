<#
.SYNOPSIS
  Builds a hand-off folder: the published desktop app (with the FL Studio plugin bundled, so "Config > Synthesizer Mode > Install to FL Studio" works),
  launchers, the notices, a testing guide, and the certification results of that exact build. Outside the repository; nothing here is committed.

.DESCRIPTION
  1. builds the Native AOT plugin (Plugin\build-dist.ps1) so the bundled DLL is current,
  2. publishes Windows\BrokenNes.Windows.csproj (self-contained, single file) into <Out>\<Tag>\BrokenNes,
  3. copies the notices and the license, writes launchers and the testing guide (docs\handoff-testing-guide.md is the template),
  4. runs, against THAT exe: UAT\plugin\install-smoke.ps1, UAT\entrypoints\desktop-smoke.ps1, UAT\entrypoints\sega-preview-smoke.ps1, UAT\plugin\synth-smoke.ps1
     (unless -SkipCertify), and writes the results into CERTIFIED.txt next to the exe.
  The UI smoke tests drive real windows: do not use the mouse or keyboard, and stop other agents' desktop automation, while they run.

.EXAMPLE
  pwsh -File UAT\handoff.ps1 -Tag 2026-10-04-sega-wip
#>
[CmdletBinding()]
param(
    [string]$Tag = (Get-Date -Format 'yyyy-MM-dd') + '-sega-wip',
    [string]$Out = 'C:\BrokenNes-handoff',
    [switch]$SkipPlugin,
    [switch]$SkipCertify
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Out.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to build the hand-off inside the repository: $Out" }
$dest = Join-Path $Out $Tag
$app = Join-Path $dest 'BrokenNes'
New-Item -ItemType Directory -Force $app | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
$git = (& git -C $repo rev-parse --short HEAD).Trim()
$dirty = @(& git -C $repo status --porcelain).Count
Write-Host "hand-off $Tag from commit $git ($dirty uncommitted paths) -> $dest"
if ($dirty) { Write-Warning "the working tree has uncommitted changes; the build is not exactly commit $git" }

# ---- 1. the plugin DLL ----
if (-not $SkipPlugin) { & pwsh -NoProfile -File (Join-Path $repo 'Plugin\build-dist.ps1'); if ($LASTEXITCODE -ne 0) { throw 'plugin build failed' } }
$pluginDll = Join-Path $repo 'Plugin\dist\BrokenNes2_x64.dll'
if (-not (Test-Path $pluginDll)) { throw "no plugin DLL at $pluginDll (run without -SkipPlugin)" }

# ---- 2. the desktop app ----
$art = Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\bn_handoff'
& dotnet.exe publish (Join-Path $repo 'Windows\BrokenNes.Windows.csproj') -c Release -o $app --artifacts-path $art -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed ($LASTEXITCODE)" }
$exe = Join-Path $app 'BrokenNes.Windows.exe'
$bundled = Join-Path $app 'Plugin\BrokenNes2_x64.dll'
if (-not (Test-Path $exe)) { throw "no exe at $exe" }
if (-not (Test-Path $bundled)) { throw "the plugin DLL did not ship beside the exe ($bundled)" }
if ((Get-FileHash $bundled).Hash -ne (Get-FileHash $pluginDll).Hash) { throw 'the bundled plugin DLL is not the one just built' }

# ---- 3. notices, license, launchers, guide ----
foreach ($f in 'THIRD_PARTY_NOTICES.md', 'LICENSE', 'LICENSE.md', 'LICENSE.txt') { $p = Join-Path $repo $f; if (Test-Path $p) { Copy-Item $p $app -Force } }
Copy-Item (Join-Path $repo 'Windows\NesEmulator\sega\THIRD_PARTY_NOTICES.md') (Join-Path $app 'THIRD_PARTY_NOTICES-sega.md') -Force
@'
@echo off
rem BrokenNes with the Genesis / Mega Drive preview switched on (it stays hidden otherwise until the Genesis picture is certified).
set BROKENNES_SEGA=1
start "" "%~dp0BrokenNes\BrokenNes.Windows.exe"
'@ | Set-Content (Join-Path $dest 'Run BrokenNes (Genesis preview on).cmd') -Encoding ASCII
@'
@echo off
start "" "%~dp0BrokenNes\BrokenNes.Windows.exe"
'@ | Set-Content (Join-Path $dest 'Run BrokenNes.cmd') -Encoding ASCII
@'
@echo off
rem Opens the installer window for FL Studio (asks for administrator rights, as the Program Files folder needs them). Close FL Studio first.
"%~dp0BrokenNes\BrokenNes.Windows.exe" --install-vst
'@ | Set-Content (Join-Path $dest 'Install to FL Studio.cmd') -Encoding ASCII
@'
@echo off
rem The plugin as a playable standalone synth (a one-off restart; MIDI keyboard or the PC keyboard).
"%~dp0BrokenNes\BrokenNes.Windows.exe" --synth
'@ | Set-Content (Join-Path $dest 'Run standalone synth.cmd') -Encoding ASCII
$guide = Join-Path $repo 'docs\handoff-testing-guide.md'
if (Test-Path $guide) { (Get-Content $guide -Raw).Replace('{{TAG}}', $Tag).Replace('{{COMMIT}}', $git).Replace('{{DATE}}', (Get-Date -Format 'yyyy-MM-dd HH:mm')) | Set-Content (Join-Path $dest 'READ ME FIRST - testing guide.md') -Encoding UTF8 }

# ---- 4. certify that exact build ----
$cert = New-Object System.Collections.Generic.List[string]
$cert.Add("hand-off $Tag  commit $git  built $(Get-Date -Format 'yyyy-MM-dd HH:mm')  plugin DLL sha256 $((Get-FileHash $bundled).Hash)")
if (-not $SkipCertify) {
    $runs = @(
        @{ Name = 'install-smoke (Install to FL Studio, fake FL folders)'; Script = 'UAT\plugin\install-smoke.ps1' },
        @{ Name = 'desktop-smoke (NES in the desktop app)'; Script = 'UAT\entrypoints\desktop-smoke.ps1' },
        @{ Name = 'sega-preview-smoke (Master System, Game Gear, Genesis in the desktop app)'; Script = 'UAT\entrypoints\sega-preview-smoke.ps1' },
        @{ Name = 'synth-smoke (standalone synth)'; Script = 'UAT\plugin\synth-smoke.ps1' })
    foreach ($r in $runs) {
        Write-Host "certifying: $($r.Name)"
        $o = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo $r.Script) -Exe $exe 2>&1
        $code = $LASTEXITCODE
        $fails = @($o | Where-Object { $_ -match '^\[FAIL\]| FAIL ' }) | Select-Object -First 6
        $passes = @($o | Where-Object { $_ -match '^\[PASS\]|  ok  ' }).Count
        $line = "{0}: {1}  ({2} passes{3})" -f $r.Name, $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }), $passes, $(if ($fails) { '; ' + ($fails -join ' | ') } else { '' })
        Write-Host $line; $cert.Add($line)
    }
} else { $cert.Add('certification skipped (-SkipCertify)') }
$cert.Add('NOT verified by any script: audio through a real playback device (none on the build machine), the UAC prompt of the FL installer, the plugin inside FL Studio.')
$cert | Set-Content (Join-Path $dest 'CERTIFIED.txt') -Encoding UTF8
"done in {0:n0}s: {1}" -f $sw.Elapsed.TotalSeconds, $dest

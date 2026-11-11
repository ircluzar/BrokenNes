<#
.SYNOPSIS
  The Vaults GB MUSIC TEST player with an FL project in its FL TEST slot: -Flp x.flp -Out dir -> <Out>\<safe>.vaults-gb-mt.gbc + <safe>.vaults-gb-mt.fence.txt
  (<safe> = the .flp's base name, every character outside [A-Za-z0-9_-] -> '_'). Without -Flp: the player with the roster's placeholder (<Out>\placeholder.vaults-gb-mt.*).

.DESCRIPTION
  1. FruityHost mt-export vocgb (Plugin/BrokenNes.FruityHost/MusicTest.Export.cs): the .flp -> VocGbBackend (unchanged) -> <name>.vocsong.json (the game roster's
     "events" import) + the fence (first line: "... N over the limit, ...").
  2. The game repo's own build of the player (VRUN_VOC_dev musictest\build_musictest.ps1 -FlTest <json>): the game's converter and music driver, every track of the
     game + the FL TEST slot (the list's last entry). The game repo is not written (the build goes to -Work).
  Exit 0 when the ROM was written; otherwise a line starting FAIL and exit 1.
#>
param(
    [string]$Flp = '',
    [string]$Out = 'C:\BrokenNes-work\flp_out',
    [string]$Work = 'C:\BrokenNes-work\flp_musictest',
    [string]$Game = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_VOC_dev',
    [string]$FruityHost = ''
)
$ErrorActionPreference = 'Stop'
function Fail($m) { Write-Host "FAIL  $m"; exit 1 }
$tag = 'vaults-gb-mt'; $ext = 'gbc'; $kind = 'vocgb'
$player = Join-Path $Game 'musictest\build_musictest.ps1'
if (-not (Test-Path $player)) { Fail "no player build in the game repo: $player" }
if ($Flp) {
    if (-not (Test-Path -LiteralPath $Flp)) { Fail "no such .flp: $Flp" }
    $name = [IO.Path]::GetFileNameWithoutExtension($Flp)
    $safe = $name -replace '[^A-Za-z0-9_-]', '_'
} else { $name = 'placeholder'; $safe = 'placeholder' }
if (-not $FruityHost) {
    foreach ($c in @("$env:BROKENNES_WORK\mt_build", 'C:\BrokenNes-work\mt_build', "$env:BROKENNES_WORK\bn_build", 'C:\BrokenNes-work\flp2_build', 'C:\BrokenNes-work\bn_build')) {
        $x = Join-Path $c 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'
        if ((Test-Path $x) -and ((& $x 'mt-export' 2>&1 | Out-String) -match 'usage: mt-export')) { $FruityHost = $x; break }
    }
}
$w = Join-Path $Work ("$safe-$tag-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $w, $Out | Out-Null
$fenceOut = Join-Path $Out "$safe.$tag.fence.txt"
$args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $player, '-Out', (Join-Path $w 'build'))
if ($Flp) {
    if (-not $FruityHost -or -not (Test-Path $FruityHost)) { Fail 'FruityHost with mt-export not built (dotnet build Plugin/BrokenNes.FruityHost -c Release --artifacts-path C:\BrokenNes-work\mt_build) or pass -FruityHost' }
    & $FruityHost mt-export $kind $Flp $w | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { Fail "mt-export $kind (exit $LASTEXITCODE)" }
    $json = Join-Path $w "$name.vocsong.json"
    if (-not (Test-Path -LiteralPath $json)) { Fail "mt-export wrote no $json" }
    Copy-Item -LiteralPath (Join-Path $w "$name.$kind.fence.txt") $fenceOut -Force
    $title = ($name.ToUpperInvariant() -replace '[^ -~]', '?')
    $args2 += @('-FlTest', $json, '-FlTitle', $title)
} else {
    @("placeholder for $kind (music test): 0 over the limit, 0 played differently, 0 inside", 'note [music test] no -Flp: the FL TEST slot plays the roster''s placeholder (test_songs.fl_test)') | Set-Content -LiteralPath $fenceOut
}
$log = Join-Path $w 'build.log'
& powershell.exe @args2 *> $log
$rc = $LASTEXITCODE
Get-Content $log | Select-Object -Last 3 | ForEach-Object { Write-Host "   $_" }
if ($rc -ne 0) { Fail "the player's build (exit $rc, log $log)" }
$rom = Join-Path $w 'build\vrun_voc_musictest.gbc'
if (-not (Test-Path $rom)) { Fail "the build made no $rom" }
$dst = Join-Path $Out "$safe.$tag.$ext"
Copy-Item $rom $dst -Force
$h = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "OK    $dst  sha256 $h  (fence: $fenceOut; the FL TEST slot is the list's last entry)"
exit 0

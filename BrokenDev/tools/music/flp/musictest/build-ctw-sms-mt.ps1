<#
.SYNOPSIS
  The CTW SMS MUSIC TEST with an FL project's song in its FL TEST slot: <Out>\<safe>.ctw-sms-mt.sms + <Out>\<safe>.ctw-sms-mt.fence.txt.

.DESCRIPTION
  The music test ROM is the game's own Sound Test as a ROM (VRUN_CTW_SMS_dev musictest/build-musictest.ps1, BrokenDev project
  ctw-sms-musictest): the game's sound driver, its 11 tracks and an FL TEST slot (list 12) whose four streams are padded to 1280 bytes.
  The song is made by the plugin's CTW SMS back end (Plugin/BrokenNes2.Plugin/Music/Flp/CtwSmsBackend.cs: Build = VRUN's converter + the
  Master System fence, Patch = written over the slot as over the plugin's title-song template) through `FruityHost musictest-ctwsms`
  (Plugin/BrokenNes.FruityHost/MusicTest.CtwSms.cs), which also writes the slot's loop length for the screen.
  -Flp: the .flp (without it: the placeholder song). <safe> = the .flp's base name, characters not [A-Za-z0-9_-] -> '_' ("placeholder"
  without -Flp). -Rom / -Manifest: the music test build (default: the game repo's build\musictest, built first when missing or -Rebuild).
  -NesAsSms: BrokenNes2 instances on the NES console read as the Master System channel of the same number (an opt-in line in the fence).
  Exit 0 when both files are written (the fence may list notes over the limit: the ROM plays the song without them); else 1 + "FAIL ...".

.EXAMPLE
  pwsh BrokenDev/tools/music/flp/musictest/build-ctw-sms-mt.ps1 -Flp "C:\Users\philt\Desktop\Reverse Coaster\overworld.flp" -Out C:\BrokenNes-work\flp_out
#>
param(
    [string]$Flp = '',
    [Parameter(Mandatory)] [string]$Out,
    [string]$Game = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_CTW_SMS_dev',
    [string]$Rom = '',
    [string]$Manifest = '',
    [string]$FruityHost = '',
    [switch]$Rebuild,
    # read BrokenNes2 NES instances as the Master System channel of the same number (said in the fence); without it only Master System instances play
    [switch]$NesAsSms
)
$ErrorActionPreference = 'Stop'
function Fail($m) { Write-Host "FAIL  $m"; exit 1 }
try {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path
    if ($Flp -and -not (Test-Path -LiteralPath $Flp)) { Fail "no such .flp: $Flp" }
    $safe = if ($Flp) { [IO.Path]::GetFileNameWithoutExtension($Flp) -replace '[^A-Za-z0-9_-]', '_' } else { 'placeholder' }
    if (-not $Rom) {
        $mt = Join-Path $Game 'build\musictest'
        $Rom = Join-Path $mt 'vrun_ctw_sms_mt.sms'
        if (-not $Manifest) { $Manifest = Join-Path $mt 'vrun_ctw_sms_mt.txt' }
        if ($Rebuild -or -not (Test-Path -LiteralPath $Rom) -or -not (Test-Path -LiteralPath $Manifest)) {
            Write-Host "== building the music test ($Game musictest\build-musictest.ps1)"
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Game 'musictest\build-musictest.ps1') -Out $mt | ForEach-Object { Write-Host "   $_" }
            if ($LASTEXITCODE -ne 0) { Fail "the music test build (exit $LASTEXITCODE)" }
        }
    }
    if (-not $Manifest) { $Manifest = [IO.Path]::ChangeExtension($Rom, '.txt') }
    foreach ($f in $Rom, $Manifest) { if (-not (Test-Path -LiteralPath $f)) { Fail "not found: $f" } }
    if (-not $FruityHost) {
        foreach ($c in @('C:\BrokenNes-work\mt_build_sms', "$env:BROKENNES_WORK\bn_build", 'C:\BrokenNes-work\flp2_build', 'C:\BrokenNes-work\bn_build')) {
            $x = Join-Path $c 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'; if (Test-Path $x) { $FruityHost = $x; break }
        }
    }
    if (-not $FruityHost -or -not (Test-Path -LiteralPath $FruityHost)) { Fail "FruityHost not built (dotnet build $repo\Plugin\BrokenNes.FruityHost -c Release --artifacts-path C:\BrokenNes-work\mt_build_sms) or pass -FruityHost" }
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $outRom = Join-Path $Out "$safe.ctw-sms-mt.sms"
    $outFence = Join-Path $Out "$safe.ctw-sms-mt.fence.txt"
    $fa = @('musictest-ctwsms', $Rom, $Manifest, $outRom, $outFence)
    if ($Flp) { $fa += (Resolve-Path -LiteralPath $Flp).Path }
    if ($NesAsSms) { $fa += '--nes-as-sms' }
    $log = & $FruityHost @fa
    $rc = $LASTEXITCODE
    $log | ForEach-Object { Write-Host "   $_" }
    if ($rc -ne 0) { $why = ($log | Where-Object { $_ -like 'FAIL*' } | Select-Object -Last 1); Fail "FruityHost musictest-ctwsms (exit $rc): $why" }
    if (-not (Test-Path -LiteralPath $outRom) -or -not (Test-Path -LiteralPath $outFence)) { Fail 'FruityHost wrote no ROM / fence' }
    $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $outRom).Hash.ToLowerInvariant()
    Write-Host "OK    $outRom  sha256 $h"
    Write-Host "      $outFence  $((Get-Content -LiteralPath $outFence -TotalCount 1))"
    exit 0
} catch { Fail $_.Exception.Message }

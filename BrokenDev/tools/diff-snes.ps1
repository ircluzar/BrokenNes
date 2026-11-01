<#
.SYNOPSIS
  Differential replay of one SNES ROM, BrokenNes against Mesen 2 (a black-box oracle): the same ROM and the same input script, the picture
  after chosen frame counts from each emulator, compared pixel by pixel.

.DESCRIPTION
    Mesen    : Mesen.exe <rom> --luaScriptsToLoad mesen-snes-capture.lua --testRunner         (MESEN_* environment, a private deterministic copy)
    BrokenNes: BrokenNes.Workshop.exe --console-run --rom <rom> --png-at ... --input ...      (the SNES cores of the app)
    compare  : tools\imgdiff.py   (identical pixels, or how many differ and where; magenta diff pictures with -KeepDiffs)

  The private Mesen copy (made once under -Work) starts from -MesenDir's Mesen.exe and -MesenSettings (the settings.json of the user's own Mesen), then
  sets the three things that make a headless SNES run reproducible: Snes.DisableFrameSkipping=true (otherwise an unthrottled Mesen skips rendering frames
  and the picture depends on host load), Snes.RamPowerOnState=AllZeros and Snes.EnableRandomPowerOnState=false. Saves are emptied before the run (a battery
  file left by an earlier run makes a game skip its initialisation). The user's Mesen install and saves are never touched.

  Tolerance: Mesen expands a 5-bit colour channel x to 8 bits as (x<<3)|(x>>2) and BrokenNes as x*255/31, which differ by one level at some values (231 against 230
  for x = 28). -Tolerance 1 (the default here) forgives exactly that; -Tolerance 0 demands byte-identical RGB.

  What agreement proves: the two emulators drew the same picture at those frames for that input. It is not a proof of the whole game; a disagreement may be
  Mesen's or BrokenNes' (Mesen is the accepted baseline: the user calls it so accurate to real hardware it is a great baseline). Only frames listed are compared.

  Exit code: 0 every compared frame identical, 1 a difference, 2 usage or a tool failed.
#>
param(
    [Parameter(Mandatory = $true)][string]$Rom,
    [Parameter(Mandatory = $true)][string]$Work,
    [Parameter(Mandatory = $true)][string]$MesenDir,
    [Parameter(Mandatory = $true)][string]$MesenSettings,
    [Parameter(Mandatory = $true)][string]$Workshop,
    [Parameter(Mandatory = $true)][string]$Python,
    [Parameter(Mandatory = $true)][string]$Frames,
    [string]$InputScript = '',
    [string]$Tag = 'snes',
    [int]$CropTop = 0,
    [int]$CropBottom = 0,
    [int]$Tolerance = 1,
    [int]$InputShift = 2,
    [int]$MaxPixels = 0,
    [string]$BrokenNesInput = '',
    [int]$WindowBefore = 1,
    [int]$WindowAfter = 4,
    [switch]$KeepDiffs,
    # Mesen's test runner stops a script after about 100 s of wall clock; a positive value is passed as Mesen's own
    # --timeout=<seconds> so a longer run fits in one segment (0 = Mesen's default, as before).
    [int]$MesenTimeoutSec = 0
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($p in @($Rom, $MesenDir, $MesenSettings, $Workshop, $Python, (Join-Path $here 'mesen-snes-capture.lua'), (Join-Path $here 'imgdiff.py'))) { if (-not (Test-Path -LiteralPath $p)) { Write-Host "missing: $p"; exit 2 } }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

# ---- the private Mesen copy ------------------------------------------------------------------------------------------
$priv = Join-Path $Work 'mesen'
if (-not (Test-Path -LiteralPath (Join-Path $priv 'Mesen.exe'))) {
    New-Item -ItemType Directory -Force -Path $priv | Out-Null
    foreach ($f in (Get-ChildItem -LiteralPath $MesenDir -File | Where-Object { $_.Extension -in '.exe', '.dll' })) { Copy-Item -LiteralPath $f.FullName -Destination $priv -Force }
    $txt = [System.IO.File]::ReadAllText($MesenSettings)
    $a = $txt.IndexOf('"Snes"'); if ($a -lt 0) { Write-Host 'no "Snes" section in the Mesen settings'; exit 2 }
    $b = $txt.IndexOf('"Gameboy"', $a); if ($b -lt 0) { $b = $txt.Length }
    $snes = $txt.Substring($a, $b - $a)
    $snes2 = $snes -replace '"DisableFrameSkipping"\s*:\s*(true|false)', '"DisableFrameSkipping": true' -replace '"RamPowerOnState"\s*:\s*"[A-Za-z]+"', '"RamPowerOnState": "AllZeros"' -replace '"EnableRandomPowerOnState"\s*:\s*(true|false)', '"EnableRandomPowerOnState": false'
    foreach ($need in '"DisableFrameSkipping": true', '"RamPowerOnState": "AllZeros"', '"EnableRandomPowerOnState": false') { if ($snes2 -notmatch [regex]::Escape($need)) { Write-Host "could not set $need in the private Mesen settings"; exit 2 } }
    [System.IO.File]::WriteAllText((Join-Path $priv 'settings.json'), $txt.Substring(0, $a) + $snes2 + $txt.Substring($b), (New-Object System.Text.UTF8Encoding($false)))
}
foreach ($d in 'Saves', 'SaveStates') { $p = Join-Path $priv $d; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

$mesenOut = Join-Path $Work 'mesen-shots'; $bnOut = Join-Path $Work 'brokennes-shots'
foreach ($d in $mesenOut, $bnOut) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }; New-Item -ItemType Directory -Force -Path $d | Out-Null }
$Window = '-' + $WindowBefore + ':' + $WindowAfter
$frameList = @($Frames -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$maxFrame = ($frameList | Measure-Object -Maximum).Maximum

# ---- Mesen ---------------------------------------------------------------------------------------------------------------
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $priv 'Mesen.exe'
$psi.Arguments = '"' + $Rom + '" --luaScriptsToLoad "' + (Join-Path $here 'mesen-snes-capture.lua') + '" --testRunner'
if ($MesenTimeoutSec -gt 0) { $psi.Arguments += " --timeout=$MesenTimeoutSec" }
$waitMs = [math]::Max(600000, ($MesenTimeoutSec + 60) * 1000)
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.WorkingDirectory = $priv
$psi.EnvironmentVariables['MESEN_OUT'] = $mesenOut
$psi.EnvironmentVariables['MESEN_TAG'] = 'm'
$psi.EnvironmentVariables['MESEN_FRAMES'] = ($frameList -join ',')
$psi.EnvironmentVariables['MESEN_INPUT'] = $InputScript
$psi.EnvironmentVariables['MESEN_WINDOW'] = $Window
$psi.EnvironmentVariables['MESEN_INPUT_SHIFT'] = [string]$InputShift
Write-Host "diff-snes: Mesen ($maxFrame frames) ..."
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = [System.Diagnostics.Process]::Start($psi)
if (-not $p.WaitForExit($waitMs)) { try { & taskkill.exe /PID $p.Id /T /F | Out-Null } catch { }; Write-Host "Mesen did not finish in $([int]($waitMs / 1000)) s"; exit 2 }
Write-Host "diff-snes: Mesen exit $($p.ExitCode) in $([math]::Round($sw.Elapsed.TotalSeconds, 1)) s; $(@(Get-ChildItem $mesenOut -Filter *.png).Count) pictures"
if (@(Get-ChildItem $mesenOut -Filter *.png).Count -lt $frameList.Count) { Write-Host 'Mesen did not write the requested pictures (its test runner stops a script after about 100 s)'; exit 2 }

# ---- BrokenNes ---------------------------------------------------------------------------------------------------------
$a = @('--console-run', '--rom', $Rom, '--frames', [string]$maxFrame, '--png-at', ($frameList -join ','), '--out', $bnOut, '--tag', 'b')
# -BrokenNesInput is the negative control: a different pad script for BrokenNes than Mesen got must make the comparison FAIL.
$bnInput = if ($PSBoundParameters.ContainsKey('BrokenNesInput')) { $BrokenNesInput } else { $InputScript }
if ($bnInput) { $a += @('--input', $bnInput) }
Write-Host 'diff-snes: BrokenNes --console-run ...'
$r = & $Workshop @a 2>&1 | Out-String
if (@(Get-ChildItem $bnOut -Filter *.png).Count -ne $frameList.Count) { Write-Host ("BrokenNes wrote no pictures:`n" + $r); exit 2 }

# ---- compare -------------------------------------------------------------------------------------------------------------
$diffDir = if ($KeepDiffs) { Join-Path $Work 'diffs' } else { '' }
$ia = @((Join-Path $here 'imgdiff.py'), $mesenOut, 'm_d', $bnOut, 'b_snes_f', '--frames', ($frameList -join ','), '--out', (Join-Path $Work "$Tag.imgdiff.json"), '--label-a', 'Mesen', '--label-b', 'BrokenNes', '--scan', $Window, '--tolerance', [string]$Tolerance, '--max-pixels', [string]$MaxPixels)
if ($CropTop -gt 0) { $ia += @('--crop-top', [string]$CropTop) }
if ($CropBottom -gt 0) { $ia += @('--crop-bottom', [string]$CropBottom) }
if ($diffDir) { $ia += @('--diff-dir', $diffDir) }
& $Python -I @ia
$code = $LASTEXITCODE
if ($code -eq 0) { Write-Host "diff-snes: PASS (every compared frame is pixel-identical to Mesen)" } else { Write-Host "diff-snes: FAIL (see above; report $(Join-Path $Work "$Tag.imgdiff.json"))" }
exit $code

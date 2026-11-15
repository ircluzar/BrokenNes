<#
  gg2sms cross-check: run converted .sms ROMs (and their GG originals) on BrokenDev's other emulators (Mesen 2, BizHawk Genesis Plus GX in
  SMS mode / SMSHawk for the GG) with the harness's pad script, through BrokenDev's own compat adapters, and save pictures at the
  checkpoints. gg2sms_crosscheck.py compares the pictures; this script only runs the emulators.

    powershell -NoProfile -File gg2sms_crosscheck.ps1 -Rom <file> -Console sms|gg -Emu mesen|bizhawk-gpgx-sms|bizhawk-smshawk-gg
                -Out <dir> -Frames N -Shots 300,900 [-Pad "120:Select,126:"]
#>
param([Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][ValidateSet('sms', 'gg')][string]$Console,
      [Parameter(Mandatory)][string]$Emu, [Parameter(Mandatory)][string]$Out, [int]$Frames = 1000, [string]$Shots = '', [string]$Pad = '',
      [int]$TimeoutSec = 600)
$ErrorActionPreference = 'Stop'
$bd = Resolve-Path (Join-Path $PSScriptRoot '..\..')
foreach ($l in 'Common', 'Tools', 'Archive', 'Toolchains', 'Manifest', 'Build', 'Verify', 'Diff', 'DevTools', 'Emulators', 'Compat') { . (Join-Path $bd "lib\$l.ps1") }
$J = Read-BdEmulators
$e = Get-BdEmulator -Id $Emu -J $J
if (-not $e) { throw "no emulator '$Emu'" }
$pkg = if (Get-BdProp $e 'package' $null) { Get-BdEmuPackage -Id ([string]$e.package) -J $J } else { $null }
$exe = Get-BdEmuExe -Emu $e -J $J
if (-not $exe) { throw "$Emu is not installed" }
$fn = Get-BdAdapterFunction -Adapter ([string]$e.adapter)
$Out = [System.IO.Path]::GetFullPath($Out)
$work = Join-Path $Out 'work'; $save = Join-Path $work 'saves'
New-Item -ItemType Directory -Force -Path $Out, $work, $save | Out-Null
$romCopy = Join-Path $work (Split-Path -Leaf $Rom)
Copy-Item -LiteralPath $Rom -Destination $romCopy -Force
$shotList = @($Shots -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$text = New-BdCompatSpecText -Run @{ Console = $Console; Rom = $romCopy; Out = $Out; SaveDir = $save; Frames = $Frames; Reads = @(); Input = $Pad; Shots = $shotList }
$specPath = Join-Path $Out 'spec.txt'
[System.IO.File]::WriteAllText($specPath, $text, (New-Object System.Text.UTF8Encoding($false)))
$r = & $fn -Emu $e -Package $pkg -Exe $exe -SpecPath $specPath -Spec (Read-BdCompatSpec -Text $text) -Scratch $work -TimeoutSec $TimeoutSec
"exit=$($r.ExitCode) seconds=$($r.Seconds)"

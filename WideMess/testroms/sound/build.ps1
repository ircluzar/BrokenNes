<#
.SYNOPSIS
  Builds the WideMess sound test ROM and its negative control into WideMess\testroms\bin:
    sound.sfc            the SNES-format image (on a SNES the new voices are silent and the S-DSP's 8 play)
    sound.wms            the WideMess game file (header + image; needs WMFEAT bit 2, the sound)
    sound_nounlock.wms   the negative control: the same program without the unlock (a WideMess must sound exactly as a SNES)
    sound.json           the manifest Workshop --wms-test measures against (gen_data.py)
.PARAMETER Wla
  Folder holding wla-65816.exe, wla-spc700.exe and wlalink.exe. Default: $env:BROKENDEV_WLA, else the PVSnesLib toolchain beside this repo
  (PROJECTS\SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin).
.PARAMETER Workshop
  BrokenNes.Workshop.exe (for --wms-pack). Default: $env:BROKENNES_WORKSHOP, else the newest build under %LOCALAPPDATA%\VRUN_Nes_Dev_work.
#>
param([string]$Wla = '', [string]$Workshop = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $src '..\..\..')).Path
$bin = Join-Path $repo 'WideMess\testroms\bin'
New-Item -ItemType Directory -Force $bin | Out-Null

if (-not $Wla) { $Wla = $env:BROKENDEV_WLA }
if (-not $Wla) { $Wla = Join-Path (Split-Path $repo -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin' }
# a git worktree under .claude\worktrees sits deeper: look beside the main checkout too
if (-not (Test-Path -LiteralPath (Join-Path $Wla 'wla-65816.exe')) -and $repo -match '^(.*?)\\BrokenNes\\\.claude\\worktrees\\') { $Wla = Join-Path $Matches[1] 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin' }
$asm = Join-Path $Wla 'wla-65816.exe'; $spc = Join-Path $Wla 'wla-spc700.exe'; $link = Join-Path $Wla 'wlalink.exe'
foreach ($t in $asm, $spc, $link) { if (-not (Test-Path -LiteralPath $t)) { throw "$([IO.Path]::GetFileName($t)) not found in $Wla (pass -Wla or set BROKENDEV_WLA)" } }
if (-not $Workshop) { $Workshop = $env:BROKENNES_WORKSHOP }
if (-not $Workshop) {
    $Workshop = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work') -Recurse -Filter 'BrokenNes.Workshop.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Workshop -or -not (Test-Path -LiteralPath $Workshop)) { throw 'BrokenNes.Workshop.exe not found (pass -Workshop or set BROKENNES_WORKSHOP)' }

function Invoke-Tool([string]$exe, [string[]]$argList, [string]$cwd) {
    $out = Join-Path ([IO.Path]::GetTempPath()) ("wms_" + [guid]::NewGuid() + '.txt')
    $err = "$out.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $cwd -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    $text = (Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $err -Raw -ErrorAction SilentlyContinue)
    Remove-Item -LiteralPath $out, $err -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed ($($p.ExitCode)): $text" }
    return $text
}

& python -I (Join-Path $src 'gen_data.py') $src $bin
if ($LASTEXITCODE -ne 0) { throw 'gen_data.py failed' }

# the SPC700 program: a plain image from $0200 (wlalink -b)
Invoke-Tool $spc @('-o', 'spc.o', 'spc.asm') $src | Out-Null
Set-Content -LiteralPath (Join-Path $src 'spc.link') -Value "[objects]`r`nspc.o" -Encoding ascii
Invoke-Tool $link @('-b', 'spc.link', 'spc.bin') $src | Out-Null
Remove-Item -LiteralPath (Join-Path $src 'spc.o'), (Join-Path $src 'spc.link') -ErrorAction SilentlyContinue

foreach ($v in @(@{ Name = 'sound'; Defines = @() }, @{ Name = 'sound_nounlock'; Defines = @('-DNOUNLOCK') })) {
    $obj = "$($v.Name).o"; $linkFile = Join-Path $src "$($v.Name).link"; $sfc = Join-Path $bin "$($v.Name).sfc"
    Invoke-Tool $asm (@($v.Defines) + @('-o', $obj, 'sound.asm')) $src | Out-Null
    Set-Content -LiteralPath $linkFile -Value "[objects]`r`n$obj" -Encoding ascii
    Invoke-Tool $link @('-r', $linkFile, $sfc) $src | Out-Null
    Remove-Item -LiteralPath (Join-Path $src $obj), $linkFile -ErrorAction SilentlyContinue
    $title = if ($v.Name -eq 'sound') { 'WideMess sound' } else { 'WideMess sound (no unlock)' }
    Invoke-Tool $Workshop @('--wms-pack', '--in', $sfc, '--out', (Join-Path $bin "$($v.Name).wms"), '--title', "`"$title`"", '--features', '4') $src
}
# the negative control is used as a .wms only (the check runs its image on the SNES console too)
Remove-Item -LiteralPath (Join-Path $bin 'sound_nounlock.sfc') -ErrorAction SilentlyContinue
Get-ChildItem $bin | Select-Object Name, Length

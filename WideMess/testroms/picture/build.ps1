<#
.SYNOPSIS
  Builds the WideMess picture test ROMs (milestone 2) and the pictures they must draw into WideMess\testroms\bin:
    sprites.wms,  sprites_nounlock.wms     the 256-sprite table in the WideMess mode at 400 (and its negative control)
    windows.wms,  windows_nounlock.wms     windows and colour math at 400
    widesnes.wms, widesnes_nounlock.wms    SNES modes 1, 0, 5, 2 and 7 drawn 400 wide, the SNES sprite table across 400
    <rom>.expect.png, <rom>_nounlock.expect.png, <rom>.<variant>.expect.png
                                           the pictures gen_picture.py's model of the spec draws for them (Workshop --wms-test compares)
  The negative controls are the same ROMs assembled with -D NOUNLOCK: they write every WideMess register without unlocking first.
.PARAMETER Wla
  Folder holding wla-65816.exe and wlalink.exe. Default: $env:BROKENDEV_WLA, else the PVSnesLib toolchain beside this repo (PROJECTS\SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin).
.PARAMETER Workshop
  BrokenNes.Workshop.exe (for --wms-pack). Default: $env:BROKENNES_WORKSHOP, else the newest build under %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_wms.
#>
param([string]$Wla = '', [string]$Workshop = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $src '..\..\..')).Path
$bin = Join-Path $repo 'WideMess\testroms\bin'
New-Item -ItemType Directory -Force $bin | Out-Null

if (-not $Wla) { $Wla = $env:BROKENDEV_WLA }
if (-not $Wla) { $Wla = Join-Path (Split-Path $repo -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin' }
if (-not (Test-Path -LiteralPath $Wla) -and $repo -match '\\\.claude\\worktrees\\') {   # from a .claude\worktrees checkout: beside the main checkout
    $Wla = Join-Path (Split-Path ($repo -replace '\\\.claude\\worktrees\\.*$', '') -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin'
}
$asm = Join-Path $Wla 'wla-65816.exe'; $link = Join-Path $Wla 'wlalink.exe'
if (-not (Test-Path -LiteralPath $asm)) { throw "wla-65816.exe not found in $Wla (pass -Wla or set BROKENDEV_WLA)" }
if (-not $Workshop) { $Workshop = $env:BROKENNES_WORKSHOP }
if (-not $Workshop) {
    $Workshop = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\bn_wms') -Recurse -Filter 'BrokenNes.Workshop.exe' -ErrorAction SilentlyContinue |
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

$work = Join-Path ([IO.Path]::GetTempPath()) ('wms_picture_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    & python -I (Join-Path $src 'gen_picture.py') $work $bin
    if ($LASTEXITCODE -ne 0) { throw 'gen_picture.py failed' }
    $titles = @{ sprites = 'WideMess sprites'; windows = 'WideMess windows'; widesnes = 'WideMess SNES modes wide' }
    foreach ($rom in 'sprites', 'windows', 'widesnes') {
        $dir = Join-Path $work $rom
        Copy-Item -LiteralPath (Join-Path $src 'player.asm') -Destination $dir
        foreach ($v in @(@{ Name = $rom; Defines = @(); Title = $titles[$rom] }, @{ Name = "${rom}_nounlock"; Defines = @('-DNOUNLOCK'); Title = "$($titles[$rom]) (no unlock)" })) {
            $obj = "$($v.Name).o"; $linkFile = Join-Path $dir "$($v.Name).link"; $sfc = Join-Path $dir "$($v.Name).sfc"
            Invoke-Tool $asm (@($v.Defines) + @('-o', $obj, 'player.asm')) $dir | Out-Null
            Set-Content -LiteralPath $linkFile -Value "[objects]`r`n$obj" -Encoding ascii
            Invoke-Tool $link @('-r', $linkFile, $sfc) $dir | Out-Null
            Invoke-Tool $Workshop @('--wms-pack', '--in', $sfc, '--out', (Join-Path $bin "$($v.Name).wms"), '--title', "`"$($v.Title)`"", '--features', '3') $dir
        }
    }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem $bin | Select-Object Name, Length

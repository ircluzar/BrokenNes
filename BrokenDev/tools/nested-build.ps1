<#
.SYNOPSIS
  Turns a mapper 30 NES ROM into a Project Nested SNES ROM with the m30pipe pipeline, for any game, without touching the
  restored pipeline's own game profiles. Run it with the PowerShell the pipeline's injector needs: PowerShell 7.6+ (pwsh) for
  our fork's .NET 10 injector (Project-Nested-core, 2026-11-10), Windows PowerShell 5.1 for master's .NET Framework release exe.

.DESCRIPTION
  This is the "a new build of a known game" recipe of the Nested Builder (its ui\README.md), as one non-interactive command:

    1. compute the PRG CRC32 of the NES ROM (the key of a Nested profile);
    2. write a fresh profile under -Work (never into the pipeline's games\ folder): the injector settings copied from a
       base profile, the post-build steps the game needs, and an empty entry-point list;
         -Post flash      run tools\flash_patch.py --sectors <the build's flash save sectors, detected from the .mlb>
                          (flash saves: the sector numbers move between builds, a stale number saves into the wrong place)
         -Post dmc_status run tools\dmc_status.py (Nested never clears the DMC-active bit of $4015; a game that waits for a
                          DMC sample would hang)
    3. -Feedback none     build with no entry-point feedback (the first run of each part of the game recompiles briefly);
       -Feedback harvest  run bootstrap\bootstrap.ps1 (a headless NES exploration finds the entry points, then SNES
                          verification rounds close the loop; needs Mesen, takes 10 to 20 minutes) and keep its profile;
       -Feedback brokennes  the classic Nested feedback loop, played headless on BrokenNes (no Mesen, about a minute):
                          build, run every plan of -Plans on the Workshop's --snesrun with a fresh SRAM, read the entry
                          points each run's .srm recorded (the core's runtime compiles) with the injector's own reader,
                          append the new ones, rebuild; repeat until a round adds nothing (-Rounds at most). Without it
                          the first run of each part of the game compiles at run time: Nested VRUN lagged 134 frames in
                          verify's play scenario (2026-11-07), 0 with it.
    4. m30build.ps1 writes the SNES ROM to -Out (with <Out>.build.log / .build.json beside it).

  Nothing game-specific is in the pipeline: everything a game needs is a parameter here.

.PARAMETER Nes        the mapper 30 .nes (with its .mlb beside it when -Post flash is used)
.PARAMETER Out        the .smc to write
.PARAMETER Pipe       the restored ...\tools\m30pipe folder
.PARAMETER Python     a Python 3.10+ with pillow and numpy
.PARAMETER Work       a work folder for the profile and the harvest (outside git)
.PARAMETER BaseProfile  PRG CRC32 of an existing profile in the pipeline whose settings are copied (e.g. 50a5ca15 for VRUN, d257beb1 for the SMB1 remake)
.PARAMETER Post       comma list: flash, dmc_status
.PARAMETER SnesTitle  the SNES header title (20 characters at most)
.PARAMETER Feedback   none | harvest | brokennes
.PARAMETER Workshop   brokennes: BrokenNes.Workshop.exe
.PARAMETER CoreDir    a core folder (Project_Nested.smc + manifest.json) to inject instead of the pipeline's out\core: 'brokendev.ps1 nested'
                      passes the core it assembled from source (Project-Nested-core, tools/nestedasm). Not used by -Feedback harvest.
.PARAMETER Nmi        the per-game accuracy/speed choice for the NES vertical-blank NMI (the injector's NmiMode settings):
                      fast (or empty) = Project Nested's default, the NMI is delivered when the game reaches its idle loop
                      (both NmiMode flags; nothing is written, so a build stays byte-identical to the earlier ones);
                      accurate = at a FIXED time, the SNES vblank, like the NES (NmiMode.AtVblank only, AutoDetect off).
.PARAMETER Plans      brokennes: a JSON list of { "name", "frames", "input" } (Workshop pad scripts)
.PARAMETER Rounds     brokennes: most build + play rounds
.PARAMETER Minutes    harvest wall-clock budget
.PARAMETER Seeds      harvest seeds (fewer seeds = fewer parallel Mesen processes)
.PARAMETER MesenSource  a Mesen 2 folder to copy into a private deterministic copy (harvest only; the original is never modified)
#>
param(
    [Parameter(Mandatory = $true)][string]$Nes,
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Pipe,
    [Parameter(Mandatory = $true)][string]$Python,
    [Parameter(Mandatory = $true)][string]$Work,
    [string]$BaseProfile = '',
    [string]$Post = '',
    [string]$SnesTitle = '',
    [ValidateSet('none', 'harvest', 'brokennes')][string]$Feedback = 'none',
    [string]$Workshop = '',
    [string]$Plans = '',
    [int]$Rounds = 4,
    [int]$Minutes = 20,
    [string]$Seeds = '1,2',
    [string]$MesenSource = '',
    [string]$CoreDir = '',
    [ValidateSet('', 'fast', 'accurate')][string]$Nmi = ''
)
$ErrorActionPreference = 'Stop'
# Which injector the pipeline loads: our fork's lib\nested.ps1 has Assert-InjectorHost (Project_Nested.dll, .NET 10, pwsh);
# master's loads the .NET Framework Project_Nested.exe (Windows PowerShell 5.1). Child scripts run in this same PowerShell.
$net10 = [bool](Select-String -LiteralPath (Join-Path $Pipe 'lib\nested.ps1') -Pattern 'function Assert-InjectorHost' -Quiet)
if ($net10 -and ($PSVersionTable.PSEdition -ne 'Core' -or [Environment]::Version.Major -lt 10)) { throw "the pipeline $Pipe runs the .NET 10 injector: run this with PowerShell 7.6+ (pwsh.exe)" }
if (-not $net10 -and $PSVersionTable.PSEdition -eq 'Core') { throw "the pipeline $Pipe runs the .NET Framework injector: run this with Windows PowerShell 5.1 (powershell.exe)" }
$shell = (Get-Process -Id $PID).Path

function Get-PrgCrc32([string]$Path) {
    $code = @"
import sys, zlib
d = open(sys.argv[1], 'rb').read()
assert d[:4] == b'NES\x1a', 'not an iNES file'
prg = d[4] * 16384
off = 16 + (512 if d[6] & 4 else 0)
print('%08x' % (zlib.crc32(d[off:off + prg]) & 0xffffffff))
"@
    $r = & $Python -I -c $code $Path
    if ($LASTEXITCODE -ne 0 -or $r -notmatch '^[0-9a-f]{8}$') { throw "could not compute the PRG CRC32 of $Path" }
    $r.Trim()
}

$Nes = (Resolve-Path -LiteralPath $Nes).Path
$Pipe = (Resolve-Path -LiteralPath $Pipe).Path
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$crc = Get-PrgCrc32 $Nes
$profileDir = Join-Path $Work ("profile\" + $crc)
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
Write-Host "nested-build: $Nes  prg crc32 $crc  -> profile $profileDir"

# ---- settings from the base profile -----------------------------------------------------------
$settings = [ordered]@{}
if ($BaseProfile) {
    $bp = Join-Path $Pipe ("games\" + $BaseProfile + "\game.json")
    if (-not (Test-Path -LiteralPath $bp)) { throw "base profile $BaseProfile not found at $bp" }
    $bj = Get-Content -LiteralPath $bp -Raw | ConvertFrom-Json
    foreach ($p in $bj.settings.PSObject.Properties) { $settings[$p.Name] = [string]$p.Value }
}
if ($SnesTitle) { $settings['EmulatorName'] = $SnesTitle }
if ($Nmi -eq 'accurate') { $settings['NmiMode.AtVblank'] = 'True'; $settings['NmiMode.AutoDetect'] = 'False' }

# ---- post-build steps --------------------------------------------------------------------------
$postSteps = @()
# (PowerShell variables are case-insensitive: the parameter $Post and a local $post would be one variable)
foreach ($name in ($Post -split ',' | Where-Object { $_ })) {
    switch ($name.Trim()) {
        'flash' {
            $mlb = [System.IO.Path]::ChangeExtension($Nes, '.mlb')
            if (-not (Test-Path -LiteralPath $mlb)) { throw "-Post flash needs the NESFab symbol file next to the ROM: $mlb" }
            $r = & $Python -I (Join-Path $Pipe 'tools\flash_sectors.py') $Nes --json
            if ($LASTEXITCODE -ne 0) { throw "flash_sectors.py failed: $r" }
            $j = ($r | Out-String) | ConvertFrom-Json
            Write-Host ("nested-build: flash sectors: " + ($r | Out-String).Trim())
            $secs = @()
            $src = $null
            if ($j.PSObject.Properties['sectors']) { $src = $j.sectors } elseif ($j -is [System.Array]) { $src = $j }
            foreach ($s in $src) { $secs += ('0x{0:X2}' -f [int]$(if ($s.PSObject.Properties['sector']) { $s.sector } else { $s })) }
            if ($secs.Count -lt 1) { throw "no flash save sectors were detected in $Nes ($mlb); not guessing" }
            $postSteps += [ordered]@{ name = 'flash_patch'; run = 'python'; script = '{pipe}/tools/flash_patch.py'; args = @('{in}', '{out}', '--sectors', ($secs -join ',')) }
        }
        'dmc_status' { $postSteps += [ordered]@{ name = 'dmc_status'; run = 'python'; script = '{pipe}/tools/dmc_status.py'; args = @('{in}', '{out}') } }
        default { throw "unknown -Post step '$name' (known: flash, dmc_status)" }
    }
}

$game = [ordered]@{
    format = 'm30pipe-game/1'; name = [System.IO.Path]::GetFileNameWithoutExtension($Nes); prg_crc32 = $crc
    rom = [ordered]@{ file = [System.IO.Path]::GetFileName($Nes) }
    notes = @("Written by BrokenDev tools\nested-build.ps1 (feedback: $Feedback). Base profile: $(if ($BaseProfile) { $BaseProfile } else { 'none' }).")
    feedback = [ordered]@{ calls = @('calls.txt'); srm = @() }
    settings = $settings
    post_build = @($postSteps)
}
$gj = Join-Path $profileDir 'game.json'
[System.IO.File]::WriteAllText($gj, ($game | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
$calls = Join-Path $profileDir 'calls.txt'
if (-not (Test-Path -LiteralPath $calls)) { [System.IO.File]::WriteAllText($calls, "# entry points; appended by the bootstrap, never sorted`r`n") }

$ps = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File')
$outDir = Split-Path -Parent $Out
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ($Feedback -eq 'harvest') {
    # the harness wants a private deterministic Mesen copy and an output root: the two machine-specific paths
    $mesenPriv = Join-Path $Work 'mesen'
    if (-not (Test-Path -LiteralPath (Join-Path $mesenPriv 'Mesen.exe'))) {
        if (-not $MesenSource) { throw '-Feedback harvest needs -MesenSource (a Mesen 2 folder); a private copy is made, the original is never changed' }
        & $shell @ps (Join-Path $Pipe 'harness\setup_mesen.ps1') -Source $MesenSource -Dest $mesenPriv
        if ($LASTEXITCODE -ne 0) { throw "setup_mesen.ps1 failed (exit $LASTEXITCODE)" }
    }
    $env:M30_MESEN_DIR = $mesenPriv
    $env:M30_OUT_ROOT = Join-Path $Work 'runs'
    New-Item -ItemType Directory -Force -Path $env:M30_OUT_ROOT | Out-Null
    $a = $ps + @((Join-Path $Pipe 'bootstrap\bootstrap.ps1'), '-Nes', $Nes, '-Profile', $profileDir, '-Out', $Out, '-Minutes', $Minutes, '-Seeds', $Seeds, '-MaxPar', 2, '-Python', $Python, '-AppendToExisting')
    Write-Host "nested-build: bootstrap (harvest) ..."
    & $shell @a
    $code = $LASTEXITCODE
    # 0 converged, 3 the rounds ran out without converging (the ROM and profile are still written)
    if ($code -ne 0 -and $code -ne 3) { throw "bootstrap.ps1 failed (exit $code)" }
    if ($code -eq 3) { Write-Host 'nested-build: WARNING the bootstrap did not converge in the budget; the ROM is built with the entry points found so far' }
    if (-not (Test-Path -LiteralPath $Out)) { throw "bootstrap finished but wrote no ROM at $Out" }
    exit 0
}

$a = $ps + @((Join-Path $Pipe 'm30build.ps1'), '-Nes', $Nes, '-Profile', $profileDir, '-Out', $Out, '-Python', $Python, '-AllowMd5Mismatch')
if ($CoreDir) { $a += @('-CoreDir', $CoreDir) }
& $shell @a
if ($LASTEXITCODE -ne 0) { throw "m30build.ps1 failed (exit $LASTEXITCODE); see $Out.build.log" }
if ($Feedback -eq 'brokennes') {
    if (-not $Workshop -or -not (Test-Path -LiteralPath $Workshop)) { throw '-Feedback brokennes needs -Workshop (BrokenNes.Workshop.exe)' }
    if (-not $Plans -or -not (Test-Path -LiteralPath $Plans)) { throw '-Feedback brokennes needs -Plans (a JSON list of {name, frames, input})' }
    # (Windows PowerShell's ConvertFrom-Json hands a JSON list over as one object: unroll it)
    $planList = @((Get-Content -LiteralPath $Plans -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
    # The runtime writes the SRAM feedback header from the ROM's title, which m30build replaces after injection (EmulatorName);
    # the injector's reader looks for the core's own title, so each .srm gets the core's title back before it is read.
    $coreTitle = [byte[]]([System.IO.File]::ReadAllBytes((Join-Path $(if ($CoreDir) { $CoreDir } else { Join-Path $Pipe 'out\core' }) 'Project_Nested.smc'))[0xFFC0..(0xFFC0 + 20)])
    $romTitle = [System.Text.Encoding]::ASCII.GetBytes($(if ($SnesTitle) { $SnesTitle.PadRight(21) } else { '' }))
    $fbDir = Join-Path $Work 'feedback'
    for ($round = 1; $round -le $Rounds; $round++) {
        $rd = Join-Path $fbDir ("round$round"); New-Item -ItemType Directory -Force -Path $rd | Out-Null
        $srms = @()
        foreach ($pl in $planList) {
            $srm = Join-Path $rd ([string]$pl.name + '.srm')
            if (Test-Path -LiteralPath $srm) { Remove-Item -LiteralPath $srm }
            & $Workshop --snesrun --rom $Out --frames ([string]$pl.frames) --input ([string]$pl.input) --sram $srm | Out-Null
            if (-not (Test-Path -LiteralPath $srm)) { throw "the Workshop wrote no SRAM for plan $($pl.name)" }
            $b = [System.IO.File]::ReadAllBytes($srm)
            if ($romTitle.Length -gt 0) {
                $hit = -1
                for ($i = 0; $i -le $b.Length - $romTitle.Length -and $hit -lt 0; $i += 1) {
                    if ($b[$i] -ne $romTitle[0]) { continue }
                    $ok = $true; for ($j = 1; $j -lt $romTitle.Length; $j++) { if ($b[$i + $j] -ne $romTitle[$j]) { $ok = $false; break } }
                    if ($ok) { $hit = $i }
                }
                if ($hit -ge 0) { [Array]::Copy($coreTitle, 0, $b, $hit, 21); [System.IO.File]::WriteAllBytes($srm, $b) }
            }
            $srms += $srm
        }
        $got = Join-Path $rd 'calls.txt'
        & $shell @ps (Join-Path $Pipe 'srm2calls.ps1') -Nes $Nes -Srm ($srms -join ';') -Out $got -Python $Python | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "srm2calls.ps1 failed (exit $LASTEXITCODE)" }
        $have = @(Get-Content -LiteralPath $calls | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() })
        $add = @(Get-Content -LiteralPath $got | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() } | Where-Object { $have -notcontains $_ })
        Write-Host ("nested-build: feedback round {0}: {1} known entry points, {2} new from {3} plan(s)" -f $round, $have.Count, $add.Count, $planList.Count)
        if ($add.Count -eq 0) { break }
        [System.IO.File]::AppendAllText($calls, ("# +{0} from BrokenNes feedback round {1}`r`n" -f $add.Count, $round) + (($add | ForEach-Object { "$_`r`n" }) -join ''))
        & $shell @a
        if ($LASTEXITCODE -ne 0) { throw "m30build.ps1 failed (exit $LASTEXITCODE); see $Out.build.log" }
    }
}
if (-not (Test-Path -LiteralPath $Out)) { throw "m30build reported success but wrote no ROM at $Out" }
# The post-build steps are what make saves and the DMC wait work; a build that silently skipped one is wrong, so check the record.
$bjPath = "$Out.build.json"
if (-not (Test-Path -LiteralPath $bjPath)) { throw "m30build wrote no build record ($bjPath)" }
$bj = Get-Content -LiteralPath $bjPath -Raw | ConvertFrom-Json
$ran = @(); if ($bj.PSObject.Properties['post_build'] -and $bj.post_build) { $ran = @($bj.post_build | ForEach-Object { if ($_.PSObject.Properties['name']) { [string]$_.name } else { [string]$_ } }) }
foreach ($want in $postSteps) { if ($ran -notcontains [string]$want.name) { throw "the post-build step '$($want.name)' was requested but is not in the build record (ran: $($ran -join ', '))" } }
Write-Host ("nested-build: post-build steps run: " + ($ran -join ', '))
exit 0

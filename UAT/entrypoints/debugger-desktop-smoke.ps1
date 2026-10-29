<#
.SYNOPSIS
  The debugger in the real desktop app, over HTTP (BrokenDev milestone D1). Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app twice with a scratch settings folder (BROKENNES_CONFIG_DIR: the user's config.json is never read or written),
  on the debugger's test ROMs (written by Workshop --debug-test --write-roms):
    READONLY  with the default config the debugger's state is readable (GET /api/debug/state, registers, memory by spec) and its control
              routes answer 403 (control is opted into: "enableDebuggerControl").
    MEMORY    the /api/memory routes know the running console: SNES WRAM / VRAM / CGRAM / OAM / ARAM / REGS / BUS (the test ROM's DMA put
              11 22 33 44 at VRAM $2000), Game Boy WRAM / HRAM / OAM / IO; the NES keeps its domains.
    CONTROL   with "enableDebuggerControl": true: attach, load symbols, break on a symbol (NES), the call stack, the trace, step over,
              a mirror watch (SNES $7E0013 written through $80:0013), a banked symbol (Game Boy bank 2), detach (nothing armed, frames run).
    LIVE      while parked at a breakpoint the app still answers (GET /api/console), and loading another game lets the parked emulation go.
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\debugger-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -Workshop C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Workshop,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_debugger_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'debugger-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null, [switch]$Raw) {
    if ($Raw) { return Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -Raw -TimeoutSec 60 }
    Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60
}
function Load([int]$procId, [string]$rom) {
    Api $procId POST '/api/emulator/load-rom' @{ path = $rom } | Out-Null
    $end = (Get-Date).AddSeconds(10)
    do { Start-Sleep -Milliseconds 300; $s = Api $procId GET '/api/debug/state' } while ((-not $s.success) -and (Get-Date) -lt $end)
    Start-Sleep -Milliseconds 500
}
function Start-App([string]$configJson) {
    $cfgDir = Join-Path $WorkDir 'config'
    if (Test-Path -LiteralPath $cfgDir) { Remove-Item -LiteralPath $cfgDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    if ($configJson) { Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value $configJson -Encoding UTF8 }
    $env:BROKENNES_CONFIG_DIR = $cfgDir
    # battery saves and bug bundles too: debugtest.nes has a battery, and without this its save went to %APPDATA%\BrokenNes\BatterySaves
    $env:BROKENNES_SAVES_DIR = Join-Path $WorkDir 'saves'; $env:BROKENNES_BUGS_DIR = Join-Path $WorkDir 'bugs'
    $i = Start-BrokenNesInstance -ExePath $Exe
    Start-Sleep -Seconds 4   # the app loads its embedded test.nes a few seconds after the API answers
    return $i
}

$inst = $null
$savedConfigDir = $env:BROKENNES_CONFIG_DIR
$savedSavesDir = $env:BROKENNES_SAVES_DIR; $savedBugsDir = $env:BROKENNES_BUGS_DIR
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $roms = Join-Path $WorkDir 'roms'
    $p = Start-Process -FilePath $Workshop -ArgumentList @('--debug-test', '--write-roms', "`"$roms`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $WorkDir 'write-roms.txt')
    if ($p.ExitCode -ne 0) { throw "Workshop --debug-test --write-roms failed ($($p.ExitCode))" }
    $labels = Get-Content -LiteralPath (Join-Path $roms 'labels.json') -Raw | ConvertFrom-Json

    # ---------------------------------------------------------------- READONLY: the default config
    $inst = Start-App $null
    $id = $inst.ProcessId
    Load $id (Join-Path $roms 'debugtest.sfc')
    $st = Api $id GET '/api/debug/state'
    Check 'readonly_state' ($st.success -and $st.console -eq 'snes' -and -not $st.attached -and -not $st.controlEnabled -and @($st.cpus).Count -eq 2) "console $($st.console), cpus $(@($st.cpus | ForEach-Object id) -join ', '), attached $($st.attached), control $($st.controlEnabled)"
    $mem = Api $id GET '/api/debug/memory?spec=vram:$2000&length=4'
    Check 'readonly_memory_by_spec' ($mem.success -and (($mem.data -join ',') -eq '17,34,51,68')) "vram:`$2000 = $($mem.data -join ' ') ($($mem.resolved))"
    $r = Api $id POST '/api/debug/attach' $null -Raw
    Check 'readonly_control_refused' ($r.StatusCode -eq 403 -and $r.Body.error -like '*enableDebuggerControl*') "attach answered $($r.StatusCode): $($r.Body.error)"
    $r = Api $id POST '/api/debug/breakpoints' @{ spec = '$8000' } -Raw
    Check 'readonly_breakpoint_refused' ($r.StatusCode -eq 403) "POST /api/debug/breakpoints answered $($r.StatusCode)"
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null

    # ---------------------------------------------------------------- CONTROL: a scratch config with control on
    $inst = Start-App '{ "enableDebuggerControl": true }'
    $id = $inst.ProcessId

    # MEMORY: the consoles' domains through the old routes
    Load $id (Join-Path $roms 'debugtest.sfc')
    $doms = Api $id GET '/api/memory/domains'
    $names = @($doms.domains | ForEach-Object name)
    $vr = Api $id GET '/api/memory/peek-range?domain=VRAM&address=8192&length=4'
    Check 'memory_snes_domains' ($doms.console -eq 'snes' -and ($names -contains 'WRAM') -and ($names -contains 'VRAM') -and ($names -contains 'CGRAM') -and ($names -contains 'OAM') -and ($names -contains 'ARAM') -and ($names -contains 'REGS') -and ($names -contains 'BUS')) "console $($doms.console): $($names -join ', ')"
    Check 'memory_snes_vram' ((($vr.data -join ',') -eq '17,34,51,68')) "VRAM `$2000: $($vr.data -join ' ')"
    $pk = Api $id POST '/api/memory/poke' @{ domain = 'WRAM'; address = 0x1F00; value = 0x5A }
    $pv = Api $id GET '/api/memory/peek?domain=WRAM&address=7936'
    Check 'memory_snes_poke' ($pk.success -and $pv.value -eq 0x5A) "WRAM `$1F00 = $($pv.value)"

    # SNES watch through a mirror
    $a = Api $id POST '/api/debug/attach'
    $sy = Api $id POST '/api/debug/symbols' @{ path = (Join-Path $roms 'debugtest.sym') }
    $bp = Api $id POST '/api/debug/breakpoints' @{ spec = 'mirror_target'; kind = 'write' }
    $w = Api $id GET '/api/debug/wait?timeoutMs=5000'
    Check 'snes_mirror_watch' ($a.success -and $sy.loaded -gt 0 -and $w.parked -and $w.lastBreak.address -eq 0x800013 -and $bp.breakpoint.resolved -eq 'wram:$13') "attach $($a.success), $($sy.loaded) symbols, $($bp.breakpoint.resolved); $($w.lastBreak.text)"
    $cs = Api $id GET '/api/debug/state'
    $con = Api $id GET '/api/console'
    Check 'live_app_answers_while_parked' ($cs.parked -and $con.success -and $con.status.console -eq 'snes') "parked $($cs.parked); /api/console: $($con.status.description)"
    $spc = Api $id GET '/api/debug/registers?cpu=spc'
    Check 'snes_spc_registers' ($spc.success -and $spc.cpu.name -eq 'SPC700' -and $spc.cpu.registers.PC) "SPC700 PC $($spc.cpu.registers.PC)"
    $dt = Api $id POST '/api/debug/detach'
    Start-Sleep -Milliseconds 800
    $s1 = Api $id GET '/api/debug/state'; Start-Sleep -Milliseconds 600; $s2 = Api $id GET '/api/debug/state'
    Check 'snes_detach_runs_free' ($dt.detached -and -not $s2.attached -and -not $s2.armed -and $s2.frame -gt $s1.frame) "detached; frames $($s1.frame) -> $($s2.frame)"

    # NES: a breakpoint on a NESFab-style symbol, the call stack, the trace, step over
    Load $id (Join-Path $roms 'debugtest.nes')
    $doms = Api $id GET '/api/memory/domains'
    Check 'memory_nes_unchanged' (@($doms.domains | ForEach-Object name) -contains 'System RAM') "NES domains: $(@($doms.domains | ForEach-Object name) -join ', ')"
    Api $id POST '/api/debug/attach' | Out-Null
    Api $id POST '/api/debug/symbols' @{ path = (Join-Path $roms 'debugtest.mlb') } | Out-Null
    $bp = Api $id POST '/api/debug/breakpoints' @{ spec = 'sub2'; kind = 'exec' }
    $w = Api $id GET '/api/debug/wait?timeoutMs=5000'
    $stk = Api $id GET '/api/debug/callstack'
    Check 'nes_break_and_stack' ($w.parked -and $w.lastBreak.pc -eq $labels.nes.sub2 -and @($stk.frames).Count -eq 2 -and $stk.frames[1].targetSymbol -eq 'sub2') "$($w.lastBreak.text); stack: $(@($stk.frames | ForEach-Object text) -join ' | ')"
    $tr = Api $id GET '/api/debug/trace?count=3'
    Check 'nes_trace' (@($tr.lines).Count -eq 3 -and $tr.lines[1] -like '*JSR sub2*') ($tr.lines -join ' || ')
    Api $id DELETE "/api/debug/breakpoints/$($bp.breakpoint.id)" | Out-Null
    Api $id POST '/api/debug/step' @{ mode = 'out' } | Out-Null
    $so = Api $id POST '/api/debug/step' @{ mode = 'out' }
    Check 'nes_step_out' ($so.parked -and $so.lastBreak.pc -eq $labels.nes.inc10) "$($so.lastBreak.text)"
    $dis = Api $id GET "/api/debug/disasm?address=$('{0:X4}' -f $labels.nes.loop)&count=2"
    Check 'nes_disasm' ($dis.lines[0].text -eq 'JSR sub1' -and $dis.lines[0].label -eq 'loop') "$($dis.lines[0].hex) $($dis.lines[0].text) ; $($dis.lines[0].label)"

    # LIVE: loading another game while parked lets the parked emulation go (no hang)
    Api $id POST '/api/debug/pause' | Out-Null
    $t0 = Get-Date
    Load $id (Join-Path $roms 'debugtest.gb')
    $ld = ((Get-Date) - $t0).TotalSeconds
    $doms = Api $id GET '/api/memory/domains'
    $gnames = @($doms.domains | ForEach-Object name)
    Check 'live_load_while_parked' ($ld -lt 15 -and $doms.console -eq 'gb') "the Game Boy ROM loaded in $([math]::Round($ld, 1)) s while the NES was parked"
    Check 'memory_gb_domains' (($gnames -contains 'WRAM') -and ($gnames -contains 'HRAM') -and ($gnames -contains 'OAM') -and ($gnames -contains 'IO') -and ($gnames -contains 'BUS')) "console $($doms.console): $($gnames -join ', ')"

    # Game Boy: a banked symbol (GBDK .noi)
    Api $id POST '/api/debug/attach' | Out-Null
    Api $id POST '/api/debug/symbols' @{ path = (Join-Path $roms 'debugtest_gb.noi') } | Out-Null
    $bp = Api $id POST '/api/debug/breakpoints' @{ spec = '_bank2_func'; kind = 'exec' }
    $w = Api $id GET '/api/debug/wait?timeoutMs=5000'
    Check 'gb_banked_symbol' ($w.parked -and $w.lastBreak.pc -eq 0x4000 -and $bp.breakpoint.resolved -eq 'rom:$8000') "$($bp.breakpoint.resolved); $($w.lastBreak.text)"
    Api $id POST '/api/debug/detach' | Out-Null

    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    $env:BROKENNES_CONFIG_DIR = $savedConfigDir
    $env:BROKENNES_SAVES_DIR = $savedSavesDir; $env:BROKENNES_BUGS_DIR = $savedBugsDir
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }

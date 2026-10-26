<#
.SYNOPSIS
  The debug windows in the real desktop app (BrokenDev milestone D8). Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with scratch BROKENNES_CONFIG_DIR, BROKENNES_SAVES_DIR, BROKENNES_BUGS_DIR and BROKENDEV_HOME (the user's
  %APPDATA%\BrokenNes is listed before and after and must be unchanged) and the debugger's test ROMs (Workshop --debug-test --write-roms). The
  config keeps "enableDebuggerControl" off: the windows are the user's own UI and work without it, while the HTTP control routes stay refused.
  Through UI Automation it opens Emulator > Debug Windows > Debugger and drives it; the HTTP API (/api/debug/state, /api/debug/callstack) is
  the cross-check. Each check has its negative control:
    CLOSED    before the window opens nothing is attached or armed.
    DEBUGGER  Pause parks the NES; the window's registers and the disassembly's PC line agree with the API (and a PC that is not the API's
              would not match: the check compares the text, not "something is there"); the symbols beside the ROM load by themselves; a
              breakpoint on sub2 typed into the window stops there with sub1 > sub2 in the window's call stack; Step Over moves the PC; the
              breakpoint's removal empties the API's list; the HTTP control routes still answer 403 (the window opened no gate).
    CLOSE     closing the window detaches its debugger: nothing armed, frames run.
    SNES / GB the same window on the SNES (the CPU picker offers the SPC700, whose registers show) and the Game Boy.
  Rule zero (UAT\README.md): never kill by process name; only the PID started here is touched.

.EXAMPLE
  powershell -File UAT\entrypoints\debug-windows-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -Workshop C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Workshop,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_debug_windows')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'debug-windows-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null, [switch]$Raw) {
    if ($Raw) { return Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -Raw -TimeoutSec 60 }
    Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60
}
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Load([int]$procId, [string]$rom) {
    Api $procId POST '/api/emulator/load-rom' @{ path = $rom } | Out-Null
    $end = (Get-Date).AddSeconds(10)
    do { Start-Sleep -Milliseconds 300; $s = Api $procId GET '/api/debug/state' } while ((-not $s.success) -and (Get-Date) -lt $end)
    Start-Sleep -Milliseconds 700
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function WaitFor([scriptblock]$probe, [int]$ms = 8000) {
    $end = (Get-Date).AddMilliseconds($ms)
    do { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 150 } while ((Get-Date) -lt $end)
    return $null
}
function FindByName($root, [string]$name, $scope = $TS::Descendants) { $root.FindFirst($scope, (Cond $AE::NameProperty $name)) }
function FindById($root, [string]$id) { $root.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Expand-El($el) { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
function TextOf($el) {
    $vp = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
    return $el.Current.Name
}
function SetText($el, [string]$t) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($t) }
function Items($el) {
    @($el.FindAll($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name })
}
function Click([string]$id) { Invoke-El (FindById $script:win $id); Start-Sleep -Milliseconds 400 }

# Opens Emulator > Debug Windows > Debugger through the menu; returns the window's element.
function Open-Debugger([int]$procId) {
    $main = WaitFor { $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))))) }
    if (-not $main) { throw 'the main window was not found' }
    $emu = WaitFor { FindByName $main 'Emulator' }
    Expand-El $emu; Start-Sleep -Milliseconds 400
    $sub = WaitFor { FindByName $AE::RootElement 'Debug Windows' }
    if (-not $sub) { $sub = WaitFor { FindByName $main 'Debug Windows' } }
    Expand-El $sub; Start-Sleep -Milliseconds 400
    $item = WaitFor { FindByName $AE::RootElement 'Debugger (registers, disassembly, breakpoints, step)' }
    if (-not $item) { throw 'the Debugger menu item was not found' }
    Invoke-El $item
    $w = WaitFor { $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::NameProperty 'BrokenNes Debugger')))) }
    if (-not $w) { throw 'the debugger window did not open' }
    Start-Sleep -Milliseconds 500
    return $w
}
function Close-Window($w) { $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep -Milliseconds 800 }
function RegPc([string]$regsText) { if ($regsText -match 'PC (\$[0-9A-F]+)') { return $Matches[1] } else { return '' } }
function PcLine($w) { (Items (FindById $w 'disassembly')) | Where-Object { $_ -like '>*' } | Select-Object -First 1 }

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $saveDir = Join-Path $WorkDir 'saves'; $bdHome = Join-Path $WorkDir 'bdhome'; $bugs = Join-Path $WorkDir 'bugs'
    New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir, $bdHome, $bugs | Out-Null
    $roms = Join-Path $WorkDir 'roms'
    $p = Start-Process -FilePath $Workshop -ArgumentList @('--debug-test', '--write-roms', "`"$roms`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $WorkDir 'write-roms.txt')
    if ($p.ExitCode -ne 0) { throw "Workshop --debug-test --write-roms failed ($($p.ExitCode))" }
    $labels = Get-Content -LiteralPath (Join-Path $roms 'labels.json') -Raw | ConvertFrom-Json
    Set-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Value '{ "bugCaptureEnabled": false }' -Encoding UTF8

    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = $saveDir; $env:BROKENDEV_HOME = $bdHome; $env:BROKENNES_BUGS_DIR = $bugs
    $inst = Start-BrokenNesInstance -ExePath $Exe
    $id = $inst.ProcessId
    Start-Sleep -Seconds 4   # the app loads its embedded test.nes a few seconds after the API answers

    # ---------------------------------------------------------------- NES
    Load $id (Join-Path $roms 'debugtest.nes')
    $s0 = Api $id GET '/api/debug/state'
    Check 'closed_nothing_attached' ($s0.success -and -not $s0.attached -and -not $s0.armed -and -not $s0.controlEnabled) "before the window: attached $($s0.attached), armed $($s0.armed), control $($s0.controlEnabled)"

    $win = Open-Debugger $id
    $sOpen = Api $id GET '/api/debug/state'
    Check 'open_reads_only' (-not $sOpen.attached -and -not $sOpen.armed) "the window open, nothing pressed: attached $($sOpen.attached), armed $($sOpen.armed)"

    Click 'pause'
    $st = WaitFor { $x = Api $id GET '/api/debug/state'; if ($x.parked) { $x } }
    if (-not $st) { $st = Api $id GET '/api/debug/state' }
    Start-Sleep -Milliseconds 400
    $regs = TextOf (FindById $win 'registers')
    $apiPc = $st.cpus[0].pcHex
    Check 'pause_parks' ($st.attached -and $st.parked) "Pause: attached $($st.attached), parked $($st.parked) at $apiPc"
    Check 'registers_match_api' ((RegPc $regs) -eq $apiPc -and $regs -match 'A = \$') "window PC $(RegPc $regs), API PC $apiPc; $(($regs -split "`r?`n" | Select-Object -First 3) -join ' / ')"
    $pl = PcLine $win
    Check 'disassembly_pc_line' ($pl -and $pl.Contains($apiPc)) "the marked line: $pl"
    $wrong = '$' + ('{0:X4}' -f (([Convert]::ToInt32($apiPc.TrimStart('$'), 16) + 0x111) -band 0xFFFF))
    Check 'disassembly_pc_line_negative' (-not $pl.Contains($wrong)) "the marked line does not name a PC that is not the API's ($wrong)"
    Check 'symbols_beside_rom' ($st.symbols -gt 0 -or (Api $id GET '/api/debug/state').symbols -gt 0) "$((Api $id GET '/api/debug/state').symbols) symbols loaded from debugtest.mlb by themselves"

    SetText (FindById $win 'bpspec') 'sub2'
    Click 'addbreakpoint'
    $bpl = Api $id GET '/api/debug/breakpoints'
    Check 'breakpoint_added' (@($bpl.breakpoints).Count -eq 1 -and $bpl.breakpoints[0].spec -eq 'sub2') "API breakpoints: $(@($bpl.breakpoints | ForEach-Object text) -join ' | ')"
    Click 'continue'
    $w2 = Api $id GET '/api/debug/wait?timeoutMs=5000'
    Start-Sleep -Milliseconds 500
    $stackItems = Items (FindById $win 'callstack')
    $regs2 = TextOf (FindById $win 'registers')
    Check 'break_on_sub2' ($w2.parked -and $w2.lastBreak.pc -eq $labels.nes.sub2 -and (RegPc $regs2) -eq ('$' + ('{0:X4}' -f $labels.nes.sub2))) "parked at $('{0:X4}' -f $w2.lastBreak.pc) (sub2 = $('{0:X4}' -f $labels.nes.sub2)); window PC $(RegPc $regs2)"
    Check 'window_call_stack' ($stackItems.Count -eq 2 -and $stackItems[0] -like '*-> sub2*' -and $stackItems[1] -like '*-> sub1*') "the window's stack: $($stackItems -join ' | ')"
    $pcBefore = RegPc $regs2
    Click 'stepover'
    Start-Sleep -Milliseconds 400
    $pcAfter = RegPc (TextOf (FindById $win 'registers'))
    $sAfter = Api $id GET '/api/debug/state'
    Check 'step_over_moves' ($sAfter.parked -and $pcAfter -ne $pcBefore -and $pcAfter -eq $sAfter.cpus[0].pcHex) "Step Over: $pcBefore -> $pcAfter (API $($sAfter.cpus[0].pcHex))"
    $bpList = FindById $win 'breakpoints'
    $first = $bpList.FindFirst($TS::Children, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))
    $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Click 'removebreakpoint'
    $bpl2 = Api $id GET '/api/debug/breakpoints'
    Check 'breakpoint_removed' (@($bpl2.breakpoints).Count -eq 0) "API breakpoints after Remove: $(@($bpl2.breakpoints).Count)"
    $r403 = Api $id POST '/api/debug/breakpoints' @{ spec = '$8000' } -Raw
    Check 'http_control_still_refused' ($r403.StatusCode -eq 403) "POST /api/debug/breakpoints with the window attached: $($r403.StatusCode)"

    Close-Window $win
    $c1 = Api $id GET '/api/debug/state'; Start-Sleep -Milliseconds 700; $c2 = Api $id GET '/api/debug/state'
    Check 'close_detaches' (-not $c2.attached -and -not $c2.armed -and -not $c2.parked -and $c2.frame -gt $c1.frame) "closed: attached $($c2.attached), armed $($c2.armed), frames $($c1.frame) -> $($c2.frame)"

    # ---------------------------------------------------------------- SNES (the SPC700 in the CPU picker)
    Load $id (Join-Path $roms 'debugtest.sfc')
    $win = Open-Debugger $id
    Click 'pause'
    $ss = WaitFor { $x = Api $id GET '/api/debug/state'; if ($x.parked) { $x } }
    Start-Sleep -Milliseconds 400
    $sregs = TextOf (FindById $win 'registers')
    $spc = ($ss.cpus | Where-Object id -eq 'spc')
    Check 'snes_registers_match_api' ($ss.console -eq 'snes' -and (RegPc $sregs) -eq $ss.cpus[0].pcHex) "65816: window PC $(RegPc $sregs), API $($ss.cpus[0].pcHex)"
    $cpuBox = FindById $win 'cpu'
    Expand-El $cpuBox; Start-Sleep -Milliseconds 300
    $spcItem = WaitFor { FindByName $cpuBox 'spc' }
    if ($spcItem) { $spcItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    $cpuBox.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Start-Sleep -Milliseconds 500
    $spcRegs = TextOf (FindById $win 'registers')
    Check 'snes_spc_registers' ($spcRegs -like 'SPC700*' -and (RegPc $spcRegs) -eq $spc.pcHex) "SPC700: window PC $(RegPc $spcRegs), API $($spc.pcHex)"
    Close-Window $win
    $sc = Api $id GET '/api/debug/state'
    Check 'snes_close_detaches' (-not $sc.attached -and -not $sc.armed) "closed: attached $($sc.attached), armed $($sc.armed)"

    # ---------------------------------------------------------------- Game Boy
    Load $id (Join-Path $roms 'debugtest.gb')
    $win = Open-Debugger $id
    Click 'pause'
    $gs = WaitFor { $x = Api $id GET '/api/debug/state'; if ($x.parked) { $x } }
    Start-Sleep -Milliseconds 400
    $gregs = TextOf (FindById $win 'registers')
    $gpl = PcLine $win
    Check 'gb_registers_match_api' ($gs.console -eq 'gb' -and (RegPc $gregs) -eq $gs.cpus[0].pcHex -and $gpl.Contains($gs.cpus[0].pcHex)) "SM83: window PC $(RegPc $gregs), API $($gs.cpus[0].pcHex); $gpl"
    Close-Window $win
    $gc = Api $id GET '/api/debug/state'
    Check 'gb_close_detaches' (-not $gc.attached -and -not $gc.armed) "closed: attached $($gc.attached), armed $($gc.armed)"

    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] -ErrorAction SilentlyContinue; if (-not $saved[$k]) { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue } }
}
$after = Listing $userDir
Check 'user_appdata_unchanged' ($before -eq $after) "the user's $userDir $(if ($before -eq $after) { 'is unchanged' } else { 'CHANGED' })"
if ($before -ne $after) { $result.pass = $false }
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }

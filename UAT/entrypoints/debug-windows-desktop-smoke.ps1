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
              Only the NES's own kind of symbol file loads (the SNES test ROM's debugtest.sym beside debugtest.nes does not).
    CLOSE     closing the window detaches its debugger: nothing armed, frames run.
    MEMORY    Emulator > Debug Windows > Memory: a PRG row agrees with /api/memory/peek-range (and is not another row's bytes); RAM moves
              live while PRG does not; a watch on a symbol resolves and reads, an unknown one says so; nothing is attached. SNES: the
              domains, VRAM $2000 holds the test ROM's DMA bytes and $2010 does not. Game Boy: the domains, a BUS row agrees with the API.
    PALETTES  Emulator > Debug Windows > Palettes. NES: 8 palettes, every index through the 2C02 colours, $3F10 mirroring $3F00 (the NES's
              HTTP routes have no PPU space, so the decoding is the check). SNES: a CGRAM word poked through the API (7C00) shows in row 0
              exactly as the API reads CGRAM, and not in row 1. Game Boy: BGP / OBP0 / OBP1 as the API reads them, shades decoded.
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

# Opens Emulator > Debug Windows > <item> through the menu; returns the window's element.
function Open-Debugger([int]$procId) { Open-DebugWindow $procId 'Debugger (registers, disassembly, breakpoints, step)' 'BrokenNes Debugger' }
function Open-DebugWindow([int]$procId, [string]$itemName, [string]$title) {
    $main = WaitFor { $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))))) }
    if (-not $main) { throw 'the main window was not found' }
    $emu = WaitFor { FindByName $main 'Emulator' }
    Expand-El $emu; Start-Sleep -Milliseconds 400
    $sub = WaitFor { FindByName $AE::RootElement 'Debug Windows' }
    if (-not $sub) { $sub = WaitFor { FindByName $main 'Debug Windows' } }
    Expand-El $sub; Start-Sleep -Milliseconds 400
    $item = WaitFor { FindByName $AE::RootElement $itemName }
    if (-not $item) { throw "the menu item '$itemName' was not found" }
    Invoke-El $item
    $w = WaitFor { $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::NameProperty $title)))) }
    if (-not $w) { throw "the window '$title' did not open" }
    Start-Sleep -Milliseconds 500
    return $w
}
function Close-Window($w) { $w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep -Milliseconds 800 }
function RegPc([string]$regsText) { if ($regsText -match 'PC (\$[0-9A-F]+)') { return $Matches[1] } else { return '' } }
function Open-Memory([int]$procId) { Open-DebugWindow $procId 'Memory (live hex, watches)' 'BrokenNes Memory' }
function Pick-Domain($w, [string]$id) {
    $box = FindById $w 'domain'
    Expand-El $box; Start-Sleep -Milliseconds 300
    $it = @($box.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))) | Where-Object { $_.Current.Name -like "$id (*" } | Select-Object -First 1
    if ($it) { $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Start-Sleep -Milliseconds 400
    return [bool]$it
}
function Domains($w) {
    $box = FindById $w 'domain'
    Expand-El $box; Start-Sleep -Milliseconds 300
    $n = @($box.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { ($_.Current.Name -split ' ')[0] })
    $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 200
    return $n
}
function GoTo($w, [string]$where) { SetText (FindById $w 'goto') $where; Invoke-El (FindById $w 'gobutton'); Start-Sleep -Milliseconds 400 }
function HexRow($w, [int]$i) { (Items (FindById $w 'hex'))[$i] }
function RowBytes([string]$row) { if ($row -match '^[0-9A-F]+: (.{49})') { return (($Matches[1] -replace '\s+', ' ').Trim()) } else { return '' } }
function ApiRow([int]$procId, [string]$domain, [int]$address) { ((Api $procId GET "/api/memory/peek-range?domain=$domain&address=$address&length=16").data | ForEach-Object { '{0:X2}' -f [int]$_ }) -join ' ' }
function Open-Palettes([int]$procId) { Open-DebugWindow $procId 'Palettes' 'BrokenNes Palettes' }
function PalLine($w, [string]$prefix) { (Items (FindById $w 'palettes')) | Where-Object { $_ -like "$prefix*" } | Select-Object -First 1 }
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
    $sf = Api $id GET '/api/debug/state'
    $files = @($sf.symbolFiles | ForEach-Object { Split-Path -Leaf $_ })
    Check 'symbols_beside_rom' ($sf.symbols -gt 0 -and ($files -join ',') -eq 'debugtest.mlb') "$($sf.symbols) symbols loaded by themselves from $($files -join ', ')"
    Check 'symbols_other_console_negative' (-not ($files -contains 'debugtest.sym')) "the SNES test ROM's debugtest.sym beside debugtest.nes is not loaded into the NES debugger"

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

    # ---------------------------------------------------------------- MEMORY (NES)
    $win = Open-Memory $id
    $ok = Pick-Domain $win 'PRG'
    GoTo $win '20'
    $r = HexRow $win 0; $apiRow = ApiRow $id 'PRG ROM' 0x20; $apiRow0 = ApiRow $id 'PRG ROM' 0
    Check 'memory_prg_row' ($ok -and $r -like '0020:*' -and (RowBytes $r) -eq $apiRow) "PRG row: $r ; API PRG `$20: $apiRow"
    Check 'memory_prg_row_negative' ($apiRow0 -eq $apiRow -or (RowBytes $r) -ne $apiRow0) "the row is not PRG `$00's bytes ($apiRow0)"
    Pick-Domain $win 'RAM' | Out-Null
    $a1 = HexRow $win 1; Start-Sleep -Milliseconds 600; $a2 = HexRow $win 1
    $p1 = Items (FindById $win 'hex'); Pick-Domain $win 'PRG' | Out-Null; $q1 = HexRow $win 0; Start-Sleep -Milliseconds 600; $q2 = HexRow $win 0
    Check 'memory_live' ($a1 -like '0010:*' -and $a1 -ne $a2) "RAM `$10 (inc10 counts) moves: '$a1' -> '$a2'"
    Check 'memory_live_negative' ($q1 -eq $q2) "PRG does not: '$q1'"
    SetText (FindById $win 'watchspec') 'mirror_target'; Invoke-El (FindById $win 'addwatch'); Start-Sleep -Milliseconds 400
    SetText (FindById $win 'watchspec') 'no_such_symbol'; Invoke-El (FindById $win 'addwatch'); Start-Sleep -Milliseconds 400
    $wl = Items (FindById $win 'watches')
    Check 'memory_watch_symbol' ($wl.Count -eq 2 -and $wl[0] -like 'mirror_target (*$12*' -and $wl[0] -match ': [0-9A-F]{2} = \d+$') "watch: $($wl[0])"
    Check 'memory_watch_unknown_negative' ($wl[1] -like 'no_such_symbol:*' -and $wl[1] -notmatch '= \d+$') "watch: $($wl[1])"
    $sm = Api $id GET '/api/debug/state'
    Check 'memory_reads_only' (-not $sm.attached -and -not $sm.armed) "the memory window open: attached $($sm.attached), armed $($sm.armed)"
    Close-Window $win

    # ---------------------------------------------------------------- PALETTES (NES): palette RAM (PPU $3F00-$3F1F) through the NES colours
    # (the NES's old HTTP memory routes have no PPU space, and pokes of $2006 / $2007 meet the game's address latch, so the NES check is the
    # decoding: every index through the 2C02 table, and the hardware's mirror of $3F10 onto $3F00)
    $win = Open-Palettes $id
    $nesRgb = @('666666','002A88','1412A7','3B00A4','5C007E','6E0040','6C0600','561D00','333500','0B4800','005200','004F08','00404D','000000','000000','000000',
                'ADADAD','155FD9','4240FF','7527FE','A01ACC','B71E7B','B53120','994E00','6B6D00','388700','0C9300','008F32','007C8D','000000','000000','000000',
                'FFFEFF','64B0FF','9290FF','C676FF','F36AFF','FE6ECC','FE8170','EA9E22','BCBE00','88D800','5CE430','45E082','48CDDE','4F4F4F','000000','000000',
                'FFFEFF','C0DFFF','D3D2FF','E8C8FF','FBC2FF','FEC4EA','FECCC5','F7D8A5','E4E594','CFEF96','BDF4AB','B3F3CC','B5EBF2','B8B8B8','000000','000000')
    $nl = @(Items (FindById $win 'palettes'))
    $bad = @(); $labelsOk = $true
    for ($k = 0; $k -lt $nl.Count; $k++) {
        $want = $(if ($k -lt 4) { "BG$k" } else { "SP$($k - 4)" })
        if ($nl[$k] -notlike "${want}:*") { $labelsOk = $false }
        if ($nl[$k] -match ': (\$[0-9A-F]{2}) (\$[0-9A-F]{2}) (\$[0-9A-F]{2}) (\$[0-9A-F]{2}) \| #(\w{6}) #(\w{6}) #(\w{6}) #(\w{6})$') {
            for ($c = 1; $c -le 4; $c++) { if ($nesRgb[[Convert]::ToInt32($Matches[$c].TrimStart('$'), 16)] -ne $Matches[$c + 4]) { $bad += "$want[$($c - 1)]" } }
        } else { $bad += "$want (format)" }
    }
    Check 'palettes_nes' ($nl.Count -eq 8 -and $labelsOk -and $bad.Count -eq 0) "8 palettes, every index through the 2C02 colours: $($nl[0]) ; $($nl[4])"
    $bg0 = ($nl[0] -split ' ')[1]; $sp0 = ($nl[4] -split ' ')[1]
    Check 'palettes_nes_mirror' ($bg0 -eq $sp0) "SP0's colour 0 (`$3F10) mirrors BG0's (`$3F00): $sp0 = $bg0"
    $wrongRow = $nl[0] -replace '#000000', '#FFFFFF'
    Check 'palettes_nes_negative' ($wrongRow -eq $nl[0] -or -not ($wrongRow -match '\| #(\w{6})' -and $nesRgb[[Convert]::ToInt32(($bg0).TrimStart('$'), 16)] -eq $Matches[1])) "a colour that is not the index's would fail the decoding check ($wrongRow)"
    Close-Window $win
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
    $win = Open-Memory $id
    $sd = Domains $win
    Check 'memory_snes_domains' (($sd -contains 'WRAM') -and ($sd -contains 'VRAM') -and ($sd -contains 'CGRAM') -and ($sd -contains 'OAM') -and ($sd -contains 'ARAM')) "SNES domains: $($sd -join ', ')"
    Pick-Domain $win 'VRAM' | Out-Null
    GoTo $win '2000'; $v0 = HexRow $win 0
    Check 'memory_snes_vram' ($v0 -like '2000:*' -and (RowBytes $v0) -like '11 22 33 44*') "VRAM row: $v0 (the test ROM's DMA put 11 22 33 44 at `$2000)"
    GoTo $win '2010'; $v1 = HexRow $win 0
    Check 'memory_snes_vram_negative' ($v1 -like '2010:*' -and (RowBytes $v1) -notlike '11 22 33 44*') "VRAM `$2010: $v1"
    Close-Window $win
    $win = Open-Palettes $id
    Api $id POST '/api/memory/poke' @{ domain = 'CGRAM'; address = 2; value = 0x00 } | Out-Null
    Api $id POST '/api/memory/poke' @{ domain = 'CGRAM'; address = 3; value = 0x7C } | Out-Null
    Start-Sleep -Milliseconds 600
    $r0 = PalLine $win 'row 0:'
    $cg = (Api $id GET '/api/memory/peek-range?domain=CGRAM&address=0&length=32').data
    $words = for ($i = 0; $i -lt 16; $i++) { '{0:X4}' -f ([int]$cg[2 * $i] -bor ([int]$cg[2 * $i + 1] -shl 8)) }
    Check 'palettes_snes' ($r0 -eq ('row 0: ' + ($words -join ' ')) -and $r0 -like 'row 0: * 7C00 *') "window: $r0 ; API: $($words -join ' ')"
    Check 'palettes_snes_negative' (@(Items (FindById $win 'palettes')).Count -eq 16 -and (PalLine $win 'row 1:') -notlike 'row 1: * 7C00 *') "16 rows; row 1: $(PalLine $win 'row 1:')"
    Close-Window $win

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
    $win = Open-Memory $id
    $gd = Domains $win
    Check 'memory_gb_domains' (($gd -contains 'WRAM') -and ($gd -contains 'HRAM') -and ($gd -contains 'OAM') -and ($gd -contains 'IO')) "Game Boy domains: $($gd -join ', ')"
    Pick-Domain $win 'BUS' | Out-Null
    GoTo $win '100'; $g0 = HexRow $win 0; $gApi = ApiRow $id 'BUS' 0x100
    Check 'memory_gb_bus_row' ($g0 -like '0100:*' -and (RowBytes $g0) -eq $gApi) "BUS row: $g0 ; API: $gApi"
    Close-Window $win
    $win = Open-Palettes $id
    # the Game Boy's palette registers are read only through the API (the I/O domain refuses writes), so the check is the values against
    # the API's and their decoding: shade i of a register is bits 2i+1..2i
    $gl = @(); $gbBad = @()
    foreach ($r in @(@('BGP', 0x47), @('OBP0', 0x48), @('OBP1', 0x49))) {
        $v = (Api $id GET "/api/memory/peek?domain=IO&address=$($r[1])").value
        $want = "$($r[0]) `$$('{0:X2}' -f $v): $((0..3 | ForEach-Object { ($v -shr (2 * $_)) -band 3 }) -join ' ')"
        $got = PalLine $win "$($r[0]) "
        $gl += $got
        if ($got -ne $want) { $gbBad += "$got (want $want)" }
    }
    Check 'palettes_gb' ($gbBad.Count -eq 0 -and $gl.Count -eq 3) "$($gl -join ' ; ')"
    $flip = "BGP `$1B: 3 2 1 0"
    Check 'palettes_gb_negative' ($gl[0] -ne $flip -or ((Api $id GET '/api/memory/peek?domain=IO&address=71').value -eq 0x1B)) "the window is not showing another value's shades ($flip); no colour RAM rows on a monochrome game: $(-not (PalLine $win 'BG0:'))"
    Close-Window $win

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

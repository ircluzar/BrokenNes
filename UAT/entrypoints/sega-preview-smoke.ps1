<#
.SYNOPSIS
  Sega console plumbing, end to end in the real desktop app. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  The Sega consoles sit behind a preview switch (BROKENNES_SEGA=1) until their boards exist; a console whose core is finished (the Ready flag in
  ConsoleSystems.cs: the Master System and the Game Gear since milestone A1, the Genesis since B2a) is on show without it. This proves, in the shipped
  WinForms app, that the switch and the flags do what they say:

    OFF  the Console menu lists the four consoles it always had plus the three ready ones (Master System, Game Gear, Genesis); a stored Genesis
         selection and the retired joke CPU core id in config.json do not break start-up; a Master System program loads and runs without the switch,
         and so does a Genesis program (the VDP's real picture: a 68000 program that draws the pad as the backdrop colour, which changes when
         Right is held).
    ON   the Console menu lists seven. The Master System and Game Gear load a REAL program (a three-colour Mode 4 picture that scrolls while Right
         is held; hand-assembled, carried below as hex and checked against the Workshop's copy by --smstest --only smoke) through RomDetect into
         the real session: right console, core ids, frame size, a picture that is still without input and moves with it. The Genesis loads its
         program the same way (a flat backdrop that is still without input and changes colour with it).
    BATTERY  a Master System and a Game Gear program that keeps a counter in cartridge RAM: the first boot shows red and, after a graceful close,
         leaves SmsSaves\<sha1>.sav / GgSaves\<sha1>.sav holding the marker and the count 1; a second app instance loads the save, shows green and
         leaves the count 2.

    SWAP  (in the switch-ON run) for each of the three consoles, through the desktop's own swap (POST /api/console/core/{slot}/{id}, the call the PPU and APU menus
         make): the picture chip goes to NES:FIXS (the bridge: a 256x240 NES picture, different from the native one, the status naming the bridge) and back (the
         console's own picture again, the same pixels), the program still answers the pad on the NES chip and after the swap back; the sound unit goes to NES:FIXS
         and back (Master System and Game Gear in place, the status note empty; the Genesis restarts the game and the note says why, "sound unit"); an id the menu
         does not list is refused and nothing changes. One more check drives the real PPU menu of the Master System through UI Automation (PPU > the NES:FIXS entry)
         and sees the same swap. That check needs the app window in front: when another window holds the foreground it is reported NOT RUN (never a pass, never
         a failure of the app) and the run says status NOT RUN with exit 0, like UAT\plugin\synth-smoke.ps1. -SwapsOnly runs just this section.

  The synthetic ROMs are written to the work folder (programs of a few hundred bytes, no game data). The user's config.json is copied
  aside first and put back at the end, because a console switch is persisted there; the two synthetic saves are removed before and after.

  Rule zero (UAT\README.md): never kill by process name; only PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\sega-preview-smoke.ps1
  pwsh -File UAT\entrypoints\sega-preview-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_sega_preview'),
    # run only the core-swap section (the three consoles through the HTTP API, one through the PPU menu): a few minutes shorter, for working on swaps; the full run is the certification
    [switch]$SwapsOnly
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$notes = New-Object System.Collections.Generic.List[string]
$result = [ordered]@{ entrypoint = 'sega-preview'; pass = $false }
$configPath = Join-Path $env:APPDATA 'BrokenNes\config.json'
$configBackup = Join-Path $WorkDir 'config.json.bak'
$haveBackup = $false

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

# The programs (Z80 machine code from $0100; the ROM image is a JP $0100 at $0000 and zeros). Written in Workshop/Sega/SmsAsm.cs (SmokeProgs); `BrokenNes.Workshop.exe --smstest --only smoke`
# checks that these lines are byte for byte what that code builds, and `--smstest --print-smoke-hex` prints them again if the programs change.
$SmsPictureHex = 'F3ED5631F0DF0EBF0616218401EDB33E00D3BF3EC0D3BF0EBE0620219A01EDB33E00D3BF3E40D3BF068021BA01EDB33E00D3BF3E78D3BF1100030E0179D3BEAFD3BE0C79FE0420020E011B7AB320ED3E00D3BF3E7FD3BF3ED0D3BE3EC0D3BF3E81D3BFDB7EFEC020FADBDCE608200D3A00C13C3200C1D3BF3E88D3BFDB7EFEC028FA18DF04808081FF82FF83FF84FF85FF86008700880089FF8A00300C03000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF00000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF'
$GgPictureHex = 'F3ED5631F0DF0EBF0616218401EDB33E00D3BF3EC0D3BF0EBE0640219A01EDB33E00D3BF3E40D3BF068021DA01EDB33E00D3BF3E78D3BF1100030E0179D3BEAFD3BE0C79FE0420020E011B7AB320ED3E00D3BF3E7FD3BF3ED0D3BE3EC0D3BF3E81D3BFDB7EFEC020FADBDCE608200D3A00C13C3200C1D3BF3E88D3BFDB7EFEC028FA18DF04808081FF82FF83FF84FF85FF86008700880089FF8A0000000FF0000F0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF00000000FF000000FF000000FF000000FF000000FF000000FF000000FF000000FF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF'
$SmsBatteryHex = 'F3ED5631F0DF3E0832FCFF3A0080FE5A20123A0180FEA5200B3A02803C320280160118113E5A3200803EA53201803E0132028016003E04D3BF3E80D3BF3E80D3BF3E81D3BF3E10D3BF3EC0D3BF3E03D3BE3E0CD3BE7AD3BF3E87D3BF18FE'
$GgBatteryHex = 'F3ED5631F0DF3E0832FCFF3A0080FE5A20123A0180FEA5200B3A02803C320280160118113E5A3200803EA53201803E0132028016003E04D3BF3E80D3BF3E80D3BF3E81D3BF3E20D3BF3EC0D3BF3E0FD3BE3E00D3BE3EF0D3BE3E00D3BE7AD3BF3E87D3BF18FE'

# A Master System / Game Gear ROM from a program: the program, a JP to it, and the "TMR SEGA" header (system code 4 = SMS export, 6 = GG export).
function New-ProgramRom([string]$hex, [byte]$system) {
    $rom = New-Object byte[] 0x8000
    $rom[0] = 0xC3; $rom[1] = 0x00; $rom[2] = 0x01
    $prog = [byte[]]@(for ($i = 0; $i -lt $hex.Length; $i += 2) { [Convert]::ToByte($hex.Substring($i, 2), 16) })
    [Array]::Copy($prog, 0, $rom, 0x100, $prog.Length)
    [Text.Encoding]::ASCII.GetBytes('TMR SEGA').CopyTo($rom, 0x7FF0); $rom[0x7FFF] = [byte]($system -shl 4 -bor 0x0C)
    return ,$rom
}
function Get-RomSha1([byte[]]$rom) { ([BitConverter]::ToString([Security.Cryptography.SHA1]::Create().ComputeHash($rom)) -replace '-', '').ToLowerInvariant() }

# The ROMs go in the work folder; the Genesis one has no core yet (a header and zeros: enough for RomDetect and the placeholder session).
function New-SegaRoms($dir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    # The Genesis ROM carries a real 68000 program (written for this script, no game data): it sets H40 and the display on, drives the first pad's TH
    # line high and, in a loop, reads the pad and writes it as colour 0 of the palette, which is the backdrop: the picture is the pad's state.
    # move.w #$8004,$C00004 / #$8144 / #$8C81 / #$8700 / #$8F02 ; move.b #$40,$A10009 (TH an output) ; move.b #$40,$A10003 (TH high)
    # loop: move.b $A10003,d0 ; andi.w #$3F,d0 ; lsl.w #3,d0 ; move.l #$C0000000,$C00004 ; move.w d0,$C00000 ; bra loop
    $code = '33fc800400c0000433fc814400c0000433fc8c8100c0000433fc870000c0000433fc8f0200c0000413fc004000a1000913fc004000a10003103900a100030240003fe74823fcc000000000c0000433c000c0000060e2'
    $rom = @{ 'smoke.sms' = (New-ProgramRom $SmsPictureHex 4); 'smoke.gg' = (New-ProgramRom $GgPictureHex 6); 'battery.sms' = (New-ProgramRom $SmsBatteryHex 4); 'battery.gg' = (New-ProgramRom $GgBatteryHex 6) }
    foreach ($k in $rom.Keys) { [IO.File]::WriteAllBytes((Join-Path $dir $k), [byte[]]$rom[$k]) }
    $md = New-Object byte[] 0x20000
    $md[1] = 0xFF; $md[2] = 0xFE; $md[3] = 0x00; $md[6] = 0x02; $md[7] = 0x00           # vectors: stack $FFFE00, entry $200
    [Text.Encoding]::ASCII.GetBytes('SEGA MEGA DRIVE ').CopyTo($md, 0x100)
    [Text.Encoding]::ASCII.GetBytes('GM 00000000-00').CopyTo($md, 0x180)
    for ($i = 0; $i -lt 16; $i++) { $md[0x1F0 + $i] = 0x20 }; $md[0x1F0] = [byte][char]'U'
    for ($i = 0; $i -lt $code.Length / 2; $i++) { $md[0x200 + $i] = [Convert]::ToByte($code.Substring($i * 2, 2), 16) }
    [IO.File]::WriteAllBytes((Join-Path $dir 'smoke.md'), $md)
    $script:batterySms = Get-RomSha1 $rom['battery.sms']; $script:batteryGg = Get-RomSha1 $rom['battery.gg']
}

# With no audio output device (a build machine, or the interface switched off) the app shows an "Audio Warning" box at start-up that
# blocks everything, a graceful close included. Dismiss whatever modal boxes the instance has; returns their titles.
function Clear-StartupDialogs([int]$procId) {
    $titles = @()
    for ($i = 0; $i -lt 10; $i++) {
        $d = @(Dismiss-BrokenNesDialogs -ProcessId $procId)
        if ($d.Count) { $titles += $d; Start-Sleep -Milliseconds 500 } elseif ($titles.Count) { break } else { Start-Sleep -Milliseconds 500 }
    }
    return $titles
}

# The names of the entries under the native "Console" menu (the app's own menu bar, direct-play screen).
function Get-ConsoleMenuNames([int]$procId) {
    # the menu bar is driven through UI Automation, which now and then answers before the window is ready: ask again a few times
    for ($try = 0; $try -lt 4; $try++) {
        $names = @(Get-ConsoleMenuNamesOnce $procId)
        if ($names.Count -gt 0) { return $names }
        Start-Sleep -Milliseconds 800
    }
    return $names
}
function Get-ConsoleMenuNamesOnce([int]$procId) {
    $wins =[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)))
    $main = $wins | Where-Object { $_.Current.Name -like 'BrokenNes*' } | Select-Object -First 1
    if (-not $main) { return $null }
    $item = Find-ByName $main 'Console'
    if (-not $item) { return $null }
    $pattern = $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $pattern.Expand(); Start-Sleep -Milliseconds 500
    $names = @()
    foreach ($k in $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($k.Current.Name -and $k.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem) { $names += $k.Current.Name }
    }
    $pattern.Collapse()
    return $names
}
function Get-Frame([int]$procId) {
    $fb = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console/framebuffer'
    $bytes = [Convert]::FromBase64String([string]$fb.data)
    $sha = [Security.Cryptography.SHA1]::Create()
    $colors = New-Object 'System.Collections.Generic.HashSet[int]'; $nonBlack = 0; $n = $fb.width * $fb.height
    for ($i = 0; $i -lt $n; $i++) {
        $o = $i * 4; $c = ([int]$bytes[$o] -shl 16) -bor ([int]$bytes[$o + 1] -shl 8) -bor [int]$bytes[$o + 2]   # (a [byte] shifted stays a byte: a green backdrop would count as black)
        [void]$colors.Add($c); if ($c -ne 0) { $nonBlack++ }
    }
    $mid = (([int]($fb.height / 2) * $fb.width) + [int]($fb.width / 2)) * 4
    [pscustomobject]@{ Width = [int]$fb.width; Height = [int]$fb.height; Colors = $colors.Count; NonBlackPct = [math]::Round(100.0 * $nonBlack / $n, 1); Hash = [BitConverter]::ToString($sha.ComputeHash($bytes))
                       Mid = [pscustomobject]@{ R = [int]$bytes[$mid]; G = [int]$bytes[$mid + 1]; B = [int]$bytes[$mid + 2] } }
}

# A session shows nothing until its first frame has run, so a frame read straight after a load can be blank: poll until the picture has something in it (or give up after $Seconds and return the last frame).
function Wait-Picture([int]$procId, [double]$MinNonBlackPct = 5, [int]$Seconds = 6) {
    $until = (Get-Date).AddSeconds($Seconds)
    do { $f = Get-Frame $procId; if ($f.NonBlackPct -ge $MinNonBlackPct) { return $f }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $until)
    return $f
}
# Hold a button and look for the picture to change from $Baseline: sampled repeatedly while the button is held (one sample races the hold's end), released afterwards.
function Test-InputChangesPicture([int]$procId, $Baseline, [string[]]$Button = @('Right'), [int]$Seconds = 4) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @($Button) } | Out-Null
    $changed = $false; $until = (Get-Date).AddSeconds($Seconds)
    # The sampling interval is random: a scrolling picture comes back to the same pixels every 8 or 16 pixels, and samples a fixed 16 frames apart (about 267 ms, which the 250 ms sleep plus the
    # cost of reading a frame can land on) would all show the same picture while the game scrolls, and the check would say the pad did nothing.
    try { do { Start-Sleep -Milliseconds (Get-Random -Minimum 90 -Maximum 330); $f = Get-Frame $procId; if ($f.Hash -ne $Baseline.Hash) { $changed = $true } } while (-not $changed -and (Get-Date) -lt $until) }
    finally { Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @() } | Out-Null }
    return $changed
}
function Is-Red($p) { $p.R -gt 0x80 -and $p.G -lt 0x40 }
function Is-Green($p) { $p.G -gt 0x80 -and $p.R -lt 0x40 }
function Wait-Frame([int]$procId, [scriptblock]$ok, [int]$seconds = 8) {
    $end = (Get-Date).AddSeconds($seconds); $f = $null
    do { Start-Sleep -Milliseconds 400; $f = Get-Frame $procId; if (& $ok $f) { break } } while ((Get-Date) -lt $end)
    return $f
}
function Get-SavePath([string]$dir, [string]$sha) { Join-Path $env:APPDATA "BrokenNes\$dir\$sha.sav" }

# ---- core swaps -------------------------------------------------------------------------------------------------------------------------------
# A check that cannot be made is NOT RUN: not a pass and not a failure of the app (the same rule as UAT\plugin\synth-smoke.ps1). The menu check drives the real PPU menu through UI Automation,
# which needs the window to be the one in front; when another program (an agent's emulator window, a dialog) keeps the foreground the check says so instead of failing.
if (-not ('SmokeWin' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class SmokeWin {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  public static void Take(IntPtr h) {
    uint pid; IntPtr fg = GetForegroundWindow(); uint theirs = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid); uint mine = GetCurrentThreadId();
    bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
    ShowWindow(h, 9); BringWindowToTop(h); SetForegroundWindow(h);
    if (GetForegroundWindow() != h) SwitchToThisWindow(h, true);
    if (attached) AttachThreadInput(mine, theirs, false);
  }
  public static string Title() { var sb = new StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, 256); return sb.ToString(); }
}
'@
}
$script:fgHolder = ''
function NotRun($name, $why) {
    $script:checks[$name] = [ordered]@{ ok = $false; notRun = $true; detail = "NOT RUN: $why" }
    Write-Host ("[NOT RUN] {0} - {1}" -f $name, $why)
}
# true when the app's window really holds the foreground afterwards ($script:fgHolder names the window that kept it otherwise)
function Take-Foreground([int]$procId) {
    $h = (Get-Process -Id $procId).MainWindowHandle
    for ($try = 0; $try -lt 4; $try++) {
        [SmokeWin]::Take($h); Start-Sleep -Milliseconds 400
        if ([SmokeWin]::GetForegroundWindow() -eq $h) { return $true }
    }
    $script:fgHolder = [SmokeWin]::Title()
    return $false
}
function Get-ConsoleStatus([int]$procId) { (Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console').status }
# POST /api/console/core/{slot}/{id}: the desktop's own swap (the one the PPU / APU menus call); an id the console's menu does not list answers success=false
function Set-ConsoleCore([int]$procId, [string]$slot, [string]$coreId) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path ("/api/console/core/$slot/" + [Uri]::EscapeDataString($coreId))
}
# two frames a moment apart that are the same: the picture of a program that is waiting for input
function Get-StableFrame([int]$procId, [int]$Seconds = 8) {
    $until = (Get-Date).AddSeconds($Seconds); $a = Get-Frame $procId
    do { Start-Sleep -Milliseconds 400; $b = Get-Frame $procId; if ($b.Hash -eq $a.Hash -and $b.NonBlackPct -ge 5) { return $b }; $a = $b } while ((Get-Date) -lt $until)
    return $b
}

$inst = $null
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    . (Join-Path $repo 'UAT\lib\UiaHelpers.ps1')
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath -Destination $configBackup -Force; $haveBackup = $true }

    # ---------------- build ----------------
    if (-not $Exe) {
        $art = Join-Path $WorkDir 'artifacts'
        $log = Join-Path $WorkDir 'build.log'
        Write-Host "building Windows\BrokenNes.Windows.csproj (log: $log) ..."
        & dotnet build (Join-Path $repo 'Windows\BrokenNes.Windows.csproj') -c Release --artifacts-path $art -nologo -v q *> $log
        $code = $LASTEXITCODE
        $found = Get-ChildItem -Path $art -Recurse -Filter BrokenNes.Windows.exe -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Check 'build_ok' ($code -eq 0 -and $null -ne $found) "dotnet build exit $code; exe=$($found.FullName)"
        if ($code -ne 0 -or -not $found) { throw "build failed, see $log" }
        $Exe = $found.FullName
    } else {
        Check 'build_ok' (Test-Path -LiteralPath $Exe) "using supplied -Exe $Exe"
    }
    $result.exe = $Exe
    $roms = Join-Path $WorkDir 'roms'; New-SegaRoms $roms

    if (-not $SwapsOnly) {
    # ================= switch OFF =================
    $env:BROKENNES_SEGA = $null
    # a stored Sega selection must not break start-up when the consoles are not on
    if ($haveBackup) {
        $cfg = Get-Content -LiteralPath $configBackup -Raw | ConvertFrom-Json
        $cfg | Add-Member -NotePropertyName SelectedConsole -NotePropertyValue 'sms' -Force
        $cfg | Add-Member -NotePropertyName SelectedCpuCore -NotePropertyValue 'Z80' -Force   # the retired joke core's id
        ($cfg | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $configPath -Encoding UTF8
    }
    $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
    $id = $inst.ProcessId
    $dlg = Clear-StartupDialogs $id; if ($dlg.Count) { $notes.Add('dismissed start-up dialog(s): ' + ($dlg -join ', ')) }
    Check 'off_starts_with_stored_sega_selection' ([bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health')) "pid $id, config.json SelectedConsole=sms and SelectedCpuCore=Z80, switch off"
    Show-BrokenNesEmulator -ProcessId $id | Out-Null; Start-Sleep -Milliseconds 800
    $st = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console'
    $cpu = Get-BrokenNesCore -ProcessId $id -Kind cpu
    Check 'off_retired_cpu_id_falls_back' ($cpu -and $cpu -notlike '*Z80*') "config.json SelectedCpuCore=Z80 (retired); the app runs CPU '$cpu'"
    Check 'off_console_is_one_it_has' ($st.status.console -in 'nes', 'snes', 'gb', 'gbc', 'sms', 'gg', 'md') "console=$($st.status.console) (a stored 'md' is a ready console now: it starts with the Genesis selected, or falls back to one it has)"
    $fgOk = Take-Foreground $id      # the menu is read through UI Automation, which needs the app in front
    $off = if ($fgOk) { Get-ConsoleMenuNames $id } else { $null }
    if (-not $fgOk) { NotRun 'off_menu_lists_seven_ready_consoles' "another window holds the foreground ([$script:fgHolder])" } else {
    Check 'off_menu_lists_seven_ready_consoles' ($null -ne $off -and @($off | Where-Object { $_ -match 'Genesis' }).Count -ge 1 -and @($off | Where-Object { $_ -match 'Master System' }).Count -ge 1 -and @($off | Where-Object { $_ -match 'Game Gear' }).Count -ge 1 -and @($off).Count -ge 7) ("menu: " + ($off -join ' | '))
    }
    # a ready console needs no switch: load a Master System program and see it run
    $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms 'smoke.sms')
    Start-Sleep -Seconds 2
    $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
    $f = Get-Frame $id
    Check 'off_sms_runs_without_the_switch' ($l.success -and $st.console -eq 'sms' -and $f.Colors -ge 3 -and $f.NonBlackPct -ge 90) "load success=$($l.success) console=$($st.console) picture $($f.Colors) colours, $($f.NonBlackPct)% non-black"
    # ... and so does the Genesis: its program loads as a real session and the pad reaches it
    $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms 'smoke.md')
    $g0 = Wait-Picture $id 90
    $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
    Check 'off_genesis_loads_without_the_switch' ($l.success -and $st.console -eq 'md' -and $st.consoleName -like '*Genesis*') "load success=$($l.success) console=$($st.console) name=$($st.consoleName) game=$($st.game)"
    Check 'off_genesis_real_picture' (($g0.Width -in @(256, 320)) -and ($g0.Height -in @(224, 240)) -and $g0.NonBlackPct -ge 90) "framebuffer $($g0.Width)x$($g0.Height), $($g0.Colors) colours, $($g0.NonBlackPct)% non-black, middle pixel $($g0.Mid.R),$($g0.Mid.G),$($g0.Mid.B) (the program fills the backdrop)"
    $moved = Test-InputChangesPicture $id $g0
    Check 'off_genesis_input_reaches_the_game' $moved "backdrop changed while Right was held: $moved"
    $r = Stop-BrokenNesInstance -ProcessId $id -TimeoutSeconds 25
    Check 'off_graceful_shutdown' ($r.Stopped -and $r.Method -in 'CloseMainWindow', 'already-exited') "method=$($r.Method)"
    $inst = $null
    if ($haveBackup) { Copy-Item -LiteralPath $configBackup -Destination $configPath -Force }
    }   # -not $SwapsOnly

    # ================= switch ON =================
    $env:BROKENNES_SEGA = '1'
    # the quick save / load API is the opt-in "webmodule savestate debug shortcuts" (off by default, answers success=false gated=true); the save-state checks below need it on for this run (the stored config comes back after)
    if ($haveBackup) {
        $cfg = Get-Content -LiteralPath $configBackup -Raw | ConvertFrom-Json
        $cfg | Add-Member -NotePropertyName enableWebmoduleSavestateDebugShortcuts -NotePropertyValue $true -Force
        ($cfg | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $configPath -Encoding UTF8
    }
    $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
    $id = $inst.ProcessId
    $dlg = Clear-StartupDialogs $id; if ($dlg.Count) { $notes.Add('dismissed start-up dialog(s): ' + ($dlg -join ', ')) }
    Check 'on_api_up' ([bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health')) "pid $id, BROKENNES_SEGA=1"
    Show-BrokenNesEmulator -ProcessId $id | Out-Null; Start-Sleep -Milliseconds 800
    $fgOk = Take-Foreground $id
    $on = if ($fgOk) { Get-ConsoleMenuNames $id } else { $null }
    if (-not $fgOk) { NotRun 'on_menu_lists_seven' "another window holds the foreground ([$script:fgHolder])" } else {
    Check 'on_menu_lists_seven' ($null -ne $on -and @($on | Where-Object { $_ -match 'Master System' }).Count -ge 1 -and @($on | Where-Object { $_ -match 'Game Gear' }).Count -ge 1 -and @($on | Where-Object { $_ -match 'Genesis' }).Count -ge 1 -and @($on).Count -ge 7) ("menu: " + ($on -join ' | '))
    }

    $expect = @(
        @{ Key = 'sms'; File = 'smoke.sms'; W = 256; H = 192; Name = 'Master System'; Real = $true; MinColors = 3 },
        @{ Key = 'gg';  File = 'smoke.gg';  W = 160; H = 144; Name = 'Game Gear'; Real = $true; MinColors = 3 },
        @{ Key = 'md';  File = 'smoke.md';  W = 320; H = 224; Name = 'Genesis'; Real = $true; MinColors = 1 })   # the Genesis program: a flat backdrop, so one colour
    foreach ($e in $(if ($SwapsOnly) { @() } else { $expect })) {
        $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms $e.File)
        $f0 = Wait-Picture $id 5
        $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
        Check "$($e.Key)_loads_as_its_console" ($l.success -and $st.console -eq $e.Key -and $st.consoleName -like "*$($e.Name)*") "load success=$($l.success) console=$($st.console) name=$($st.consoleName) game=$($st.game)"
        Check "$($e.Key)_frame_size" ($st.frameWidth -eq $e.W -and $st.frameHeight -eq $e.H) "status says $($st.frameWidth)x$($st.frameHeight), expected $($e.W)x$($e.H)"
        Check "$($e.Key)_picture" ($f0.Width -eq $e.W -and $f0.Height -eq $e.H -and $f0.Colors -ge $e.MinColors -and $f0.NonBlackPct -ge 5) "framebuffer $($f0.Width)x$($f0.Height), $($f0.Colors) colours, $($f0.NonBlackPct)% non-black, middle pixel $($f0.Mid)"
        Check "$($e.Key)_cores_named" ($st.cpu -and $st.ppu -and $st.apu) "cpu=$($st.cpu) ppu=$($st.ppu) apu=$($st.apu)"
        if ($e.Real) {
            # the real program is still without input: two pictures a moment apart are the same (the first picture after a load can still be the boot's partly drawn one, so the pair is taken after it)
            Start-Sleep -Milliseconds 400
            $f0 = Get-Frame $id
            Start-Sleep -Milliseconds 400
            $f0b = Get-Frame $id
            Check "$($e.Key)_real_picture_is_still_without_input" ($f0b.Hash -eq $f0.Hash) "hash equal: $($f0b.Hash -eq $f0.Hash)"
        }
        # input: hold Right; the real program scrolls (the Genesis one recolours its backdrop), so the frame must change
        $moved = Test-InputChangesPicture $id $f0
        Check "$($e.Key)_input_reaches_session" $moved "frame hash changed while Right was held: $moved"
        Start-Sleep -Milliseconds 400

        # save state through the app's own quick save / quick load (the API calls the same methods as F7 and F5): save the still picture, scroll away, load, and the still picture is back.
        # All three Sega consoles have states (the Genesis since Track B4b: the 68000, the VDP with its picture, the Z80, the YM2612 and the PSG, the cartridge).
        $stateDir = Join-Path $env:APPDATA 'BrokenNes\States'
        if ($e.Real -and $e.Key -in 'sms', 'gg', 'md') {
            $slot = Join-Path $stateDir "$($e.Key)\$(Get-RomSha1 ([IO.File]::ReadAllBytes((Join-Path $roms $e.File)))).quick.bnstate"
            if (Test-Path -LiteralPath $slot) { Remove-Item -LiteralPath $slot -Force }
            Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms $e.File) | Out-Null
            Wait-Picture $id 5 | Out-Null; Start-Sleep -Milliseconds 800
            $still = Get-Frame $id
            $sv = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/quick-save-state'
            $slotLen = if (Test-Path -LiteralPath $slot) { (Get-Item -LiteralPath $slot).Length } else { 0 }
            Check "$($e.Key)_quick_save_writes_the_slot" ([bool]$sv.success -and $slotLen -gt 1000) "success=$($sv.success); $slot is $slotLen bytes"
            $away = Test-InputChangesPicture $id $still
            $ld = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/quick-load-state'
            $back = Wait-Frame $id { param($x) $x.Hash -eq $still.Hash } 6
            Check "$($e.Key)_quick_load_restores_the_picture" ([bool]$ld.success -and $away -and $back.Hash -eq $still.Hash) "load success=$($ld.success); the picture had moved away: $away; it is the saved one again: $($back.Hash -eq $still.Hash)"
            if (Test-Path -LiteralPath $slot) { Remove-Item -LiteralPath $slot -Force }
        }
    }
    # ---- core swaps, all three consoles, through the desktop's own swap (POST /api/console/core/{slot}/{id}, the call the PPU and APU menus make) ----
    # The picture chip goes to NES:FIXS (the bridge: a 256x240 NES picture of the Sega game) and back; the sound unit goes to NES:FIXS and back. A swap that can be done in place leaves the game running
    # (the program still answers the pad), one that cannot restarts the game and the status note says why; an id the menu does not list is refused and nothing changes.
    $swapSet = @(
        @{ Key = 'sms'; File = 'smoke.sms'; OwnPpu = 'SMS'; OwnApu = 'SMS'; ApuInPlace = $true;  Pad = @('Right') },
        @{ Key = 'gg';  File = 'smoke.gg';  OwnPpu = 'GG';  OwnApu = 'GG';  ApuInPlace = $true;  Pad = @('Right') },
        # the Genesis program draws the pad as its backdrop colour: a bridged picture shows it in the NES colour nearest, so one direction can land on the same colour; three buttons do not
        @{ Key = 'md';  File = 'smoke.md';  OwnPpu = 'MD';  OwnApu = 'MD';  ApuInPlace = $false; Pad = @('Right', 'B', 'C') })
    foreach ($e in $swapSet) {
        $k = $e.Key
        # the cores a console last used are kept in config.json (the user's, or an earlier run's): start from the console's own, whatever was left there
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @() } | Out-Null      # no button held from the console before
        Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms $e.File) | Out-Null
        Set-ConsoleCore $id 'ppu' $e.OwnPpu | Out-Null; Set-ConsoleCore $id 'apu' $e.OwnApu | Out-Null
        Wait-Picture $id 5 | Out-Null; Start-Sleep -Milliseconds 800
        $base = Get-StableFrame $id
        $s0 = Get-ConsoleStatus $id
        Write-Host "[INFO] ${k}: baseline picture $($base.Width)x$($base.Height) $($base.Hash.Substring(0, 8)) (idle program, no button held)"
        if ($s0.ppu -ne $e.OwnPpu -or $s0.apu -ne $e.OwnApu -or $s0.pictureBridge) { Write-Host "[WARN] ${k}: could not start from the console's own cores: ppu=$($s0.ppu) apu=$($s0.apu) bridge=[$($s0.pictureBridge)]" }
        # --- the picture chip out: the NES chip draws the game
        $r = Set-ConsoleCore $id 'ppu' 'NES:FIXS'
        $b = Wait-Frame $id { param($x) $x.Width -eq 256 -and $x.Height -eq 240 -and $x.NonBlackPct -ge 5 } 14
        $s = Get-ConsoleStatus $id
        Check "${k}_swap_ppu_to_nes_fixs" ([bool]$r.success -and $s.ppu -eq 'NES:FIXS' -and "$($s.pictureBridge)" -like '*NES:FIXS*' -and $b.Width -eq 256 -and $b.Height -eq 240 -and $b.NonBlackPct -ge 5 -and $b.Hash -ne $base.Hash -and -not $s.note) "success=$($r.success) ppu=$($s.ppu) bridge=[$($s.pictureBridge)] picture $($b.Width)x$($b.Height) $($b.NonBlackPct)% non-black, differs from the native one: $($b.Hash -ne $base.Hash); note=[$($s.note)]"
        # --- and back: the console's own chip again, the same picture (the program was only waiting)
        $r = Set-ConsoleCore $id 'ppu' $e.OwnPpu
        $back = Wait-Frame $id { param($x) $x.Width -eq $base.Width -and $x.Height -eq $base.Height -and $x.Hash -eq $base.Hash } 14
        $s = Get-ConsoleStatus $id
        Check "${k}_swap_ppu_back_restores_the_picture" ([bool]$r.success -and $s.ppu -eq $e.OwnPpu -and -not $s.pictureBridge -and $back.Width -eq $base.Width -and $back.Hash -eq $base.Hash) "success=$($r.success) ppu=$($s.ppu) bridge=[$($s.pictureBridge)] picture $($back.Width)x$($back.Height), the one from before the swap: $($back.Hash -eq $base.Hash) (before $($base.Hash.Substring(0, 8)) after $($back.Hash.Substring(0, 8)))"
        # --- the game keeps running through it: on the NES chip and on the way back the program still answers the pad
        Set-ConsoleCore $id 'ppu' 'NES:FIXS' | Out-Null
        Wait-Frame $id { param($x) $x.Width -eq 256 -and $x.Height -eq 240 } 14 | Out-Null; Start-Sleep -Milliseconds 500
        $movedBridged = Test-InputChangesPicture $id (Get-Frame $id) -Button $e.Pad -Seconds 10
        Set-ConsoleCore $id 'ppu' $e.OwnPpu | Out-Null
        Wait-Frame $id { param($x) $x.Width -eq $base.Width -and $x.Height -eq $base.Height } 14 | Out-Null; Start-Sleep -Milliseconds 500
        $movedNative = Test-InputChangesPicture $id (Get-Frame $id) -Button $e.Pad -Seconds 10
        Check "${k}_swap_ppu_game_keeps_running" ($movedBridged -and $movedNative) "the picture changed under the pad on the NES chip: $movedBridged; and again on the console's own chip afterwards: $movedNative (fps $([math]::Round([double](Get-ConsoleStatus $id).fps)))"
        # --- the sound unit out and back
        $r = Set-ConsoleCore $id 'apu' 'NES:FIXS'
        $s = Get-ConsoleStatus $id
        Start-Sleep -Milliseconds 800; Wait-Picture $id 5 12 | Out-Null
        $movedApu = Test-InputChangesPicture $id (Get-Frame $id) -Button $e.Pad -Seconds 10
        if ($e.ApuInPlace) {
            Check "${k}_swap_apu_in_place" ([bool]$r.success -and $s.apu -eq 'NES:FIXS' -and -not $s.note -and $movedApu) "success=$($r.success) apu=$($s.apu) note=[$($s.note)] the game still answers the pad: $movedApu"
        } else {
            # The Genesis sound unit either hands its state over live (the game goes on, no note) or the game restarts and the note gives the reason: both are honest, a restart with no reason is not.
            # (Track B4b's branch makes the swap live; this branch restarts. The check passes on both sides of that merge.)
            $note = "$($s.note)"; $live = $note.Length -eq 0; $says = $note -match '^restarted on APU NES:FIXS: .{20,}'
            Check "${k}_swap_apu_is_live_or_restarts_and_says_why" ([bool]$r.success -and $s.apu -eq 'NES:FIXS' -and ($live -or $says) -and $movedApu) "success=$($r.success) apu=$($s.apu) $(if ($live) { 'swapped live' } else { 'restarted' }) note=[$note]; the game answers the pad: $movedApu (fps $([math]::Round([double]$s.fps)))"
        }
        $r = Set-ConsoleCore $id 'apu' $e.OwnApu
        $s = Get-ConsoleStatus $id
        Start-Sleep -Milliseconds 800; Wait-Picture $id 5 12 | Out-Null
        $movedApu2 = Test-InputChangesPicture $id (Get-Frame $id) -Button $e.Pad -Seconds 10
        Check "${k}_swap_apu_back" ([bool]$r.success -and $s.apu -eq $e.OwnApu -and $movedApu2) "success=$($r.success) apu=$($s.apu); the game answers the pad: $movedApu2"
        # --- an id the console's menu does not list is refused, nothing changes
        $r = Set-ConsoleCore $id 'ppu' 'NES:NOPE'
        $s = Get-ConsoleStatus $id
        Check "${k}_swap_refuses_an_id_the_menu_does_not_list" (-not [bool]$r.success -and $s.ppu -eq $e.OwnPpu -and -not $s.pictureBridge) "success=$($r.success) error=[$($r.error)] ppu stays $($s.ppu)"
    }

    # ---- the same swap through the real PPU menu (UI Automation, the user's path): Master System program, PPU > the NES:FIXS entry ----
    Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms 'smoke.sms') | Out-Null
    Wait-Picture $id 5 | Out-Null; Start-Sleep -Milliseconds 800
    $baseUi = Get-StableFrame $id
    $menus = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console/cores'
    $fixsLabel = (@($menus.menus.ppu) | Where-Object { $_.id -eq 'NES:FIXS' } | Select-Object -First 1).label
    if (-not (Take-Foreground $id)) {
        NotRun 'ui_ppu_menu_swaps_the_picture' "another window holds the foreground ([$script:fgHolder]); the menu check needs the app in front"
    } else {
        $win = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $id))) | Where-Object { $_.Current.Name -like 'BrokenNes*' } | Select-Object -First 1
        $clicked = $false; $itemName = ''
        if ($win -and $fixsLabel) {
            $menu = Find-ByName $win 'PPU'
            if ($menu) {
                $menu.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 600
                $item = Find-ByName $menu $fixsLabel
                if (-not $item) {
                    # the label is the catalog's text, the menu's name for it can differ in small ways (a mnemonic, a trimmed end): find the NES:FIXS entry by its start
                    $names = @($menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem } | ForEach-Object { $_.Current.Name })
                    $short = ($fixsLabel -split ' - ')[0]
                    $item = $menu.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -and $_.Current.Name -like "$short*" } | Select-Object -First 1
                    if (-not $item) { Write-Host "[INFO] PPU menu items found: $($names.Count): $(($names | Select-Object -First 6) -join ' || ') ; wanted [$fixsLabel]" }
                }
                if ($item) { $itemName = $item.Current.Name; try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); $clicked = $true } catch { } }
                try { $menu.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
            }
        }
        $stillFront = [SmokeWin]::GetForegroundWindow() -eq (Get-Process -Id $id).MainWindowHandle
        $bm = Wait-Frame $id { param($x) $x.Width -eq 256 -and $x.Height -eq 240 } 14
        $s = Get-ConsoleStatus $id
        if (-not $clicked -and -not $stillFront) { NotRun 'ui_ppu_menu_swaps_the_picture' "the app lost the foreground to [$([SmokeWin]::Title())] before the menu could be driven" }
        else {
            Check 'ui_ppu_menu_swaps_the_picture' ($clicked -and $s.ppu -eq 'NES:FIXS' -and "$($s.pictureBridge)" -like '*NES:FIXS*' -and $bm.Width -eq 256 -and $bm.Height -eq 240 -and $bm.Hash -ne $baseUi.Hash) "menu item [$itemName] invoked=$clicked; ppu=$($s.ppu) bridge=[$($s.pictureBridge)] picture $($bm.Width)x$($bm.Height), differs from the native one: $($bm.Hash -ne $baseUi.Hash)"
        }
        Set-ConsoleCore $id 'ppu' 'SMS' | Out-Null     # leave the console on its own chip
    }

    # ---- a file no console recognises: a box that says what it is not and what BrokenNes plays, and the game that was running goes on ----
    Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms 'smoke.sms') | Out-Null
    Wait-Picture $id 5 | Out-Null; Start-Sleep -Milliseconds 800
    $junk = Join-Path $roms 'notes.txt'; [IO.File]::WriteAllText($junk, "this is not a ROM`n")
    $gameBefore = (Get-ConsoleStatus $id).game
    try { Load-BrokenNesRom -ProcessId $id -Path $junk -TimeoutSec 6 | Out-Null } catch { }      # the call waits for the box to be closed: it times out, the box is read, then closed
    Start-Sleep -Milliseconds 600
    $boxText = ''
    foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $id)), (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770'))))))
    { $boxText += ' ' + (($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' ') }
    $closed = @(Dismiss-BrokenNesDialogs -ProcessId $id)
    Check 'unrecognised_file_says_what_it_plays' ($closed.Count -ge 1 -and $boxText -match 'does not recognise' -and $boxText -match 'notes\.txt' -and $boxText -match '\.nes' -and $boxText -match '\.sms' -and $boxText -match '\.gg' -and $boxText -match '\.md' -and $boxText -match 'zip') "box(es) closed: $($closed.Count); text: [$($boxText.Trim() -replace '\s+', ' ')]"
    Start-Sleep -Milliseconds 800
    $s = Get-ConsoleStatus $id
    Check 'unrecognised_file_leaves_the_game_running' ($s.console -eq 'sms' -and $s.game -eq $gameBefore -and (Test-InputChangesPicture $id (Get-Frame $id) -Button @('Right') -Seconds 10)) "console=$($s.console) game=[$($s.game)] (was [$gameBefore]); the program still answers the pad"

    $alive = [bool](Get-Process -Id $id -ErrorAction SilentlyContinue)
    $health = $false; if ($alive) { try { $health = [bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health') } catch { } }
    Check 'on_no_crash' ($alive -and $health) "alive=$alive api=$health"
    $r = Stop-BrokenNesInstance -ProcessId $id -TimeoutSeconds 25
    Check 'on_graceful_shutdown' ($r.Stopped -and $r.Method -in 'CloseMainWindow', 'already-exited') "method=$($r.Method) stopped=$($r.Stopped)"
    $inst = $null
    if ($haveBackup) { Copy-Item -LiteralPath $configBackup -Destination $configPath -Force }

    if (-not $SwapsOnly) {
    # ================= battery saves, in the shipped default (switch off) =================
    $env:BROKENNES_SEGA = $null
    $saves = @(@{ Key = 'sms'; File = 'battery.sms'; Dir = 'SmsSaves'; Sha = $batterySms }, @{ Key = 'gg'; File = 'battery.gg'; Dir = 'GgSaves'; Sha = $batteryGg })
    foreach ($s in $saves) { $p = Get-SavePath $s.Dir $s.Sha; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
    foreach ($pass in 1, 2) {
        $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
        $id = $inst.ProcessId
        $dlg = Clear-StartupDialogs $id; if ($dlg.Count) { $notes.Add("battery pass ${pass}: dismissed start-up dialog(s): " + ($dlg -join ', ')) }
        Show-BrokenNesEmulator -ProcessId $id | Out-Null; Start-Sleep -Milliseconds 800
        foreach ($s in $saves) {
            $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms $s.File)
            if ($pass -eq 1) { $f = Wait-Frame $id { param($x) Is-Red $x.Mid } } else { $f = Wait-Frame $id { param($x) Is-Green $x.Mid } }
            $want = if ($pass -eq 1) { 'red (no save yet)' } else { 'green (the save was found)' }
            $got = "R=$($f.Mid.R) G=$($f.Mid.G) B=$($f.Mid.B)"
            Check "battery_$($s.Key)_pass${pass}_shows_$(if ($pass -eq 1) { 'red' } else { 'green' })" ($l.success -and $(if ($pass -eq 1) { Is-Red $f.Mid } else { Is-Green $f.Mid })) "load success=$($l.success); wanted $want, picture centre $got"
            # loading the next ROM unloads this one, which writes its save; the file is checked after the app closes below
        }
        $r = Stop-BrokenNesInstance -ProcessId $id -TimeoutSeconds 25
        Check "battery_pass${pass}_graceful_shutdown" ($r.Stopped -and $r.Method -in 'CloseMainWindow', 'already-exited') "method=$($r.Method)"
        $inst = $null
        foreach ($s in $saves) {
            $p = Get-SavePath $s.Dir $s.Sha
            $b = if (Test-Path -LiteralPath $p) { [IO.File]::ReadAllBytes($p) } else { $null }
            Check "battery_$($s.Key)_pass${pass}_save_file" ($null -ne $b -and $b.Length -eq 0x8000 -and $b[0] -eq 0x5A -and $b[1] -eq 0xA5 -and $b[2] -eq $pass) $(if ($null -eq $b) { "no file at $p" } else { "${p}: $($b.Length) bytes, first bytes $('{0:X2} {1:X2} {2}' -f $b[0], $b[1], $b[2]), count should be $pass" })
        }
    }
    foreach ($s in $saves) { $p = Get-SavePath $s.Dir $s.Sha; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
    }   # -not $SwapsOnly
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "ERROR: $($result.error)"
}
finally {
    if ($inst) {
        $r = Stop-BrokenNesInstance -ProcessId $inst.ProcessId -TimeoutSeconds 25
        Check 'on_graceful_shutdown' ($r.Stopped -and $r.Method -in 'CloseMainWindow', 'already-exited') "method=$($r.Method) stopped=$($r.Stopped)"
    }
    $env:BROKENNES_SEGA = $null
    if ($haveBackup) { Copy-Item -LiteralPath $configBackup -Destination $configPath -Force; $notes.Add('config.json restored') }
    $result.checks = $checks
    $result.notes = $notes
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($checks.Values | Where-Object { -not $_.ok -and -not $_.notRun }).Count
    $notRun = @($checks.Values | Where-Object { $_.notRun }).Count
    $result.pass = (-not $result.Contains('error')) -and $checks.Count -ge $(if ($SwapsOnly) { 20 } else { 60 }) -and $failed -eq 0 -and $notRun -eq 0
    # a check that could not be made (the menu check while another window holds the foreground) is not a failure of the app: the run says NOT RUN, exit 0, and names it
    if ($failed -eq 0 -and $notRun -gt 0 -and -not $result.Contains('error')) { $result.status = 'NOT RUN'; $result.error = "$notRun check(s) not run: $((@($checks.GetEnumerator() | Where-Object { $_.Value.notRun } | ForEach-Object { $_.Key })) -join ', ') (the app window could not be put in front)" }
    Write-Host ($result | ConvertTo-Json -Depth 6 -Compress)
    exit $(if ($result.pass -or ($result.Contains('status') -and $result.status -eq 'NOT RUN')) { 0 } else { 1 })
}

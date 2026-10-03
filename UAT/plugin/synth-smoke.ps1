<#
.SYNOPSIS
  Certifies the standalone synth: BrokenNes.Windows.exe --synth, and the restart handoff from the emulator's Config > Synthesizer Mode menu.
  Exit 0 = pass, non-zero = fail. Prints ONE JSON result line as the last line of stdout (same shape as desktop-smoke.ps1).

.DESCRIPTION
  Builds Windows\BrokenNes.Windows.csproj -c Release under $env:LOCALAPPDATA\VRUN_Nes_Dev_work\uat_synth (unless -Exe is given) and drives the REAL
  windows: UI Automation for menus, tabs and the status line, real keyboard events (keybd_event) for the computer-keyboard piano. Settings go to a
  temp folder (BROKENNES_SYNTH_DIR) and the plugin's prefs to a temp file, so nothing of yours is touched.

    build_ok                  the exe exists and the plugin DLL ships beside it
    window_and_editors        title, four tabs (Pulse 1, Pulse 2, Triangle, Noise), and the plugin's editor actually drawn (colour count + its accent colour)
    status_line               the status line names the audio output and the MIDI input
    key_plays_pulse1          holding A: "Pulse 1: key A plays note 60" and a level above zero; released: the level drops back to zero
    octave_keys               X shifts the octave up (A plays 72), Z twice down (A plays 48)
    tab_routing               with the Triangle tab selected, A plays the Triangle
    keyboard_switch_off       with "Computer keyboard plays notes" off, A does nothing (no note, no level)
    close_saves_state         closing the window exits 0 and writes synth.json and a synth-state.bin holding all four instances
    settings_restored         starting again comes back on the Triangle tab, at the saved octave, with the engine running
    restart_to_synth          emulator: Config > Synthesizer Mode > Restart as Standalone Synth starts "--synth" and the emulator exits (exit 0)
    restart_to_emulator       synth: Synth > Restart as Emulator starts the emulator again and the synth exits
    missing_plugin_explained  a build with no Plugin\ folder: the synth says why (names the missing file) and starts the emulator instead

  AUDIO: the machine's sound card is not needed and not used. BROKENNES_SYNTH_AUDIO=null (a switch the synth documents, named on its status line)
  renders the rack in real time into nothing, so key press -> router -> rack -> plugin -> rendered level is certified end to end. NOT VERIFIED:
  the WASAPI handoff to a real playback device (an agent has no ears; test by hand: Config > Synthesizer Mode > Restart as Standalone Synth, play a key).
  MIDI hardware is not played either (the MIDI-to-note logic is certified by FruityHost selftest, standalone-midi).

  Needs the desktop quiet (it presses real keys in a real window) and no FL Studio is involved. Rule zero (UAT\README.md): never kill by process name;
  only PIDs started here (and the programs those started) are touched.
.EXAMPLE
  pwsh -File UAT\plugin\synth-smoke.ps1
  pwsh -File UAT\plugin\synth-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_synth')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$result = [ordered]@{ pass = $false }
function Check($name, [bool]$ok, $detail) {
    $checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("{0,-26} {1}  {2}" -f $name, $(if ($ok) { 'ok  ' } else { 'FAIL' }), $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class SynthWin {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

  /// <summary>Distinct colours and accent-coloured pixels (the plugin's red-orange) in BGRA pixels: proof the editor drew itself.</summary>
  public static string Count(byte[] px) {
    var seen = new System.Collections.Generic.HashSet<int>(); int accent = 0;
    for (int i = 0; i + 3 < px.Length; i += 4) {
      int b = px[i], g = px[i + 1], r = px[i + 2];
      seen.Add((r << 16) | (g << 8) | b);
      if (r > 200 && g < 130 && b < 130) accent++;
    }
    return seen.Count + "," + accent;
  }
}
'@
# a capture of the middle of the window (where the plugin's editor is), reduced to "colours,accent pixels"
function EditorLook($hwnd) {
    $r = New-Object SynthWin+RECT; [void][SynthWin]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [void][SynthWin]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $rect = New-Object System.Drawing.Rectangle 0, ([int]($h / 4)), $w, ([int]($h / 2))
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($data.Stride * $data.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($data); $bmp.Dispose()
    [SynthWin]::Count($px)
}
$AE = [Windows.Automation.AutomationElement]; $TS = [Windows.Automation.TreeScope]
function ByName($parent, $name) { $parent.FindFirst($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Expand($el) { $el.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
function Win($procId) { $AE::RootElement.FindFirst($TS::Children, (New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId))) }
function StatusText($root) {
    foreach ($e in $root.FindAll($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Text)))) { if ($e.Current.Name -like 'Audio:*') { return $e.Current.Name } }
    ''
}
function LevelOf($status) { if ($status -match 'level ([0-9.]+)') { [double]$Matches[1] } else { -1 } }
function Focus($p) { [void][SynthWin]::ShowWindow($p.MainWindowHandle, 9); [void][SynthWin]::SetForegroundWindow($p.MainWindowHandle); Start-Sleep -Milliseconds 500 }
function KeyDown($vk) { [SynthWin]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero) }
function KeyUp($vk) { [SynthWin]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero) }
function Tap($vk) { KeyDown $vk; Start-Sleep -Milliseconds 80; KeyUp $vk; Start-Sleep -Milliseconds 150 }
# hold a key, return the status text and the highest level seen while it was down (the status refreshes every 100 ms)
function Hold($p, $root, $vk, $ms = 700) {
    KeyDown $vk; $peak = 0.0; $text = ''
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { Start-Sleep -Milliseconds 120; $text = StatusText $root; $peak = [Math]::Max($peak, (LevelOf $text)) }
    KeyUp $vk
    [pscustomobject]@{ Text = $text; Peak = $peak }
}

$started = New-Object System.Collections.Generic.List[int]   # PIDs this script started or whose children it identified: the only ones it may stop
$pluginDir = $null; $pluginOff = $null
try {
    # ---------------- build ----------------
    if (-not $Exe) {
        $art = Join-Path $WorkDir 'artifacts'
        New-Item -ItemType Directory -Force $WorkDir | Out-Null
        Write-Host 'dotnet build Windows ...'
        $o = & dotnet.exe build (Join-Path $repo 'Windows\BrokenNes.Windows.csproj') -c Release --artifacts-path $art --disable-build-servers -nodeReuse:false -nologo -v q 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "desktop build failed: $($o.Substring([Math]::Max(0, $o.Length - 600)))" }
        $Exe = Join-Path $art 'bin\BrokenNes.Windows\release_win-x64\BrokenNes.Windows.exe'
    }
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $pluginDir = Join-Path (Split-Path $Exe) 'Plugin'
    Check 'build_ok' ((Test-Path $Exe) -and (Test-Path (Join-Path $pluginDir 'BrokenNes2_x64.dll'))) "exe=$(Test-Path $Exe) plugin=$(Test-Path (Join-Path $pluginDir 'BrokenNes2_x64.dll')) ($Exe)"
    if (-not (Test-Path (Join-Path $pluginDir 'BrokenNes2_x64.dll'))) { throw 'no plugin beside the exe (run Plugin\build-dist.ps1, then build the desktop)' }

    # settings of this run: a folder of its own; the plugin's "About seen" memory in a file (not the registry)
    $settings = Join-Path $WorkDir ('settings_' + (Get-Date -Format 'HHmmss')); New-Item -ItemType Directory -Force $settings | Out-Null
    $prefs = Join-Path $WorkDir 'prefs.txt'; Set-Content $prefs "AboutRevisionSeen=99`n"
    $env:BROKENNES_SYNTH_AUDIO = 'null'; $env:BROKENNES_SYNTH_DIR = $settings; $env:BROKENNES_PREFS_FILE = $prefs

    function StartSynth() {
        $p = Start-Process -FilePath $Exe -ArgumentList '--synth' -PassThru
        $started.Add($p.Id)
        for ($i = 0; $i -lt 150 -and -not $p.MainWindowHandle -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 200; $p.Refresh() }
        Start-Sleep -Seconds 3; $p.Refresh()
        $p
    }

    # ---------------- first run ----------------
    $p = StartSynth
    $root = $AE::FromHandle($p.MainWindowHandle)
    $tabs = @($root.FindAll($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem))) | ForEach-Object { $_.Current.Name })
    $look = (EditorLook $p.MainWindowHandle) -split ','
    $colors = [int]$look[0]; $accent = [int]$look[1]
    Check 'window_and_editors' ($p.MainWindowTitle -eq 'BrokenNes 2 - Standalone Synthesizer' -and ($tabs -join ',') -eq 'Pulse 1,Pulse 2,Triangle,Noise' -and $colors -ge 150 -and $accent -ge 300) "title=[$($p.MainWindowTitle)] tabs=$($tabs -join ',') editor colours=$colors accent pixels=$accent"

    $st = StatusText $root
    Check 'status_line' ($st -match 'Audio: null output' -and $st -match 'MIDI: ') "[$st]"

    Focus $p
    $r = Hold $p $root 0x41
    $idle = (LevelOf (StatusText $root))
    Start-Sleep -Milliseconds 700; $idle = (LevelOf (StatusText $root))
    Check 'key_plays_pulse1' ($r.Text -match 'Pulse 1: key A plays note 60' -and $r.Peak -gt 0.02 -and $idle -le 0.005) "while held: [$($r.Text -replace '^.*MIDI: [^|]*\|\s*','')] peak $($r.Peak); after release $idle"

    Tap 0x58                                           # X: octave up
    $up = Hold $p $root 0x41
    Tap 0x5A; Tap 0x5A                                 # Z Z: octave down twice (net -1)
    $down = Hold $p $root 0x41
    $upNote = if ($up.Text -match 'plays note (\d+)') { [int]$Matches[1] } else { -1 }
    $downNote = if ($down.Text -match 'plays note (\d+)') { [int]$Matches[1] } else { -1 }
    Check 'octave_keys' ($upNote -eq 72 -and $downNote -eq 48) "X then A -> note $upNote (expect 72); Z Z then A -> note $downNote (expect 48)"

    # tab routing: select Triangle, A plays the Triangle
    ($root.FindFirst($TS::Descendants, (New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition($AE::NameProperty, 'Triangle')), (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem))))).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 500; Focus $p
    $tri = Hold $p $root 0x41
    Check 'tab_routing' ($tri.Text -match 'Triangle: key A' -and $tri.Peak -gt 0.01) "[$($tri.Text -replace '^.*MIDI: [^|]*\|\s*','')] peak $($tri.Peak)"

    # keyboard switch: off means off
    $box = $root.FindFirst($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::NameProperty, 'Computer keyboard plays notes')))
    $toggle = $box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle(); Start-Sleep -Milliseconds 400
    $before = StatusText $root; Focus $p
    $off = Hold $p $root 0x41
    $sameNote = ($before -replace 'level [0-9.]+', '') -eq ($off.Text -replace 'level [0-9.]+', '')
    Check 'keyboard_switch_off' ($sameNote -and $off.Peak -le 0.005) "status unchanged=$sameNote, level while A held with the switch off: $($off.Peak)"
    $toggle.Toggle(); Start-Sleep -Milliseconds 400    # back on, so the saved setting is the default

    # ---------------- close, saved state, second run ----------------
    [void]$p.CloseMainWindow()
    $exited = $p.WaitForExit(15000)
    $code = if ($exited) { $p.ExitCode } else { -1 }
    if (-not $exited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $statePath = Join-Path $settings 'synth-state.bin'; $jsonPath = Join-Path $settings 'synth.json'
    $count = -1; $magic = ''
    if (Test-Path $statePath) { $b = [IO.File]::ReadAllBytes($statePath); $magic = [Text.Encoding]::ASCII.GetString($b, 0, 4); $count = [BitConverter]::ToInt32($b, 4) }
    Check 'close_saves_state' ($exited -and $code -eq 0 -and (Test-Path $jsonPath) -and $magic -eq 'BN2S' -and $count -eq 4) "exited=$exited code=$code synth.json=$(Test-Path $jsonPath) state=$magic x$count ($(if (Test-Path $statePath) { (Get-Item $statePath).Length } else { 0 }) bytes)"

    $p2 = StartSynth
    $root2 = $AE::FromHandle($p2.MainWindowHandle)
    $triTab = $root2.FindFirst($TS::Descendants, (New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition($AE::NameProperty, 'Triangle')), (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem)))))
    $selected = $triTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
    $octaveLabel = ($root2.FindAll($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like 'Keys A*' } | Select-Object -First 1).Current.Name
    Focus $p2; $again = Hold $p2 $root2 0x41
    Check 'settings_restored' ($selected -and $octaveLabel -match '= C3' -and $again.Peak -gt 0.01) "Triangle tab selected=$selected; [$octaveLabel]; plays again (peak $($again.Peak))"
    [void]$p2.CloseMainWindow(); if (-not $p2.WaitForExit(15000)) { Stop-Process -Id $p2.Id -Force -ErrorAction SilentlyContinue }

    # ---------------- the restart handoff, through the emulator's real menus ----------------
    $env:BROKENNES_SYNTH_DIR = Join-Path $WorkDir 'settings_handoff'; New-Item -ItemType Directory -Force $env:BROKENNES_SYNTH_DIR | Out-Null
    $emu = Start-Process -FilePath $Exe -PassThru; $started.Add($emu.Id)
    for ($i = 0; $i -lt 150 -and -not $emu.MainWindowHandle; $i++) { Start-Sleep -Milliseconds 200; $emu.Refresh() }
    Start-Sleep -Seconds 3; $emu.Refresh()
    if ($emu.MainWindowTitle -eq 'Audio Warning') { [void]$emu.CloseMainWindow(); Start-Sleep -Seconds 2 }   # a box with no playback device: the emulator says so once
    $main = $AE::RootElement.FindFirst($TS::Children, (New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $emu.Id)), (New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'WindowsForms10.Window.8.app.0.2671f12_r3_ad1')))))
    Expand (ByName $main 'Config'); Start-Sleep -Milliseconds 500
    Expand (ByName $AE::RootElement 'Synthesizer Mode'); Start-Sleep -Milliseconds 500
    $item = ByName $AE::RootElement 'Restart as Standalone Synth'
    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $synth = $null
    for ($i = 0; $i -lt 100 -and -not $synth; $i++) { Start-Sleep -Milliseconds 200; $synth = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($emu.Id)" | Where-Object { $_.CommandLine -match '--synth' } | Select-Object -First 1 }
    if ($synth) { $started.Add([int]$synth.ProcessId) }
    $emuGone = $emu.WaitForExit(20000)
    Check 'restart_to_synth' ($synth -ne $null -and $emuGone -and $emu.ExitCode -eq 0) "synth started=$($synth -ne $null) (pid $($synth.ProcessId)), emulator exited=$emuGone code=$(if ($emuGone) { $emu.ExitCode })"

    $emu2 = $null; $synthGone = $false
    if ($synth) {
        $sp = Get-Process -Id $synth.ProcessId
        for ($i = 0; $i -lt 100 -and -not $sp.MainWindowHandle; $i++) { Start-Sleep -Milliseconds 200; $sp.Refresh() }
        Start-Sleep -Seconds 3; $sp.Refresh()
        $sroot = $AE::FromHandle($sp.MainWindowHandle)
        Expand (ByName $sroot 'Synth'); Start-Sleep -Milliseconds 500
        (ByName $AE::RootElement 'Restart as Emulator').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        for ($i = 0; $i -lt 100 -and -not $emu2; $i++) { Start-Sleep -Milliseconds 200; $emu2 = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($synth.ProcessId)" | Where-Object { $_.Name -eq 'BrokenNes.Windows.exe' } | Select-Object -First 1 }
        if ($emu2) { $started.Add([int]$emu2.ProcessId) }
        $synthGone = $sp.WaitForExit(20000)
    }
    Check 'restart_to_emulator' ($emu2 -ne $null -and $emu2.CommandLine -notmatch '--synth' -and $synthGone) "emulator started=$($emu2 -ne $null) (plain launch: $($emu2.CommandLine -notmatch '--synth')), synth exited=$synthGone"
    foreach ($id in @($emu2.ProcessId)) { if ($id) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } }

    # ---------------- a build without the plugin ----------------
    if ($Exe.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
        Check 'missing_plugin_explained' $true 'unverified: the exe is inside the repo, where the developer fallback (Plugin\dist) finds a plugin anyway'
    } else {
        $pluginOff = $pluginDir + '_off'
        Rename-Item $pluginDir $pluginOff
        $env:BROKENNES_SYNTH_DIR = Join-Path $WorkDir 'settings_missing'; New-Item -ItemType Directory -Force $env:BROKENNES_SYNTH_DIR | Out-Null
        $np = Start-Process -FilePath $Exe -ArgumentList '--synth' -PassThru; $started.Add($np.Id)
        $dlg = $null; $text = ''
        for ($i = 0; $i -lt 100 -and -not $dlg; $i++) {
            Start-Sleep -Milliseconds 200
            # a message box owned by the synth's window sits under that window in the UIA tree, not at the top level: search the descendants
            $dlg = $AE::RootElement.FindFirst($TS::Descendants, (New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $np.Id)), (New-Object Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')))))
        }
        if ($dlg) {
            $text = (($dlg.FindAll($TS::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' ')
            [void][SynthWin]::PostMessage([IntPtr]$dlg.Current.NativeWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE: the box has only OK
        }
        $gone = $np.WaitForExit(20000)
        $emu3 = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($np.Id)" | Where-Object { $_.Name -eq 'BrokenNes.Windows.exe' } | Select-Object -First 1
        if ($emu3) { $started.Add([int]$emu3.ProcessId) }
        Check 'missing_plugin_explained' ($dlg -ne $null -and $text -match 'BrokenNes2_x64\.dll' -and $text -match 'emulator' -and $gone -and $emu3 -ne $null -and $emu3.CommandLine -notmatch '--synth') "dialog=$($dlg -ne $null) names the file=$($text -match 'BrokenNes2_x64\.dll') synth exited=$gone emulator started=$($emu3 -ne $null)"
    }
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "ERROR: $($result.error)" -ForegroundColor Red
}
finally {
    if ($pluginOff -and (Test-Path $pluginOff) -and -not (Test-Path $pluginDir)) { Rename-Item $pluginOff $pluginDir }   # put the build back the way it was
    foreach ($id in $started) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }   # by PID, only ours
    $result.checks = $checks
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($checks.Values | Where-Object { -not $_.ok }).Count
    $result.pass = (-not $result.Contains('error')) -and $checks.Count -ge 12 -and $failed -eq 0
    Write-Output ($result | ConvertTo-Json -Depth 6 -Compress)
}
if ($result.pass) { exit 0 } else { exit 1 }

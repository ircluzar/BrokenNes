<#
.SYNOPSIS
  Live check of the d-pad protection in the real desktop app: real key presses into the real window, read back from the HTTP API.
  Exit 0 = every check that ran passed, 2 = NOT RUN (the app could not be given the keyboard), non-zero otherwise. Ends with ONE JSON result line.

.DESCRIPTION
  1. Starts the desktop exe (-Exe) with the machine's own config (this script assumes what a default install has: Player 1 on the keyboard with the arrow keys, d-pad protection
     at its default, which is ON; with other bindings the key steps FAIL, they are not skipped), shows the emulator, loads the repository's own tiny test ROM.
  2. API input is untouched: POST /api/input/set-buttons with Left+Right+Up+Down; GET /api/input/state then reports all four held (what the NES was handed).
  3. Human input is protected: the window is brought to the foreground and REAL key presses (keybd_event) are sent: Left alone reads Left; Left+Right reads neither;
     Right let go reads Left again; Up+Down reads neither; Up+Down+Right reads Right. The keys are always released at the end. The keys are only sent while the app's window
     is the foreground window (checked before every step); otherwise the live part is NOT RUN (never PASS, never FAIL).
  Not verified: a real XInput pad, the setting turned OFF in the controller window (that is `Workshop --input-test`, on the same InputManager code), other consoles.

  Rule zero (UAT\README.md): never kill by process name; only the PID started here is touched. Arrow keys are typed into the foreground window, so run it while the desktop is quiet.
.EXAMPLE
  pwsh -File UAT\entrypoints\dpad-live.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Rom,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_dpad_live')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Rom) { $Rom = Join-Path $repo 'Windows\Resources\test.nes' }
. (Join-Path $repo 'UAT\lib\ApiClient.ps1')
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'dpad-live'; pass = $false }
function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class DpadLiveKeys {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  public const uint Extended = 1, KeyUp = 2;
  public static void Key(byte vk, bool down) { keybd_event(vk, 0, Extended | (down ? 0u : KeyUp), UIntPtr.Zero); }
}
'@
$VK = @{ Left = 0x25; Up = 0x26; Right = 0x27; Down = 0x28 }
$inst = $null
$down = New-Object System.Collections.Generic.HashSet[string]
try {
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $inst = Start-BrokenNesInstance -ExePath $Exe
    $pid_ = $inst.ProcessId
    Show-BrokenNesEmulator -ProcessId $pid_ | Out-Null
    $r = Load-BrokenNesRom -ProcessId $pid_ -Path $Rom
    Check 'rom_loaded' ([bool]$r.success) "$Rom"
    Start-Sleep -Milliseconds 1500
    function State { (Invoke-BrokenNesApi -ProcessId $pid_ -Method GET -Path '/api/input/state').players[0] }
    function Held { @((State).held) -join '+' }
    $st = Invoke-BrokenNesApi -ProcessId $pid_ -Method GET -Path '/api/input/state'
    Check 'emulation_active' ([bool]$st.emulationActive) "frames=$($st.frameCount)"

    # ---- API input is untouched
    Invoke-BrokenNesApi -ProcessId $pid_ -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @('Left', 'Right', 'Up', 'Down'); holdMs = 5000 } | Out-Null
    Start-Sleep -Milliseconds 400
    $held = (Held)
    Check 'api_all_four_directions_reach_the_game' (($held -split '\+' | Where-Object { $_ -in 'Left', 'Right', 'Up', 'Down' }).Count -eq 4) "held = $held"
    Invoke-BrokenNesApi -ProcessId $pid_ -Method POST -Path '/api/input/clear' | Out-Null
    Start-Sleep -Milliseconds 300
    Check 'api_cleared' ((Held) -eq '') "held = '$(Held)'"

    # ---- human input: real keys
    $hwnd = $inst.Process.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { $hwnd = (Get-Process -Id $pid_).MainWindowHandle }
    [void][DpadLiveKeys]::ShowWindow($hwnd, 9)
    [void][DpadLiveKeys]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 600
    $haveKeyboard = ([DpadLiveKeys]::GetForegroundWindow() -eq $hwnd)
    if (-not $haveKeyboard) {
        $result.status = 'NOT RUN'; $result.error = 'the app window could not be brought to the foreground (another window holds it): the real-key part was not run'
        Write-Host $result.error -ForegroundColor Yellow
    } else {
        function Press([string[]]$names) {
            foreach ($n in $names) { [DpadLiveKeys]::Key([byte]$VK[$n], $true); [void]$down.Add($n) }
        }
        function Release([string[]]$names) {
            foreach ($n in $names) { [DpadLiveKeys]::Key([byte]$VK[$n], $false); [void]$down.Remove($n) }
        }
        function StillForeground { [DpadLiveKeys]::GetForegroundWindow() -eq $hwnd }
        # what a protected pad shows for the keys that are down (written out, not computed with the app's code): an opposite pair cancels, a lone direction stays
        function Expected { $u = $down.Contains('Up'); $d = $down.Contains('Down'); $l = $down.Contains('Left'); $r = $down.Contains('Right')
            $out = @(); if ($u -and -not $d) { $out += 'Up' }; if ($d -and -not $u) { $out += 'Down' }; if ($l -and -not $r) { $out += 'Left' }; if ($r -and -not $l) { $out += 'Right' }; $out -join '+' }
        $steps = @(
            @{ name = 'left_alone_passes';       press = @('Left');          release = @() },
            @{ name = 'left_plus_right_neutral'; press = @('Right');         release = @() },
            @{ name = 'right_let_go_gives_left'; press = @();                release = @('Right') },
            @{ name = 'up_plus_down_neutral';    press = @('Up', 'Down');    release = @('Left') },
            @{ name = 'up_down_right_is_right';  press = @('Right');         release = @() }
        )
        foreach ($s in $steps) {
            if (-not (StillForeground)) { $result.status = 'NOT RUN'; $result.error = "the app lost the foreground before step $($s.name)"; break }
            Press $s.press; Release $s.release
            Start-Sleep -Milliseconds 350
            $got = (Held); $want = (Expected)
            Check $s.name ($got -eq $want) "keys down: $($down -join '+'); the game was handed: '$got' (expected '$want')"
        }
    }
    $result.pass = (@($checks.Values | Where-Object { -not $_.ok }).Count -eq 0) -and -not $result.status
}
catch {
    $result.error = "$($_.Exception.Message)"
    Write-Host "ERROR: $($result.error)" -ForegroundColor Red
}
finally {
    foreach ($n in @($down)) { [DpadLiveKeys]::Key([byte]$VK[$n], $false) }      # never leave a key held
    if ($inst) { try { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null } catch { } }
}
$result.checks = $checks
Write-Host ($result | ConvertTo-Json -Depth 6 -Compress)
if ($result.status -eq 'NOT RUN') { exit 2 }
exit $(if ($result.pass) { 0 } else { 1 })

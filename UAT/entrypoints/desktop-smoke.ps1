<#
.SYNOPSIS
  Entrypoint smoke test: BrokenNes desktop app (Windows\BrokenNes.Windows.csproj).
  Exit 0 = pass, non-zero = fail. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  1. Builds Windows\BrokenNes.Windows.csproj -c Release with --artifacts-path under
     $env:LOCALAPPDATA\VRUN_Nes_Dev_work\uat_desktop (nothing lands in the repo tree), unless -Exe is given.
  2. Launches it via UAT\lib\ApiClient.ps1 (Start-BrokenNesInstance) and drives the local HTTP API.
  3. Applies CPU/PPU/APU = FIX (mapper-30 VRUN needs the FIX CPU), navigates to the emulator view,
     loads the NES ROM, then samples for -RunSeconds.

  VERIFIED AUTOMATICALLY (each is a named check in the JSON):
    build_ok            exe exists (and, unless -Exe, the build exited 0)
    api_up              /api/health answered for OUR pid
    rom_loaded          load-rom success + /api/emulator/current-rom reports the ROM, not the test ROM
    frames_advance      /api/input/state frameCount increases by >= MinFrames over the run window and
                        emulationActive is true (rate is reported; ~60/s expected)
    picture_nonblank    /api/ppu/framebuffer RGBA: >= MinColors distinct colours and >= MinNonBlackPct
    picture_changes     framebuffer hash differs across samples taken over the run window
                        (ROM is animating / reacting; a frozen frame fails)
    no_crash            process still alive and API still answering at the end
    graceful_shutdown   CloseMainWindow by PID; exits within timeout (forced kill => FAIL)
    audio               see below

  AUDIO - what the API does and does not expose:
    The HTTP API has NO endpoint for the emulator's APU output (no sample tap, no level).
    /api/audio/status and friends control only the *game's* music/SFX engine (isMusicPlaying etc.),
    and /api/apu/channels returns hard-coded constants - neither says anything about NES audio.
    So audio is checked out-of-band: the Windows Core Audio session meter (IAudioMeterInformation)
    for the app's own PID is polled while the ROM runs; audio passes if any peak > 0 is seen.
    This proves the app opened an audio session and pushed non-silent samples to the device. If no
    session/output device exists the check is reported as "unverified" (not a failure) unless
    -RequireAudio is passed.

  NOT VERIFIED: pixel/audio *correctness* (only "alive, animated, non-trivial"), input handling,
  other mappers/cores, the WebView2 front-end screens.

  Rule zero (UAT\README.md): never kill by process name. Only the PID started here is touched, and
  foreign BrokenNes instances are merely counted.

.EXAMPLE
  pwsh -File UAT\entrypoints\desktop-smoke.ps1
  pwsh -File UAT\entrypoints\desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Rom = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\builds\2026-09-27_nes\vrun_game.nes',
    [int]$RunSeconds = 8,
    [int]$MinFrames = 120,
    [int]$MinColors = 3,
    [double]$MinNonBlackPct = 5,
    [switch]$RequireAudio,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$notes = New-Object System.Collections.Generic.List[string]
$result = [ordered]@{ entrypoint = 'desktop'; pass = $false }
$inst = $null

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

# ---- Core Audio peak meter for one PID (best effort) ----
$audioSrc = @'
using System; using System.Runtime.InteropServices;
public static class UatAudio {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDevEnumComObj {}
  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator { int EnumAudioEndpoints(int f,int m,out IntPtr d); int GetDefaultAudioEndpoint(int f,int r,out IMMDevice d); }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice { int Activate(ref Guid id,int ctx,IntPtr p,[MarshalAs(UnmanagedType.IUnknown)] out object o); }
  [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionManager2 { int a(); int b(); int GetSessionEnumerator(out IAudioSessionEnumerator e); }
  [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionEnumerator { int GetCount(out int c); int GetSession(int i,out IAudioSessionControl s); }
  [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionControl { }
  [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionControl2 { int a(); int b(); int c(); int d(); int e(); int f(); int g(); int h(); int i(); int j(); int k(); int GetProcessId(out uint pid); }
  [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioMeterInformation { int GetPeakValue(out float p); }
  public static string Sessions() {
    var en = (IMMDeviceEnumerator)new MMDevEnumComObj();
    IMMDevice dev; if (en.GetDefaultAudioEndpoint(0,1,out dev) != 0) return "noendpoint";
    object o; var g = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    dev.Activate(ref g, 23, IntPtr.Zero, out o);
    var mgr = (IAudioSessionManager2)o; IAudioSessionEnumerator se; mgr.GetSessionEnumerator(out se);
    int n; se.GetCount(out n); string r = "n=" + n;
    for (int i=0;i<n;i++){ IAudioSessionControl s; se.GetSession(i,out s); var s2=(IAudioSessionControl2)s; uint p; s2.GetProcessId(out p); float v=0; ((IAudioMeterInformation)s).GetPeakValue(out v); r += " [" + p + ":" + v + "]"; }
    return r;
  }
  // returns -2 = no endpoint, -1 = no session for pid, else peak 0..1
  public static float Peak(int pid) {
    var en = (IMMDeviceEnumerator)new MMDevEnumComObj();
    IMMDevice dev; if (en.GetDefaultAudioEndpoint(0,1,out dev) != 0) return -2;
    object o; var g = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    dev.Activate(ref g, 23, IntPtr.Zero, out o);
    var mgr = (IAudioSessionManager2)o; IAudioSessionEnumerator se; mgr.GetSessionEnumerator(out se);
    int n; se.GetCount(out n); float best = -1;
    for (int i=0;i<n;i++){ IAudioSessionControl s; se.GetSession(i,out s);
      var s2=(IAudioSessionControl2)s; uint p; s2.GetProcessId(out p); if (p!=(uint)pid) continue;
      float v=0; ((IAudioMeterInformation)s).GetPeakValue(out v); if (v>best) best=v; }
    return best;
  }
}
'@
$audioOk = $true
try { Add-Type -TypeDefinition $audioSrc -ErrorAction Stop } catch { $audioOk = $false; $notes.Add("audio meter type failed to compile: $($_.Exception.Message)") }

try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    if (-not (Test-Path -LiteralPath $Rom)) { throw "ROM not found: $Rom" }

    # ---------------- build ----------------
    if (-not $Exe) {
        $art = Join-Path $WorkDir 'artifacts'
        New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
        $log = Join-Path $WorkDir 'build.log'
        Write-Host "building Windows\BrokenNes.Windows.csproj (log: $log) ..."
        $bsw = [Diagnostics.Stopwatch]::StartNew()
        & dotnet build (Join-Path $repo 'Windows\BrokenNes.Windows.csproj') -c Release --artifacts-path $art -nologo -v q *> $log
        $code = $LASTEXITCODE
        $found = Get-ChildItem -Path $art -Recurse -Filter BrokenNes.Windows.exe -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Check 'build_ok' ($code -eq 0 -and $null -ne $found) ("dotnet build exit $code in {0:n0}s; exe=$($found.FullName)" -f $bsw.Elapsed.TotalSeconds)
        if ($code -ne 0 -or -not $found) { throw "build failed, see $log" }
        $Exe = $found.FullName
    } else {
        Check 'build_ok' (Test-Path -LiteralPath $Exe) "using supplied -Exe $Exe"
        if (-not (Test-Path -LiteralPath $Exe)) { throw 'exe missing' }
    }
    $result.exe = $Exe; $result.rom = $Rom

    # ---------------- launch ----------------
    $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
    $id = $inst.ProcessId
    $result.pid = $id
    $h = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health'
    Check 'api_up' ([bool]$h) "pid $id on $($inst.BaseUrl)"
    $foreign = @(Get-BrokenNesInstances | Where-Object { $_.ProcessId -ne $id }).Count
    if ($foreign) { $notes.Add("$foreign foreign BrokenNes instance(s) running (left untouched)") }

    # FIX cores: mapper 30 crashes CPU_FMC on frame 0 (see UAT README). Normal (gated) setter - persists to config.json.
    # Only swap what differs: in testing, calling /api/cores/apply with a PPU or APU id (even the one
    # already active) left the process unable to shut down gracefully (WM_CLOSE -> OnFormClosing, then hang).
    # NOTE the selection is persisted to %APPDATA%\BrokenNes\config.json (shared by every build on this machine).
    Show-BrokenNesEmulator -ProcessId $id | Out-Null     # loop only runs in emulator view; core ids are empty until then
    Start-Sleep -Milliseconds 800
    $want = @{ cpu = 'CPU_FIX'; ppu = 'PPU_FIX'; apu = 'APU_FIX' }
    $swap = @{}
    foreach ($k in 'cpu','ppu','apu') {
        if ((Get-BrokenNesCore -ProcessId $id -Kind $k) -ne $want[$k]) { $swap[(Get-Culture).TextInfo.ToTitleCase($k) + 'Id'] = 'FIX' }
    }
    if ($swap.Count) {
        $notes.Add("hot-swapped cores: $($swap.Keys -join ',') (may make graceful shutdown hang - known app issue)")
        $c = Set-BrokenNesCores -ProcessId $id @swap
        if (-not $c.success) { $notes.Add("set cores: $($c.error)") }
    }
    $l = Load-BrokenNesRom -ProcessId $id -Path $Rom
    Start-Sleep -Seconds 2
    $cur = Get-BrokenNesCurrentRom -ProcessId $id
    $coreStr = (@('cpu','ppu','apu' | ForEach-Object { Get-BrokenNesCore -ProcessId $id -Kind $_ }) -join '/')
    Check 'rom_loaded' ($l.success -and -not $cur.isTestRom) "load success=$($l.success) current=$($cur.name) isTestRom=$($cur.isTestRom) cores=$coreStr"

    # ---------------- run + sample ----------------
    $s0 = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/input/state'
    $t0 = Get-Date
    $hashes = @{}; $maxColors = 0; $maxNonBlack = 0.0; $peak = -1.0
    $end = $t0.AddSeconds($RunSeconds)
    $png = Join-Path $WorkDir 'desktop-frame.png'
    $sha1 = [Security.Cryptography.SHA1]::Create()
    $i = 0
    while ((Get-Date) -lt $end) {
        if ($audioOk) { try { $p = [UatAudio]::Peak($id); if ($p -gt $peak) { $peak = $p } } catch { $audioOk = $false; $notes.Add("audio meter: $($_.Exception.Message)") } }
        if ($i % 4 -eq 0) {
            $fbArgs = @{ ProcessId = $id }
            if ($i -eq 0) { $fbArgs.OutPng = $png }
            $fb = Get-BrokenNesFramebuffer @fbArgs
            $hashes[[BitConverter]::ToString($sha1.ComputeHash($fb.Bytes))] = 1
            $maxColors = [math]::Max($maxColors, $fb.UniqueColors); $maxNonBlack = [math]::Max($maxNonBlack, $fb.NonBlackPercent)
        }
        $i++; Start-Sleep -Milliseconds 100
    }
    $s1 = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/input/state'
    $secs = ((Get-Date) - $t0).TotalSeconds
    $df = [int]$s1.frameCount - [int]$s0.frameCount
    Check 'frames_advance' ($df -ge $MinFrames -and [bool]$s1.emulationActive) ("{0} frames in {1:n1}s = {2:n1} fps, emulationActive={3}" -f $df, $secs, ($df / $secs), $s1.emulationActive)
    Check 'picture_nonblank' ($maxColors -ge $MinColors -and $maxNonBlack -ge $MinNonBlackPct) "colors=$maxColors nonBlack=$maxNonBlack% (png: $png)"
    Check 'picture_changes' ($hashes.Count -ge 2) "$($hashes.Count) distinct framebuffer hashes across samples"

    if ($peak -ge 0) {
        Check 'audio' ($peak -gt 0) ("Core Audio session peak (max over run) = {0:n4}" -f $peak)
    } elseif ($RequireAudio) {
        Check 'audio' $false 'no audio session for pid (and -RequireAudio set)'
    } else {
        $sess = ''; try { $sess = [UatAudio]::Sessions() } catch { $sess = "enum failed: $($_.Exception.Message)" }
        $checks['audio'] = [ordered]@{ ok = $true; detail = "unverified: no Core Audio session for pid; default-device sessions: $sess" }
        Write-Host '[WARN] audio - unverified (no audio session found)'
    }

    $alive = [bool](Get-Process -Id $id -ErrorAction SilentlyContinue)
    $health = $false; if ($alive) { try { $health = [bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health') } catch { } }
    Check 'no_crash' ($alive -and $health) "alive=$alive api=$health"
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "ERROR: $($result.error)"
}
finally {
    if ($inst) {
        $r = Stop-BrokenNesInstance -ProcessId $inst.ProcessId -TimeoutSeconds 25   # by PID only; graceful first
        Check 'graceful_shutdown' ($r.Stopped -and $r.Method -in 'CloseMainWindow','already-exited') "method=$($r.Method) stopped=$($r.Stopped)"
    }
    $result.checks = $checks
    $result.notes = $notes
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($checks.Values | Where-Object { -not $_.ok }).Count
    $result.pass = (-not $result.Contains('error')) -and $checks.Count -ge 9 -and $failed -eq 0
    Write-Output ($result | ConvertTo-Json -Depth 6 -Compress)
}
if ($result.pass) { exit 0 } else { exit 1 }

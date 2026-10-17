<#
.SYNOPSIS
  SNES save states in the real desktop app, end to end. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  The SNES has save states since round 5 (BOARD_SFC and every core inside it write their own part; SnesSession carries them in the shared console state
  container). This proves, in the shipped WinForms app and through the same methods F7 / F5 and the Save State / Load State menu items call (the HTTP quick
  save / quick load API reaches them), that they work:

    PROGRAM  a 32 KB SNES program of ours (no game data; carried below as hex, checked against Workshop/SnesProgs.cs by `--snes-state-test --only program`)
             that adds one to a counter for every frame Right is held and shows the counter as the backdrop colour. Loaded as a real SNES game: right console,
             SupportsState true in /api/console, a still picture without input and a changing one with Right held. Then: save the still picture (F7), hold
             Right so the picture moves, load (F5): the saved picture is back, byte for byte; hold Right again: the program runs on from there. The slot file is
             written under States\snes\ and its header names the sound unit and the chip.
    REFUSAL  the sound unit is swapped to the silent stand-in (the live swap the APU menu makes) and the quick load of the state saved with the real unit is refused with
             a reason that names both units, and nothing changes (the picture stays as it was); swapped back, the same state loads.
    GAME     (when the library is there) a real game, Super Mario World: loads, quick save writes a slot of a size a real SNES state has, a few seconds of play later the
             quick load answers success and the game runs on.

  The user's config.json is copied aside first and put back at the end (the quick save / load API is the opt-in `enableWebmoduleSavestateDebugShortcuts`, switched on
  for this run); the slot files the run makes are removed. Rule zero (UAT\README.md): never kill by process name; only PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\snes-state-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_snes_state'),
    # a folder with the SNES library (the real-game check is skipped, with the reason, when Super Mario World is not in it)
    [string]$Library = 'X:\EMULATION\Super Nintendo\Super Nintendo (U)'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$notes = New-Object System.Collections.Generic.List[string]
$result = [ordered]@{ entrypoint = 'snes-state'; pass = $false }
$configPath = Join-Path $env:APPDATA 'BrokenNes\config.json'
$configBackup = Join-Path $WorkDir 'config.json.bak'
$haveBackup = $false

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Skip($name, $why) {
    $script:checks[$name] = [ordered]@{ ok = $true; skipped = $true; detail = "SKIPPED: $why" }
    Write-Host ("[SKIP] {0} - {1}" -f $name, $why)
}

# The program (65816 machine code from $8000; Workshop/SnesProgs.cs builds the same bytes): wait for vblank, add one to the counter at $10/$11 for every frame pad 1's Right is held,
# write the counter as colour 0 of the palette (the backdrop, which is the whole picture: no layer is on).
$SnesCounterHex = '78A9018D0042A90F8D0021A90085108511AD124230FBAD124210FBAD12422901D0F9AD19422901F006E610D002E611A9008D2121A5108D2221A511297F8D22214C1180'

# A LoROM cartridge from the program: 32 KB, the header (title, map mode, size, reset vector) and a valid checksum.
function New-SnesProgramRom([string]$hex) {
    $rom = New-Object byte[] 0x8000
    $prog = [byte[]]@(for ($i = 0; $i -lt $hex.Length; $i += 2) { [Convert]::ToByte($hex.Substring($i, 2), 16) })
    [Array]::Copy($prog, 0, $rom, 0, $prog.Length)
    $h = 0x7FC0
    [Text.Encoding]::ASCII.GetBytes('BROKENNES SNES SMOKE ').CopyTo($rom, $h)
    $rom[$h + 0x15] = 0x20; $rom[$h + 0x16] = 0x00; $rom[$h + 0x17] = 0x05; $rom[$h + 0x18] = 0x00; $rom[$h + 0x19] = 0x01; $rom[$h + 0x1A] = 0x33
    $rom[$h + 0x3C] = 0x00; $rom[$h + 0x3D] = 0x80; $rom[$h + 0x2C] = 0x00; $rom[$h + 0x2D] = 0x80
    $rom[$h + 0x1C] = 0xFF; $rom[$h + 0x1D] = 0xFF; $rom[$h + 0x1E] = 0; $rom[$h + 0x1F] = 0
    $sum = 0; foreach ($b in $rom) { $sum += $b }
    $cs = $sum -band 0xFFFF
    $rom[$h + 0x1E] = [byte]($cs -band 0xFF); $rom[$h + 0x1F] = [byte]($cs -shr 8)
    $ncs = (-bnot $cs) -band 0xFFFF
    $rom[$h + 0x1C] = [byte]($ncs -band 0xFF); $rom[$h + 0x1D] = [byte]($ncs -shr 8)
    return ,$rom
}
function Get-RomSha1([byte[]]$rom) { ([BitConverter]::ToString([Security.Cryptography.SHA1]::Create().ComputeHash($rom)) -replace '-', '').ToLowerInvariant() }

function Clear-StartupDialogs([int]$procId) {
    $titles = @()
    for ($i = 0; $i -lt 10; $i++) {
        $d = @(Dismiss-BrokenNesDialogs -ProcessId $procId)
        if ($d.Count) { $titles += $d; Start-Sleep -Milliseconds 500 } elseif ($titles.Count) { break } else { Start-Sleep -Milliseconds 500 }
    }
    return $titles
}
function Get-Frame([int]$procId) {
    $fb = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console/framebuffer'
    $bytes = [Convert]::FromBase64String([string]$fb.data)
    $sha = [Security.Cryptography.SHA1]::Create()
    [pscustomobject]@{ Width = [int]$fb.width; Height = [int]$fb.height; Hash = [BitConverter]::ToString($sha.ComputeHash($bytes)); R = [int]$bytes[0]; G = [int]$bytes[1]; B = [int]$bytes[2] }
}
function Get-ConsoleStatus([int]$procId) { (Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console').status }
function Wait-Frame([int]$procId, [scriptblock]$ok, [int]$seconds = 8) {
    $end = (Get-Date).AddSeconds($seconds); $f = $null
    do { Start-Sleep -Milliseconds 300; $f = Get-Frame $procId; if (& $ok $f) { break } } while ((Get-Date) -lt $end)
    return $f
}
# hold the button for a while (the picture of the program changes every frame while Right is held), release it, and wait for a picture that stays the same
function Hold-Right([int]$procId, [double]$seconds) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @('Right') } | Out-Null
    Start-Sleep -Milliseconds ([int]($seconds * 1000))
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @() } | Out-Null
}
function Get-StillFrame([int]$procId, [int]$Seconds = 8) {
    $until = (Get-Date).AddSeconds($Seconds); $a = Get-Frame $procId
    do { Start-Sleep -Milliseconds 350; $b = Get-Frame $procId; if ($b.Hash -eq $a.Hash) { return $b }; $a = $b } while ((Get-Date) -lt $until)
    return $b
}
function Set-ConsoleCore([int]$procId, [string]$slot, [string]$coreId) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path ("/api/console/core/$slot/" + [Uri]::EscapeDataString($coreId))
}
function Quick([int]$procId, [string]$what) { Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path "/api/emulator/quick-$what-state" }

$inst = $null
$slots = @()
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
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
    $romBytes = New-SnesProgramRom $SnesCounterHex
    $romPath = Join-Path $WorkDir 'counter.sfc'
    [IO.File]::WriteAllBytes($romPath, $romBytes)
    $sha = Get-RomSha1 $romBytes
    $slot = Join-Path $env:APPDATA "BrokenNes\States\snes\sfc$sha.quick.bnstate"
    $slots += $slot
    if (Test-Path -LiteralPath $slot) { Remove-Item -LiteralPath $slot -Force }

    # the quick save / load API is the opt-in "webmodule savestate debug shortcuts" (off by default): on for this run, the stored config comes back after
    if ($haveBackup) {
        $cfg = Get-Content -LiteralPath $configBackup -Raw | ConvertFrom-Json
        $cfg | Add-Member -NotePropertyName enableWebmoduleSavestateDebugShortcuts -NotePropertyValue $true -Force
        ($cfg | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $configPath -Encoding UTF8
    }
    $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
    $id = $inst.ProcessId
    $dlg = Clear-StartupDialogs $id; if ($dlg.Count) { $notes.Add('dismissed start-up dialog(s): ' + ($dlg -join ', ')) }
    Check 'api_up' ([bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health')) "pid $id"
    Show-BrokenNesEmulator -ProcessId $id | Out-Null; Start-Sleep -Milliseconds 800
    # cores that an earlier run left in config.json: the SNES's own, whatever was stored
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @() } | Out-Null

    # ================= the program =================
    $l = Load-BrokenNesRom -ProcessId $id -Path $romPath
    Start-Sleep -Seconds 2
    Set-ConsoleCore $id 'apu' 'SFC' | Out-Null; Set-ConsoleCore $id 'ppu' 'SFC' | Out-Null
    Start-Sleep -Milliseconds 800
    $st = Get-ConsoleStatus $id
    Check 'snes_program_loads' ($l.success -and $st.console -eq 'snes' -and $st.consoleName -like '*SNES*') "load success=$($l.success) console=$($st.console) name=$($st.consoleName) game=$($st.game)"
    Check 'snes_reports_save_states' ($st.supportsState -eq $true -and -not $st.stateUnsupportedReason) "supportsState=$($st.supportsState) reason=[$($st.stateUnsupportedReason)]"
    Check 'snes_cores_named' ($st.cpu -and $st.ppu -eq 'SFC' -and $st.apu -eq 'SFC') "cpu=$($st.cpu) ppu=$($st.ppu) apu=$($st.apu)"
    $f0 = Get-StillFrame $id
    Check 'snes_frame_size' ($f0.Width -eq 256 -and $f0.Height -eq 224) "framebuffer $($f0.Width)x$($f0.Height)"
    Hold-Right $id 1.2
    $p1 = Get-StillFrame $id
    Check 'snes_input_reaches_the_program' ($p1.Hash -ne $f0.Hash) "backdrop $($f0.R),$($f0.G),$($f0.B) -> $($p1.R),$($p1.G),$($p1.B) after Right was held"

    # --- quick save (F7), move on, quick load (F5): the saved picture is back
    $sv = Quick $id 'save'
    $slotLen = if (Test-Path -LiteralPath $slot) { (Get-Item -LiteralPath $slot).Length } else { 0 }
    Check 'quick_save_writes_the_slot' ([bool]$sv.success -and $slotLen -gt 2000) "success=$($sv.success) reason=[$($sv.reason)]; $slot is $slotLen bytes"
    if ($slotLen -gt 100) {
        $hdr = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($slot), 0, 200)
        Check 'slot_header_names_console_unit_and_chip' ($hdr.StartsWith('BNCS') -and $hdr.Contains('snes') -and $hdr.Contains('sfc' + $sha) -and $hdr.Contains('apu') -and $hdr.Contains('chip') -and $hdr.Contains('sfc-1')) "the first bytes read: $($hdr -replace '[^\x20-\x7E]', '.')"
    }
    Hold-Right $id 1.2
    $p2 = Get-StillFrame $id
    Check 'picture_moved_on_after_the_save' ($p2.Hash -ne $p1.Hash) "backdrop $($p1.R),$($p1.G),$($p1.B) -> $($p2.R),$($p2.G),$($p2.B)"
    $ld = Quick $id 'load'
    $back = Wait-Frame $id { param($x) $x.Hash -eq $p1.Hash } 6
    Check 'quick_load_restores_the_picture' ([bool]$ld.success -and $back.Hash -eq $p1.Hash) "load success=$($ld.success) reason=[$($ld.reason)]; the saved picture is back: $($back.Hash -eq $p1.Hash)"
    Hold-Right $id 0.6
    $p3 = Get-StillFrame $id
    Check 'the_program_runs_on_after_the_load' ($p3.Hash -ne $p1.Hash) "backdrop after the load $($p1.R),$($p1.G),$($p1.B) -> $($p3.R),$($p3.G),$($p3.B) with Right held again"

    # ================= refusal: another sound unit =================
    $r = Set-ConsoleCore $id 'apu' 'HLE'
    $s = Get-ConsoleStatus $id
    Start-Sleep -Milliseconds 600
    $before = Get-StillFrame $id
    $ld = Quick $id 'load'
    $after = Get-StillFrame $id
    Check 'state_of_another_sound_unit_is_refused_with_its_reason' ($r.success -and $s.apu -eq 'HLE' -and -not [bool]$ld.success -and "$($ld.reason)" -match 'sound unit' -and "$($ld.reason)" -match 'SFC' -and "$($ld.reason)" -match 'HLE' -and $after.Hash -eq $before.Hash) "swap success=$($r.success) apu=$($s.apu); load success=$($ld.success) reason=[$($ld.reason)]; the picture did not change: $($after.Hash -eq $before.Hash)"
    Set-ConsoleCore $id 'apu' 'SFC' | Out-Null
    Start-Sleep -Milliseconds 600
    $ld = Quick $id 'load'
    $back = Wait-Frame $id { param($x) $x.Hash -eq $p1.Hash } 6
    Check 'swapped_back_the_same_state_loads' ([bool]$ld.success -and $back.Hash -eq $p1.Hash) "load success=$($ld.success) reason=[$($ld.reason)]; the saved picture is back: $($back.Hash -eq $p1.Hash)"
    if (Test-Path -LiteralPath $slot) { Remove-Item -LiteralPath $slot -Force }

    # ================= a real game =================
    $smw = Join-Path $Library 'Super Mario World (U) [!].smc'
    if (Test-Path -LiteralPath $smw) {
        $gameSha = Get-RomSha1 ([IO.File]::ReadAllBytes($smw))
        $gslot = Join-Path $env:APPDATA "BrokenNes\States\snes\sfc$gameSha.quick.bnstate"
        $slots += $gslot
        if (Test-Path -LiteralPath $gslot) { Remove-Item -LiteralPath $gslot -Force }
        $l = Load-BrokenNesRom -ProcessId $id -Path $smw
        Start-Sleep -Seconds 4
        $st = Get-ConsoleStatus $id
        $a = Get-Frame $id
        Check 'game_loads' ($l.success -and $st.console -eq 'snes' -and $st.supportsState -eq $true) "load success=$($l.success) console=$($st.console) game=$($st.game) supportsState=$($st.supportsState)"
        $sv = Quick $id 'save'
        $len = if (Test-Path -LiteralPath $gslot) { (Get-Item -LiteralPath $gslot).Length } else { 0 }
        Check 'game_quick_save_writes_a_real_state' ([bool]$sv.success -and $len -gt 30000 -and $len -lt 600000) "success=$($sv.success) reason=[$($sv.reason)]; slot is $len bytes ($([math]::Round($len / 1024)) KB)"
        Start-Sleep -Seconds 3
        $mid = Get-Frame $id
        $ld = Quick $id 'load'
        Start-Sleep -Milliseconds 700
        $c = Get-Frame $id
        $fps = [math]::Round([double](Get-ConsoleStatus $id).fps)
        Check 'game_quick_load_succeeds_and_the_game_runs_on' ([bool]$ld.success -and $c.Width -eq 256 -and (Get-ConsoleStatus $id).console -eq 'snes') "load success=$($ld.success) reason=[$($ld.reason)]; fps $fps"
        Start-Sleep -Seconds 2
        $d = Get-Frame $id
        Check 'game_runs_after_the_load' ($d.Hash -ne $c.Hash -or $d.Hash -ne $mid.Hash) "the picture changes after the load: $($d.Hash -ne $c.Hash)"
        if (Test-Path -LiteralPath $gslot) { Remove-Item -LiteralPath $gslot -Force }
    } else { Skip 'game_quick_save_and_load' "Super Mario World is not at $smw" }

    $alive = [bool](Get-Process -Id $id -ErrorAction SilentlyContinue)
    $health = $false; if ($alive) { try { $health = [bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health') } catch { } }
    Check 'no_crash' ($alive -and $health) "alive=$alive api=$health"
    $r = Stop-BrokenNesInstance -ProcessId $id -TimeoutSeconds 25
    Check 'graceful_shutdown' ($r.Stopped -and $r.Method -in 'CloseMainWindow', 'already-exited') "method=$($r.Method)"
    $inst = $null
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "ERROR: $($result.error)"
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId -TimeoutSeconds 25 | Out-Null }
    foreach ($p in $slots) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
    if ($haveBackup) { Copy-Item -LiteralPath $configBackup -Destination $configPath -Force; $notes.Add('config.json restored') }
    $result.checks = $checks
    $result.notes = $notes
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($checks.Values | Where-Object { -not $_.ok }).Count
    $result.pass = (-not $result.Contains('error')) -and $checks.Count -ge 14 -and $failed -eq 0
    Write-Host ($result | ConvertTo-Json -Depth 6 -Compress)
    exit $(if ($result.pass) { 0 } else { 1 })
}

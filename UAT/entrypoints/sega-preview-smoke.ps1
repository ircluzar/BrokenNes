<#
.SYNOPSIS
  Sega console plumbing, end to end in the real desktop app. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  The Sega consoles sit behind a preview switch (BROKENNES_SEGA=1) until their boards exist; the Genesis has been finished since
  Track B2a (Consoles.GenesisReady) and is no longer behind it. This proves, in the shipped WinForms app, that the switch does
  what it says in both positions:

    OFF  the Console menu lists the four consoles it always had plus the finished Genesis (and no Master System or Game Gear),
         and a stored Sega selection of a console that is not finished (sms) does not break start-up (the app falls back to a
         console it has); a Genesis program loads as a real Genesis game (the VDP's picture, not a placeholder) and the pad
         reaches it.
    ON   the Console menu lists seven; a Master System and a Game Gear ROM each load through RomDetect into the labelled
         placeholder session with the right console, core ids, frame size and a non-blank colour-bar picture, and a Genesis
         program into its real session; holding a button changes the picture (the placeholder's square moves, the Genesis
         program colours its backdrop from the pad); the app shuts down gracefully.

  The synthetic ROMs are written to the work folder (a header and zeros, no game data). The user's config.json is copied
  aside first and put back at the end, because a console switch is persisted there.

  Rule zero (UAT\README.md): never kill by process name; only PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\sega-preview-smoke.ps1
  pwsh -File UAT\entrypoints\sega-preview-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_sega_preview')
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

# A ROM is a header and zeros: enough for RomDetect and the placeholder session, nothing of any game.
function New-SegaRoms($dir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $sms = New-Object byte[] 0x8000; [Text.Encoding]::ASCII.GetBytes('TMR SEGA').CopyTo($sms, 0x7FF0); $sms[0x7FFF] = 0x4C
    $gg = New-Object byte[] 0x8000; [Text.Encoding]::ASCII.GetBytes('TMR SEGA').CopyTo($gg, 0x7FF0); $gg[0x7FFF] = 0x6C
    # The Genesis ROM carries a real 68000 program (written for this script, no game data): it sets H40 and the display on, drives the first pad's TH
    # line high and, in a loop, reads the pad and writes it as colour 0 of the palette, which is the backdrop: the picture is the pad's state.
    # move.w #$8004,$C00004 / #$8144 / #$8C81 / #$8700 / #$8F02 ; move.b #$40,$A10009 (TH an output) ; move.b #$40,$A10003 (TH high)
    # loop: move.b $A10003,d0 ; andi.w #$3F,d0 ; lsl.w #3,d0 ; move.l #$C0000000,$C00004 ; move.w d0,$C00000 ; bra loop
    $code = '33fc800400c0000433fc814400c0000433fc8c8100c0000433fc870000c0000433fc8f0200c0000413fc004000a1000913fc004000a10003103900a100030240003fe74823fcc000000000c0000433c000c0000060e2'
    $md = New-Object byte[] 0x20000
    $md[1] = 0xFF; $md[2] = 0xFE; $md[3] = 0x00; $md[6] = 0x02; $md[7] = 0x00           # vectors: stack $FFFE00, entry $200
    [Text.Encoding]::ASCII.GetBytes('SEGA MEGA DRIVE ').CopyTo($md, 0x100)
    [Text.Encoding]::ASCII.GetBytes('GM 00000000-00').CopyTo($md, 0x180)
    for ($i = 0; $i -lt 16; $i++) { $md[0x1F0 + $i] = 0x20 }; $md[0x1F0] = [byte][char]'U'
    for ($i = 0; $i -lt $code.Length / 2; $i++) { $md[0x200 + $i] = [Convert]::ToByte($code.Substring($i * 2, 2), 16) }
    [IO.File]::WriteAllBytes((Join-Path $dir 'smoke.sms'), $sms)
    [IO.File]::WriteAllBytes((Join-Path $dir 'smoke.gg'), $gg)
    [IO.File]::WriteAllBytes((Join-Path $dir 'smoke.md'), $md)
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
        # [int] casts: a -shl on a [byte] stays a byte in PowerShell, which would drop the two high channels (a green/yellow backdrop would count as black)
        $o = $i * 4; $c = ([int]$bytes[$o] -shl 16) -bor ([int]$bytes[$o + 1] -shl 8) -bor [int]$bytes[$o + 2]
        [void]$colors.Add($c); if ($c -ne 0) { $nonBlack++ }
    }
    [pscustomobject]@{ Width = [int]$fb.width; Height = [int]$fb.height; Colors = $colors.Count; NonBlackPct = [math]::Round(100.0 * $nonBlack / $n, 1); Hash = [BitConverter]::ToString($sha.ComputeHash($bytes)); Mid = "$($bytes[$n * 2]),$($bytes[$n * 2 + 1]),$($bytes[$n * 2 + 2])" }
}

# A session shows nothing until its first frame has run, so a frame read straight after a load can be blank: poll until the picture has something in it (or give up after $Seconds and return the last frame).
function Wait-Picture([int]$procId, [double]$MinNonBlackPct = 5, [int]$Seconds = 6) {
    $until = (Get-Date).AddSeconds($Seconds)
    do { $f = Get-Frame $procId; if ($f.NonBlackPct -ge $MinNonBlackPct) { return $f }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $until)
    return $f
}
# Hold a button and look for the picture to change from $Baseline: sampled repeatedly while the button is held (one sample races the hold's end), released afterwards.
function Test-InputChangesPicture([int]$procId, $Baseline, [string]$Button = 'Right', [int]$Seconds = 4) {
    Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @($Button) } | Out-Null
    $changed = $false; $until = (Get-Date).AddSeconds($Seconds)
    try { do { Start-Sleep -Milliseconds 250; $f = Get-Frame $procId; if ($f.Hash -ne $Baseline.Hash) { $changed = $true } } while (-not $changed -and (Get-Date) -lt $until) }
    finally { Invoke-BrokenNesApi -ProcessId $procId -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @() } | Out-Null }
    return $changed
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
    Check 'off_console_is_one_it_has' ($st.status.console -in 'nes', 'snes', 'gb', 'gbc', 'md') "console=$($st.status.console)"
    $off = Get-ConsoleMenuNames $id
    Check 'off_menu_lists_old_four_and_the_finished_genesis' ($null -ne $off -and @($off | Where-Object { $_ -match 'Master System|Game Gear' }).Count -eq 0 -and @($off | Where-Object { $_ -match 'Genesis' }).Count -ge 1 -and @($off).Count -ge 5) ("menu: " + ($off -join ' | '))
    # the finished Genesis works with the switch off: its program loads as a real session and the pad reaches it
    $roms = Join-Path $WorkDir 'roms'; New-SegaRoms $roms
    $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms 'smoke.md')
    $g0 = Wait-Picture $id 90
    $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
    Check 'off_genesis_loads_without_the_switch' ($l.success -and $st.console -eq 'md' -and $st.consoleName -like '*Genesis*') "load success=$($l.success) console=$($st.console) name=$($st.consoleName) game=$($st.game)"
    Check 'off_genesis_real_picture' (($g0.Width -in @(256, 320)) -and ($g0.Height -in @(224, 240)) -and $g0.NonBlackPct -ge 90) "framebuffer $($g0.Width)x$($g0.Height), $($g0.Colors) colours, $($g0.NonBlackPct)% non-black, middle pixel $($g0.Mid) (the program fills the backdrop)"
    $moved = Test-InputChangesPicture $id $g0
    Check 'off_genesis_input_reaches_the_game' $moved "backdrop changed while Right was held: $moved"
    $r = Stop-BrokenNesInstance -ProcessId $id -TimeoutSeconds 25
    Check 'off_graceful_shutdown' ($r.Stopped -and $r.Method -in 'CloseMainWindow', 'already-exited') "method=$($r.Method)"
    $inst = $null
    if ($haveBackup) { Copy-Item -LiteralPath $configBackup -Destination $configPath -Force }

    # ================= switch ON =================
    $env:BROKENNES_SEGA = '1'
    $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
    $id = $inst.ProcessId
    $dlg = Clear-StartupDialogs $id; if ($dlg.Count) { $notes.Add('dismissed start-up dialog(s): ' + ($dlg -join ', ')) }
    Check 'on_api_up' ([bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health')) "pid $id, BROKENNES_SEGA=1"
    Show-BrokenNesEmulator -ProcessId $id | Out-Null; Start-Sleep -Milliseconds 800
    $on = Get-ConsoleMenuNames $id
    Check 'on_menu_lists_seven' ($null -ne $on -and @($on | Where-Object { $_ -match 'Master System' }).Count -ge 1 -and @($on | Where-Object { $_ -match 'Game Gear' }).Count -ge 1 -and @($on | Where-Object { $_ -match 'Genesis' }).Count -ge 1 -and @($on).Count -ge 7) ("menu: " + ($on -join ' | '))

    $expect = @(
        @{ Key = 'sms'; File = 'smoke.sms'; W = 256; H = 192; Name = 'Master System'; MinColors = 3 },
        @{ Key = 'gg';  File = 'smoke.gg';  W = 160; H = 144; Name = 'Game Gear'; MinColors = 3 },
        @{ Key = 'md';  File = 'smoke.md';  W = 320; H = 224; Name = 'Genesis'; MinColors = 1 })
    foreach ($e in $expect) {
        $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms $e.File)
        $f0 = Wait-Picture $id 5
        $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
        Check "$($e.Key)_loads_as_its_console" ($l.success -and $st.console -eq $e.Key -and $st.consoleName -like "*$($e.Name)*") "load success=$($l.success) console=$($st.console) name=$($st.consoleName) game=$($st.game)"
        Check "$($e.Key)_frame_size" ($st.frameWidth -eq $e.W -and $st.frameHeight -eq $e.H) "status says $($st.frameWidth)x$($st.frameHeight), expected $($e.W)x$($e.H)"
        Check "$($e.Key)_picture" ($f0.Width -eq $e.W -and $f0.Height -eq $e.H -and $f0.Colors -ge $e.MinColors -and $f0.NonBlackPct -ge 5) "framebuffer $($f0.Width)x$($f0.Height), $($f0.Colors) colours, $($f0.NonBlackPct)% non-black, middle pixel $($f0.Mid)"
        Check "$($e.Key)_cores_named" ($st.cpu -and $st.ppu -and $st.apu) "cpu=$($st.cpu) ppu=$($st.ppu) apu=$($st.apu)"
        # input: hold Right for a moment; the placeholder's square moves (the Genesis program recolours its backdrop), so the frame must change
        $moved = Test-InputChangesPicture $id $f0
        Check "$($e.Key)_input_reaches_session" $moved "frame hash changed while Right was held: $moved"
        Start-Sleep -Milliseconds 400
    }
    $alive = [bool](Get-Process -Id $id -ErrorAction SilentlyContinue)
    $health = $false; if ($alive) { try { $health = [bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health') } catch { } }
    Check 'on_no_crash' ($alive -and $health) "alive=$alive api=$health"
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
    $failed = @($checks.Values | Where-Object { -not $_.ok }).Count
    $result.pass = (-not $result.Contains('error')) -and $checks.Count -ge 20 -and $failed -eq 0
    Write-Host ($result | ConvertTo-Json -Depth 6 -Compress)
    exit $(if ($result.pass) { 0 } else { 1 })
}

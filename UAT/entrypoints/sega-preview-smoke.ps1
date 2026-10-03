<#
.SYNOPSIS
  Sega console plumbing, end to end in the real desktop app. Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  The Sega consoles sit behind a preview switch (BROKENNES_SEGA=1) until their boards exist. This proves, in the shipped
  WinForms app, that the switch does what it says in both positions:

    OFF  the Console menu lists exactly the four consoles it always had, and a stored Sega selection in config.json does not
         break start-up (the app falls back to a console it has).
    ON   the Console menu lists seven; a Master System, Game Gear and Genesis ROM each load through RomDetect into the
         labelled placeholder session with the right console, core ids, frame size and a non-blank colour-bar picture;
         holding a button moves the placeholder's square (input reaches the session); the app shuts down gracefully.

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
    $md = New-Object byte[] 0x20000
    [Text.Encoding]::ASCII.GetBytes('SEGA MEGA DRIVE ').CopyTo($md, 0x100)
    [Text.Encoding]::ASCII.GetBytes('GM 00000000-00').CopyTo($md, 0x180)
    for ($i = 0; $i -lt 16; $i++) { $md[0x1F0 + $i] = 0x20 }; $md[0x1F0] = [byte][char]'U'
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
    $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
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
        $o = $i * 4; $c = ($bytes[$o] -shl 16) -bor ($bytes[$o + 1] -shl 8) -bor $bytes[$o + 2]
        [void]$colors.Add($c); if ($c -ne 0) { $nonBlack++ }
    }
    [pscustomobject]@{ Width = [int]$fb.width; Height = [int]$fb.height; Colors = $colors.Count; NonBlackPct = [math]::Round(100.0 * $nonBlack / $n, 1); Hash = [BitConverter]::ToString($sha.ComputeHash($bytes)) }
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
        $cfg | Add-Member -NotePropertyName SelectedConsole -NotePropertyValue 'md' -Force
        $cfg | Add-Member -NotePropertyName SelectedCpuCore -NotePropertyValue 'Z80' -Force   # the retired joke core's id
        ($cfg | ConvertTo-Json -Depth 20) | Set-Content -LiteralPath $configPath -Encoding UTF8
    }
    $inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
    $id = $inst.ProcessId
    $dlg = Clear-StartupDialogs $id; if ($dlg.Count) { $notes.Add('dismissed start-up dialog(s): ' + ($dlg -join ', ')) }
    Check 'off_starts_with_stored_sega_selection' ([bool](Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/health')) "pid $id, config.json SelectedConsole=md and SelectedCpuCore=Z80, switch off"
    Show-BrokenNesEmulator -ProcessId $id | Out-Null; Start-Sleep -Milliseconds 800
    $st = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console'
    $cpu = Get-BrokenNesCore -ProcessId $id -Kind cpu
    Check 'off_retired_cpu_id_falls_back' ($cpu -and $cpu -notlike '*Z80*') "config.json SelectedCpuCore=Z80 (retired); the app runs CPU '$cpu'"
    Check 'off_console_is_one_it_has' ($st.status.console -in 'nes', 'snes', 'gb', 'gbc') "console=$($st.status.console)"
    $off = Get-ConsoleMenuNames $id
    Check 'off_menu_lists_four' ($null -ne $off -and @($off | Where-Object { $_ -match 'Master System|Game Gear|Genesis' }).Count -eq 0 -and @($off).Count -ge 4) ("menu: " + ($off -join ' | '))
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
        @{ Key = 'sms'; File = 'smoke.sms'; W = 256; H = 192; Name = 'Master System' },
        @{ Key = 'gg';  File = 'smoke.gg';  W = 160; H = 144; Name = 'Game Gear' },
        @{ Key = 'md';  File = 'smoke.md';  W = 320; H = 224; Name = 'Genesis' })
    foreach ($e in $expect) {
        $l = Load-BrokenNesRom -ProcessId $id -Path (Join-Path $roms $e.File)
        Start-Sleep -Seconds 2
        $st = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
        Check "$($e.Key)_loads_as_its_console" ($l.success -and $st.console -eq $e.Key -and $st.consoleName -like "*$($e.Name)*") "load success=$($l.success) console=$($st.console) name=$($st.consoleName) game=$($st.game)"
        Check "$($e.Key)_frame_size" ($st.frameWidth -eq $e.W -and $st.frameHeight -eq $e.H) "status says $($st.frameWidth)x$($st.frameHeight), expected $($e.W)x$($e.H)"
        $f0 = Get-Frame $id
        Check "$($e.Key)_picture" ($f0.Width -eq $e.W -and $f0.Height -eq $e.H -and $f0.Colors -ge 3 -and $f0.NonBlackPct -ge 5) "framebuffer $($f0.Width)x$($f0.Height), $($f0.Colors) colours, $($f0.NonBlackPct)% non-black"
        Check "$($e.Key)_cores_named" ($st.cpu -and $st.ppu -and $st.apu) "cpu=$($st.cpu) ppu=$($st.ppu) apu=$($st.apu)"
        # input: hold Right for a moment; the placeholder's square moves, so the frame must change
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/input/set-buttons' -Body @{ player = 1; buttons = @('Right'); holdMs = 600 } | Out-Null
        Start-Sleep -Milliseconds 500
        $f1 = Get-Frame $id
        Check "$($e.Key)_input_reaches_session" ($f1.Hash -ne $f0.Hash) "frame hash changed while Right was held: $($f1.Hash -ne $f0.Hash)"
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

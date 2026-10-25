<#
.SYNOPSIS
  The Game Boy hello world of VRUN: Vaults of Obsolete Code in the real desktop app, over HTTP (BrokenDev milestone D2). Exit 0 = pass.
  Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with a scratch settings folder (BROKENNES_CONFIG_DIR) and a scratch saves folder (BROKENNES_SAVES_DIR):
  the user's config.json and saves are never read or written, and the script proves it (a listing of %APPDATA%\BrokenNes before and after:
  the same names, sizes and times).
    COLOR      the ROM (BrokenDev's build, out\vrun-voc\vrun_voc.gbc) opens on the Game Boy Color: the picture in colour (more than 4 colours),
               is_color = 1, the boot counter 1 (a new cartridge).
    INPUT      Right held through POST /api/input/set-buttons moves the sprite (player_x grows, the picture changes); A beeps (beep_count 1,
               channel 1 on in NR52).
    SAVE       the app closes normally (it writes the battery save to GbSaves\<sha1>.sav in the scratch saves folder); started again, the game
               finds it: boot_count 2.
    ORIGINAL   the same ROM on the Game Boy (POST /api/console/select/gb restarts the running game on it): four shades at most, is_color = 0,
               the same save: boot_count 3.
  The variables are read by the build's own symbols (vrun_voc.noi) through /api/memory/peek on the BUS domain.
  Rule zero (UAT\README.md): never kill by process name; only the PIDs started here are touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\vrun-voc-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Rom = 'P:\BrokenDev\env\out\vrun-voc\vrun_voc.gbc',
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_vrun_voc_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'vrun-voc-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Peek([int]$procId, [string]$sym, [int]$size = 1) {
    $a = $script:syms[$sym]; if ($null -eq $a) { throw "no symbol $sym in the .noi" }
    $v = 0; for ($i = 0; $i -lt $size; $i++) { $v += [int](Api $procId GET "/api/memory/peek?domain=BUS&address=$($a + $i)").value * [math]::Pow(256, $i) }
    [int]$v
}
function Picture([int]$procId) {
    $f = Api $procId GET '/api/console/framebuffer'
    $bytes = [Convert]::FromBase64String([string]$f.data)
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($i = 0; $i + 3 -lt $bytes.Length; $i += 4) { [void]$seen.Add(([int]$bytes[$i] -shl 16) -bor ([int]$bytes[$i + 1] -shl 8) -bor [int]$bytes[$i + 2]) }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    [pscustomobject]@{ Width = [int]$f.width; Height = [int]$f.height; Colors = $seen.Count; Hash = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').Substring(0, 16) }
}
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
function Start-App {
    $env:BROKENNES_CONFIG_DIR = $script:cfgDir
    $env:BROKENNES_SAVES_DIR = $script:saveDir
    $env:BROKENNES_DEV = '1'
    $i = Start-BrokenNesInstance -ExePath $Exe
    Start-Sleep -Seconds 4   # the app loads its embedded test.nes a few seconds after the API answers
    return $i
}
function Load([int]$procId) {
    Api $procId POST '/api/emulator/load-rom' @{ path = $script:romCopy } | Out-Null
    Start-Sleep -Seconds 3   # a few seconds of play: past the start-up
}

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENNES_DEV = $env:BROKENNES_DEV }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $saveDir = Join-Path $WorkDir 'saves'
    New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir | Out-Null
    $romCopy = Join-Path $WorkDir 'vrun_voc.gbc'
    Copy-Item -LiteralPath $Rom -Destination $romCopy
    $syms = @{}
    foreach ($l in [System.IO.File]::ReadAllLines([System.IO.Path]::ChangeExtension($Rom, '.noi'))) { if ($l -match '^DEF (_\w+) 0x([0-9A-Fa-f]+)$') { $syms[$Matches[1]] = [Convert]::ToInt32($Matches[2], 16) } }
    $sha1 = ([BitConverter]::ToString([System.Security.Cryptography.SHA1]::Create().ComputeHash([System.IO.File]::ReadAllBytes($Rom))) -replace '-', '').ToLowerInvariant()

    # ---------------------------------------------------------------- COLOR
    $inst = Start-App; $id = $inst.ProcessId
    Load $id
    $st = (Api $id GET '/api/console').status
    $p0 = Picture $id
    Check 'color_console' ($st.console -eq 'gbc' -and $st.frameWidth -eq 160 -and $st.frameHeight -eq 144) "console $($st.console): $($st.description)"
    Check 'color_picture' ($p0.Colors -gt 4) "$($p0.Width) x $($p0.Height), $($p0.Colors) colours"
    Check 'color_is_color' ((Peek $id '_is_color') -eq 1) "is_color = $(Peek $id '_is_color')"
    Check 'color_first_boot' ((Peek $id '_boot_count' 2) -eq 1 -and (Peek $id '_save_was_fresh') -eq 1) "boot_count $(Peek $id '_boot_count' 2), save_was_fresh $(Peek $id '_save_was_fresh')"

    # ---------------------------------------------------------------- INPUT
    $x0 = Peek $id '_player_x'
    Api $id POST '/api/input/set-buttons' @{ player = 1; buttons = @('Right') } | Out-Null
    Start-Sleep -Milliseconds 700
    Api $id POST '/api/input/clear' | Out-Null
    Start-Sleep -Milliseconds 200
    $x1 = Peek $id '_player_x'
    $p1 = Picture $id
    Check 'input_sprite_moved' ($x1 -gt $x0 -and $p1.Hash -ne $p0.Hash) "player_x $x0 -> $x1 after Right; picture $($p0.Hash) -> $($p1.Hash)"
    $b0 = Peek $id '_beep_count' 2
    Api $id POST '/api/input/set-buttons' @{ player = 1; buttons = @('A'); holdMs = 150 } | Out-Null
    Start-Sleep -Milliseconds 80
    $nr52 = [int](Api $id GET '/api/memory/peek?domain=BUS&address=65318').value   # $FF26
    Start-Sleep -Milliseconds 400
    $b1 = Peek $id '_beep_count' 2
    Check 'input_a_beeps' ($b1 -eq $b0 + 1 -and ($nr52 -band 0x81) -eq 0x81) ("beep_count {0} -> {1}; NR52 = `${2:X2} while the beep played (sound on, channel 1 on)" -f $b0, $b1, $nr52)
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null
    Start-Sleep -Seconds 1

    # ---------------------------------------------------------------- SAVE
    $sav = Join-Path $saveDir "GbSaves\$sha1.sav"
    Check 'save_written' ((Test-Path -LiteralPath $sav) -and (Get-Item -LiteralPath $sav).Length -ge 32768) "$sav$(if (Test-Path -LiteralPath $sav) { ' ' + (Get-Item -LiteralPath $sav).Length + ' bytes' } else { ' missing' })"
    $inst = Start-App; $id = $inst.ProcessId
    Load $id
    Check 'save_second_boot' ((Peek $id '_boot_count' 2) -eq 2 -and (Peek $id '_save_was_fresh') -eq 0) "boot_count $(Peek $id '_boot_count' 2), save_was_fresh $(Peek $id '_save_was_fresh')"

    # ---------------------------------------------------------------- ORIGINAL
    # a console switch restarts the running game on the other model (its battery save written first, then read back): the third power-on
    Api $id POST '/api/console/select/gb' | Out-Null
    Start-Sleep -Seconds 3
    $st = (Api $id GET '/api/console').status
    $p2 = Picture $id
    Check 'original_console' ($st.console -eq 'gb') "console $($st.console): $($st.description)"
    Check 'original_shades' ($p2.Colors -le 4 -and (Peek $id '_is_color') -eq 0) "$($p2.Colors) colours, is_color = $(Peek $id '_is_color')"
    Check 'original_third_boot' ((Peek $id '_boot_count' 2) -eq 3) "boot_count $(Peek $id '_boot_count' 2) (the save made on the Color)"
    Stop-BrokenNesInstance -ProcessId $id | Out-Null; $inst = $null
    Start-Sleep -Seconds 1

    $after = Listing $userDir
    Check 'user_saves_untouched' ($before -eq $after) "$userDir : $(@($before -split "`n").Count) files, $(if ($before -eq $after) { 'the same names, sizes and times' } else { 'CHANGED' })"
    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }

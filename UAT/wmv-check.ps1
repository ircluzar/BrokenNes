# WideMess Virtual (docs/projects/next-pool-plans.md, P4) in the real desktop app, over the HTTP API.
#
#   powershell -File .\UAT\wmv-check.ps1 -Exe <BrokenNes.Windows.exe> -Cartridge <game.wmv> -Bad <bad.wmv>
#
# Starts a scratch instance (BROKENNES_CONFIG_DIR / SAVES / BUGS in a temp folder: never the user's), loads the cartridge by path, and checks:
# the picture is 400 x 224 with black columns 0 and 399 and is not blank; it changes; the pad (API-injected Down, Down, B) opens the title's settings;
# pause freezes the picture; a core swap and a quick save are refused; Close ROM stops the game's server; a bad cartridge is refused; closing the app
# leaves no server. The offscreen check of the session itself is `BrokenNes.Windows.exe --wmv-check` (Windows/Virtual/VirtualCartCheck.cs).
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Cartridge,
    [string]$Bad,
    [string]$Scratch = (Join-Path $env:TEMP "brokennes-wmv-uat")
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lib\ApiClient.ps1"

foreach ($d in 'cfg', 'saves', 'bugs') { New-Item -ItemType Directory -Force (Join-Path $Scratch $d) | Out-Null }
$env:BROKENNES_CONFIG_DIR = Join-Path $Scratch 'cfg'
$env:BROKENNES_SAVES_DIR = Join-Path $Scratch 'saves'
$env:BROKENNES_BUGS_DIR = Join-Path $Scratch 'bugs'

$results = New-Object System.Collections.Generic.List[object]
function Check($name, [bool]$pass, $detail) { $results.Add([pscustomobject]@{ Check = $name; Pass = $pass; Detail = $detail }); Write-Host ("{0} {1}: {2}" -f ($(if ($pass) { 'PASS' } else { 'FAIL' })), $name, $detail) }

function Frame($id) {
    $r = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console/framebuffer'
    $bytes = if ($r.data -is [string]) { [Convert]::FromBase64String($r.data) } else { [byte[]]@($r.data | ForEach-Object { [byte]$_ }) }
    [pscustomobject]@{ W = [int]$r.width; H = [int]$r.height; Px = $bytes }
}
function PxDiff($a, $b) { $n = 0; for ($i = 0; $i -lt $a.Px.Length; $i += 4) { if ($a.Px[$i] -ne $b.Px[$i] -or $a.Px[$i + 1] -ne $b.Px[$i + 1] -or $a.Px[$i + 2] -ne $b.Px[$i + 2]) { $n++ } }; $n }
function Servers($appPid) { @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$appPid AND Name='node.exe'" -ErrorAction SilentlyContinue) }

$inst = Start-BrokenNesInstance -ExePath $Exe
$id = $inst.ProcessId
try {
    Show-BrokenNesEmulator -ProcessId $id | Out-Null
    Start-Sleep -Milliseconds 800
    $load = Load-BrokenNesRom -ProcessId $id -Path $Cartridge -TimeoutSec 90
    Check 'load' ([bool]$load.success) "load-rom success=$($load.success)"
    $srv = @(Servers $id)
    Check 'server started' ($srv.Count -eq 1) "node processes under the app: $($srv.Count) $($srv | ForEach-Object { $_.ProcessId })"
    Start-Sleep -Seconds 5
    $f0 = Frame $id
    $black = $true; $lit = 0
    for ($y = 0; $y -lt $f0.H; $y++) {
        foreach ($x in 0, ($f0.W - 1)) { $o = ($y * $f0.W + $x) * 4; if ($f0.Px[$o] -or $f0.Px[$o + 1] -or $f0.Px[$o + 2]) { $black = $false } }
    }
    for ($i = 0; $i -lt $f0.Px.Length; $i += 4) { if ($f0.Px[$i] -or $f0.Px[$i + 1] -or $f0.Px[$i + 2]) { $lit++ } }
    Check 'picture' ($f0.W -eq 400 -and $f0.H -eq 224 -and $black -and $lit -gt 20000) "$($f0.W) x $($f0.H), columns black: $black, lit pixels: $lit"
    Start-Sleep -Milliseconds 1000
    $f1 = Frame $id
    $idle = PxDiff $f0 $f1
    Check 'picture changes' ($idle -gt 0) "$idle pixels changed in 1 s at the title"

    foreach ($b in 'Down', 'Down', 'B') {
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/input/set-buttons' -Body @{ Player = 1; Buttons = @($b); HoldMs = 150 } | Out-Null
        Start-Sleep -Milliseconds 450
    }
    Start-Sleep -Milliseconds 600
    $f2 = Frame $id
    $opened = PxDiff $f1 $f2
    Check 'pad opens the settings' ($opened -gt 60000) "$opened pixels changed after Down, Down, B (idle change was $idle)"
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/input/set-buttons' -Body @{ Player = 1; Buttons = @('A'); HoldMs = 150 } | Out-Null
    Start-Sleep -Milliseconds 900

    Suspend-BrokenNesEmulation -ProcessId $id | Out-Null
    Start-Sleep -Milliseconds 700
    $p0 = Frame $id; Start-Sleep -Milliseconds 1000; $p1 = Frame $id
    $still = PxDiff $p0 $p1
    Resume-BrokenNesEmulation -ProcessId $id | Out-Null
    Start-Sleep -Milliseconds 1000
    $r0 = Frame $id; Start-Sleep -Milliseconds 700; $r1 = Frame $id
    $moving = PxDiff $r0 $r1
    Check 'pause' ($still -eq 0 -and $moving -gt 0) "while paused $still pixels changed in 1 s; after resume $moving"

    $swap = try { Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/console/core/cpu/FIX' } catch { $_.Exception.Message }
    $status = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console').status
    Check 'core swap refused, no state' ($status.cpu -like '*virtual*' -and $status.supportsState -eq $false -and "$($status.stateUnsupportedReason)" -like '*virtual cartridge*') "cpu=$($status.cpu); supportsState=$($status.supportsState); reason: $($status.stateUnsupportedReason)"
    Check 'still running after the refusals' (@(Servers $id).Count -eq 1) "node processes: $(@(Servers $id).Count)"

    $before = (Servers $id | ForEach-Object { $_.ProcessId })
    Close-BrokenNesRom -ProcessId $id | Out-Null
    Start-Sleep -Seconds 2
    $alive = @($before | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
    Check 'Close ROM stops the server' ($alive.Count -eq 0) "server pids $before; still alive: $($alive.Count)"

    if ($Bad) {
        $badLoad = Load-BrokenNesRom -ProcessId $id -Path $Bad
        Check 'bad cartridge refused' (-not $badLoad.success -and @(Servers $id).Count -eq 0) "load-rom success=$($badLoad.success); node processes: $(@(Servers $id).Count)"
    }

    Load-BrokenNesRom -ProcessId $id -Path $Cartridge -TimeoutSec 90 | Out-Null
    $again = Servers $id | ForEach-Object { $_.ProcessId }
}
finally {
    Stop-BrokenNesInstance -ProcessId $id | Out-Null
}
Start-Sleep -Seconds 2
$left = @($again | Where-Object { $_ -and (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
Check 'closing the app stops the server' (@($again).Count -ge 1 -and $left.Count -eq 0) "server pids at exit: $again; still alive: $($left.Count)"

$fail = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ("WMV UAT: {0} pass, {1} fail" -f ($results.Count - $fail), $fail)
exit ([int]($fail -gt 0))

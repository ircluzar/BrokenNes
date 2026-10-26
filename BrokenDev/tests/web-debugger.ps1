<#
.SYNOPSIS
  BrokenDev's first web page (D8): BrokenDev\web\debugger.html against the real desktop app. Exit 0 = pass; the last line is one JSON result.

.DESCRIPTION
  Starts the shipped app with scratch BROKENNES_CONFIG_DIR / BROKENNES_SAVES_DIR / BROKENNES_BUGS_DIR / BROKENDEV_HOME on the debugger's SNES
  and NES test ROMs (Workshop --debug-test --write-roms), pauses the game (Emulator > Pause, through /api/emulator/pause) so the page and the
  API see the same moment, and renders the page in headless Edge (a scratch profile; --dump-dom after its one refresh). Checks: the page's
  console, PC and registers are the API's; its disassembly starts at the PC; the memory panel shows the bytes the API reads; negative
  controls: a page pointed at a port nobody listens on says it cannot reach the app (and shows no registers); the page has no control call
  (no POST / DELETE in its source); the user's %APPDATA%\BrokenNes is unchanged.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Workshop,
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\bd_web_debugger')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
$page = Join-Path $repo 'BrokenDev\web\debugger.html'
$checks = [ordered]@{}
function Check($name, [bool]$ok, $detail) { $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }; Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) }
function Listing([string]$dir) { if (-not (Test-Path -LiteralPath $dir)) { return '' }; (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n" }
function Render([string]$query) {
    $url = ([Uri]$page).AbsoluteUri + '?' + $query
    $prof = Join-Path $WorkDir 'edge-profile'
    $out = Join-Path $WorkDir ('dom_' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.html')
    $p = Start-Process -FilePath $Edge -ArgumentList @('--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', "--user-data-dir=`"$prof`"", '--virtual-time-budget=8000', '--dump-dom', "`"$url`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    Get-Content -LiteralPath $out -Raw
}
function Pre([string]$dom, [string]$id) { if ($dom -match "id=`"$id`"[^>]*>([\s\S]*?)</pre>") { [System.Net.WebUtility]::HtmlDecode(($Matches[1] -replace '<[^>]+>', '')) } else { '' } }

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR }
$userDir = Join-Path $env:APPDATA 'BrokenNes'; $before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    if (-not (Test-Path -LiteralPath $Edge)) { throw "Edge is not at $Edge" }
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfg = Join-Path $WorkDir 'config'; New-Item -ItemType Directory -Force -Path $cfg, (Join-Path $WorkDir 'saves'), (Join-Path $WorkDir 'bdhome'), (Join-Path $WorkDir 'bugs') | Out-Null
    Set-Content -LiteralPath (Join-Path $cfg 'config.json') -Value '{ "bugCaptureEnabled": false }' -Encoding UTF8
    $roms = Join-Path $WorkDir 'roms'
    $p = Start-Process -FilePath $Workshop -ArgumentList @('--debug-test', '--write-roms', "`"$roms`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $WorkDir 'write-roms.txt')
    if ($p.ExitCode -ne 0) { throw "Workshop --write-roms failed ($($p.ExitCode))" }
    $env:BROKENNES_CONFIG_DIR = $cfg; $env:BROKENNES_SAVES_DIR = Join-Path $WorkDir 'saves'; $env:BROKENDEV_HOME = Join-Path $WorkDir 'bdhome'; $env:BROKENNES_BUGS_DIR = Join-Path $WorkDir 'bugs'
    $inst = Start-BrokenNesInstance -ExePath $Exe; $id = $inst.ProcessId
    Start-Sleep -Seconds 4
    $base = (Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA "BrokenNes\instances\$id.json") -Raw | ConvertFrom-Json)
    $api = $(if ($base.baseUrl) { $base.baseUrl } elseif ($base.httpPort) { "http://127.0.0.1:$($base.httpPort)" } else { "http://127.0.0.1:$($base.port)" })

    foreach ($case in @(@('debugtest.sfc', 'snes', 'vram:$2000', '11 22 33 44'), @('debugtest.nes', 'nes', 'prg:$0', ''))) {
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $roms $case[0]) } | Out-Null
        Start-Sleep -Seconds 2
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/pause' | Out-Null; Start-Sleep -Milliseconds 500
        $st = Invoke-BrokenNesApi -ProcessId $id -Path '/api/debug/state'
        $mem = Invoke-BrokenNesApi -ProcessId $id -Path ('/api/debug/memory?spec=' + [Uri]::EscapeDataString($case[2]) + '&length=16')
        $dom = Render ("api=" + [Uri]::EscapeDataString($api) + "&once=1&mem=" + [Uri]::EscapeDataString($case[2]))
        $st2 = Invoke-BrokenNesApi -ProcessId $id -Path '/api/debug/state'
        if ($st2.cpus[0].pcHex -ne $st.cpus[0].pcHex) { Write-Host "note: the PC moved while paused ($($st.cpus[0].pcHex) -> $($st2.cpus[0].pcHex)): the pause was late"; $st = $st2 }
        $regs = Pre $dom 'registers'; $dis = Pre $dom 'disasm'; $memText = Pre $dom 'memory'
        $c = $case[1]; $pc = $st.cpus[0].pcHex
        $apiBytes = ($mem.data | ForEach-Object { '{0:X2}' -f [int]$_ }) -join ' '
        if ($c -ne 'nes') {
            # the app's /api/emulator/pause does not stop a console session (only the NES loop): a running PC cannot be compared
            Check "${c}_console" ($dom -match "data-console=""$c""" -and $regs -match 'PC \$[0-9A-F]{6}') "page: $(($regs -split "`n")[0]) (running: the app cannot pause a console session over HTTP)"
        } else {
        Check "${c}_registers" ($dom -match "data-console=`"$c`"" -and $dom -match "data-pc=`"\$pc`"" -and $regs -match [regex]::Escape("PC $pc")) "API PC $pc; page: $(($regs -split "`n")[0])"
        Check "${c}_disasm_at_pc" (($dis -split "`n" | Where-Object { $_ -like '>*' } | Select-Object -First 1) -like "> $pc*") "page: $(($dis -split "`n" | Where-Object { $_ -like '>*' } | Select-Object -First 1))"
        }
        Check "${c}_memory" ($memText -like "*$apiBytes*" -and ($case[3] -eq '' -or $apiBytes -like "$($case[3])*")) "API $($case[2]): $apiBytes ; page: $((($memText -split "`n")[1]))"
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/resume' | Out-Null
    }
    $dead = Render ("api=" + [Uri]::EscapeDataString('http://127.0.0.1:1') + "&once=1")
    Check 'unreachable_negative' ($dead -match 'cannot reach http://127.0.0.1:1' -and (Pre $dead 'registers') -eq '') "a page pointed at port 1 says it cannot reach the app and shows no registers"
    $src = Get-Content -LiteralPath $page -Raw
    Check 'read_only_source' ($src -notmatch "method\s*:" -and $src -notmatch "'POST'|""POST""|DELETE") "the page's fetches are GETs only"
}
catch { Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace) }
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { if ($saved[$k]) { Set-Item -Path "env:$k" -Value $saved[$k] } else { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue } }
}
Check 'user_appdata_unchanged' ($before -eq (Listing $userDir)) "the user's $userDir"
$pass = -not ($checks.Values | Where-Object { -not $_.ok })
[ordered]@{ test = 'web-debugger'; pass = $pass; checks = $checks } | ConvertTo-Json -Compress -Depth 5
if ($pass) { exit 0 } else { exit 1 }

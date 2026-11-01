<#
.SYNOPSIS
  BrokenDev D8 case 2: the read-only debug pages on the LAN (Emulator > Debug Windows > Share Read-Only Pages on the LAN).
.DESCRIPTION
  Starts the shipped WinForms app with scratch BROKENNES_CONFIG_DIR, BROKENNES_SAVES_DIR, BROKENNES_BUGS_DIR and BROKENDEV_HOME, and
  BROKENNES_DEBUG_LAN_ADDRESS=127.0.0.2: the share's real code path, bound to a loopback alias instead of the LAN address, because binding
  the LAN address would raise the Windows firewall question (we never touch firewall settings). The share is switched through the real menu
  (UI Automation) and the URL with the token is read from the app's window, as the user would. Checks:
  - off (the negative control): the app listens on nothing but 127.0.0.1, /api/debug/lan says off;
  - on: the page and the GET routes answer with the token and match the loopback routes; the page renders in headless Edge (scratch
    profile) from the LAN listener with the paused PC; the header token works too;
  - refused: no token / a wrong token 401; POST / DELETE / PUT 405 (the poke and attach change nothing); control routes, the poke,
    progression, saves and the status route itself 404; the listener is bound to the one address, never to all;
  - off again: the port refuses a connect, nothing listens on 127.0.0.2; on again makes a new token and the old one is refused;
  - nothing saved: the scratch config.json is byte for byte the same; the user's %APPDATA%\BrokenNes is unchanged.
.EXAMPLE
  powershell -File UAT\entrypoints\debug-pages-lan-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe -Workshop C:\path\BrokenNes.Workshop.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Workshop,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_debug_lan'),
    [string]$Alias = '127.0.0.2',
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'debug-pages-lan'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Api([int]$procId, [string]$method, [string]$path, $body = $null) { Invoke-BrokenNesApi -ProcessId $procId -Method $method -Path $path -Body $body -TimeoutSec 60 }
function Listing([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (@(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })) -join "`n"
}
# A raw request to the share: status, body (never throws on 4xx).
function Lan([string]$method, [string]$url, [hashtable]$headers = @{}) {
    Add-Type -AssemblyName System.Net.Http
    $c = New-Object System.Net.Http.HttpClient; $c.Timeout = [TimeSpan]::FromSeconds(20)
    try {
        $m = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
        foreach ($k in $headers.Keys) { $m.Headers.TryAddWithoutValidation($k, $headers[$k]) | Out-Null }
        if ($method -ne 'GET') { $m.Content = New-Object System.Net.Http.StringContent('{"domain":"RAM","address":0,"value":171}', [Text.Encoding]::UTF8, 'application/json') }
        $r = $c.SendAsync($m).GetAwaiter().GetResult()
        [pscustomobject]@{ status = [int]$r.StatusCode; body = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
    } catch { [pscustomobject]@{ status = -1; body = $_.Exception.GetBaseException().Message } } finally { $c.Dispose() }
}
function Listeners([int]$procId) { @(Get-NetTCPConnection -State Listen -OwningProcess $procId -ErrorAction SilentlyContinue | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) }
function Refused([string]$addr, [int]$port) {
    $t = New-Object System.Net.Sockets.TcpClient
    try { $t.Connect($addr, $port); return $false } catch { return $true } finally { $t.Dispose() }
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function WaitFor([scriptblock]$probe, [int]$ms = 8000) {
    $end = (Get-Date).AddMilliseconds($ms)
    do { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 150 } while ((Get-Date) -lt $end)
    return $null
}
function FindByName($root, [string]$name) { $root.FindFirst($TS::Descendants, (Cond $AE::NameProperty $name)) }
function FindById($root, [string]$id) { $root.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function TextOf($el) { $vp = $null; if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }; $el.Current.Name }
# Emulator > Debug Windows > Share Read-Only Pages on the LAN, through the menu (a click toggles it).
function Toggle-Share([int]$procId) {
    $main = WaitFor { $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))))) }
    if (-not $main) { throw 'the main window was not found' }
    $emu = WaitFor { FindByName $main 'Emulator' }
    $emu.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400
    $sub = WaitFor { FindByName $AE::RootElement 'Debug Windows' }
    $sub.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 400
    $item = WaitFor { FindByName $AE::RootElement 'Share Read-Only Pages on the LAN' }
    if (-not $item) { throw 'the menu item Share Read-Only Pages on the LAN was not found' }
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
}
function ShareWindow([int]$procId, [int]$ms = 8000) { WaitFor { $AE::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::ProcessIdProperty $procId), (Cond $AE::NameProperty 'BrokenNes LAN Share')))) } $ms }
function Render([string]$url, [string]$name) {
    $prof = Join-Path $WorkDir "edge-$name"; $out = Join-Path $WorkDir "$name.html"
    $p = Start-Process -FilePath $Edge -ArgumentList @('--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', "--user-data-dir=`"$prof`"", '--virtual-time-budget=30000', '--dump-dom', "`"$url`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    Get-Content -LiteralPath $out -Raw
}
function Pre([string]$dom, [string]$id) { if ($dom -match "id=`"$id`"[^>]*>([\s\S]*?)</pre>") { [System.Net.WebUtility]::HtmlDecode(($Matches[1] -replace '<[^>]+>', '')) } else { '' } }

$inst = $null
$saved = @{ BROKENNES_CONFIG_DIR = $env:BROKENNES_CONFIG_DIR; BROKENNES_SAVES_DIR = $env:BROKENNES_SAVES_DIR; BROKENDEV_HOME = $env:BROKENDEV_HOME; BROKENNES_BUGS_DIR = $env:BROKENNES_BUGS_DIR; BROKENNES_DEBUG_LAN_ADDRESS = $env:BROKENNES_DEBUG_LAN_ADDRESS }
$userDir = Join-Path $env:APPDATA 'BrokenNes'
$before = Listing $userDir
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $cfgDir = Join-Path $WorkDir 'config'; $saveDir = Join-Path $WorkDir 'saves'; $bdHome = Join-Path $WorkDir 'bdhome'; $bugs = Join-Path $WorkDir 'bugs'
    New-Item -ItemType Directory -Force -Path $cfgDir, $saveDir, $bdHome, $bugs | Out-Null
    $roms = Join-Path $WorkDir 'roms'
    $p = Start-Process -FilePath $Workshop -ArgumentList @('--debug-test', '--write-roms', "`"$roms`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $WorkDir 'write-roms.txt')
    if ($p.ExitCode -ne 0) { throw "Workshop --debug-test --write-roms failed ($($p.ExitCode))" }
    $cfgFile = Join-Path $cfgDir 'config.json'
    Set-Content -LiteralPath $cfgFile -Value '{ "bugCaptureEnabled": false }' -Encoding UTF8

    $env:BROKENNES_CONFIG_DIR = $cfgDir; $env:BROKENNES_SAVES_DIR = $saveDir; $env:BROKENDEV_HOME = $bdHome; $env:BROKENNES_BUGS_DIR = $bugs
    $env:BROKENNES_DEBUG_LAN_ADDRESS = $Alias
    $inst = Start-BrokenNesInstance -ExePath $Exe
    $id = $inst.ProcessId
    Start-Sleep -Seconds 4
    Api $id POST '/api/emulator/load-rom' @{ path = (Join-Path $roms 'debugtest.nes') } | Out-Null
    $s = WaitFor { $x = Api $id GET '/api/debug/state'; if ($x.success -and $x.console -eq 'nes') { $x } } 10000
    Start-Sleep -Milliseconds 800
    Api $id POST '/api/emulator/pause' | Out-Null; Start-Sleep -Milliseconds 600
    $cfgHash0 = (Get-FileHash -LiteralPath $cfgFile).Hash
    $loop = (Api $id GET '/api/debug/state').cpus[0].pcHex

    # ---------------------------------------------------------------- off (the negative control)
    $l0 = Listeners $id
    $st0 = Api $id GET '/api/debug/lan'
    Check 'off_nothing_on_lan' ((@($l0 | Where-Object { $_ -notlike '127.0.0.1:*' }).Count -eq 0) -and -not $st0.on) "share off: the app listens on $($l0 -join ', '); /api/debug/lan on=$($st0.on) ($($st0.why))"

    # ---------------------------------------------------------------- on (the menu)
    Toggle-Share $id
    $w = ShareWindow $id
    if (-not $w) { throw 'the LAN share window did not open' }
    $url = TextOf (FindById $w 'lanShareUrl'); $durl = TextOf (FindById $w 'lanShareDebuggerUrl')
    $st1 = Api $id GET '/api/debug/lan'
    $u = [Uri]$url
    $token = if ($url -match '[?&]t=([0-9a-f]+)') { $Matches[1] } else { '' }
    $base = "http://$($u.Host):$($u.Port)"
    Check 'on_window_shows_url' ($u.Host -eq $Alias -and $u.AbsolutePath -eq '/debug/viewers.html' -and $token.Length -ge 32 -and $st1.on -and $st1.port -eq $u.Port) "the window shows $($url -replace [regex]::Escape($token), '<token>') ; /api/debug/lan on=$($st1.on) port $($st1.port)"
    Check 'status_never_has_token' (-not ((Api $id GET '/api/debug/lan' | ConvertTo-Json -Depth 5).Contains($token))) "the loopback status route does not print the token"
    $l1 = Listeners $id
    Check 'bound_to_one_address' (($l1 -contains "${Alias}:$($u.Port)") -and -not (@($l1 | Where-Object { $_ -like '0.0.0.0:*' -or $_ -like '[::]*' -or $_ -like '::*' }).Count)) "listening: $($l1 -join ', ')"

    $pg = Lan GET $url
    Check 'page_viewers' ($pg.status -eq 200 -and $pg.body.Contains('BrokenNes') -and $pg.body.Contains('withToken')) "GET /debug/viewers.html?t=: $($pg.status), $($pg.body.Length) bytes"
    $pd = Lan GET $durl
    Check 'page_debugger' ($pd.status -eq 200 -and $pd.body.Contains('id="registers"')) "GET /debug/debugger.html?t=: $($pd.status)"
    $memLan = Lan GET "$base/api/debug/memory?spec=%248000&length=32&t=$token"
    $memLoop = Api $id GET '/api/debug/memory?spec=$8000&length=32'
    $ml = $memLan.body | ConvertFrom-Json
    Check 'route_matches_loopback' ($memLan.status -eq 200 -and (($ml.data | ConvertTo-Json -Compress) -eq ($memLoop.data | ConvertTo-Json -Compress)) -and $ml.data.Count -gt 0) "memory `$8000: LAN $($memLan.status) $(($ml.data | Select-Object -First 6) -join ',')... = loopback $(($memLoop.data | Select-Object -First 6) -join ',')..."
    $dl = (Lan GET "$base/api/memory/domains?t=$token").body | ConvertFrom-Json
    $dlo = Api $id GET '/api/memory/domains'
    Check 'domains_match_loopback' ((($dl.domains.name) -join ',') -eq (($dlo.domains.name) -join ',') -and $dl.domains.Count -gt 0) "domains: $(($dl.domains.name) -join ', ')"
    $dom0 = $dlo.domains[0].name
    $stl = (Lan GET "$base/api/debug/state" @{ 'X-BrokenNes-Token' = $token }).body | ConvertFrom-Json
    Check 'header_token_state' ($stl.success -and $stl.cpus[0].pcHex -eq $loop) "the header token: state PC $($stl.cpus[0].pcHex), loopback $loop (paused)"
    $routes = @('/api/debug/disasm?count=4', '/api/debug/callstack', '/api/debug/registers', '/api/debug/palettes', '/api/debug/tiles', '/api/debug/map', '/api/debug/sprites', '/api/debug/sound', '/api/debug/events', ('/api/memory/peek-range?domain=' + [Uri]::EscapeDataString($dom0) + '&address=0&length=16'))
    $bad = @(foreach ($r in $routes) { $x = Lan GET ("$base$r" + $(if ($r.Contains('?')) { '&' } else { '?' }) + "t=$token"); if ($x.status -ne 200 -or -not $x.body.StartsWith('{')) { "$r=$($x.status)" } })
    Check 'all_read_routes_answer' ($bad.Count -eq 0) "the $($routes.Count) other GET routes answer JSON with the token$(if ($bad) { ': ' + ($bad -join ', ') })"

    $probeFile = Join-Path $WorkDir 'edge-probe.html'; Set-Content -LiteralPath $probeFile -Value '<html><body><p id="probe">rendered</p></body></html>'
    $edgeWorks = (Test-Path -LiteralPath $Edge) -and ((Render ([Uri]$probeFile).AbsoluteUri 'probe') -match 'rendered')
    if (-not $edgeWorks) { $result.skipped = @('page_renders_from_lan: headless Edge renders nothing on this machine (not even a local file), so the page itself was not rendered; the page bytes and every route it reads are checked above') ; Write-Host '[SKIP] page_renders_from_lan - headless Edge renders nothing here (not even a local file)' }
    elseif ($true) {
        $dom = Render $durl 'lan-debugger'
        $regs = Pre $dom 'registers'
        Check 'page_renders_from_lan' ($regs -match [regex]::Escape("PC $loop")) "headless Edge on the LAN URL: $(($regs -split "`n")[0]) (loopback PC $loop)"
    }

    # ---------------------------------------------------------------- refused
    $n1 = Lan GET "$base/debug/viewers.html"; $n2 = Lan GET "$base/api/debug/state"; $n3 = Lan GET "$base/api/debug/state?t=$('0' * $token.Length)"; $n4 = Lan GET "$base/api/debug/state?t=$($token.Substring(0, $token.Length - 1))"
    Check 'no_token_401' ($n1.status -eq 401 -and $n2.status -eq 401 -and $n3.status -eq 401 -and $n4.status -eq 401 -and -not $n2.body.StartsWith('{')) "no token: page $($n1.status), route $($n2.status); a wrong token $($n3.status); a shortened token $($n4.status)"
    $ram0 = (Api $id GET ('/api/memory/peek-range?domain=' + [Uri]::EscapeDataString($dom0) + '&address=0&length=1')).data[0]
    $pk = Lan POST "$base/api/memory/poke?t=$token"
    $at = Lan POST "$base/api/debug/attach?t=$token"
    $dlt = Lan DELETE "$base/api/debug/breakpoints?t=$token"
    $pu = Lan PUT "$base/api/debug/state?t=$token"
    $ram1 = (Api $id GET ('/api/memory/peek-range?domain=' + [Uri]::EscapeDataString($dom0) + '&address=0&length=1')).data[0]
    $sa = Api $id GET '/api/debug/state'
    Check 'non_get_405' ($pk.status -eq 405 -and $at.status -eq 405 -and $dlt.status -eq 405 -and $pu.status -eq 405 -and $ram1 -eq $ram0 -and -not $sa.attached) "POST poke $($pk.status), POST attach $($at.status), DELETE $($dlt.status), PUT $($pu.status); RAM[0] $ram0 -> $ram1, attached $($sa.attached)"
    $others = @('/api/debug/trace', '/api/debug/wait', '/api/debug/eval?expr=1', '/api/debug/breakpoints', '/api/debug/port', '/api/debug/lan', '/api/memory/poke', '/api/memory/peek?domain=RAM&address=0', '/api/emulator/pause', '/api/progression', '/api/save/list', '/api/input/state', '/', '/debug/', '/debug/../api/debug/attach', '/api/debug/state/../attach', '/API/DEBUG/STATE')
    $leak = @(foreach ($r in $others) { $x = Lan GET ("$base$r" + $(if ($r.Contains('?')) { '&' } else { '?' }) + "t=$token"); if ($x.status -ne 404) { "$r=$($x.status)" } })
    Check 'other_paths_404' ($leak.Count -eq 0) "$($others.Count) control / save / progression / unknown paths: 404$(if ($leak) { ' EXCEPT ' + ($leak -join ', ') })"

    # ---------------------------------------------------------------- off again, on again
    Toggle-Share $id
    $st2 = Api $id GET '/api/debug/lan'
    $l2 = Listeners $id
    $gone = WaitFor { if (Refused $Alias $u.Port) { $true } } 4000
    Check 'off_closes' ([bool]$gone -and -not $st2.on -and (@($l2 | Where-Object { $_ -notlike '127.0.0.1:*' }).Count -eq 0) -and -not (ShareWindow $id 1000)) "off: a connect to ${Alias}:$($u.Port) is refused $([bool]$gone), status on=$($st2.on), listening $($l2 -join ', ')"
    Toggle-Share $id
    $w2 = ShareWindow $id
    $url2 = TextOf (FindById $w2 'lanShareUrl'); $u2 = [Uri]$url2; $token2 = if ($url2 -match '[?&]t=([0-9a-f]+)') { $Matches[1] } else { '' }
    $old = Lan GET "http://$($u2.Host):$($u2.Port)/api/debug/state?t=$token"
    $new = Lan GET "http://$($u2.Host):$($u2.Port)/api/debug/state?t=$token2"
    Check 'new_token_each_time' ($token2 -ne $token -and $old.status -eq 401 -and $new.status -eq 200) "on again: a new token ($($token2 -ne $token)); the old one $($old.status), the new one $($new.status)"
    Toggle-Share $id
    Check 'off_again_closes' ((Refused $Alias $u2.Port) -and -not (Api $id GET '/api/debug/lan').on) "off: ${Alias}:$($u2.Port) refuses"
    $cfgHash1 = (Get-FileHash -LiteralPath $cfgFile).Hash
    Check 'nothing_saved' ($cfgHash0 -eq $cfgHash1 -and -not ((Get-Content -LiteralPath $cfgFile -Raw) -match '"debugPagesLan"\s*:\s*true')) "the scratch config.json $(if ($cfgHash0 -eq $cfgHash1) { 'is byte for byte the same' } else { 'CHANGED' }) across on / off / on / off"

    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false ($_.Exception.Message + ' ' + $_.ScriptStackTrace)
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    foreach ($k in $saved.Keys) { Set-Item -Path "env:$k" -Value $saved[$k] -ErrorAction SilentlyContinue; if (-not $saved[$k]) { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue } }
}
$after = Listing $userDir
Check 'user_appdata_unchanged' ($before -eq $after) "the user's $userDir $(if ($before -eq $after) { 'is unchanged' } else { 'CHANGED' })"
if ($before -ne $after) { $result.pass = $false }
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }

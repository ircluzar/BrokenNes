<#
.SYNOPSIS
  Entrypoint smoke test: BrokenNes 2 Lite (WebLite\BrokenNes.Lite.csproj, Blazor WebAssembly).
  Exit 0 = pass, non-zero = fail. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  1. `dotnet publish WebLite\BrokenNes.Lite.csproj -c Release` with --artifacts-path / -o under
     $env:LOCALAPPDATA\VRUN_Nes_Dev_work\uat_web (nothing lands in the repo tree). Default is the
     NON-AOT publish (the AOT publish needs the wasm-tools workload and takes far longer); pass -Aot
     to add -p:EnableWasmAot=true.
  2. Serves <publish>\wwwroot with `python -m http.server` bound to 127.0.0.1 on a free port.
  3. Launches headless Edge/Chrome (--headless=new, own temp profile) and drives it over the Chrome
     DevTools Protocol with a small ClientWebSocket client (no Node / Playwright needed).
  4. Opens  /?rom=vrun.nes  (the Lite page loads that built-in ROM and starts it by itself).

  VERIFIED AUTOMATICALLY (named checks in the JSON):
    publish_ok          dotnet publish exit 0 and wwwroot\index.html exists
    static_files        EVERY published file (except the .br/.gz siblings) answers HEAD 200 through the
                        server, and .wasm is served as application/wasm
    page_loads          no HTTP >= 400 / failed request seen by the browser (favicon excepted)
    app_boots           the Blazor app rendered (#nes-canvas present, "STATUS" text) with no
                        #blazor-error-ui banner and no uncaught JS exception
    rom_running         page text reports "ROM: vrun.nes" and "State: Running"
    fps_reported        the page's own FPS counter parses to > 0 (frames really are being produced)
    picture_nonblank    a DevTools screenshot clipped to #nes-canvas contains MinColors..MaxColors distinct colours (the upper bound rejects the "no signal" static-noise screen)

  Informational only (recorded in notes, never fail): whether two screenshots a few seconds apart
  differ (VRUN's title screen is mostly static), the reported FPS, JS console errors.

  NOT VERIFIED: audio (headless Chrome has no audible output and the page exposes no sample tap), the
  AOT build (unless -Aot), SNES/GB sessions, touch UI, other browsers, gameplay input, correctness of
  pixels (only "non-blank"). The ROM is the bundled WebLite\wwwroot\vrun.nes, which is NOT the same
  file as VRUN_Nes_Dev\builds\...\vrun_game.nes (the desktop smoke uses that one).
  Non-AOT WASM is an interpreter: FPS is low (single digits) - that is expected, not a failure.

  Requires: dotnet SDK + wasm workload used by the project, Python 3, Edge or Chrome installed.
  Every process started here is stopped BY PID (python, browser tree via taskkill /T /PID).

.EXAMPLE
  pwsh -File UAT\entrypoints\web-smoke.ps1
  pwsh -File UAT\entrypoints\web-smoke.ps1 -SkipPublish     # reuse the last publish output
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$Aot,
    [string]$RomKey = 'vrun.nes',
    [int]$BootTimeoutSeconds = 150,
    [int]$MinColors = 3,
    [int]$MaxColors = 200,
    [string]$Python = (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python314\python.exe'),
    [string]$Browser,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_web')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$notes = New-Object System.Collections.Generic.List[string]
$result = [ordered]@{ entrypoint = 'web-lite'; pass = $false }
$pyProc = $null; $brProc = $null; $ws = $null

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Get-FreePort {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start()
    $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Security.Cryptography;
public static class UatPx {
  public static string Analyse(byte[] bgra, int w, int h, int stride) {
    var set = new HashSet<int>();
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int o = y*stride + x*4; set.Add(bgra[o] | bgra[o+1]<<8 | bgra[o+2]<<16); }
    return set.Count + ":" + BitConverter.ToString(SHA1.Create().ComputeHash(bgra)).Replace("-","");
  }
}
'@
function Get-PngStats([byte[]]$Png) {
    $ms = New-Object IO.MemoryStream(,$Png)
    $src = [System.Drawing.Bitmap]::new($ms)
    $bmp = [System.Drawing.Bitmap]::new($src.Width, $src.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.DrawImage($src, 0, 0, $src.Width, $src.Height); $g.Dispose()
    $d = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height), 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $buf = New-Object byte[] ($d.Stride * $d.Height)
    [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length); $bmp.UnlockBits($d)
    $r = [UatPx]::Analyse($buf, $bmp.Width, $bmp.Height, $d.Stride)
    $bmp.Dispose(); $src.Dispose(); $ms.Dispose()
    return $r
}

# ---------------- minimal CDP client ----------------
$script:cdpId = 0; $script:events = New-Object System.Collections.Generic.List[object]
function Receive-Cdp {
    $buf = New-Object byte[] 65536; $ms = New-Object IO.MemoryStream
    do { $r = $script:ws.ReceiveAsync([ArraySegment[byte]]::new($buf), [Threading.CancellationToken]::None).GetAwaiter().GetResult(); $ms.Write($buf, 0, $r.Count) } until ($r.EndOfMessage)
    return ([Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json)
}
function Invoke-Cdp([string]$Method, $Params = @{}) {
    $id = ++$script:cdpId
    $json = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $script:ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    while ($true) {
        $m = Receive-Cdp
        if ($m.PSObject.Properties.Name -contains 'id' -and $m.id -eq $id) {
            if ($m.PSObject.Properties.Name -contains 'error') { throw "CDP $Method failed: $($m.error.message)" }
            return $m.result
        }
        $script:events.Add($m)
    }
}
function Eval-Js([string]$Expr) {
    $r = Invoke-Cdp 'Runtime.evaluate' @{ expression = $Expr; returnByValue = $true; awaitPromise = $true }
    if ($r.exceptionDetails) { throw "JS error: $($r.exceptionDetails.text) $($r.exceptionDetails.exception.description)" }
    return $r.result.value
}

try {
    $pubRoot = Join-Path $WorkDir 'publish'
    $wwwroot = Join-Path $pubRoot 'wwwroot'
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

    # ---------------- publish ----------------
    if (-not $SkipPublish) {
        $log = Join-Path $WorkDir 'publish.log'
        $pargs = @('publish', (Join-Path $repo 'WebLite\BrokenNes.Lite.csproj'), '-c', 'Release', '--artifacts-path', (Join-Path $WorkDir 'artifacts'), '-o', $pubRoot, '-nologo', '-v', 'q')
        if ($Aot) { $pargs += '-p:EnableWasmAot=true' }
        Write-Host "dotnet publish WebLite (log: $log) ..."
        $psw = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @pargs *> $log
        $code = $LASTEXITCODE
        Check 'publish_ok' ($code -eq 0 -and (Test-Path (Join-Path $wwwroot 'index.html'))) ("exit $code in {0:n0}s, aot=$($Aot.IsPresent)" -f $psw.Elapsed.TotalSeconds)
        if ($code -ne 0) { throw "publish failed, see $log" }
    } else {
        Check 'publish_ok' (Test-Path (Join-Path $wwwroot 'index.html')) "-SkipPublish: reusing $wwwroot"
        if (-not (Test-Path (Join-Path $wwwroot 'index.html'))) { throw 'no previous publish output' }
    }

    # ---------------- serve ----------------
    if (-not (Test-Path -LiteralPath $Python)) { $c = Get-Command python -ErrorAction SilentlyContinue; if ($c) { $Python = $c.Source } else { throw "python not found: $Python" } }
    $port = Get-FreePort
    $pyProc = Start-Process -FilePath $Python -ArgumentList @('-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $wwwroot) -PassThru -WindowStyle Hidden
    $base = "http://127.0.0.1:$port"
    $http = [Net.Http.HttpClient]::new(); $http.Timeout = [TimeSpan]::FromSeconds(30)
    $up = $false
    for ($i = 0; $i -lt 40 -and -not $up; $i++) { try { $up = ($http.GetAsync("$base/").GetAwaiter().GetResult().StatusCode -eq 200) } catch { Start-Sleep -Milliseconds 250 } }
    if (-not $up) { throw 'static server did not come up' }
    $result.url = $base; $result.serverPid = $pyProc.Id

    $files = Get-ChildItem -LiteralPath $wwwroot -Recurse -File | Where-Object { $_.Extension -notin '.br', '.gz' }
    $bad = New-Object System.Collections.Generic.List[string]; $wasmBadMime = 0
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($wwwroot.Length).TrimStart('\').Replace('\', '/')
        $req = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Head, [uri]("$base/" + (($rel -split '/' | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')))
        $resp = $http.SendAsync($req).GetAwaiter().GetResult()
        if ([int]$resp.StatusCode -ne 200) { $bad.Add("$rel -> $([int]$resp.StatusCode)") }
        elseif ($f.Extension -eq '.wasm' -and $resp.Content.Headers.ContentType.MediaType -ne 'application/wasm') { $wasmBadMime++; $bad.Add("$rel mime=$($resp.Content.Headers.ContentType.MediaType)") }
    }
    Check 'static_files' ($bad.Count -eq 0) ("{0} files HEAD-checked, {1} problems{2}" -f $files.Count, $bad.Count, $(if ($bad.Count) { ': ' + ($bad | Select-Object -First 5) -join '; ' } else { '' }))

    # ---------------- browser ----------------
    if (-not $Browser) {
        $cands = @("$env:ProgramFiles (x86)\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
                   "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe")
        $Browser = $cands | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $Browser) { throw 'no Edge/Chrome found; pass -Browser' }
    $result.browser = $Browser
    $dbgPort = Get-FreePort
    $profile = Join-Path $WorkDir ("profile-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $brProc = Start-Process -FilePath $Browser -PassThru -ArgumentList @('--headless=new', "--remote-debugging-port=$dbgPort", "--user-data-dir=$profile",
        '--no-first-run', '--no-default-browser-check', '--window-size=1280,1000', '--enable-unsafe-swiftshader', '--use-angle=swiftshader', 'about:blank')
    $target = $null
    for ($i = 0; $i -lt 60 -and -not $target; $i++) {
        try { $target = (Invoke-RestMethod "http://127.0.0.1:$dbgPort/json/list" -TimeoutSec 3) | Where-Object { $_.type -eq 'page' } | Select-Object -First 1 } catch { }
        if (-not $target) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $target) { throw 'browser DevTools endpoint did not appear' }
    $ws = [Net.WebSockets.ClientWebSocket]::new()
    $ws.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    foreach ($d in 'Page', 'Runtime', 'Network', 'Log') { [void](Invoke-Cdp "$d.enable") }

    $url = "$base/?rom=$([uri]::EscapeDataString($RomKey))"
    $tNav = Get-Date
    [void](Invoke-Cdp 'Page.navigate' @{ url = $url })

    # wait for boot: ROM loaded and running
    $state = $null; $booted = $false
    $probe = "(() => { const t = document.body ? document.body.innerText : ''; const f = t.match(/FPS:\s*([0-9.]+)/); return JSON.stringify({ canvas: !!document.querySelector('#nes-canvas'), status: /STATUS/i.test(t), rom: /ROM:\s*$([regex]::Escape($RomKey))/.test(t), running: /State:\s*Running/.test(t), fps: f ? parseFloat(f[1]) : null, err: (() => { const e = document.querySelector('#blazor-error-ui'); return !!e && getComputedStyle(e).display !== 'none' })() }) })()"
    while (((Get-Date) - $tNav).TotalSeconds -lt $BootTimeoutSeconds) {
        try { $state = (Eval-Js $probe) | ConvertFrom-Json } catch { $state = $null }
        if ($state -and $state.rom -and $state.running) { $booted = $true; break }
        Start-Sleep -Milliseconds 1000
    }
    $bootSecs = ((Get-Date) - $tNav).TotalSeconds
    Start-Sleep -Seconds 5                      # let it run so FPS counter and picture settle
    $state = (Eval-Js $probe) | ConvertFrom-Json
    $result.bootSeconds = [math]::Round($bootSecs, 1)

    Check 'app_boots' ($state.canvas -and $state.status -and -not $state.err) ("canvas=$($state.canvas) statusText=$($state.status) errorBanner=$($state.err) after {0:n1}s" -f $bootSecs)
    Check 'rom_running' ($booted -and $state.rom -and $state.running) "rom=$($state.rom) running=$($state.running) (rom key '$RomKey')"
    Check 'fps_reported' ($null -ne $state.fps -and $state.fps -gt 0) "page FPS counter = $($state.fps)"

    # canvas screenshots (WebGL canvas cannot be read back from JS reliably; the compositor screenshot can)
    $rect = (Eval-Js "(() => { const r = document.querySelector('#nes-canvas').getBoundingClientRect(); return JSON.stringify({x:r.x,y:r.y,w:r.width,h:r.height}) })()") | ConvertFrom-Json
    $shots = @()
    foreach ($n in 1, 2) {
        $clip = @{ x = [math]::Max(0, $rect.x); y = [math]::Max(0, $rect.y); width = [math]::Max(1, $rect.w); height = [math]::Max(1, $rect.h); scale = 1 }
        $s = Invoke-Cdp 'Page.captureScreenshot' @{ format = 'png'; clip = $clip }
        $bytes = [Convert]::FromBase64String($s.data)
        if ($n -eq 1) { $png = Join-Path $WorkDir 'web-frame.png'; [IO.File]::WriteAllBytes($png, $bytes) }
        $shots += Get-PngStats $bytes
        if ($n -eq 1) { Start-Sleep -Seconds 4 }
    }
    $colors = [int]($shots[0].Split(':')[0])
    Check 'picture_nonblank' ($colors -ge $MinColors -and $colors -le $MaxColors) "$colors distinct colours (need $MinColors..$MaxColors; >$MaxColors = no-signal static noise) in canvas screenshot ($([int]$rect.w)x$([int]$rect.h), png: $png)"
    $notes.Add("picture changed between screenshots: $($shots[0].Split(':')[1] -ne $shots[1].Split(':')[1]) (informational)")

    # flush remaining events, then judge network/JS errors
    [void](Eval-Js '1')
    $netBad = @($script:events | Where-Object { $_.method -eq 'Network.responseReceived' -and $_.params.response.status -ge 400 -and $_.params.response.url -notmatch 'favicon' } | ForEach-Object { "$($_.params.response.status) $($_.params.response.url)" })
    $netFail = @($script:events | Where-Object { $_.method -eq 'Network.loadingFailed' -and -not $_.params.canceled } | ForEach-Object { $_.params.errorText })
    $jsExc = @($script:events | Where-Object { $_.method -eq 'Runtime.exceptionThrown' } | ForEach-Object { $_.params.exceptionDetails.exception.description })
    $conErr = @($script:events | Where-Object { $_.method -eq 'Runtime.consoleAPICalled' -and $_.params.type -eq 'error' } | ForEach-Object { ($_.params.args | ForEach-Object { $_.value }) -join ' ' })
    $reqCount = @($script:events | Where-Object { $_.method -eq 'Network.responseReceived' }).Count
    Check 'page_loads' ($netBad.Count -eq 0 -and $netFail.Count -eq 0 -and $jsExc.Count -eq 0) ("{0} responses seen; http>=400: {1}; failed: {2}; uncaught exceptions: {3}{4}" -f $reqCount, $netBad.Count, $netFail.Count, $jsExc.Count, $(if ($netBad + $netFail + $jsExc) { ' -> ' + (($netBad + $netFail + $jsExc) | Select-Object -First 3) -join ' | ' } else { '' }))
    if ($conErr.Count) { $notes.Add("console.error x$($conErr.Count): " + ($conErr | Select-Object -First 2) -join ' | ') }
    $notes.Add("reported FPS=$($state.fps) (non-AOT interpreter is slow; informational)")
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "ERROR: $($result.error)"
}
finally {
    if ($ws) { try { [void](Invoke-Cdp 'Browser.close') } catch { } ; try { $ws.Dispose() } catch { } }
    if ($brProc) { Start-Sleep -Milliseconds 500; & taskkill.exe /T /F /PID $brProc.Id *> $null }     # our browser tree, by PID
    if ($pyProc) { Stop-Process -Id $pyProc.Id -Force -ErrorAction SilentlyContinue }               # our server, by PID
    $result.checks = $checks
    $result.notes = $notes
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($checks.Values | Where-Object { -not $_.ok }).Count
    $result.pass = (-not $result.Contains('error')) -and $checks.Count -ge 7 -and $failed -eq 0
    Write-Output ($result | ConvertTo-Json -Depth 6 -Compress)
}
if ($result.pass) { exit 0 } else { exit 1 }

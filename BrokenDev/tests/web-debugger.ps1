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
function Render([string]$query, [string]$file = $page) {
    $url = ([Uri]$file).AbsoluteUri + '?' + $query
    $prof = Join-Path $WorkDir 'edge-profile'
    $out = Join-Path $WorkDir ('dom_' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.html')
    $p = Start-Process -FilePath $Edge -ArgumentList @('--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', "--user-data-dir=`"$prof`"", '--virtual-time-budget=30000', '--dump-dom', "`"$url`"") -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    Get-Content -LiteralPath $out -Raw
}
function Tile0([string]$dom) { if ($dom -match 'data-tile0="([^"]*)"') { $Matches[1] } else { '' } }
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

    foreach ($case in @(@('debugtest.sfc', 'snes', 'vram:$2000', '11 22 33 44'), @('debugtest.gb', 'gb', 'rom:$100', '00 C3 50 01'), @('debugtest.nes', 'nes', 'prg:$0', ''))) {
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $roms $case[0]) } | Out-Null
        Start-Sleep -Seconds 2
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/pause' | Out-Null; Start-Sleep -Milliseconds 500
        $st = Invoke-BrokenNesApi -ProcessId $id -Path '/api/debug/state'
        $mem = Invoke-BrokenNesApi -ProcessId $id -Path ('/api/debug/memory?spec=' + [Uri]::EscapeDataString($case[2]) + '&length=16')
        $dom = Render ("api=" + [Uri]::EscapeDataString($api) + "&once=1&mem=" + [Uri]::EscapeDataString($case[2]))
        $st2 = Invoke-BrokenNesApi -ProcessId $id -Path '/api/debug/state'
        Check "$($case[1])_paused" ($st2.frame -eq $st.frame -and $st2.cpus[0].pcHex -eq $st.cpus[0].pcHex) "paused through /api/emulator/pause: frame $($st.frame) -> $($st2.frame) over the render, PC $($st.cpus[0].pcHex) -> $($st2.cpus[0].pcHex)"
        $regs = Pre $dom 'registers'; $dis = Pre $dom 'disasm'; $memText = Pre $dom 'memory'
        $c = $case[1]; $pc = $st.cpus[0].pcHex
        $apiBytes = ($mem.data | ForEach-Object { '{0:X2}' -f [int]$_ }) -join ' '
        Check "${c}_registers" ($dom -match "data-console=`"$c`"" -and $dom -match "data-pc=`"\$pc`"" -and $regs -match [regex]::Escape("PC $pc")) "API PC $pc; page: $(($regs -split "`n")[0])"
        Check "${c}_disasm_at_pc" (($dis -split "`n" | Where-Object { $_ -like '>*' } | Select-Object -First 1) -like "> $pc*") "page: $(($dis -split "`n" | Where-Object { $_ -like '>*' } | Select-Object -First 1))"
        Check "${c}_memory" ($memText -like "*$apiBytes*" -and ($case[3] -eq '' -or $apiBytes -like "$($case[3])*")) "API $($case[2]): $apiBytes ; page: $((($memText -split "`n")[1]))"
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/resume' | Out-Null
        Start-Sleep -Milliseconds 700
        $st3 = Invoke-BrokenNesApi -ProcessId $id -Path '/api/debug/state'
        Check "${c}_resumed_negative" ($st3.frame -gt $st2.frame) "resumed: frame $($st2.frame) -> $($st3.frame) (the pause is what held it)"
    }

    # ---------------------------------------------------------------- viewers.html: memory, palettes and tiles (SNES, paused)
    $viewers = Join-Path $repo 'BrokenDev\web\viewers.html'
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $roms 'debugtest.sfc') } | Out-Null
    Start-Sleep -Seconds 2
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/pause' | Out-Null; Start-Sleep -Milliseconds 500
    foreach ($pv in @(@(2, 0x00), @(3, 0x7C))) { Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/memory/poke' -Body @{ domain = 'CGRAM'; address = $pv[0]; value = $pv[1] } | Out-Null }
    function PokeAll([string]$domain, $pairs) { foreach ($pv in $pairs) { Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/memory/poke' -Body @{ domain = $domain; address = $pv[0]; value = $pv[1] } | Out-Null } }
    PokeAll 'OAM' @(@(0, 0x20), @(1, 0x30), @(2, 0x05), @(3, 0x04), @(5, 0xE0))
    PokeAll 'VRAM' @(@(((1 * 32 + 6) * 2), 0x23), @(((1 * 32 + 6) * 2 + 1), 0x45), @(((1 * 32 + 7) * 2), 0), @(((1 * 32 + 7) * 2 + 1), 0))
    PokeAll 'DSP' @(@(2, 0x34), @(3, 0x12), @(0x12, 0), @(0x13, 0))
    $mdom = Render ("api=" + [Uri]::EscapeDataString($api) + "&once=1&layer=bg1&cellx=6&celly=1") $viewers
    $mdom2 = Render ("api=" + [Uri]::EscapeDataString($api) + "&once=1&layer=bg1&cellx=7&celly=1") $viewers
    $mc = Pre $mdom 'mapcell'; $mc2 = Pre $mdom2 'mapcell'
    Check 'viewers_map' ($mc -like 'cell 6,1: tile $123 palette 1 entry $4523*') "$mc"
    Check 'viewers_map_negative' ($mc2 -like 'cell 7,1: tile $0 palette 0*') "the next cell: $mc2"
    $spr = Pre $mdom 'sprites'
    Check 'viewers_sprites' ([bool](($spr -split "`n") -match '^#0\s+x\s+32 y\s+48 8x8 tile \$005 pal 2')) "$(($spr -split "`n")[0])"
    Check 'viewers_sprites_negative' (-not [bool](($spr -split "`n") -match '^#1\s')) "sprite 1 (y 224) is off screen and not listed"
    $snd = Pre $mdom 'sound'
    Check 'viewers_sound' ((($snd -split "`n")[0]) -like 'Voice 0:*pitch $1234*' -and (($snd -split "`n")[1]) -like 'Voice 1:*pitch $0000*' -and $mdom -match 'mute \$00') "$(($snd -split "`n")[0]) ; $(($snd -split "`n")[1])"
    $vdom = Render ("api=" + [Uri]::EscapeDataString($api) + "&once=1&domain=VRAM&address=2000&start=2000&bpp=2") $viewers
    $vdom2 = Render ("api=" + [Uri]::EscapeDataString($api) + "&once=1&domain=VRAM&address=2010&start=2010&bpp=2") $viewers
    $vb = @((Invoke-BrokenNesApi -ProcessId $id -Path '/api/memory/peek-range?domain=VRAM&address=8192&length=16').data | ForEach-Object { [int]$_ })
    $grey = @('000000', '555555', 'AAAAAA', 'FFFFFF')
    $want0 = (0..7 | ForEach-Object { $grey[((($vb[0] -shr (7 - $_)) -band 1) -bor ((($vb[1] -shr (7 - $_)) -band 1) -shl 1))] }) -join ' '
    $t0 = Tile0 $vdom; $t0row = (($t0 -split ' ') | Select-Object -First 8) -join ' '
    Check 'viewers_memory' ((Pre $vdom 'memory') -like '2000: 11 22 33 44 55*' -and (Pre $vdom2 'memory') -like '2010: 00 00 00 00*') "VRAM `$2000: $(((Pre $vdom 'memory') -split "`n")[0]) ; `$2010: $(((Pre $vdom2 'memory') -split "`n")[0])"
    Check 'viewers_palettes' ((Pre $vdom 'palettes') -like 'row 0: 0000 7C00 *' -and ([regex]::Matches($vdom, 'class="sw"')).Count -ge 256) "$(((Pre $vdom 'palettes') -split "`n")[0]); $(([regex]::Matches($vdom, 'class="sw"')).Count) swatches"
    Check 'viewers_tiles' ($t0row -eq $want0) "tile at VRAM `$2000 (2bpp, greys), row 0: $t0row ; decoded here from the API's bytes: $want0"
    Check 'viewers_tiles_negative' ((((Tile0 $vdom2) -split ' ') | Select-Object -Unique) -join ',' -eq '000000') "the zero tile at `$2010 is all black: $(((Tile0 $vdom2) -split ' ' | Select-Object -First 8) -join ' ')"
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/resume' | Out-Null
    $vsrc = Get-Content -LiteralPath $viewers -Raw
    Check 'viewers_read_only_source' ($vsrc -notmatch "method\s*:" -and $vsrc -notmatch "'POST'|""POST""|DELETE") "the viewers page's fetches are GETs only"
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

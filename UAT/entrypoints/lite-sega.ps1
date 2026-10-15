<#
.SYNOPSIS
  Entrypoint test: BrokenNes Lite (the Blazor WebAssembly AOT build) with the Master System, the Game Gear and the Genesis, in a real Edge/Chrome window over the DevTools protocol.
  Exit 0 = every check passed. Ends with ONE JSON result line (entrypoint, pass, checks, notes, seconds), like the other entrypoint scripts.

.DESCRIPTION
  1. Publishes WebLite as AOT (`dotnet publish -c Release -p:EnableWasmAot=true`, output under %LOCALAPPDATA%\VRUN_Nes_Dev_work\uat_lite_sega) unless -Publish names an existing publish folder (its wwwroot or itself).
  2. Gathers the games it plays into a scratch folder (never the repository): two Master System, two Game Gear and two Genesis games from the user's libraries
     (X:\EMULATION\..., the .7z archives opened with 7-Zip) unless -Roms names a folder that already has sms\ gg\ md\ subfolders.
  3. Runs UAT\lite\test-lite-sega.mjs (node 22 or newer): groups console, layout, rom, import, pickers, swap, saves, states, video, audio, control (see the header of that file for what each proves and what it cannot see).
  4. With -Bench also runs UAT\lite\bench-lite.mjs, the speed table (frames per second per console and per path in the browser window).

  VERIFIED: the console dropdown and the touch layouts (sms | gg | md: the right buttons, no two overlapping, all on screen), ?rom= routes, the file input including a .zip, a refused .7z (with its message), the core pickers from the
  catalog, hot swapping through TrySwapCore (in place, or restarted with a visible message), IndexedDB battery saves round trip (a program that adds one to its save on every run), save states (the next 60 frames equal after a load),
  the picture size and aspect for 256x192, 160x144, 320x224 and 256x224 and a width change while running, the audio rate and amount, pause and reset.
  NOT VERIFIED: sound through a loudspeaker, a real phone's touch, PAL games, Genesis states until the sound unit can save one (the test accepts a clear message).

.EXAMPLE
  pwsh -File UAT\entrypoints\lite-sega.ps1                          # publish, gather games, run the functional suite
  pwsh -File UAT\entrypoints\lite-sega.ps1 -Publish <dir> -Only rom,saves -Bench
#>
[CmdletBinding()]
param(
    [string]$Publish,
    [string]$Roms,
    [string[]]$Only,
    [switch]$Bench,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_lite_sega')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{ entrypoint = 'lite-sega'; pass = $false }
$notes = New-Object System.Collections.Generic.List[string]
New-Item -ItemType Directory -Force $WorkDir | Out-Null
try {
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node) { throw 'node (22 or newer) is needed' }
    # ---- publish
    if (-not $Publish) {
        $Publish = Join-Path $WorkDir 'publish'
        $log = Join-Path $WorkDir 'publish.log'
        Write-Host "dotnet publish WebLite (AOT; log: $log) ..."
        & dotnet publish (Join-Path $repo 'WebLite\BrokenNes.Lite.csproj') -c Release -p:EnableWasmAot=true --artifacts-path (Join-Path $WorkDir 'artifacts') -o $Publish -nologo -v q *> $log
        if ($LASTEXITCODE -ne 0) { throw "publish failed, see $log" }
    }
    if (-not (Test-Path (Join-Path $Publish 'wwwroot\index.html')) -and -not (Test-Path (Join-Path $Publish 'index.html'))) { throw "no published site in $Publish" }
    # ---- the games
    if (-not $Roms) {
        $Roms = Join-Path $WorkDir 'roms'
        foreach ($d in 'sms', 'gg', 'md') { New-Item -ItemType Directory -Force (Join-Path $Roms $d) | Out-Null }
        $sz = 'C:\Program Files\7-Zip\7z.exe'
        $lib = 'X:\EMULATION'
        $wantSms = 'Alex Kidd in Miracle World.7z', 'Fantasy Zone.7z', 'Phantasy Star.7z'
        foreach ($f in $wantSms) { $p = Join-Path "$lib\Sega Master System" $f; if ((Test-Path $p) -and (Test-Path $sz)) { & $sz x -y "-o$(Join-Path $Roms 'sms')" $p | Out-Null } }
        $gg = 'Sonic The Hedgehog (U)*', 'Columns (U)*'
        foreach ($g in $gg) { Get-ChildItem "$lib\Sega Game Gear" -Filter "$g.gg" -ErrorAction SilentlyContinue | Select-Object -First 1 | Copy-Item -Destination (Join-Path $Roms 'gg') }
        foreach ($m in 'Sonic the Hedgehog (JUE) [!].bin', 'Streets of Rage (JUE) (REV 00) [!].bin', 'Gunstar Heroes (E) [!].bin', 'NBA Jam (EU) (REV 01) [!].bin', 'Wonder Boy In Monster World (UE) [!].bin') { $p = Join-Path "$lib\Sega Genesis" $m; if (Test-Path $p) { Copy-Item $p (Join-Path $Roms 'md') } }
    }
    $notes.Add("publish: $Publish; games: $Roms")
    # ---- the suite
    $jsonOut = Join-Path $WorkDir 'lite-sega.json'
    $testArgs = @((Join-Path $repo 'UAT\lite\test-lite-sega.mjs'), '--publish', $Publish, '--roms', $Roms, '--json', $jsonOut)
    if ($Only) { $testArgs += @('--only', ($Only -join ',')) }
    $out = & $node @testArgs 2>&1 | Tee-Object -FilePath (Join-Path $WorkDir 'lite-sega.txt')
    $code = $LASTEXITCODE
    $out | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    $checks = if (Test-Path $jsonOut) { @(Get-Content $jsonOut -Raw | ConvertFrom-Json) } else { @() }
    $result.checks = [ordered]@{}
    foreach ($c in $checks) { $result.checks["$($c.group): $($c.name)"] = [ordered]@{ ok = $c.ok; detail = $c.detail } }
    if ($Bench) {
        Write-Host 'speed table ...'
        & $node (Join-Path $repo 'UAT\lite\bench-lite.mjs') --publish $Publish --roms $Roms --json (Join-Path $WorkDir 'bench-lite.json') 2>&1 | Tee-Object -FilePath (Join-Path $WorkDir 'bench-lite.txt') | ForEach-Object { Write-Host $_ }
        $notes.Add("speed table: $(Join-Path $WorkDir 'bench-lite.txt')")
    }
    $failed = @($checks | Where-Object { -not $_.ok }).Count
    $result.pass = ($code -eq 0) -and $checks.Count -gt 0 -and $failed -eq 0
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "ERROR: $($result.error)"
}
finally {
    $result.notes = $notes
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    Write-Output ($result | ConvertTo-Json -Depth 6 -Compress)
}
if ($result.pass) { exit 0 } else { exit 1 }

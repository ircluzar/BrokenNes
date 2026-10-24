<#
.SYNOPSIS
  Entrypoint test: WideMess in BrokenNes Lite (the Blazor WebAssembly AOT build), in a real Edge/Chrome window over the DevTools protocol (WideMess milestone 7a).
  Exit 0 = every check passed. Ends with ONE JSON result line (entrypoint, pass, checks, speed, notes, seconds), like the other entrypoint scripts.

.DESCRIPTION
  1. Publishes WebLite as AOT (`dotnet publish -c Release -p:EnableWasmAot=true`, output under %LOCALAPPDATA%\VRUN_Nes_Dev_work\uat_lite_widemess) unless -Publish names an existing publish folder.
  2. Runs UAT\lite\test-lite-widemess.mjs (node 22 or newer) on the WideMess test ROMs (WideMess\testroms\bin, copied into a scratch folder) and, when the user's SNES library has it, Super Mario World:
     groups console, picture, model, control, snes, sound, saves, states, layout (the browser's software renderer, as the other Lite suites), then speed with LITELAB_GPU=1 (the machine's GPU draws the
     picture, as in a real browser). See the header of that file for what each group proves and what it cannot see.

  VERIFIED: the Console menu, .wms files and the console's own cores, the 400 x 224 picture at its square-pixel 16:9 shape, the milestone-2 pictures pixel for pixel against the spec's model, the negative
  control, a SNES image on the WideMess and back on the SNES, Super Mario World identical on both consoles frame for frame (picture and sound), the WideMess voices against the locked control, battery RAM in
  IndexedDB across pages, save states (also in a fresh page), the touch layout, frames per second for layers, sprites, windows, widesnes and Wide Blaster.
  NOT VERIFIED: sound through a loudspeaker, a real phone (the speed of a phone's browser is not measured), a browser other than Edge/Chrome.

.EXAMPLE
  pwsh -File UAT\entrypoints\lite-widemess.ps1                       # publish, then every group
  pwsh -File UAT\entrypoints\lite-widemess.ps1 -Publish <dir> -Only model,speed
#>
[CmdletBinding()]
param(
    [string]$Publish,
    [string]$Library,
    [string[]]$Only,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_lite_widemess')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{ entrypoint = 'lite-widemess'; pass = $false }
$notes = New-Object System.Collections.Generic.List[string]
New-Item -ItemType Directory -Force $WorkDir | Out-Null
try {
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $node) { throw 'node (22 or newer) is needed' }
    if (-not $Publish) {
        $Publish = Join-Path $WorkDir 'publish'
        $log = Join-Path $WorkDir 'publish.log'
        Write-Host "dotnet publish WebLite (AOT; log: $log) ..."
        & dotnet publish (Join-Path $repo 'WebLite\BrokenNes.Lite.csproj') -c Release -p:EnableWasmAot=true --artifacts-path (Join-Path $WorkDir 'artifacts') -o $Publish -nologo -v q *> $log
        if ($LASTEXITCODE -ne 0) { throw "publish failed, see $log" }
    }
    if (-not (Test-Path (Join-Path $Publish 'wwwroot\index.html')) -and -not (Test-Path (Join-Path $Publish 'index.html'))) { throw "no published site in $Publish" }
    $roms = Join-Path $WorkDir ('roms-' + [guid]::NewGuid().ToString('N').Substring(0, 8))      # a fresh scratch folder each run (the test copies the games in)
    New-Item -ItemType Directory -Force $roms | Out-Null
    $notes.Add("publish: $Publish; games: $roms")
    $test = Join-Path $repo 'UAT\lite\test-lite-widemess.mjs'
    $groups = if ($Only) { @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) } else { @('console', 'picture', 'model', 'control', 'snes', 'sound', 'saves', 'states', 'layout', 'enhance', 'speed') }
    $checks = @(); $speed = $null; $codes = @()
    $plain = @($groups | Where-Object { $_ -ne 'speed' })
    if ($plain.Count) {
        $json = Join-Path $WorkDir 'functional.json'
        $a = @($test, '--publish', $Publish, '--roms', $roms, '--json', $json, '--only', ($plain -join ','))
        if ($Library) { $a += @('--library', $Library) }
        & $node @a 2>&1 | Tee-Object -FilePath (Join-Path $WorkDir 'functional.txt') | ForEach-Object { Write-Host $_ }
        $codes += $LASTEXITCODE
        if (Test-Path $json) { $checks += @((Get-Content $json -Raw | ConvertFrom-Json).results) }
    }
    if ($groups -contains 'speed') {
        $json = Join-Path $WorkDir 'speed.json'
        $env:LITELAB_GPU = '1'
        try {
            $a = @($test, '--publish', $Publish, '--roms', $roms, '--json', $json, '--only', 'speed')
            if ($Library) { $a += @('--library', $Library) }
            & $node @a 2>&1 | Tee-Object -FilePath (Join-Path $WorkDir 'speed.txt') | ForEach-Object { Write-Host $_ }
            $codes += $LASTEXITCODE
        }
        finally { Remove-Item Env:LITELAB_GPU -ErrorAction SilentlyContinue }
        if (Test-Path $json) { $j = Get-Content $json -Raw | ConvertFrom-Json; $checks += @($j.results); $speed = $j.speed }
    }
    $result.checks = [ordered]@{}
    foreach ($c in $checks) { $result.checks["$($c.group): $($c.name)"] = [ordered]@{ ok = $c.ok; detail = $c.detail } }
    if ($speed) { $result.speed = @($speed | ForEach-Object { [ordered]@{ case = $_.case; fps = $_.fps; p95Ms = $_.p95Ms; fpsDrawn = $_.fpsDrawn; p95DrawnMs = $_.p95DrawnMs } }) }
    $failed = @($checks | Where-Object { -not $_.ok }).Count
    $result.pass = $checks.Count -gt 0 -and $failed -eq 0 -and -not ($codes | Where-Object { $_ -ne 0 })
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

<#
.SYNOPSIS
  Builds and certifies every way BrokenNes runs, and writes one report.
.DESCRIPTION
  The four entrypoints and how each is proven:

    desktop    Windows\ (WinForms app)        UAT\entrypoints\desktop-smoke.ps1: launch, load VRUN through the control API,
                                              frames advance, picture non-blank and animated, audio meter, clean shutdown
    web-lite   WebLite\ (Blazor WASM)         UAT\entrypoints\web-smoke.ps1: publish, serve, headless browser boots a ROM
    plugin-host  Plugin\ BrokenNes2 DLL       FruityHost selftest: the Native AOT plugin DLL loaded by a faithful FL host
                                              stand-in; 21 checks of identity, parameters, pitch, slides, volume, pan, voices,
                                              every sound chip, ROM mode (game, picture, live CPU/PPU/APU swaps), robustness, real-time and allocation
    plugin-fl    the same DLL inside FL 2026  UAT\plugin\fl\fl-certify.ps1: FL renders a generated project, the audio is
                                              measured against the fixture. Needs the plugin installed in FL (admin, once:
                                              Config > Synthesizer Mode > Install to FL Studio, or BrokenNes.Windows.exe --install-vst);
                                              otherwise reported as NOT RUN.
    plugin-install  "Install to FL Studio"    UAT\plugin\install-smoke.ps1: BrokenNes.Windows.exe --install-vst against fake FL folders
                                              (detection of real installs, fresh/idempotent/replace, FL running, needs-admin, the picker)
    cores      shared emulator cores          Workshop headless run of a VRUN ROM on the FIX cores must reproduce a recorded
                                              picture hash (guards the shared tree both desktop and plugin modes use)

  Build output goes to %LOCALAPPDATA%\VRUN_Nes_Dev_work (never into the repo). Exit code 0 = nothing FAILED
  (NOT RUN entrypoints do not fail unless -RequireFl).
.EXAMPLE
  pwsh -File UAT\certify.ps1                    # everything
  pwsh -File UAT\certify.ps1 -Only plugin-host  # one entrypoint
#>
param(
    [string[]]$Only,
    [switch]$SkipBuild,
    [switch]$RequireFl,
    [string]$Rom = 'C:\Users\philt\OneDrive\Documents\PROJECTS\VRUN_Nes_Dev\builds\2026-09-27_nes\vrun_game.nes'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workRoot = Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work'
$art = Join-Path $workRoot 'bn_build'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$outDir = Join-Path $workRoot "certify\$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"   # Native AOT needs vswhere on PATH

$results = New-Object System.Collections.ArrayList
function Want($name) { (-not $Only) -or ($Only -contains $name) }
function Record($name, $status, $detail, $seconds) {
    [void]$results.Add([pscustomobject]@{ entrypoint = $name; status = $status; detail = $detail; seconds = [math]::Round($seconds, 1) })
    $color = switch ($status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("{0,-12} {1,-8} {2}  ({3:0}s)" -f $name, $status, $detail, $seconds) -ForegroundColor $color
}
function Run($name, [scriptblock]$body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    try { $r = & $body; Record $name $r.Status $r.Detail $sw.Elapsed.TotalSeconds }
    catch { Record $name 'FAIL' $_.Exception.Message $sw.Elapsed.TotalSeconds }
}
# --disable-build-servers / nodeReuse off: otherwise MSBuild's worker processes inherit the output pipe and keep it open
# after the build ends, so capturing the output would never return.
function Invoke-Dotnet { & dotnet.exe @args --disable-build-servers -nodeReuse:false 2>&1 | Out-String }
# The smoke scripts end with one JSON line whose "checks" is an object: name -> { ok, detail }.
function CheckSummary($j) {
    if (-not $j) { return 'no result line' }
    $all = @($j.checks.PSObject.Properties)
    $bad = @($all | Where-Object { -not $_.Value.ok } | ForEach-Object { $_.Name })
    "$($all.Count - $bad.Count)/$($all.Count) checks" + $(if ($bad) { " (failed: $($bad -join ', '))" } else { '' })
}

# ----------------------------------------------------------------------------------------------------
Run 'cores' {
    if (-not (Want 'cores')) { return @{ Status = 'SKIPPED'; Detail = 'not selected' } }
    if (-not (Test-Path $Rom)) { return @{ Status = 'NOT RUN'; Detail = "ROM not found: $Rom" } }
    if (-not $SkipBuild) { $o = Invoke-Dotnet build (Join-Path $repo 'Workshop\BrokenNes.Workshop.csproj') -c Release --artifacts-path $art; if ($LASTEXITCODE -ne 0) { throw "Workshop build failed: $($o.Substring([Math]::Max(0, $o.Length - 400)))" } }
    $exe = Join-Path $art 'bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe'
    $shots = Join-Path $outDir 'cores'; New-Item -ItemType Directory -Force $shots | Out-Null
    $input = '200:Start,206:,320:Right,360:Right+A,380:Right,420:Right+B,470:Left+B'
    & $exe --mixlab nes --rom $Rom --cpu FIX --ppu FIX --apu FIX --frames 600 --png-at '300,600' --input $input --out-dir $shots --tag fix | Out-Null
    $golden = Join-Path $PSScriptRoot 'plugin\golden\cores-vrun-fix.json'
    $now = [ordered]@{}
    foreach ($f in 300, 600) { $now["f$f"] = (Get-FileHash (Join-Path $shots ("fix_f{0:D5}.png" -f $f)) -Algorithm SHA256).Hash }
    $now['rom'] = (Get-FileHash $Rom -Algorithm SHA256).Hash
    if (-not (Test-Path $golden)) {
        New-Item -ItemType Directory -Force (Split-Path $golden) | Out-Null
        $now | ConvertTo-Json | Set-Content $golden
        return @{ Status = 'PASS'; Detail = "no golden yet: recorded $golden" }
    }
    $g = Get-Content $golden -Raw | ConvertFrom-Json
    if ($g.rom -ne $now['rom']) { return @{ Status = 'NOT RUN'; Detail = 'the ROM differs from the golden run''s ROM' } }
    $bad = @('f300', 'f600') | Where-Object { $g.$_ -ne $now[$_] }
    if ($bad) { return @{ Status = 'FAIL'; Detail = "picture differs from the recorded run at: $($bad -join ', ')" } }
    @{ Status = 'PASS'; Detail = 'VRUN on CPU/PPU/APU FIX reproduces the recorded pictures (frames 300, 600)' }
}

Run 'plugin-host' {
    if (-not (Want 'plugin-host')) { return @{ Status = 'SKIPPED'; Detail = 'not selected' } }
    if (-not $SkipBuild) {
        $o = Invoke-Dotnet publish (Join-Path $repo 'Plugin\BrokenNes2.Plugin\BrokenNes2.Plugin.csproj') -c Release --artifacts-path $art
        if ($LASTEXITCODE -ne 0) { throw "plugin publish failed: $($o.Substring([Math]::Max(0, $o.Length - 600)))" }
        $o = Invoke-Dotnet build (Join-Path $repo 'Plugin\BrokenNes.FruityHost\BrokenNes.FruityHost.csproj') -c Release --artifacts-path $art
        if ($LASTEXITCODE -ne 0) { throw "test host build failed: $($o.Substring([Math]::Max(0, $o.Length - 600)))" }
    }
    $dll = Join-Path $art 'publish\BrokenNes2.Plugin\release_win-x64\BrokenNes2_x64.dll'
    # A copy in the repo (Plugin\dist, git-ignored): the build output under %LOCALAPPDATA% can be a per-app private
    # store (packaged apps such as the Claude desktop app are redirected there), invisible to the user's own shell.
    $dist = Join-Path $repo 'Plugin\dist'
    New-Item -ItemType Directory -Force (Join-Path $dist 'host') | Out-Null
    Copy-Item (Join-Path $art 'publish\BrokenNes2.Plugin\release_win-x64\BrokenNes2_x64.dll') $dist -Force
    Copy-Item (Join-Path $art 'bin\BrokenNes.FruityHost\release\*') (Join-Path $dist 'host') -Recurse -Force
    $host_ = Join-Path $art 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'
    $json = Join-Path $outDir 'plugin-selftest.json'
    $romArgs = if (Test-Path $Rom) { @('--rom', $Rom) } else { @() }   # enables the ROM-mode tests
    & $host_ selftest $dll @romArgs --json $json --md (Join-Path $outDir 'plugin-selftest.md') | Out-Host
    $code = $LASTEXITCODE
    $r = Get-Content $json -Raw | ConvertFrom-Json
    $detail = "$($r.passed) passed, $($r.warnings) warnings, $($r.failed) failed (DLL $([int]((Get-Item $dll).Length/1MB)) MB)"
    @{ Status = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }); Detail = $detail }
}

Run 'desktop' {
    if (-not (Want 'desktop')) { return @{ Status = 'SKIPPED'; Detail = 'not selected' } }
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'entrypoints\desktop-smoke.ps1') 2>&1
    $code = $LASTEXITCODE
    $out | Out-File (Join-Path $outDir 'desktop-smoke.txt')
    $last = ($out | Where-Object { "$_" -like '{*' } | Select-Object -Last 1)
    $j = if ($last) { $last | ConvertFrom-Json } else { $null }
    @{ Status = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }); Detail = (CheckSummary $j) }
}

Run 'web-lite' {
    if (-not (Want 'web-lite')) { return @{ Status = 'SKIPPED'; Detail = 'not selected' } }
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'entrypoints\web-smoke.ps1') 2>&1
    $code = $LASTEXITCODE
    $out | Out-File (Join-Path $outDir 'web-smoke.txt')
    $last = ($out | Where-Object { "$_" -like '{*' } | Select-Object -Last 1)
    $j = if ($last) { $last | ConvertFrom-Json } else { $null }
    @{ Status = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }); Detail = (CheckSummary $j) }
}

Run 'plugin-fl' {
    if (-not (Want 'plugin-fl')) { return @{ Status = 'SKIPPED'; Detail = 'not selected' } }
    $installed = 'C:\Program Files\Image-Line\FL Studio 2026\Plugins\Fruity\Generators\BrokenNes2\BrokenNes2_x64.dll'
    $dll = Join-Path $art 'publish\BrokenNes2.Plugin\release_win-x64\BrokenNes2_x64.dll'
    $current = (Test-Path $installed) -and (Test-Path $dll) -and ((Get-FileHash $installed).Hash -eq (Get-FileHash $dll).Hash)
    if (-not $current) {
        $s = if ($RequireFl) { 'FAIL' } else { 'NOT RUN' }
        return @{ Status = $s; Detail = 'the current plugin build is not installed in FL 2026 (needs administrator, once): BrokenNes.Windows.exe --install-vst (or Config > Synthesizer Mode > Install to FL Studio)' }
    }
    if (Get-Process -Name FL64 -ErrorAction SilentlyContinue) { return @{ Status = 'NOT RUN'; Detail = 'FL Studio is open: close it (a command-line render would be handed to the running instance)' } }
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'plugin\fl\fl-certify.ps1') -WorkDir (Join-Path $outDir 'fl') 2>&1
    $code = $LASTEXITCODE
    $out | Out-File (Join-Path $outDir 'fl-certify.txt')
    @{ Status = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }); Detail = ($out | Where-Object { "$_" -match 'checks passed|FAIL' } | Select-Object -Last 1) }
}

Run 'plugin-install' {
    if (-not (Want 'plugin-install')) { return @{ Status = 'SKIPPED'; Detail = 'not selected' } }
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'plugin\install-smoke.ps1') -WorkDir (Join-Path $outDir 'install') 2>&1
    $code = $LASTEXITCODE
    $out | Out-File (Join-Path $outDir 'install-smoke.txt')
    $last = ($out | Where-Object { "$_" -like '{*' } | Select-Object -Last 1)
    $j = if ($last) { $last | ConvertFrom-Json } else { $null }
    if ($j -and $j.status -eq 'NOT RUN') { return @{ Status = 'NOT RUN'; Detail = $j.error } }
    @{ Status = $(if ($code -eq 0) { 'PASS' } else { 'FAIL' }); Detail = (CheckSummary $j) }
}

# ----------------------------------------------------------------------------------------------------
$summary = "# BrokenNes certification $stamp`n`n| Entrypoint | Result | Detail | Time |`n|---|---|---|---|`n"
foreach ($r in $results) { $summary += "| $($r.entrypoint) | $($r.status) | $($r.detail -replace '\|', '/') | $($r.seconds)s |`n" }
Set-Content (Join-Path $outDir 'summary.md') $summary
$results | ConvertTo-Json | Set-Content (Join-Path $outDir 'summary.json')
Write-Host "`n$summary"
Write-Host "report: $outDir"
$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
exit $(if ($failed) { 1 } else { 0 })

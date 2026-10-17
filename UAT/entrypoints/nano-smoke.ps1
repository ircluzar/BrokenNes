<#
.SYNOPSIS
  Entrypoint test: BrokenNes Nano (Nano\), the NES sound chip as a synth for web pages. Exit 0 = every check passed. Ends with ONE JSON result line.
.DESCRIPTION
  1. build       node Nano\build.mjs --check: Nano\dist is exactly what the build makes from Nano\src.
  2. engine      node Nano\test\engine.test.mjs: every preset audible and on pitch, presets distinct, voice stealing, DPCM, determinism, robustness.
  3. parity      node Nano\test\parity.mjs: the web module against Core's NesApuInstrument (and, for DPCM, the FIX core on a 16 KB cartridge) and a second C#
                 implementation of the instrument layer, sample for sample (builds Nano\tools\NanoParity into -Artifacts). -Mutants also injects the errors it must catch.
  4. plugin      node Nano\test\plugin-parity.mjs: against the BrokenNes2 plugin DLL rendered by the FL test host. NOT RUN (and said so) without -FruityHost and -PluginDll.
  5. browser     node Nano\test\browser-test.mjs: headless Edge or Chrome, the AudioWorklet itself. NOT RUN without a browser. -- not run from file:// (Chromium refuses worklets there; the test says so).
  What it cannot see: sound through a loudspeaker (the dev machine has no sound card: the browser test measures what the browser's own analyser hears), Firefox and Safari, a real MIDI device.
.EXAMPLE
  pwsh -File UAT\entrypoints\nano-smoke.ps1
  pwsh -File UAT\entrypoints\nano-smoke.ps1 -Mutants -FruityHost <BrokenNes.FruityHost.exe> -PluginDll <BrokenNes2_x64.dll>
#>
[CmdletBinding()]
param(
    [string]$Artifacts = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_nano'),
    [string]$FruityHost,
    [string]$PluginDll,
    [switch]$Mutants
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$notes = New-Object System.Collections.Generic.List[string]
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) {
    [ordered]@{ entrypoint = 'nano'; pass = $false; status = 'NOT RUN'; error = 'node (22 or newer) is needed'; checks = $checks; seconds = 0 } | ConvertTo-Json -Compress
    exit 2
}
function Step($name, [string[]]$nodeArgs, $env_ = @{}) {
    $old = @{}
    foreach ($k in $env_.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $env_[$k]) }
    try { $out = & $node @nodeArgs 2>&1; $code = $LASTEXITCODE } finally { foreach ($k in $env_.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
    $tail = (($out | Select-Object -Last 3) -join ' | ')
    $ok = ($code -eq 0)
    $checks[$name] = [ordered]@{ ok = $ok; detail = $(if ($code -eq 2) { "NOT RUN: $tail" } else { $tail }) }
    Write-Host ("[{0}] {1}  {2}" -f $(if ($ok) { 'PASS' } elseif ($code -eq 2) { 'SKIP' } else { 'FAIL' }), $name, $tail)
    if ($code -eq 2) { $checks[$name].ok = $true; $checks[$name].notRun = $true; $notes.Add("$name was not run") }
}
$nano = Join-Path $repo 'Nano'
New-Item -ItemType Directory -Force $Artifacts | Out-Null
Step 'build: dist is what src builds' @((Join-Path $nano 'build.mjs'), '--check')
Step 'engine tests' @((Join-Path $nano 'test\engine.test.mjs'))
$pa = @((Join-Path $nano 'test\parity.mjs')); if ($Mutants) { $pa += '--mutants' }
Step 'parity with Core (bit for bit)' $pa @{ NANO_ARTIFACTS = $Artifacts }
if ($FruityHost -and $PluginDll) { Step 'parity with the BrokenNes2 plugin DLL' @((Join-Path $nano 'test\plugin-parity.mjs'), '--host', $FruityHost, '--dll', $PluginDll) }
else { $notes.Add('the plugin parity was not run: no -FruityHost and -PluginDll'); Write-Host '[SKIP] parity with the BrokenNes2 plugin DLL (no -FruityHost / -PluginDll)' }
Step 'browser: the AudioWorklet in headless Edge/Chrome' @((Join-Path $nano 'test\browser-test.mjs'))

$bad = @($checks.GetEnumerator() | Where-Object { -not $_.Value.ok })
[ordered]@{ entrypoint = 'nano'; pass = ($bad.Count -eq 0); checks = $checks; notes = ($notes -join '; '); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) } | ConvertTo-Json -Compress -Depth 5
exit $(if ($bad.Count) { 1 } else { 0 })

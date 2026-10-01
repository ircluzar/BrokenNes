<#
.SYNOPSIS
  Certifies BrokenNes2 inside the real FL Studio: renders a generated project with FL and checks the audio.
.DESCRIPTION
  1. FruityHost writes the fixture (the notes to play) and the plugin's default state.
  2. make_template.py derives an FL project from FL's own Vocoder template: channel 0 becomes BrokenNes2 and the
     fixture's notes (with slides and note colours) are written into pattern 1.
  3. FL renders it from the command line (FL64.exe /R /Ewav /F"<dir>"): a headless run that exits by itself.
  4. FruityHost `analyze` measures the WAV against the fixture: pitch of every note on the chip's own period grid,
     slide and chain accuracy (including FL's timing offset), loudness vs velocity, silence between notes.
  Needs the plugin installed in FL (install-to-fl.ps1; -Install does it, with a UAC prompt) and FL closed: the
  command-line render would otherwise hand the project to the running instance. FL is only ever stopped by the
  process id this script started, and only on timeout.
.EXAMPLE
  pwsh -File fl-certify.ps1 -Install
#>
param(
    [string]$PluginDll,
    [string]$Host_,                                  # BrokenNes.FruityHost.exe (built if not given)
    [string]$FlRoot = "C:\Program Files\Image-Line\FL Studio 2026",
    [string]$Fixture = 'fl-certify',
    [string]$WorkDir,
    [string]$Python,
    [int]$TimeoutSec = 420,
    [switch]$Install,
    [switch]$NoRender                                # prepare + analyse an existing WAV only
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$work = if ($WorkDir) { $WorkDir } else { Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\fl_certify' }
$art = Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\bn_build'
$result = [ordered]@{ ok = $false; steps = @() }
function Step($name, $ok, $detail = '') {
    $script:result.steps += [ordered]@{ step = $name; ok = [bool]$ok; detail = $detail }
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  {0}  {1}  {2}" -f $tag, $name, $detail)
    if (-not $ok) { Finish }
}
function Finish {
    $json = $script:result | ConvertTo-Json -Depth 5
    Set-Content (Join-Path $work 'fl-certify.json') $json
    Write-Host $json
    exit $(if ($script:result.ok) { 0 } else { 1 })
}

New-Item -ItemType Directory -Force $work | Out-Null
Write-Host "FL Studio certification of BrokenNes2 (work dir $work)"

# --- locate things
$fl = Join-Path $FlRoot 'FL64.exe'
Step 'FL Studio present' (Test-Path $fl) $fl
if (-not $PluginDll) { $PluginDll = Join-Path $art 'publish\BrokenNes2.Plugin\release_win-x64\BrokenNes2_x64.dll' }
Step 'plugin DLL built' (Test-Path $PluginDll) $PluginDll
if (-not $Host_) { $Host_ = Join-Path $art 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe' }
Step 'test host built' (Test-Path $Host_) $Host_
if (-not $Python) { $Python = (Get-Command python -ErrorAction SilentlyContinue).Source }
Step 'python found' ([bool]$Python) "$Python"

# --- the project
& $Host_ fixture $Fixture (Join-Path $work 'fixture.json') | Out-Null
Step 'fixture written' ($LASTEXITCODE -eq 0)
& $Host_ state $PluginDll (Join-Path $work 'state.bin') | Out-Null
Step 'plugin default state dumped' ($LASTEXITCODE -eq 0)
$render = Join-Path $work 'render'
Remove-Item $render -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $render | Out-Null
$flp = Join-Path $render 'BrokenNes2_FLTest.flp'
$out = & $Python (Join-Path $PSScriptRoot 'make_template.py') --fixture (Join-Path $work 'fixture.json') --state (Join-Path $work 'state.bin') --out $flp --base (Join-Path $FlRoot 'Data\Templates\Utility\Vocoder\Vocoder.flp') 2>&1
Step 'FL project derived from FL''s Vocoder template' ($LASTEXITCODE -eq 0 -and (Test-Path $flp)) ($out -join ' ')

if (-not $NoRender) {
    # --- installed and current?
    $installed = Join-Path $FlRoot 'Plugins\Fruity\Generators\BrokenNes2\BrokenNes2_x64.dll'
    $current = (Test-Path $installed) -and ((Get-FileHash $installed).Hash -eq (Get-FileHash $PluginDll).Hash)
    if (-not $current -and $Install) {
        & (Join-Path $PSScriptRoot 'install-to-fl.ps1') -Dll $PluginDll -FlRoot $FlRoot
        $current = (Test-Path $installed) -and ((Get-FileHash $installed).Hash -eq (Get-FileHash $PluginDll).Hash)
    }
    Step 'plugin installed in FL and identical to the build' $current $(if ($current) { $installed } else { 'run install-to-fl.ps1 (needs administrator) or pass -Install' })

    # --- FL must not already be running
    $running = Get-Process -Name FL64 -ErrorAction SilentlyContinue
    Step 'FL Studio is closed' (-not $running) $(if ($running) { 'close FL first: a command-line render would be handed to the running instance' } else { '' })

    # --- render
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $fl -ArgumentList "/R /Ewav /F`"$render`"" -PassThru
    $done = $p.WaitForExit($TimeoutSec * 1000)
    if (-not $done) { Stop-Process -Id $p.Id -Force; }
    Step 'FL rendered and exited' $done ("{0:0}s, exit code {1}" -f $sw.Elapsed.TotalSeconds, $(if ($done) { $p.ExitCode } else { 'killed after timeout (a dialog? plugin not found?)' }))
}

# --- analyse
$wav = Get-ChildItem $render -Filter *.wav | Select-Object -First 1
Step 'FL wrote a WAV' ([bool]$wav) $(if ($wav) { "$($wav.Name), $([int]($wav.Length/1KB)) KB" } else { '' })
$report = & $Host_ analyze $wav.FullName (Join-Path $work 'fixture.json') --json (Join-Path $work 'analysis.json') 2>&1
$report | ForEach-Object { Write-Host "    $_" }
$analysisOk = $LASTEXITCODE -eq 0
Copy-Item $wav.FullName (Join-Path $work 'fl-render.wav') -Force
Step 'audio matches the fixture (pitch, slides, velocity, silence)' $analysisOk ($report | Select-Object -Last 1)

$result.ok = $true
Finish

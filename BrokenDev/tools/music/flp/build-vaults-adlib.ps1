<#
.SYNOPSIS
  An FL project composed on BrokenNes2's PC console (AdLib instances) -> Vaults DOS / Vaults 95 with it as the title song, plus the fence.
  1. FruityHost flp-vocdos-adlib: the AdLib back end's Vaults writer -> music\adlib\song<NN>.opl + its fence (OVER lines = exit 3, the build goes on)
  2. a scratch copy of VRUN_VOC_DOS_dev / VRUN_VOC_95_dev with the song, built with the game's own build.ps1 (tools\adlib\gen_adlib_tab.py)
  -Song: the song index (mus_songtab, 0-10) it replaces; 6 = the title (track 0).
#>
param(
    [Parameter(Mandatory)] [string]$Flp,
    [Parameter(Mandatory)] [ValidateSet('dos', '95')] [string]$Engine,
    [string]$Out = 'C:\BrokenNes-work\flp_out',
    [string]$Work = 'C:\BrokenNes-work\flp_vaults',
    [string]$Projects = 'C:\Users\philt\OneDrive\Documents\PROJECTS',
    [string]$FruityHost = '',
    [int]$Song = 6
)
$ErrorActionPreference = 'Stop'
function Fail($m) { Write-Host "FAIL  $m"; exit 1 }
if (-not (Test-Path $Flp)) { Fail "no such .flp: $Flp" }
if (-not $FruityHost) {
    foreach ($c in @("$env:BROKENNES_WORK\bn_build", 'C:\BrokenNes-work\flp2_build', 'C:\BrokenNes-work\bn_build', 'C:\BrokenNes-work\pcvst_build')) {
        $x = Join-Path $c 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'; if (Test-Path $x) { $FruityHost = $x; break }
    }
}
if (-not $FruityHost -or -not (Test-Path $FruityHost)) { Fail 'FruityHost not built or pass -FruityHost' }
$name = [IO.Path]::GetFileNameWithoutExtension($Flp)
$safe = ($name -replace '[^A-Za-z0-9_-]', '_')
$w = Join-Path $Work "$safe-$Engine-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$src = Join-Path $Projects $(if ($Engine -eq 'dos') { 'VRUN_VOC_DOS_dev' } else { 'VRUN_VOC_95_dev' })
New-Item -ItemType Directory -Force $w, $Out | Out-Null
robocopy $src (Join-Path $w 'game') /E /NFL /NDL /NJH /NJS /NP /XD build .git __pycache__ | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "robocopy of $src ($LASTEXITCODE)" }
$adl = Join-Path $w 'game\music\adlib'
$fence = & $FruityHost flp-vocdos-adlib $Flp $Song $adl
$rc = $LASTEXITCODE
if ($rc -ne 0 -and $rc -ne 3) { $fence | ForEach-Object { Write-Host "   $_" }; Fail "flp-vocdos-adlib ($rc): not composed for the AdLib?" }
$fence | Set-Content (Join-Path $Out "$safe.vaults-$Engine.fence.txt"); $fence | ForEach-Object { Write-Host "   $_" }
Move-Item (Join-Path $adl ('song{0:00}.fence.json' -f $Song)) (Join-Path $Out "$safe.vaults-$Engine.fence.json") -Force
$log = Join-Path $w 'build.log'
& pwsh -NoProfile -File (Join-Path $w 'game\build.ps1') -Out (Join-Path $w 'build') *> $log
if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 20 | ForEach-Object { Write-Host "   $_" }; Fail "build.ps1 (log $log)" }
$exe = Get-ChildItem (Join-Path $w 'build') -Filter *.EXE | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$dst = Join-Path $Out "$safe.vaults-$Engine.exe"
Copy-Item $exe.FullName $dst -Force
$h = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
"$h *$(Split-Path $dst -Leaf)" | Set-Content "$dst.sha256"
Write-Host "OK    $dst  sha256 $h (song $Song replaced)"
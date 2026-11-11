# Dot-sourced by the four music-test build scripts (build-supervrun-dos-mt.ps1, build-supervrun-95-mt.ps1, build-vaults-dos-mt.ps1,
# build-vaults-95-mt.ps1): the contract's names, FAIL, the FruityHost lookup, the placeholder fence and publishing.
#   <Out>\<safe>.<id>.exe and <Out>\<safe>.<id>.fence.txt; <safe> = the .flp's base name with chars not [A-Za-z0-9_-] -> '_'
#   ('placeholder' without -Flp). Exit 0 ok; otherwise a line starting FAIL and a nonzero exit.
function Fail($m) { Write-Host "FAIL  $m"; exit 1 }
function Get-MtSafe($flp) { if (-not $flp) { return 'placeholder' }; return ([IO.Path]::GetFileNameWithoutExtension($flp) -replace '[^A-Za-z0-9_-]', '_') }
function Find-FruityHost($given) {
    if ($given) { if (Test-Path $given) { return $given }; Fail "no FruityHost at $given" }
    foreach ($c in @("$env:BROKENNES_WORK\mt_build_dos", 'C:\BrokenNes-work\mt_build_dos', "$env:BROKENNES_WORK\bn_build", 'C:\BrokenNes-work\flp2_build', 'C:\BrokenNes-work\bn_build')) {
        $x = Join-Path $c 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'; if (Test-Path $x) { return $x }
    }
    Fail 'FruityHost not built (dotnet build Plugin/BrokenNes.FruityHost -c Release --artifacts-path C:\BrokenNes-work\mt_build_dos) or pass -FruityHost'
}
function Write-PlaceholderFence($path, $id) {
    @("(no .flp) for ${id}: 0 over the limit, 0 played differently, 1 inside",
      'ok   [placeholder] no FL project given: the FL TEST slot holds a silent placeholder; every game song is in the list') | Set-Content $path
}
function Publish-Mt($exe, $out, $safe, $id) {
    if (-not (Test-Path $exe)) { Fail "the build made no $exe" }
    $dst = Join-Path $out "$safe.$id.exe"
    Copy-Item $exe $dst -Force
    $h = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "OK    $dst  sha256 $h"
}

# Runs out\md_mode4_test.md on BizHawk 2.11.1 Genplus-gx (BrokenDev's pinned emulator 'bizhawk-gpgx') through BrokenDev's compat
# adapter, pressing A to step through the four screens, and saves pictures in out\bizhawk\ (shot_NNNNN.png).
param([string]$Out = (Join-Path $PSScriptRoot 'out\bizhawk'))
$ErrorActionPreference = 'Stop'
$bd = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
foreach ($f in 'Common', 'Tools', 'Compat') { . (Join-Path $bd "lib\$f.ps1") }
. (Join-Path $bd 'lib\adapters\bizhawk.ps1')
$reg = Get-Content (Join-Path $bd 'emulators.json') -Raw | ConvertFrom-Json
$all = @($reg.PSObject.Properties | ForEach-Object { if ($_.Value -is [System.Array]) { $_.Value } })
$emu = $all | Where-Object { (Get-BdProp $_ 'id' '') -eq 'bizhawk-gpgx' } | Select-Object -First 1
$pkg = $all | Where-Object { (Get-BdProp $_ 'id' '') -eq 'bizhawk' -and (Get-BdProp $_ 'exe' '') } | Select-Object -First 1
$exe = Join-Path (Get-BdHome) 'emulators\bizhawk-2.11.1\EmuHawk.exe'
$rom = (Resolve-Path (Join-Path $PSScriptRoot 'out\md_mode4_test.md')).ProviderPath
New-Item -ItemType Directory -Force $Out, "$Out\save", "$Out\scratch" | Out-Null
$spec = @"
console md
rom $rom
out $Out
savedir $Out\save
frames 640
input 0 -
input 70 a
input 71 -
input 200 a
input 201 -
input 300 down
input 340 -
input 400 a
input 401 -
shot 60
shot 130
shot 190
shot 260
shot 290
shot 350
shot 470
shot 515
input 520 a
input 521 -
shot 600
"@
$specPath = Join-Path $Out 'spec.txt'
[System.IO.File]::WriteAllText($specPath, $spec)
$r = Invoke-BdAdapterBizhawk -Emu $emu -Package $pkg -Exe $exe -SpecPath $specPath -Spec (Read-BdCompatSpec -Text $spec) -Scratch "$Out\scratch" -TimeoutSec 300
$r | Format-List | Out-String | Write-Host
Get-ChildItem $Out -Filter 'shot_*' | ForEach-Object { $_.FullName }

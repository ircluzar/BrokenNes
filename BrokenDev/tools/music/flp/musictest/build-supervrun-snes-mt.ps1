# Super VRUN music test player (snes): build-supervrun-mt.ps1 -Engine snes. Contract: -Flp x.flp -Out dir (no -Flp: the placeholder song).
param([string]$Flp = '', [string]$Out = 'C:\BrokenNes-work\flp_out')
& (Join-Path $PSScriptRoot 'build-supervrun-mt.ps1') -Engine snes -Flp $Flp -Out $Out
exit $LASTEXITCODE

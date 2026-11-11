# Super VRUN music test player (wms): build-supervrun-mt.ps1 -Engine wms. Contract: -Flp x.flp -Out dir (no -Flp: the placeholder song).
param([string]$Flp = '', [string]$Out = 'C:\BrokenNes-work\flp_out')
& (Join-Path $PSScriptRoot 'build-supervrun-mt.ps1') -Engine wms -Flp $Flp -Out $Out
exit $LASTEXITCODE

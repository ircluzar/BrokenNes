<#
  The EmuHawk config.ini for a live-reference run of the BrokenNes MCP server's play_step tool (BrokenDev\mcp\Reference.cs): the very
  settings BrokenDev's compat runs pin (lib\adapters\bizhawk.ps1, New-BdBizhawkConfig -Mode compat: sound off, no throttle, no frame skip,
  hotkeys unbound, windows off screen, StartPaused, every path in the scratch folder), so the reference is the same BizHawk the compat runs use.
#>
param([Parameter(Mandatory)][string]$System, [Parameter(Mandatory)][string]$Core, [Parameter(Mandatory)][string]$Scratch,
      [Parameter(Mandatory)][string]$InstallDir, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot '..\..\lib'
. (Join-Path $lib 'Common.ps1')
. (Join-Path $lib 'adapters\bizhawk.ps1')
$cfg = New-BdBizhawkConfig -System $System -Core $Core -SaveDir (Join-Path $Scratch 'saves') -Scratch $Scratch -InstallDir $InstallDir -Mode compat
Save-BdJson -Object $cfg -Path $Out -Depth 8

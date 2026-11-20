<#
.SYNOPSIS
  N64 (cor64) SM64 bit-exact gate: boot hash, castle + title goldens, low-end castle; -Krom adds the krom RSP suite.
.DESCRIPTION
  A PowerShell front for UAT/n64/gate.sh (the checks are bash + python, as in cer64; Git for Windows' bash is used).
  ROMs and save states come from the environment or UAT/n64/n64.local.json (see lib/n64env.sh and n64.local.json.example).
.EXAMPLE
  pwsh -File UAT\n64\gate.ps1 -Build            # build third_party/cor64/bin + bin_rt, then gate
  pwsh -File UAT\n64\gate.ps1 -Env COR64_X64=0 -Krom
#>
[CmdletBinding()] param([switch]$Build, [switch]$Krom, [string]$Env = 'X=1')
$bash = @("$env:ProgramFiles\Git\bin\bash.exe", "$env:ProgramFiles\Git\usr\bin\bash.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $bash) { Write-Host 'Git for Windows bash not found' -ForegroundColor Red; exit 2 }
if ($Build) { $env:BUILD = '1' } else { Remove-Item Env:BUILD -ErrorAction SilentlyContinue }
if ($Krom) { $env:KROM = '1' } else { Remove-Item Env:KROM -ErrorAction SilentlyContinue }
& $bash (Join-Path $PSScriptRoot 'gate.sh') $Env
exit $LASTEXITCODE

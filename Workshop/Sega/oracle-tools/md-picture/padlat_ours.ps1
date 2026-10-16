[CmdletBinding(PositionalBinding=$false)]
param([string]$Lines = "230,231,232", [string]$Art = "$env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_sega_b4b_r")
# Ours: what the pad-latency probe (run_padlat.py) records when the replay sets the pad at the start of line N (the replay's --line): the HV counter at which the program sees C go down and up.
$here = Split-Path $MyInvocation.MyCommand.Path
$work = if ($env:BN_ORACLE_ROOT) { $env:BN_ORACLE_ROOT } else { "C:\BrokenNes-tools\sega-work\b4b" }
$env:BN_ORACLE_ROOT = $work; $env:BN_ARTIFACTS = $Art
$rom = Join-Path $work "roms\padlat.bin"
foreach ($l in $Lines.Split(',')) {
  $dir = Join-Path $work "our\padlat_$l"
  Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  & "$here\ws.ps1" -TimeoutSec 300 --md-oracle $rom --line $l --f0 100 --script "2:C,8:" --steps 12 --ram-at 12 --ram-dir $dir --out "$work\our\padlat_${l}_pics" *> $null
  $r = Join-Path $dir "r00012.bin"
  $b = [IO.File]::ReadAllBytes($r)
  $o = @()
  for ($k = 0; $k -lt 2; $k++) { $hv = $b[0x100 + 4 * $k] * 256 + $b[0x101 + 4 * $k]; $v = $b[0x102 + 4 * $k] * 256 + $b[0x103 + 4 * $k]; $o += ("V {0:X2} H {1:X2} value {2:X2}" -f ($hv -shr 8), ($hv -band 0xFF), $v) }
  "line $l : " + ($o -join " | ")
}

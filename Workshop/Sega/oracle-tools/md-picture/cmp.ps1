[CmdletBinding(PositionalBinding=$false)]
param([Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Script, [int]$StartupMs = 2500, [switch]$NoCapture, [string]$Region = "ntsc")
# Capture BlastEm screenshots+states along a timed script (see bl.ps1), then render every state with our renderer and diff against BlastEm's own screenshot of the same instant.
$root = Split-Path $MyInvocation.MyCommand.Path
$dir = Join-Path $root "be\$Name"
if (-not $NoCapture) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; & (Join-Path $root "bl.ps1") -Rom $Rom -OutDir $dir -Script $Script -StartupMs $StartupMs }
$ws = Join-Path $root "ws.ps1"
New-Item -ItemType Directory -Force (Join-Path $root "our\$Name") | Out-Null
$states = Get-ChildItem $dir -Filter "*.state" | Sort-Object Name
foreach ($s in $states) {
  $b = $s.BaseName
  $k = [int]($b.Substring($b.Length - 3))
  $ours = Join-Path $root "our\$Name\$b.png"
  & $ws --md-render --bstate $s.FullName --png $ours --region $Region | Out-Null
  foreach ($which in -1, 0, 1) {
    $png = Join-Path $dir ("s{0:D3}.png" -f ($k + $which))
    if (-not (Test-Path -LiteralPath $png)) { continue }
    $diff = Join-Path $root "our\$Name\${b}_${which}_diff.png"
    $r = & $ws --img-diff $ours $png --search --out $diff
    "{0,-6}{1} {2}" -f $b, @('prev','same','next')[$which + 1], (($r | Where-Object { $_ -match "ours|differences" }) -join " | " -replace ", theirs.*compared", " compared")
  }
}

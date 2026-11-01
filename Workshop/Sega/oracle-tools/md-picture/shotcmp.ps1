[CmdletBinding(PositionalBinding=$false)]
param([Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$Name, [string]$Script = "w1500,shot", [int]$StartupMs = 3000, [int]$Frames = 300, [string]$Input = "", [string]$Extra = "", [switch]$NoCapture, [switch]$Pal)
# Running comparison for ROMs whose picture settles: BlastEm's own screenshot (real time) against our picture after N frames, with the shift search.
$root = Split-Path $MyInvocation.MyCommand.Path
$dir = Join-Path $root "be\$Name"
if (-not $NoCapture) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; & (Join-Path $root "bl.ps1") -Rom $Rom -OutDir $dir -Script $Script -StartupMs $StartupMs | Out-Null }
$ws = Join-Path $root "ws.ps1"
New-Item -ItemType Directory -Force (Join-Path $root "our\$Name") | Out-Null
$ours = Join-Path $root "our\$Name\run.png"
$a = @("--md-run", $Rom, "--frames", "$Frames", "--png", $ours, "--quiet", "--force")
if ($Input) { $a += @("--input", $Input) }
if ($Pal) { $a += @("--region", "pal") }
if ($Extra) { $a += $Extra.Split(' ') }
& $ws @a | Select-String "^exit|ran |RESULT|address|Exception|usage|cannot" | ForEach-Object { $_.Line }
$shots = Get-ChildItem $dir -Filter "*.png" | Sort-Object Name
foreach ($s in $shots) {
  $diff = Join-Path $root "our\$Name\$($s.BaseName)_diff.png"
  $r = & $ws --img-diff $ours $s.FullName --search --out $diff
  "{0,-8} {1}" -f $s.BaseName, (($r | Where-Object { $_ -match "ours|differences|best" }) -join " | ")
}

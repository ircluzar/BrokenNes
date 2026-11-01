param([string]$File, [int]$Y)
Add-Type -AssemblyName System.Drawing
$b = [System.Drawing.Bitmap]::FromFile($File)
$prev = -1; $len = 0; $runs = @()
for ($x = 0; $x -lt $b.Width; $x++) {
  $c = $b.GetPixel($x, $Y).ToArgb()
  if ($c -eq $prev) { $len++ } else { if ($len -gt 0) { $runs += $len }; $prev = $c; $len = 1 }
}
$runs += $len
"line $Y : $($runs.Count) runs: " + ($runs -join ',')
$b.Dispose()

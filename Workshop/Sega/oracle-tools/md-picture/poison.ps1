# Proof that --md-pic-test can fail: inject a bug into a copy of the source, build, run, count failures, restore. Each case = (file, find, replace, label).
$wt = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
$render = "$wt\Windows\NesEmulator\md\MdVdpRender.cs"
$cart = "$wt\Windows\NesEmulator\md\MdCartridge.cs"
$cases = @(
  @($render, 'if ((sv & 15) != 0 && (sv & 0x80) != 0) v = sv;', 'if ((sv & 15) != 0) v = sv;', "sprite always above planes (priority bit ignored)"),
  @($render, 'vs = Vsram[(col << 1) + vsOff & 0x3F] & 0x3FF;', 'vs = Vsram[vsOff] & 0x3FF;', "per-column vertical scroll uses column 0"),
  @($render, 'if (xraw == 0) { if (seenVisible) masked = true; }', 'if (xraw == 0) { masked = true; }', "X=0 masks even as the first sprite"),
  @($render, 'int state = ((av | bv) & 0x80) != 0 ? 0 : 1;', 'int state = ((((av & 15) != 0 ? av : 0) | ((bv & 15) != 0 ? bv : 0)) & 0x80) != 0 ? 0 : 1;', "shadow: transparent tile priority ignored"),
  @($render, 'if (lineY >= top && lineY < top + h * 8)', 'if (lineY >= top && lineY < top + h * 8 - 1)', "sprites one line short"),
  @($render, 'int maxSprites = H40 ? 80 : 64, maxLine = H40 ? 20 : 16;', 'int maxSprites = H40 ? 80 : 64, maxLine = H40 ? 21 : 16;', "21 sprites per line in H40"),
  @($cart, 'int page = (int)((reg - 0xF1) >> 1);   // $F3 -> 1 ... $FF -> 7', 'int page = (int)((reg - 0xF1) >> 1) - 1;   // $F3 -> 1 ... $FF -> 7', "SSF2 bank register offset")
)
$res = @()
foreach ($c in $cases) {
  $file = $c[0]; $backup = "$env:TEMP\poison_backup.cs"
  Copy-Item $file $backup -Force
  $t = Get-Content $file -Raw
  if (-not $t.Contains($c[1])) { $res += "NOT FOUND: $($c[3])"; continue }
  Set-Content $file ($t.Replace($c[1], $c[2])) -NoNewline
  $b = & "$PSScriptRoot\build.ps1"
  if ($b -notmatch "build exit 0") { $res += "BUILD FAILED: $($c[3])"; Copy-Item $backup $file -Force; continue }
  $r = & "$PSScriptRoot\ws.ps1" --md-pic-test
  $failed = ($r | Select-String "^  FAIL").Count
  $res += "{0,2} failing checks: {1}" -f $failed, $c[3]
  Copy-Item $backup $file -Force
}
& "$PSScriptRoot\build.ps1" | Out-Null
$res

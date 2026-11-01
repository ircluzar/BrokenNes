param([int]$Mode = 0)
$root = Split-Path $MyInvocation.MyCommand.Path
$env:MD_WINBUG = "$Mode"
$res = @()
foreach ($f in Get-ChildItem (Join-Path $root "be\wbug_st") -Filter *.state | Sort-Object Name) {
  $st = [IO.File]::ReadAllBytes($f.FullName); $hs = (($st[0xAE + 0x7000] -shl 8) -bor $st[0xAE + 0x7001]) -band 0x3FF
  $png = Join-Path $root ("our\wb_" + $f.BaseName + ".png")
  & (Join-Path $root "ws.ps1") --md-render --bstate $f.FullName --png $png --set "17=07,18=00" | Out-Null
  $r = & (Join-Path $root "ws.ps1") --img-diff $png (Join-Path $root ("be\wbug_st\" + $f.BaseName + ".png")) --dy 9 --region "0,0,320,110"
  $line = ($r | Where-Object { $_ -match "compared" }) -replace ".*compared ", ""
  $res += [pscustomobject]@{ H = $hs; Fine = $hs -band 15; Diff = $line }
}
$res | Sort-Object H | Format-Table -AutoSize | Out-String -Width 200

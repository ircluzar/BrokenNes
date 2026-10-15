[CmdletBinding(PositionalBinding=$false)]
param([string]$Exe = "", [string]$Sets = "", [string]$SlotSet = "", [int]$TimeoutSec = 400)
# The Genesis VDP's pinned oracle scenes (Workshop\MdGolden\md_scene_sets.tsv, md_scene_oracle.tsv): every set's ROM is rebuilt from its seed (python), run on the Workshop exe, and each pinned scene's picture is
# compared with the hash that matched BlastEm's screenshot when it was pinned. No BlastEm needed; python 3 and a built Workshop exe are.
#   -Exe      the Workshop exe (default: $env:BN_WORKSHOP, else $env:BN_ARTIFACTS\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe)
#   -Sets     only these sets, comma separated (default: all)
#   -SlotSet  an experiment setting for the run ("lbshadow=0;vsmask11=0" ...), which shows that a pinned scene can fail
# The ROMs and scene lists go to $env:BN_ORACLE_ROOT\roms (default: a temp folder). Exit code 0 when every pinned scene matches.
$ErrorActionPreference = "Stop"
$root = Split-Path $MyInvocation.MyCommand.Path
$gold = Join-Path $root "..\..\..\MdGolden" | Resolve-Path
if (-not $Exe) { $Exe = if ($env:BN_WORKSHOP) { $env:BN_WORKSHOP } else { Join-Path $env:BN_ARTIFACTS "bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe" } }
if (-not (Test-Path $Exe)) { throw "Workshop exe not found: $Exe (build it, or pass -Exe)" }
if (-not $env:BN_ORACLE_ROOT) { $env:BN_ORACLE_ROOT = Join-Path $env:TEMP "bn_scene_check" }
$env:BN_WORKSHOP = $Exe
New-Item -ItemType Directory -Force (Join-Path $env:BN_ORACLE_ROOT "roms") | Out-Null
$want = @($Sets.Split(',', [StringSplitOptions]::RemoveEmptyEntries))
$pins = Join-Path $gold "md_scene_oracle.tsv"
$totOk = 0; $totBad = 0; $bad = @()
foreach ($line in Get-Content (Join-Path $gold "md_scene_sets.tsv")) {
  if ($line.StartsWith("#") -or -not $line.Trim()) { continue }
  $f = $line.Split("`t"); $name = $f[0]; $kind = $f[1]; $arg = $f[2]; $count = [int]$f[3]; $seed = $f[4]
  if ($want.Count -and $want -notcontains $name) { continue }
  $rom = Join-Path $env:BN_ORACLE_ROOT "roms\$name.bin"
  if ($kind -eq "fuzz") { & python (Join-Path $root "scenerun.py") $name --family $arg --scenes $count --seed $seed --build-only | Out-Null }
  else { $pa = @((Join-Path $root "vsprobe.py"), $rom); if ($arg -ne "1") { $pa += $arg }; & python @pa | Out-Null }
  if ($LASTEXITCODE -ne 0) { throw "building $name failed" }
  $a = @("--md-scenes", $rom, "--count", $count, "--hash-check", $pins, "--set", $name)
  if ($SlotSet) { $a += @("--slot-set", $SlotSet) }
  $o = [IO.Path]::GetTempFileName()
  $p = Start-Process -FilePath $Exe -ArgumentList ($a | ForEach-Object { if ("$_" -match '\s') { '"' + $_ + '"' } else { "$_" } }) -PassThru -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError "$o.err"
  if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); "$name : TIMEOUT"; $totBad++; $bad += $name; continue }
  $text = Get-Content $o; Remove-Item $o, "$o.err" -ErrorAction SilentlyContinue
  $sum = $text | Select-String "^== pinned scene check '.*': (\d+) of (\d+) match"
  if ($sum) { $ok = [int]$sum.Matches[0].Groups[1].Value; $n = [int]$sum.Matches[0].Groups[2].Value } else { $ok = 0; $n = 1 }
  $totOk += $ok; $totBad += ($n - $ok)
  "{0,-10} {1,3} of {2,3} pinned scenes match" -f $name, $ok, $n
  if ($ok -ne $n) { $bad += $name; $text | Where-Object { $_ -match "^scene" } | Select-Object -First 6 | ForEach-Object { "    $_" } }
}
"== pinned oracle scenes: {0} of {1} match{2}" -f $totOk, ($totOk + $totBad), $(if ($bad.Count) { "  (differ: " + ($bad -join ", ") + ")" } else { "" })
if ($totBad -gt 0) { exit 1 }

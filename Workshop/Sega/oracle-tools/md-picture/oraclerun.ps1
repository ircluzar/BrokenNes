[CmdletBinding(PositionalBinding=$false)]
param([Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$Name, [string]$Script = "", [string]$Shots = "", [int]$Steps = 0, [int]$WaitMs = 5000, [int]$StartupMs = 1500, [string]$Extra = "", [switch]$NoCapture, [string]$Region = "", [string]$Pad = "", [int]$Offset = 90, [int]$ShotEvery = 100)
# A scripted play-through compared with BlastEm frame by frame (see Workshop\Sega\MdOracleCli.cs for the protocol).
#   -Script "0:Start,4:,400:Right,500:Right+C,520:Right"   step:buttons (buttons joined by +, empty = none); the pad changes before the advance of that step
#   -Shots  "50,100,200"                                   compare the pictures after that many advances
# BlastEm runs from reset without input for -WaitMs, is paused (F7) at a frame boundary, and then gets the script one frame advance (F8) at a time; every shot is a screenshot (P) taken with an advance, spaced
# 1.25 s apart (BlastEm names screenshots by the second). The first screenshot is the pause point: our emulator finds the boundary it matches (--scan) and takes the script from there, so both machines get the same
# buttons at the same frame boundaries. Window-driven: hold the BlastEm lock (Workshop\Sega\oracle-lock.ps1 -Acquire blastem) while this runs.
$root = Split-Path $MyInvocation.MyCommand.Path
$work = if ($env:BN_ORACLE_ROOT) { $env:BN_ORACLE_ROOT } else { $root }     # screenshots (be\) and our pictures (our\) go to the private working folder when there is one (keeps the repo clean)
$dir = Join-Path $work "be\$Name"
# -Pad default [-Offset 90] [-ShotEvery 100]: the script is the sweep's pad script from frame -Offset on (step 0 = that frame), shots every -ShotEvery steps
if ($Pad) {
  if ($Steps -le 0) { throw "-Pad needs -Steps" }
  $Script = ((& (Join-Path $root "ws.ps1") --md-oracle --emit-script $Pad --offset $Offset --steps $Steps) | Where-Object { $_ -match '^\d+:' } | Select-Object -Last 1)
  if ($ShotEvery -gt 0 -and -not $Shots) { $Shots = ((1..[int]($Steps / $ShotEvery) | ForEach-Object { $_ * $ShotEvery }) -join ",") }
}
$map = @{}
foreach ($part in $Script.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) { $c = $part.IndexOf(':'); if ($c -gt 0) { $map[[int]$part.Substring(0, $c)] = @($part.Substring($c + 1).Split('+', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() }) } }
$shotList = @($Shots.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [int]$_ })
if ($Steps -le 0) { $Steps = if ($shotList.Count) { ($shotList | Measure-Object -Maximum).Maximum } else { 0 } }
# BlastEm's script: pause, the pause-point shot, then the steps
$a = New-Object System.Collections.Generic.List[string]
$a.Add("w$WaitMs"); $a.Add("pause"); $a.Add("k:P"); $a.Add("adv1"); $a.Add("w1250")       # S0: the picture at the pause point, after its own advance (shown picture = machine state)
# our f0 is that state: the advance that took the shot is part of the pause point, so the steps count from here
$held = @(); $plain = 0
function FlushPlain { if ($script:plain -gt 0) { $script:a.Add("adv$($script:plain)"); $script:plain = 0 } }
for ($s = 0; $s -lt $Steps; $s++) {
  if ($map.ContainsKey($s)) {
    $now = $map[$s]
    $changes = @()
    foreach ($b in $held) { if ($now -notcontains $b) { $changes += "u:$b" } }
    foreach ($b in $now) { if ($held -notcontains $b) { $changes += "d:$b" } }
    if ($changes.Count) { FlushPlain; foreach ($ch in $changes) { $a.Add($ch) } }
    $held = $now
  }
  $n = $s + 1
  if ($shotList -contains $n) { FlushPlain; $a.Add("k:P"); $a.Add("adv1"); $a.Add("w1250") } else { $plain++ }
}
FlushPlain
if ($held.Count) { foreach ($b in $held) { $a.Add("u:$b") } }
Write-Host ("BlastEm script: " + (($a -join ",").Substring(0, [Math]::Min(400, ($a -join ",").Length))) + " ...")
if (-not $NoCapture) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; & (Join-Path $root "bl.ps1") -Rom $Rom -OutDir $dir -Script ($a -join ",") -StartupMs $StartupMs | Out-Null }
$got = @(Get-ChildItem $dir -Filter "s*.png" | Sort-Object Name)
"BlastEm screenshots: $($got.Count) (expected $($shotList.Count + 1))"
if ($got.Count -lt 1) { return }
# BlastEm names its screenshots by the second and now and then writes one more (or one fewer) than were asked for; shots are paired with ours by their order, so a count that is not exactly right cannot be compared
if ($got.Count -ne $shotList.Count + 1) { "CAPTURE UNRELIABLE: $($got.Count) screenshots for $($shotList.Count + 1) requested; the shots cannot be paired with ours. Rerun."; return }
# ours: scan S0 for the boundary, then replay; the other screenshots are s001.. in --shots order
$theirs = Join-Path $work "be\${Name}_t"; Remove-Item $theirs -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $theirs | Out-Null
for ($i = 1; $i -lt $got.Count; $i++) { Copy-Item $got[$i].FullName (Join-Path $theirs ("s{0:D3}.png" -f ($i - 1))) }
$ws = Join-Path $root "ws.ps1"
$wsArgs = @("--md-oracle", $Rom, "--scan", $got[0].FullName, "--scan-to", "$([int]($WaitMs / 1000 * 62 + 400))", "--script", $Script, "--shots", $Shots, "--steps", "$Steps", "--theirs", $theirs, "--out", (Join-Path $work "our\$Name"))
if ($Region) { $wsArgs += @("--region", $Region) }
& $ws @wsArgs


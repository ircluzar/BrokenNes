[CmdletBinding(PositionalBinding=$false)]
param([Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$Name, [int]$StartMs = 800, [int]$DurationMs = 20000, [string]$Script = "", [string]$OutDir = "", [int]$Frames = 0, [switch]$Compare)
# A game's sound as the oracle and we write it to the chips, for comparing the two logs (Workshop --ym-render --compare-logs, whose notes check ignores where each log starts and whose timing check starts at the first key-on):
# BlastEm runs the ROM from reset, its VGM logger (key M) is switched on after -StartMs and off after -DurationMs more (-Script adds "w..,k:Start,.." actions between: the pad presses of the play being compared), then closed
# gracefully so the file is finished; ours is --md-run --vgm for -Frames frames (default: DurationMs x 60 / 1000 + 120). Window-driven: hold the BlastEm lock. Needs BN_ORACLE_ROOT (the folder with blastem\ and be_vgm\) and BN_ARTIFACTS.
$here = Split-Path $MyInvocation.MyCommand.Path
$work = if ($env:BN_ORACLE_ROOT) { $env:BN_ORACLE_ROOT } else { $here }
if (-not $OutDir) { $OutDir = Join-Path $work "vgm\$Name" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$vgmDir = Join-Path $work "be_vgm"; New-Item -ItemType Directory -Force $vgmDir | Out-Null
$before = Get-Date
$actions = @("w$StartMs", "k:M")
if ($Script) { $actions += $Script }
$actions += @("w$DurationMs", "k:M", "w1500")
& (Join-Path $here "bl.ps1") -Rom $Rom -OutDir (Join-Path $OutDir "bl") -Script ($actions -join ",") -StartupMs 800 | Out-Null
$vgm = Get-ChildItem $vgmDir -Filter "*.vgm" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $before.AddSeconds(-1) } | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $vgm) { "no BlastEm VGM written"; return }
$theirs = Join-Path $OutDir "blastem.vgm"; Move-Item $vgm.FullName $theirs -Force
"BlastEm: $theirs ($((Get-Item $theirs).Length) bytes)"
if ($Frames -le 0) { $Frames = [int]($DurationMs * 60 / 1000) + 120 }
$art = if ($env:BN_ARTIFACTS) { $env:BN_ARTIFACTS } else { "$env:LOCALAPPDATA\VRUN_Nes_Dev_work\bn_sega_b4b" }
$exe = "$art\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"
$ours = Join-Path $OutDir "ours.vgm"
$p = Start-Process $exe -ArgumentList @("--md-run", "`"$Rom`"", "--frames", "$Frames", "--quiet", "--vgm", "`"$ours`"") -RedirectStandardOutput (Join-Path $OutDir "ours.out.txt") -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(600000)) { $p.Kill() }
"ours: $ours ($((Get-Item $ours).Length) bytes)"
if ($Compare) {
  $p = Start-Process $exe -ArgumentList @("--ym-render", "--compare-logs", "`"$ours`"", "`"$theirs`"") -RedirectStandardOutput (Join-Path $OutDir "compare.txt") -PassThru -WindowStyle Hidden
  $null = $p.WaitForExit(120000); Get-Content (Join-Path $OutDir "compare.txt")
}

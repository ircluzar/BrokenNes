[CmdletBinding(PositionalBinding=$false)]
param([Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$OutDir, [string]$Script = "", [string[]]$Extra = @(), [int]$StartupMs = 2500, [string]$Prefix = "s")
# Drive BlastEm black-box: launch on a ROM (dummy audio), run a timed script of actions, collect its own screenshots (key 'p', files land in %USERPROFILE%\blastem_*.png), close it by PID.
# Script: comma-separated actions: "w1500" wait ms, "k:Start" press+release a key (names below), "d:Right" hold down, "u:Right" release, "shot" screenshot, "pause", "adv", "adv5" five advances, "frames" no-op.
# Keys: letters/digits, Up Down Left Right Enter Space Tab Esc F1..F12, and the default BlastEm pad map (a=A s=B d=C q=X w=Y e=Z f=Mode Enter=Start).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class BW {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, int extra);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint c, uint t);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
}
"@
$vkmap = @{ Up=0x26; Down=0x28; Left=0x25; Right=0x27; Enter=0x0D; Start=0x0D; Space=0x20; Tab=0x09; Esc=0x1B; F5=0x74; F7=0x76; F8=0x77; F11=0x7A
            BT=0xC0; A=0x41; B=0x53; C=0x44; X=0x51; Y=0x57; Z=0x45; Mode=0x46 }   # pad names map to BlastEm's default keys: a s d q w e f
function Key([string]$name, [bool]$down) {
  $vk = if ($vkmap.ContainsKey($name)) { $vkmap[$name] } elseif ($name.Length -eq 1) { [int][char]$name.ToUpper() } else { throw "key $name" }
  $sc = [byte][BW]::MapVirtualKey([uint32]$vk, 0)
  $ext = if (@(0x25,0x26,0x27,0x28) -contains $vk) { 1 } else { 0 }; [BW]::keybd_event([byte]$vk, $sc, ($ext -bor $(if ($down) { 0 } else { 2 })), 0)
}
function Focus() { if ([BW]::GetForegroundWindow() -ne $script:hwnd) { [BW]::ShowWindow($script:hwnd, 9) | Out-Null; [BW]::BringWindowToTop($script:hwnd) | Out-Null; [BW]::SetForegroundWindow($script:hwnd) | Out-Null; Start-Sleep -Milliseconds 60; $script:refocus++ } }
$script:refocus = 0; $script:nstate = 0
function Tap([string]$name, [int]$ms = 60) { Focus; Key $name $true; Start-Sleep -Milliseconds $ms; Key $name $false; Start-Sleep -Milliseconds 40 }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$env:SDL_AUDIODRIVER = "dummy"
$root = Split-Path $MyInvocation.MyCommand.Path
if ($env:BN_ORACLE_ROOT) { $root = $env:BN_ORACLE_ROOT }   # a private working folder holding blastem\ (its default.cfg pointing screenshot_path at be_shots and save_path at be_save), be_shots and be_save
$be = Join-Path $root "blastem\blastem.exe"
$shotDir = Join-Path $root "be_shots"; $saveRoot = Join-Path $root "be_save"
$before = Get-Date
$args = @($Extra) + @('"' + $Rom + '"')
$p = Start-Process $be -ArgumentList $args -PassThru -WorkingDirectory (Split-Path $be)
$n = 0
try {
  $deadline = (Get-Date).AddSeconds(15)
  while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100; $p.Refresh() }
  if ($p.MainWindowHandle -eq 0) { "no window"; return }
  $h = $p.MainWindowHandle; $script:hwnd = $h
  [BW]::MoveWindow($h, 20, 20, 700, 560, $true) | Out-Null
  [BW]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds $StartupMs
  foreach ($a in $Script.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $a = $a.Trim()
    if ($a -match '^w(\d+)$') { Start-Sleep -Milliseconds ([int]$Matches[1]) }
    elseif ($a -match '^k:(.+)$') { Tap $Matches[1] }
    elseif ($a -match '^d:(.+)$') { Key $Matches[1] $true; Start-Sleep -Milliseconds 30 }
    elseif ($a -match '^u:(.+)$') { Key $Matches[1] $false; Start-Sleep -Milliseconds 30 }
    elseif ($a -eq 'shot') { Tap 'P' 40; Start-Sleep -Milliseconds 1100; $n++ }
    elseif ($a -eq 'save') { Tap 'BT' 40; Start-Sleep -Milliseconds 700; $st = Join-Path $saveRoot ([IO.Path]::GetFileNameWithoutExtension($Rom) + "\quicksave.state"); if (Test-Path -LiteralPath $st) { Copy-Item -LiteralPath $st (Join-Path $OutDir ("{0}{1:D3}.state" -f $Prefix, $script:nstate)) -Force; Remove-Item -LiteralPath $st; $script:nstate++ } else { "NO STATE FILE at $st" } }
    elseif ($a -eq 'snap') { $stp = Join-Path $saveRoot ([IO.Path]::GetFileNameWithoutExtension($Rom) + "\quicksave.state"); if (Test-Path -LiteralPath $stp) { Remove-Item -LiteralPath $stp -Force }; Tap 'P' 40; Start-Sleep -Milliseconds 200; Tap 'BT' 40; Start-Sleep -Milliseconds 300; Tap 'F8' 25; $n++; $t0 = Get-Date; while (-not (Test-Path -LiteralPath $stp) -and ((Get-Date) - $t0).TotalSeconds -lt 6) { Start-Sleep -Milliseconds 100 }; Start-Sleep -Milliseconds 600; if (Test-Path -LiteralPath $stp) { Copy-Item -LiteralPath $stp (Join-Path $OutDir ("{0}{1:D3}.state" -f $Prefix, $script:nstate)) -Force; $script:nstate++ } else { "NO STATE FILE at $stp" } }
    elseif ($a -eq 'resume') { Tap 'F7' 40; Start-Sleep -Milliseconds 150 }
    elseif ($a -match '^movie(\d+)$') { for ($i = 0; $i -lt [int]$Matches[1]; $i++) { Tap 'P' 40; Start-Sleep -Milliseconds 120; Tap 'F8' 25; Start-Sleep -Milliseconds 1100; $n++ } }
    elseif ($a -eq 'pause') { Tap 'F7' 40; Start-Sleep -Milliseconds 150 }
    elseif ($a -match '^adv(\d*)$') { $c = if ($Matches[1]) { [int]$Matches[1] } else { 1 }; for ($i = 0; $i -lt $c; $i++) { Tap 'F8' 25; Start-Sleep -Milliseconds 45 } }
    else { throw "bad action '$a'" }
  }
  Start-Sleep -Milliseconds 400
} finally { if (-not $p.HasExited) { $p.Kill() } }
# collect the screenshots in order
$files = Get-ChildItem $shotDir -Filter "blastem_*.png" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $before.AddSeconds(-1) } | Sort-Object LastWriteTime, Name
$i = 0
foreach ($f in $files) { $dst = Join-Path $OutDir ("{0}{1:D3}.png" -f $Prefix, $i); Move-Item $f.FullName $dst -Force; $i++ }
"shots requested $n, collected $i into $OutDir (refocus $($script:refocus))"






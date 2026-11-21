# gui_open.ps1 PKG SHOT WAITSEC [PROMPTSHOT]: start our own desktop build, Emulator > Open game package (.n98) via UI Automation, pick PKG in the
# file dialog, wait, save a picture (of OUR window only: never the whole screen, it shows the user's other apps) of the BrokenNes 2 window to SHOT. With PROMPTSHOT: also capture a message box (the seat's restart prompt) if one appears.
param([string]$Pkg, [string]$Shot, [int]$Wait = 120, [string]$PromptShot = "")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 public struct RECT { public int L, T, R, B; } }
"@
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find-Name($root, $name, $scope = $TS::Descendants) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name); return $root.FindFirst($scope, $c) }
function Wait-Name($root, $name, $sec = 30) { for ($i = 0; $i -lt $sec * 4; $i++) { $e = Find-Name $root $name; if ($e) { return $e }; Start-Sleep -Milliseconds 250 }; throw "not found: $name" }
function Save-Screen($path) { $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height; $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size); $bmp.Save($path); $g.Dispose(); $bmp.Dispose() }
function Save-Window($hwnd, $path) {
  $r = New-Object W+RECT; [W]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($hwnd, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($path); $g.Dispose(); $bmp.Dispose() }

$env:BROKENNES_SAVES_DIR = "C:\BrokenNes-work\nano98_work\gui_saves"; $env:BROKENNES_CONFIG_DIR = "C:\BrokenNes-work\nano98_work\gui_cfg"
$env:BROKENNES_N98_BASE_D = "C:\BrokenNes-work\sc98\hd_sc_d.img"; $env:X86_NONIC = "1"
New-Item -ItemType Directory -Force $env:BROKENNES_SAVES_DIR, $env:BROKENNES_CONFIG_DIR | Out-Null
$p = Start-Process -FilePath "C:\BrokenNes-work\nano98_work\winbin\BrokenNes.Windows.exe" -WorkingDirectory "C:\BrokenNes-work\nano98" -PassThru
try {
  for ($i = 0; $i -lt 120 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
  Start-Sleep 5; $p.Refresh(); $hwnd = $p.MainWindowHandle
  [W]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep 1
  # keys only ever go to OUR app: abort unless its process owns the foreground window (main window, menu or its file dialog)
  function Assert-Ours { $fg = [W]::GetForegroundWindow(); $fp = 0; [W]::GetWindowThreadProcessId($fg, [ref]$fp) | Out-Null; if ($fp -ne $p.Id) { throw "foreground window is not ours (pid $fp): no keys sent" } }
  Assert-Ours
  [System.Windows.Forms.SendKeys]::SendWait("%e"); Start-Sleep 1     # Alt+E: the Emulator menu
  Assert-Ours; [System.Windows.Forms.SendKeys]::SendWait("{DOWN}{ENTER}"); Start-Sleep 3      # Open game &package (.n98)...
  Assert-Ours; [System.Windows.Forms.SendKeys]::SendWait($Pkg.Replace("(", "{(}").Replace(")", "{)}")); Start-Sleep 1
  Assert-Ours; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep 3
  Write-Output "opened $Pkg at $(Get-Date -f HH:mm:ss)"
  for ($t = 0; $t -lt $Wait; $t += 2) {
    Start-Sleep 2
    if ($PromptShot) {
      $mb = Find-Name $AE::RootElement "BrokenNes 2" $TS::Children
      if ($mb -and $mb.Current.NativeWindowHandle -ne [int]$hwnd) { Start-Sleep 1; Save-Window ([IntPtr]$mb.Current.NativeWindowHandle) $PromptShot; Write-Output "prompt captured at $t s"; break }
    }
  }
  Save-Window $hwnd $Shot; Write-Output "window saved $Shot"
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

# UAT automation helpers for BrokenNes (Windows desktop + Web).
#
# This is a side-project testing harness, NOT part of any shipped product (Windows/Web/WebLite/
# Workshop). It exists purely to drive UI Automation against a running BrokenNes.Windows.exe
# instance for sanity-check testing. Nothing in the shipped projects references this folder.
#
# Key discoveries this harness is built on (see UAT/README.md for the full write-up):
#   - BrokenNes.Windows has TWO distinct UI surfaces: a WebView2-hosted front end (Main Menu, Deck
#     Builder, ROM Manager, Options, achievements overlay) and a native WinForms MenuStrip (the
#     "Open Emulator" direct-play screen: Emulator/Config/Tools & Activities/SHADER/APU/CPU/PPU/Help).
#   - The native MenuStrip is a normal, fully UIA-accessible WinForms control - straightforward.
#   - The WebView2 content IS exposed via UI Automation (Chromium's built-in accessibility bridge),
#     but with two gotchas: (1) a plain top-level Descendants search from the app root often returns
#     almost nothing (~17 elements) until the WebView2's accessibility tree has been "woken up" by a
#     more specific query; (2) the outer WebView2 pane's OWN accessible Name changes per loaded page
#     (e.g. "BrokenNes - Main Menu - Web content") or may be empty entirely, so matching it by a
#     fixed name is fragile. The reliable pattern is Get-ContentPane below: enumerate top-level Pane
#     children and pick whichever one actually has a nontrivial descendant count once queried.
#   - Visible button TEXT and the UIA-accessible Name can differ (e.g. visible "BrokenNes Emulator"
#     button has accessible Name "Open Emulator"). ALWAYS enumerate real accessible names via
#     Get-AllNamedElements before assuming a name - don't guess from a screenshot's visible text.
#   - Clickable elements in the WebView2 UI aren't always ControlType.Button - some are Hyperlink,
#     some are other types. Search across all types by Name, not by (Name + ControlType.Button).
#   - Multiple independent BrokenNes.Windows.exe instances CAN run simultaneously (no single-
#     instance lock) - this is what makes parallel testing possible. Launch your own instance, keep
#     its PID, and scope every query to that PID so you never touch another agent's window.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Get-AppRoot {
    param([int]$ProcessId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)))
}

# Finds the WebView2 content pane for the CURRENT page. Re-call this after every navigation -
# element references go stale once the page changes, and the pane's own accessible Name changes
# per page so it cannot be cached by name.
function Get-ContentPane {
    param($AppRoot)
    $panes = $AppRoot.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)))
    $best = $null; $bestCount = 0
    foreach ($p in $panes) {
        try {
            $kids = $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            if ($kids.Count -gt $bestCount) { $best = $p; $bestCount = $kids.Count }
        } catch {}
    }
    return $best
}

# Lists every element with a non-empty accessible Name under a pane (or the native menu bar) -
# use this FIRST on any new/unfamiliar screen to discover real names before trying to click.
function Get-AllNamedElements {
    param($Pane)
    $kids = $Pane.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $result = @()
    foreach ($k in $kids) {
        if ($k.Current.Name) { $result += [PSCustomObject]@{ Type = $k.Current.ControlType.ProgrammaticName; Name = $k.Current.Name } }
    }
    return $result
}

function Find-ByName {
    param($Pane, [string]$Name)
    return $Pane.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
}

# Click via whichever pattern the element actually supports - WebView2 elements are inconsistent
# about which pattern they expose (Invoke, SelectionItem, or Toggle are all seen in practice).
function Click-Element {
    param($Element)
    if (-not $Element) { return $false }
    try { $p = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $p.Invoke(); return $true } catch {}
    try { $p = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern); $p.Select(); return $true } catch {}
    try { $p = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $p.Toggle(); return $true } catch {}
    try { $p = $Element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $p.Expand(); return $true } catch {}
    return $false
}

# The native MenuStrip (visible only on the "Open Emulator" direct-play screen) - ordinary WinForms
# MenuBar, no WebView2 quirks apply here.
function Get-NativeMenuBar {
    param($AppRoot)
    return $AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuBar)))
}

# Opens a top-level native menu (e.g. "CPU", "Tools & Activities") and returns its dropdown items.
function Open-NativeMenu {
    param($MenuBar, [string]$MenuName)
    $menu = $MenuBar.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $MenuName)))
    if (-not $menu) { return $null }
    Click-Element $menu | Out-Null
    Start-Sleep -Milliseconds 400
    return $menu
}

# Accurate per-window screenshot. IMPORTANT (discovered the hard way during the first parallel UAT
# round): a naive CopyFromScreen(rect-from-DwmGetWindowAttribute) reads whatever is topmost on
# screen AT THOSE PIXEL COORDINATES - with many BrokenNes.Windows.exe instances overlapping at the
# same default launch position, this silently captures a DIFFERENT instance's window. PrintWindow
# with PW_RENDERFULLCONTENT renders the target HWND's own content directly (via its DWM thumbnail),
# independent of z-order or on-screen overlap, so it stays correct no matter how many other
# instances are running or where they're positioned.
Add-Type -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool PrintWindow(System.IntPtr hwnd, System.IntPtr hdcBlt, uint nFlags);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@ -Name DwmUat -Namespace BrokenNesUat -ErrorAction SilentlyContinue

function Save-WindowScreenshot {
    param([int]$ProcessId, [string]$OutPath)
    $proc = Get-Process -Id $ProcessId
    $hwnd = $proc.MainWindowHandle
    $rect = New-Object BrokenNesUat.DwmUat+RECT
    [BrokenNesUat.DwmUat]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, [System.Runtime.InteropServices.Marshal]::SizeOf([type][BrokenNesUat.DwmUat+RECT])) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # PW_RENDERFULLCONTENT = 2 - required for WebView2/D3D-accelerated windows, PW_CLIENTONLY(1) alone misses them.
    $ok = [BrokenNesUat.DwmUat]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    if (-not $ok) { $g.Dispose(); $bmp.Dispose(); return $false }
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $true
}

# Launches a fresh, independent instance and returns its PID. ALWAYS use this rather than
# assuming a fixed PID - multiple parallel agents each need their own instance.
function Start-BrokenNesWindows {
    param([string]$ExePath = "C:\Users\philt\OneDrive\Documents\PROJECTS\BrokenNes\Windows\bin\Release\net10.0-windows\win-x64\BrokenNes.Windows.exe")
    $p = Start-Process -FilePath $ExePath -PassThru
    return $p.Id
}

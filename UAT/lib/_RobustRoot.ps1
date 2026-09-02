# Workaround for a discovered issue: after the native MenuStrip's dropdown is invoked once, a
# leftover "SysShadow" popup window (empty Name, 0 descendants) shares the app's PID as a second
# top-level element. Get-AppRoot's FindFirst(Children, ProcessId) can then non-deterministically
# return that empty shadow pane instead of the real "BrokenNes" window, breaking every subsequent
# UIA-based lookup (Get-NativeMenuBar returns null, Get-ContentPane returns null, etc). Filter by
# Name as well as ProcessId to always get the real window.
function Get-AppRootRobust {
    param([int]$ProcessId, [string]$WindowName = "BrokenNes")
    $cond = New-Object System.Windows.Automation.AndCondition(@(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $WindowName))
    ))
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

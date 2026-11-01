# Helper for hands-on gameplay UAT of a loaded ROM in BrokenNes.Windows.
#
# Dot-source it, call Start-VrunSession once, then drive the game with Send-Pad and look at what
# came back with Grab-Frame. Everything goes through the HTTP control API - no focus, no clicking,
# no z-order, so it works while the window is buried or the machine is doing something else.
#
#   . .\UAT\vrun-play.ps1
#   $s = Start-VrunSession -Rom 'C:\Users\philt\Desktop\game.nes'
#   Send-Pad -S $s -Buttons Start -HoldMs 150 -ThenWaitMs 800
#   Grab-Frame -S $s -Name '03-after-start'
#
# NOTE the order that matters: the emulation loop only runs in the app's *emulator* view. Loading a
# ROM while the shell is on its WebView2 main menu leaves emulationActive=false and the framebuffer
# blank - which looks exactly like a broken ROM. Start-VrunSession navigates first, then loads.

. "$PSScriptRoot\lib\ApiClient.ps1"

function Start-VrunSession {
    param(
        [Parameter(Mandatory)][string]$Rom,
        [string]$Cpu = 'FIX', [string]$Ppu = 'FIX', [string]$Apu = 'FIX',
        [string]$ShotDir = "$PSScriptRoot\screenshots\vrun"
    )
    $inst = Start-BrokenNesInstance
    $id = $inst.ProcessId
    Set-BrokenNesCores -ProcessId $id -CpuId $Cpu -PpuId $Ppu -ApuId $Apu | Out-Null
    Show-BrokenNesEmulator -ProcessId $id | Out-Null      # must precede the load - see note above
    Start-Sleep -Milliseconds 800
    Load-BrokenNesRom -ProcessId $id -Path $Rom | Out-Null
    Start-Sleep -Seconds 2
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    [pscustomobject]@{ ProcessId = $id; ShotDir = $ShotDir; Rom = $Rom }
}

# Hold a set of buttons for HoldMs, then (after auto-release) idle for ThenWaitMs so the game can
# react. Button names: A B Select Start Up Down Left Right. Pass none to just wait.
function Send-Pad {
    param(
        [Parameter(Mandatory)]$S,
        [string[]]$Buttons = @(),
        [int]$HoldMs = 120,
        [int]$ThenWaitMs = 500
    )
    if ($Buttons.Count -gt 0) {
        Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method POST -Path '/api/input/set-buttons' `
            -Body @{ player = 1; buttons = $Buttons; holdMs = $HoldMs } | Out-Null
        Start-Sleep -Milliseconds ($HoldMs + 60)
    }
    if ($ThenWaitMs -gt 0) { Start-Sleep -Milliseconds $ThenWaitMs }
}

# Tap the same button N times - menus and dialogue usually need repeats.
function Tap-Pad {
    param([Parameter(Mandatory)]$S, [Parameter(Mandatory)][string[]]$Buttons,
          [int]$Times = 1, [int]$HoldMs = 100, [int]$GapMs = 260)
    1..$Times | ForEach-Object { Send-Pad -S $S -Buttons $Buttons -HoldMs $HoldMs -ThenWaitMs $GapMs }
}

# Capture the raw NES framebuffer (pre-shader) to PNG and report a cheap fingerprint.
function Grab-Frame {
    param([Parameter(Mandatory)]$S, [Parameter(Mandatory)][string]$Name)
    $path = Join-Path $S.ShotDir "$Name.png"
    $fb = Get-BrokenNesFramebuffer -ProcessId $S.ProcessId -OutPng $path
    $st = Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/input/state'
    [pscustomobject]@{
        Name = $Name; Png = $path
        Colours = $fb.UniqueColors; NonBlackPct = $fb.NonBlackPercent; Blank = $fb.IsBlank
        Frame = $st.frameCount; Running = $st.emulationActive
    }
}

# Health probe: is the process alive, is the loop advancing, is the CPU in ROM space rather than
# wandering in RAM? Call between gameplay steps to catch a corruption/derail the moment it happens.
function Test-VrunHealth {
    param([Parameter(Mandatory)]$S, [string]$Label = '')
    $proc = Get-Process -Id $S.ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return [pscustomobject]@{ Label=$Label; Alive=$false; Note='PROCESS GONE' } }
    $a = (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/input/state').frameCount
    $r1 = (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/cpu/registers').registers
    Start-Sleep -Milliseconds 700
    $b = (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/input/state').frameCount
    $r2 = (Invoke-BrokenNesApi -ProcessId $S.ProcessId -Method GET -Path '/api/cpu/registers').registers
    $pc1 = [Convert]::ToInt32($r1.pc, 16); $pc2 = [Convert]::ToInt32($r2.pc, 16)
    [pscustomobject]@{
        Label = $Label; Alive = $true
        FramesAdvanced = $b - $a
        Pc1 = $r1.pc; Pc2 = $r2.pc
        PcInRom = ($pc1 -ge 0x8000 -and $pc2 -ge 0x8000)   # false => derailed into RAM/zero page
    }
}

function Stop-VrunSession { param([Parameter(Mandatory)]$S) Stop-BrokenNesInstance -ProcessId $S.ProcessId | Out-Null }

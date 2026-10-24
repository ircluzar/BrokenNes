<#
.SYNOPSIS
  WideMess in the real desktop app (docs/projects/widemess-spec.md, milestones 1 and 2). Exit 0 = pass. Prints ONE JSON result line as the last line of stdout.

.DESCRIPTION
  Starts the shipped WinForms app with a scratch settings folder (BROKENNES_CONFIG_DIR: the user's config.json is never read or written) and checks, through its
  HTTP API and its Console menu:
    MENU     the Console menu lists WideMess.
    WMS      the WideMess test ROM (WideMess\testroms\bin\layers.wms) loads: the console is "wms", the cores WMS / WMS / WMS, the picture 400 x 224 with more than 800
             colours, and it moves (the layers scroll).
    M2       the milestone-2 picture ROMs (sprites.wms, widesnes.wms) show, in the app, exactly the pictures their generator's model of the spec draws
             (<rom>.expect.png: the 256-sprite table at 400; SNES modes 1, 0, 5, 2 and 7 drawn 400 wide).
    SNES     with WideMess selected, the ROM's SNES image (layers.sfc, a SNES-format file) runs on the WideMess too (400 wide: the game finds the WideMess);
             switching the console to the SNES moves it to the SNES, where it draws its red "not a WideMess" screen at 256 x 224.
    CONTROL  the negative-control ROM (layers_nounlock.wms: the WideMess registers written without the unlock) stays a SNES picture: 256 wide, black.
    SOUND    the sound test ROM (sound.wms, milestone 3) runs on the WideMess sound chip (APU WMS) and unlocks it (Workshop --wms-test measures its sound).
    M6       the SNES games' enhancements (milestone 6): the Console menu has "WideMess enhancements"; a SNES game with no profile (sprites_nounlock's SNES image, written
             to the work folder) is told "no profile for this game" and widescreen is refused; a scratch profile through POST /api/widemess/profile lets widescreen and no flicker
             switch on: the picture is 400 wide and its centre 256 columns are exactly the SNES picture (sprites_nounlock.expect.png); "use the profile: off" gives the SNES
             picture back, 256 wide; the API's switches leave the settings file without a kept choice.
    TESTCART the Console menu has "WideMess test cartridge"; it (POST /api/widemess/testcart) starts the embedded test cartridge on the WideMess console, 400 x 224, its results table
             says WMTC and WMID $57; the pad reaches its menu and test 8 (identity), which passes its 6 self-checks (WideMess\testcart\README.md).
  Rule zero (UAT\README.md): never kill by process name; only the PID started here is touched.

.EXAMPLE
  pwsh -File UAT\entrypoints\widemess-desktop-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_widemess_desktop')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$bin = Join-Path $repo 'WideMess\testroms\bin'
$checks = [ordered]@{}
$result = [ordered]@{ entrypoint = 'widemess-desktop'; pass = $false }

function Check($name, [bool]$ok, $detail) {
    $script:checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function Get-Frame([int]$procId) {
    $fb = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console/framebuffer'
    $bytes = [Convert]::FromBase64String([string]$fb.data)
    $colors = New-Object 'System.Collections.Generic.HashSet[int]'; $n = $fb.width * $fb.height; $red = 0; $black = 0
    for ($i = 0; $i -lt $n; $i++) {
        $o = $i * 4; $c = ([int]$bytes[$o] -shl 16) -bor ([int]$bytes[$o + 1] -shl 8) -bor [int]$bytes[$o + 2]
        [void]$colors.Add($c); if ($c -eq 0xFF0000) { $red++ }; if ($c -eq 0) { $black++ }
    }
    $sha = [Security.Cryptography.SHA1]::Create()
    [pscustomobject]@{ Width = [int]$fb.width; Height = [int]$fb.height; Colors = $colors.Count; Hash = [BitConverter]::ToString($sha.ComputeHash($bytes)); AllRed = ($red -eq $n); AllBlack = ($black -eq $n) }
}
function Compare-Expect([int]$procId, [string]$png) {   # 0 when the app's picture is the PNG's pixel for pixel, else what differs
    $fb = Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console/framebuffer'
    $bytes = [Convert]::FromBase64String([string]$fb.data)
    $bmp = [System.Drawing.Bitmap]::new($png)
    try {
        if ($bmp.Width -ne [int]$fb.width -or $bmp.Height -ne [int]$fb.height) { return "the picture is $($fb.width) x $($fb.height), the model's $($bmp.Width) x $($bmp.Height)" }
        $diff = 0
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            for ($x = 0; $x -lt $bmp.Width; $x++) {
                $c = $bmp.GetPixel($x, $y); $o = ($y * $bmp.Width + $x) * 4
                if ($bytes[$o] -ne $c.R -or $bytes[$o + 1] -ne $c.G -or $bytes[$o + 2] -ne $c.B) { $diff++ }
            }
        }
        if ($diff -eq 0) { return 0 }
        return "$diff pixels differ from the model's picture"
    }
    finally { $bmp.Dispose() }
}
function Wait-Frame([int]$procId, [scriptblock]$ok, [int]$seconds = 10) {
    $end = (Get-Date).AddSeconds($seconds); $f = $null
    do { Start-Sleep -Milliseconds 400; $f = Get-Frame $procId; if (& $ok $f) { break } } while ((Get-Date) -lt $end)
    return $f
}
function Get-Status([int]$procId) { (Invoke-BrokenNesApi -ProcessId $procId -Method GET -Path '/api/console').status }
function Get-ConsoleMenuNames([int]$procId) {
    for ($try = 0; $try -lt 4; $try++) {
        $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)))
        $main = $wins | Where-Object { $_.Current.Name -like 'BrokenNes*' } | Select-Object -First 1
        $item = if ($main) { Find-ByName $main 'Console' } else { $null }
        if ($item) {
            $pattern = $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $pattern.Expand(); Start-Sleep -Milliseconds 500
            $names = @(foreach ($k in $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($k.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem) { $k.Current.Name } })
            $pattern.Collapse()
            if ($names.Count) { return $names }
        }
        Start-Sleep -Milliseconds 800
    }
    return @()
}

$inst = $null
$savedConfigDir = $env:BROKENNES_CONFIG_DIR
try {
    . (Join-Path $repo 'UAT\lib\ApiClient.ps1')
    . (Join-Path $repo 'UAT\lib\UiaHelpers.ps1')
    Add-Type -AssemblyName System.Drawing
    foreach ($f in 'layers.wms', 'layers.sfc', 'layers_nounlock.wms', 'sound.wms') { if (-not (Test-Path -LiteralPath (Join-Path $bin $f))) { throw "missing ${f}: build WideMess\testroms\layers\build.ps1 and WideMess\testroms\sound\build.ps1" } }
    foreach ($f in 'sprites.wms', 'sprites.expect.png', 'widesnes.wms', 'widesnes.expect.png') { if (-not (Test-Path -LiteralPath (Join-Path $bin $f))) { throw "missing ${f}: build WideMess\testroms\picture\build.ps1" } }
    $cfgDir = Join-Path $WorkDir 'config'
    if (Test-Path -LiteralPath $cfgDir) { Remove-Item -LiteralPath $cfgDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $env:BROKENNES_CONFIG_DIR = $cfgDir
    $inst = Start-BrokenNesInstance -ExePath $Exe
    $id = $inst.ProcessId
    $result.pid = $id
    Start-Sleep -Seconds 4   # the app loads its embedded test.nes a few seconds after the API answers

    $names = @(Get-ConsoleMenuNames $id)
    Check 'menu_lists_widemess' ($names -contains 'WideMess') ("Console menu: " + ($names -join ', '))

    # WMS: the test ROM
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $bin 'layers.wms') } | Out-Null
    $f1 = Wait-Frame $id { param($f) $f.Width -eq 400 -and $f.Colors -gt 800 }
    $st = Get-Status $id
    Check 'wms_console' ($st.console -eq 'wms' -and $st.cpu -eq 'WMS' -and $st.ppu -eq 'WMS' -and $st.apu -eq 'WMS') "console $($st.console), cores $($st.cpu)/$($st.ppu)/$($st.apu), $($st.description)"
    Check 'wms_picture' ($f1.Width -eq 400 -and $f1.Height -eq 224 -and $f1.Colors -gt 800) "$($f1.Width) x $($f1.Height), $($f1.Colors) colours"
    Start-Sleep -Milliseconds 700
    $f2 = Get-Frame $id
    Check 'wms_moves' ($f2.Hash -ne $f1.Hash) 'the layers scroll'

    # M2: the picture test ROMs show, in the app, exactly the picture the spec's model draws (WideMess\testroms\picture)
    foreach ($rom in 'sprites', 'widesnes') {
        Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $bin "$rom.wms") } | Out-Null
        $end = (Get-Date).AddSeconds(10); $diff = 'no frame'
        do { Start-Sleep -Milliseconds 400; $diff = Compare-Expect $id (Join-Path $bin "$rom.expect.png") } while ($diff -ne 0 -and (Get-Date) -lt $end)
        Check "m2_$rom" ($diff -eq 0) $(if ($diff -eq 0) { "400 x 224, every pixel as $rom.expect.png" } else { "$diff" })
    }

    # SNES: the SNES image while WideMess is selected, then moved to the SNES
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $bin 'layers.sfc') } | Out-Null
    $f3 = Wait-Frame $id { param($f) $f.Width -eq 400 }
    $st = Get-Status $id
    Check 'snes_image_on_widemess' ($st.console -eq 'wms' -and $f3.Width -eq 400) "console $($st.console), $($f3.Width) x $($f3.Height)"
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/console/select/snes' | Out-Null
    $f4 = Wait-Frame $id { param($f) $f.AllRed }
    $st = Get-Status $id
    Check 'snes_image_on_snes' ($st.console -eq 'snes' -and $f4.Width -eq 256 -and $f4.AllRed) "console $($st.console), $($f4.Width) x $($f4.Height), all red: $($f4.AllRed)"

    # CONTROL: the negative control
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $bin 'layers_nounlock.wms') } | Out-Null
    $f5 = Wait-Frame $id { param($f) $f.Width -eq 256 }
    $st = Get-Status $id
    Check 'control_stays_snes' ($st.console -eq 'wms' -and $f5.Width -eq 256 -and $f5.AllBlack) "console $($st.console), $($f5.Width) x $($f5.Height), all black: $($f5.AllBlack)"

    # SOUND: the sound test ROM (milestone 3) runs on the WideMess sound chip and unlocks it (what it sounds like is measured by Workshop --wms-test)
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = (Join-Path $bin 'sound.wms') } | Out-Null
    $end = (Get-Date).AddSeconds(10)
    do { Start-Sleep -Milliseconds 400; $st = Get-Status $id } while ((Get-Date) -lt $end -and -not ($st.description -match 'WideMess sound' -and $st.description -match 'unlocked'))
    Check 'sound_chip' ($st.console -eq 'wms' -and $st.apu -eq 'WMS' -and $st.description -match 'WideMess sound' -and $st.description -match '\| unlocked') "console $($st.console), APU $($st.apu), $($st.description)"

    # M6: the SNES games' enhancements. A SNES game that never unlocks: sprites_nounlock.wms's image (its 64-byte header off) as a .sfc
    Check 'm6_menu' ($names -contains 'WideMess enhancements') ("Console menu: " + ($names -join ', '))
    $wmsBytes = [IO.File]::ReadAllBytes((Join-Path $bin 'sprites_nounlock.wms'))
    $locked = Join-Path $WorkDir 'sprites_locked.sfc'
    [IO.File]::WriteAllBytes($locked, $wmsBytes[64..($wmsBytes.Length - 1)])
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/console/select/wms' | Out-Null
    Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/emulator/load-rom' -Body @{ path = $locked } | Out-Null
    $expect = Join-Path $bin 'sprites_nounlock.expect.png'
    $end = (Get-Date).AddSeconds(10); $diff = 'no frame'
    do { Start-Sleep -Milliseconds 400; $diff = Compare-Expect $id $expect } while ($diff -ne 0 -and (Get-Date) -lt $end)
    $en = (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/widemess/enhance').state
    Check 'm6_no_profile' ($diff -eq 0 -and $en.running -and $en.profileStatus -eq 'no profile for this game' -and "$($en.offered.widescreen.refusal)" -like '*no profile for this game*') "picture: $(if ($diff -eq 0) { 'the SNES picture' } else { $diff }); $($en.profileStatus); widescreen: $($en.offered.widescreen.refusal)"
    $r = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/widemess/enhance' -Body @{ widescreen = $true }
    Check 'm6_widescreen_refused' ((-not $r.success) -and "$($r.error)" -like '*no profile*' -and -not $r.state.on.widescreen) "$($r.error)"
    $scratch = [ordered]@{
        format = 'widemess-profile-1'; title = 'UAT scratch profile'; sha1 = @($en.sha1)
        widescreen = [ordered]@{ layers = [ordered]@{ bg1 = 'extend'; bg2 = 'extend'; bg3 = 'extend'; bg4 = 'extend'; sprites = 'extend' } }
        enhancements = [ordered]@{ widescreen = 'risky'; noFlicker = 'risky'; betterSound = 'risky'; fastCpu = 'risky' }
        recommended = @('widescreen', 'noFlicker'); issues = @(); verified = [ordered]@{ date = ''; how = 'UAT' }
    }
    $p = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/widemess/profile' -Body $scratch
    $r = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/widemess/enhance' -Body @{ useProfile = $true }
    $end = (Get-Date).AddSeconds(10); $wide = $null
    do { Start-Sleep -Milliseconds 400; $wide = Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console/framebuffer' } while ([int]$wide.width -ne 400 -and (Get-Date) -lt $end)
    $centreDiff = 'not 400 wide'
    if ([int]$wide.width -eq 400) {
        $bytes = [Convert]::FromBase64String([string]$wide.data); $bmp = [System.Drawing.Bitmap]::new($expect); $centreDiff = 0
        try { for ($y = 0; $y -lt 224; $y++) { for ($x = 0; $x -lt 256; $x++) { $c = $bmp.GetPixel($x, $y); $o = ($y * 400 + 72 + $x) * 4; if ($bytes[$o] -ne $c.R -or $bytes[$o + 1] -ne $c.G -or $bytes[$o + 2] -ne $c.B) { $centreDiff++ } } } }
        finally { $bmp.Dispose() }
    }
    Check 'm6_widescreen' ($p.success -and $r.success -and $r.state.on.widescreen -and $r.state.on.noFlicker -and $centreDiff -eq 0) "scratch profile: $($p.success); on: widescreen $($r.state.on.widescreen), no flicker $($r.state.on.noFlicker); $($wide.width) x $($wide.height), centre: $(if ($centreDiff -eq 0) { 'the SNES picture, pixel for pixel' } else { "$centreDiff pixels differ" })"
    $r = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/widemess/enhance' -Body @{ useProfile = $false }
    $end = (Get-Date).AddSeconds(10); $diff = 'no frame'
    do { Start-Sleep -Milliseconds 400; $diff = Compare-Expect $id $expect } while ($diff -ne 0 -and (Get-Date) -lt $end)
    $cfg = Get-Content -LiteralPath (Join-Path $cfgDir 'config.json') -Raw -ErrorAction SilentlyContinue
    $kept = $cfg -and ($cfg -match '"wideMessEnhancements"\s*:\s*\{\s*"')
    Check 'm6_off_is_snes' ($r.success -and -not $r.state.on.widescreen -and $diff -eq 0 -and -not $kept) "all off: $(if ($diff -eq 0) { 'the SNES picture again, 256 x 224' } else { $diff }); a choice kept in the settings: $kept"

    # TESTCART: the WideMess test cartridge, embedded in the program: Console > WideMess test cartridge (its twin, POST /api/widemess/testcart) starts it on the WideMess console;
    # its results table ($7E:1000, WideMess\testcart\README.md) says WMTC; START reaches its menu, DOWN x 7 and A run test 8 (identity), which passes its 6 self-checks
    Check 'testcart_menu' ($names -contains 'WideMess test cartridge') ("Console menu: " + ($names -join ', '))
    $r = Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/widemess/testcart'
    $f6 = Wait-Frame $id { param($f) $f.Width -eq 400 -and $f.Colors -gt 40 }
    $st = Get-Status $id
    function Get-TcTable { (Invoke-BrokenNesApi -ProcessId $id -Method GET -Path '/api/console/peek?domain=WRAM&address=4096&length=64').data }
    $t = Get-TcTable
    $magic = -join ($t[0..3] | ForEach-Object { [char]$_ })
    Check 'testcart_starts' ($r.success -and $st.console -eq 'wms' -and $f6.Width -eq 400 -and $f6.Height -eq 224 -and $magic -eq 'WMTC' -and $t[0x10] -eq 0x57) "console $($st.console), $($f6.Width) x $($f6.Height), $($f6.Colors) colours, table '$magic', WMID $('{0:X2}' -f $t[0x10])"
    function Press($b) { Invoke-BrokenNesApi -ProcessId $id -Method POST -Path '/api/input/set-buttons' -Body @{ buttons = @($b); holdMs = 120 } | Out-Null; Start-Sleep -Milliseconds 350 }
    Press 'Start'
    $end = (Get-Date).AddSeconds(10); do { Start-Sleep -Milliseconds 300; $t = Get-TcTable } while ($t[0x0A] -ne 1 -and (Get-Date) -lt $end)
    Start-Sleep -Milliseconds 600
    foreach ($k in 1..7) { Press 'Down' }
    Press 'A'
    $end = (Get-Date).AddSeconds(15); do { Start-Sleep -Milliseconds 400; $t = Get-TcTable } while (($t[0x0A] -ne 9 -or $t[0x27] -ne 0x50) -and (Get-Date) -lt $end)
    Check 'testcart_identity' ($t[0x0A] -eq 9 -and $t[0x27] -eq 0x50 -and $t[0x2F] -eq 6 -and $t[0x37] -eq 0) "screen $($t[0x0A]) (9 = test 8), its status '$([char]$t[0x27])', $($t[0x2F]) checks, $($t[0x37]) failed"

    $result.pass = -not ($checks.Values | Where-Object { -not $_.ok })
}
catch {
    Check 'run' $false $_.Exception.Message
}
finally {
    if ($inst) { Stop-BrokenNesInstance -ProcessId $inst.ProcessId | Out-Null }
    $env:BROKENNES_CONFIG_DIR = $savedConfigDir
}
$result.checks = $checks
$result | ConvertTo-Json -Compress -Depth 5
if ($result.pass) { exit 0 } else { exit 1 }

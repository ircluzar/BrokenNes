<#
.SYNOPSIS
  Certifies "Install to FL Studio": BrokenNes.Windows.exe --install-vst (the code behind Config > Synthesizer Mode > Install to FL Studio).
  Exit 0 = pass, non-zero = fail. Prints ONE JSON result line as the last line of stdout (same shape as desktop-smoke.ps1).

.DESCRIPTION
  Builds Windows\BrokenNes.Windows.csproj -c Release under $env:LOCALAPPDATA\VRUN_Nes_Dev_work\uat_install (unless -Exe is given), then drives
  the installer against FAKE FL Studio folders it makes under the work dir, so nothing in Program Files is touched and no UAC prompt appears:

    build_ok                  the exe exists and the plugin DLL ships beside it (Plugin\BrokenNes2_x64.dll)
    detect                    --list finds exactly the FL Studio folders that hold an FL64.exe under Program Files\Image-Line
                              (not "FL Studio ASIO", Minihost, Shared), newest first, certified flag only on 2026
    install_fresh             into an empty fake FL, with the shipped DLL: exit 0 and the file is byte-identical
    install_idempotent        again: exit 0, "AlreadyCurrent", file untouched
    install_replaces_build    a different build already there is replaced (exit 0, identical afterwards)
    no_partial_file           no .new leftover next to the DLL
    two_installs_at_once      two --fl folders in one run: both get it
    fl_running_refused        an FL64 process running FROM a fake FL: that install exits 1 with FlRunning and writes nothing...
    fl_other_version_ok       ...while another fake FL (not the running one) installs fine: the check is per installation
    not_fl_folder             a folder that is not FL Studio: exit 1, nothing created
    missing_dll               --dll pointing nowhere: exit 1
    needs_admin_reported      a Generators folder this user may not write to + --elevated (so no UAC): exit 1 with NeedsAdmin, nothing written
    dialog_opens_and_cancels  the picker window appears, and closing it exits 2 (cancelled) without touching anything

  NOT VERIFIED: the UAC prompt itself and a copy into the real Program Files (an agent cannot click UAC). That half is the same code path as
  needs_admin_reported plus Windows' own elevation; try it once by hand: BrokenNes.Windows.exe --install-vst.

  Rule zero (UAT\README.md): never kill by process name. Only PIDs started here are touched. Refuses (NOT RUN) while a real FL Studio is open.
.EXAMPLE
  pwsh -File UAT\plugin\install-smoke.ps1
  pwsh -File UAT\plugin\install-smoke.ps1 -Exe C:\path\BrokenNes.Windows.exe
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\uat_install')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sw = [Diagnostics.Stopwatch]::StartNew()
$checks = [ordered]@{}
$result = [ordered]@{ pass = $false }
function Check($name, [bool]$ok, $detail) {
    $checks[$name] = [ordered]@{ ok = $ok; detail = "$detail" }
    Write-Host ("{0,-26} {1}  {2}" -f $name, $(if ($ok) { 'ok  ' } else { 'FAIL' }), $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function Hash($p) { (Get-FileHash $p -Algorithm SHA256).Hash }

$started = New-Object System.Collections.Generic.List[int]   # PIDs this script started: the only ones it may stop
try {
    if (Get-Process -Name FL64 -ErrorAction SilentlyContinue) {
        $result.status = 'NOT RUN'; $result.error = 'FL Studio is open: close it (the installer treats any FL64 it cannot place as a reason to refuse)'
        Write-Host $result.error -ForegroundColor Yellow
        throw 'notrun'
    }

    # ---------------- build ----------------
    if (-not $Exe) {
        $art = Join-Path $WorkDir 'artifacts'
        New-Item -ItemType Directory -Force $WorkDir | Out-Null
        Write-Host 'dotnet build Windows ...'
        $o = & dotnet.exe build (Join-Path $repo 'Windows\BrokenNes.Windows.csproj') -c Release --artifacts-path $art --disable-build-servers -nodeReuse:false -nologo -v q 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { throw "desktop build failed: $($o.Substring([Math]::Max(0, $o.Length - 600)))" }
        $Exe = Join-Path $art 'bin\BrokenNes.Windows\release_win-x64\BrokenNes.Windows.exe'
    }
    $shipped = Join-Path (Split-Path $Exe) 'Plugin\BrokenNes2_x64.dll'
    Check 'build_ok' ((Test-Path $Exe) -and (Test-Path $shipped)) "exe=$(Test-Path $Exe) shippedDll=$(Test-Path $shipped) ($Exe)"
    if (-not ((Test-Path $Exe) -and (Test-Path $shipped))) { throw 'no exe / plugin to test (run Plugin\build-dist.ps1, then build the desktop)' }

    function Install([string[]]$ArgList, [int]$TimeoutSec = 60) {
        $resFile = Join-Path $WorkDir ("res_{0}.txt" -f [guid]::NewGuid().ToString('N'))
        $a = @('--install-vst', '--quiet', '--result', "`"$resFile`"") + $ArgList
        $p = Start-Process -FilePath $Exe -ArgumentList $a -Wait -PassThru -WindowStyle Hidden
        [pscustomobject]@{ Code = $p.ExitCode; Lines = @(if (Test-Path $resFile) { Get-Content $resFile }) }   # @(if ...) keeps a one-line file an array
    }

    # ---------------- detection ----------------
    $r = Install @('--list')
    $expected = @()
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ } | Select-Object -Unique) {
        $il = Join-Path $pf 'Image-Line'
        if (Test-Path $il) { $expected += Get-ChildItem $il -Directory -Filter 'FL Studio*' | Where-Object { Test-Path (Join-Path $_.FullName 'FL64.exe') } | ForEach-Object { $_.FullName } }
    }
    $got = @($r.Lines | ForEach-Object { ($_ -split "`t")[4] })
    $sameSet = (@($expected | Sort-Object) -join '|') -eq (@($got | Sort-Object) -join '|')
    $editions = @($r.Lines | ForEach-Object { [int](($_ -split "`t")[0]) })
    $sorted = (($editions -join ',') -eq ((@($editions | Sort-Object -Descending)) -join ','))
    $certOnly2026 = -not (@($r.Lines | Where-Object { $p = $_ -split "`t"; ($p[2] -eq 'True') -ne ($p[0] -eq '2026') }))
    Check 'detect' ($r.Code -eq 0 -and $sameSet -and $sorted -and $certOnly2026) ("found {0}: {1}; expected {2}; newest-first={3}; certified only 2026={4}" -f $got.Count, (($got | ForEach-Object { Split-Path $_ -Leaf }) -join ', '), $expected.Count, $sorted, $certOnly2026)

    # ---------------- fake FL studios ----------------
    $fake = Join-Path $WorkDir 'fakefl'
    if (Test-Path $fake) { Remove-Item $fake -Recurse -Force }
    function NewFakeFl($name) { $d = Join-Path $fake $name; New-Item -ItemType Directory -Force (Join-Path $d 'Plugins\Fruity\Generators') | Out-Null; Set-Content (Join-Path $d 'FL64.exe') 'fake'; $d }
    $flA = NewFakeFl 'FL Studio 2098'
    $flB = NewFakeFl 'FL Studio 2097'
    $flC = NewFakeFl 'FL Studio 2096'
    $destA = Join-Path $flA 'Plugins\Fruity\Generators\BrokenNes2\BrokenNes2_x64.dll'

    # a small stand-in plugin (the logic is about files, so a 3 KB file proves it as well as 7 MB and is quick), and the real shipped one first
    $small = Join-Path $WorkDir 'small_plugin.bin'; [IO.File]::WriteAllBytes($small, [byte[]](1..3000 | ForEach-Object { $_ % 251 }))
    $r = Install @('--fl', "`"$flA`"")
    Check 'install_fresh' ($r.Code -eq 0 -and (Test-Path $destA) -and ((Hash $destA) -eq (Hash $shipped))) "exit=$($r.Code) line=$($r.Lines -join ' / ')"

    $t1 = (Get-Item $destA).LastWriteTimeUtc
    Start-Sleep -Milliseconds 1100
    $r = Install @('--fl', "`"$flA`"")
    Check 'install_idempotent' ($r.Code -eq 0 -and ($r.Lines -join ' ') -match 'AlreadyCurrent' -and (Get-Item $destA).LastWriteTimeUtc -eq $t1) "exit=$($r.Code) line=$($r.Lines -join ' / ')"

    [IO.File]::WriteAllBytes($destA, [byte[]](9, 9, 9))   # "another build"
    $r = Install @('--fl', "`"$flA`"")
    Check 'install_replaces_build' ($r.Code -eq 0 -and $r.Lines[0] -cmatch '^Installed\t' -and (Hash $destA) -eq (Hash $shipped)) "exit=$($r.Code) line=$($r.Lines -join ' / ')"
    Check 'no_partial_file' (-not (Test-Path "$destA.new")) "leftover .new: $(Test-Path "$destA.new")"

    $destB = Join-Path $flB 'Plugins\Fruity\Generators\BrokenNes2\BrokenNes2_x64.dll'
    $destC = Join-Path $flC 'Plugins\Fruity\Generators\BrokenNes2\BrokenNes2_x64.dll'
    Remove-Item (Split-Path $destA) -Recurse -Force
    $r = Install @('--dll', "`"$small`"", '--fl', "`"$flA`"", '--fl', "`"$flC`"")
    Check 'two_installs_at_once' ($r.Code -eq 0 -and (Hash $destA) -eq (Hash $small) -and (Hash $destC) -eq (Hash $small)) "exit=$($r.Code) lines=$($r.Lines.Count)"
    Remove-Item (Split-Path $destC) -Recurse -Force

    # ---------------- FL running from one installation ----------------
    $ping = Join-Path $env:SystemRoot 'System32\PING.EXE'
    Copy-Item $ping (Join-Path $flB 'FL64.exe') -Force   # a real process called FL64, running from flB
    $fakeFl = Start-Process -FilePath (Join-Path $flB 'FL64.exe') -ArgumentList '-n', '180', '127.0.0.1' -WindowStyle Hidden -PassThru
    $started.Add($fakeFl.Id)
    Start-Sleep -Milliseconds 700
    $r = Install @('--dll', "`"$small`"", '--fl', "`"$flB`"")
    Check 'fl_running_refused' ($r.Code -eq 1 -and ($r.Lines -join ' ') -match 'FlRunning' -and -not (Test-Path $destB)) "exit=$($r.Code) wrote=$(Test-Path $destB) line=$($r.Lines -join ' / ')"
    $r = Install @('--dll', "`"$small`"", '--fl', "`"$flC`"")
    Check 'fl_other_version_ok' ($r.Code -eq 0 -and (Test-Path $destC)) "exit=$($r.Code) (FL64 pid $($fakeFl.Id) was running from $flB)"
    Stop-Process -Id $fakeFl.Id -Force -ErrorAction SilentlyContinue
    $fakeFl.WaitForExit(5000) | Out-Null

    # ---------------- bad input ----------------
    $notFl = Join-Path $fake 'just a folder'; New-Item -ItemType Directory -Force $notFl | Out-Null
    $r = Install @('--fl', "`"$notFl`"")
    Check 'not_fl_folder' ($r.Code -eq 1 -and ($r.Lines -join ' ') -match 'not an FL Studio folder' -and -not (Test-Path (Join-Path $notFl 'Plugins'))) "exit=$($r.Code) line=$($r.Lines -join ' / ')"
    $r = Install @('--dll', "`"$(Join-Path $WorkDir 'nothing.dll')`"", '--fl', "`"$flA`"")
    Check 'missing_dll' ($r.Code -eq 1) "exit=$($r.Code) line=$($r.Lines -join ' / ')"

    # ---------------- "needs administrator" is a reported outcome, not a crash ----------------
    $flD = NewFakeFl 'FL Studio 2095'
    $gen = Join-Path $flD 'Plugins\Fruity\Generators'
    $me = "$env:USERDOMAIN\$env:USERNAME"
    & icacls.exe $gen /deny "${me}:(OI)(CI)(WD,AD)" | Out-Null
    try {
        $r = Install @('--elevated', '--dll', "`"$small`"", '--fl', "`"$flD`"")
        Check 'needs_admin_reported' ($r.Code -eq 1 -and ($r.Lines -join ' ') -match 'NeedsAdmin' -and -not (Test-Path (Join-Path $gen 'BrokenNes2'))) "exit=$($r.Code) line=$($r.Lines -join ' / ')"
    } finally { & icacls.exe $gen /remove:d $me | Out-Null }

    # ---------------- the picker window ----------------
    $p = Start-Process -FilePath $Exe -ArgumentList '--install-vst' -PassThru
    $started.Add($p.Id)
    $title = ''
    for ($i = 0; $i -lt 100 -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 100; $p.Refresh(); $title = $p.MainWindowTitle; if ($title) { break } }
    $opened = $title -eq 'Install BrokenNes 2 to FL Studio'
    $closed = $false
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); $closed = $p.WaitForExit(8000) }
    $code = if ($p.HasExited) { $p.ExitCode } else { -1 }
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Check 'dialog_opens_and_cancels' ($opened -and $closed -and $code -eq 2) "title='$title' closedByWM_CLOSE=$closed exit=$code (2 = cancelled)"
}
catch {
    if ($_.Exception.Message -ne 'notrun') { $result.error = $_.Exception.Message; Write-Host "ERROR: $($result.error)" -ForegroundColor Red }
}
finally {
    foreach ($id in $started) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }   # by PID, only ours
    $result.checks = $checks
    $result.seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $failed = @($checks.Values | Where-Object { -not $_.ok }).Count
    $result.pass = (-not $result.Contains('error')) -and (-not $result.Contains('status')) -and $checks.Count -ge 13 -and $failed -eq 0
    Write-Output ($result | ConvertTo-Json -Depth 6 -Compress)
}
if ($result.Contains('status') -and $result.status -eq 'NOT RUN') { exit 0 }
if ($result.pass) { exit 0 } else { exit 1 }

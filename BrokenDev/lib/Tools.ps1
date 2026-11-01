# BrokenDev tool discovery. Every finder returns $null when the tool is absent (never a guess), and Test-* helpers
# return an object { Name; Ok; Required; Version; Path; Detail } that doctor.ps1 prints and brokendev.ps1 reuses.

Set-StrictMode -Version Latest

function New-BdCheck {
    param([string]$Name, [bool]$Ok, [bool]$Required = $true, [string]$Version = '', [string]$Path = '', [string]$Detail = '', [string]$Fix = '')
    [pscustomobject]@{ Name = $Name; Ok = $Ok; Required = $Required; Version = $Version; Path = $Path; Detail = $Detail; Fix = $Fix }
}

function Find-BdExecutable {
    <# First existing path among $Candidates, else the first hit of 'Get-Command $Name' that is a real file (not a Store alias). #>
    param([string]$Name, [string[]]$Candidates = @())
    foreach ($c in $Candidates) { if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return (Resolve-Path -LiteralPath $c).Path } }
    if ($Name) {
        $cmd = Get-Command $Name -All -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -eq 'Application' } | Select-Object -First 5
        foreach ($c in $cmd) {
            # The Microsoft Store 'python.exe' stub lives in WindowsApps and prints "Python was not found": never a real interpreter.
            if ($c.Source -and $c.Source -notmatch '\\WindowsApps\\') { return $c.Source }
        }
    }
    $null
}

function Get-BdVersionFromText {
    param([string]$Text, [string]$Pattern = '(\d+)\.(\d+)(?:\.(\d+))?')
    if ($Text -and $Text -match $Pattern) { return [version](($Matches[0] -split '\s')[0]) }
    $null
}

# ---------------------------------------------------------------------------------------------------- Git Bash

function Find-BdGitBash {
    $cands = @()
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs")) {
        if ($pf) { $cands += (Join-Path $pf 'Git\bin\bash.exe') }
    }
    $git = Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git -and $git.Source) {
        # ...\Git\cmd\git.exe -> ...\Git\bin\bash.exe
        $gitRoot = Split-Path -Parent (Split-Path -Parent $git.Source)
        $cands += (Join-Path $gitRoot 'bin\bash.exe')
    }
    # C:\Windows\System32\bash.exe is WSL, not Git Bash: it is deliberately not a candidate.
    Find-BdExecutable -Name $null -Candidates $cands
}

function Test-BdGitBash {
    $p = Find-BdGitBash
    if (-not $p) { return New-BdCheck 'Git Bash' $false $true '' '' 'bash.exe from Git for Windows not found' 'Install Git for Windows (winget install Git.Git)' }
    $r = Invoke-BdProcess -FilePath $p -Arguments @('-c', 'echo bd-ok; uname -s') -TimeoutSec 30
    $ok = ($r.ExitCode -eq 0 -and $r.Output -match 'bd-ok')
    $ver = ''
    if ($ok) { $v = Invoke-BdProcess -FilePath $p -Arguments @('--version') -TimeoutSec 30; $ver = (Get-BdVersionFromText $v.Output).ToString() }
    New-BdCheck 'Git Bash' $ok $true $ver $p $(if ($ok) { '' } else { "bash ran but did not answer (exit $($r.ExitCode))" })
}

# ---------------------------------------------------------------------------------------------------- Python (3.10+, pillow, numpy)

function Get-BdPythonCandidates {
    $c = New-Object System.Collections.Generic.List[string]
    $onPath = Get-Command python.exe, python3.exe -All -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -eq 'Application' -and $_.Source -notmatch '\\WindowsApps\\' }
    foreach ($x in $onPath) { $c.Add($x.Source) }
    $py = Get-Command py.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($py) {
        $list = & $py.Source -0p 2>&1
        foreach ($line in $list) { if ("$line" -match '([A-Za-z]:\\[^\r\n]*python[w]?\.exe)') { $c.Add($Matches[1].Trim()) } }
    }
    foreach ($root in @("$env:LOCALAPPDATA\Programs\Python", "$env:ProgramFiles\Python*", 'C:\Python*')) {
        foreach ($d in (Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue)) { $e = Join-Path $d.FullName 'python.exe'; if (Test-Path -LiteralPath $e) { $c.Add($e) } }
    }
    $c | Select-Object -Unique
}

function Get-BdPythonInfo {
    <# Asks one interpreter (isolated mode, so nothing in the current folder can shadow a module) what it is and which modules it has. #>
    param([Parameter(Mandatory)][string]$Path)
    $code = "import sys`nout=[sys.version.split()[0]]`nfor m in ('PIL','numpy','scipy','soundfile','mido'):`n    try:`n        mod=__import__(m); out.append(m+'='+str(getattr(mod,'__version__','?')))`n    except Exception:`n        out.append(m+'=MISSING')`nprint(';'.join(out))"
    # A path that does not exist is "no such interpreter", not an exception.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { $r = Invoke-BdProcess -FilePath $Path -Arguments @('-I', '-c', $code) -TimeoutSec 60 } catch { return $null }
    if ($r.ExitCode -ne 0) { return $null }
    $line = ($r.Output -split "`r?`n" | Where-Object { $_ -match '^\d+\.\d+' } | Select-Object -First 1)
    if (-not $line) { return $null }
    $parts = $line -split ';'
    $info = [ordered]@{ Path = $Path; Version = [version]($parts[0] -replace '[^\d\.].*$', ''); Modules = @{} }
    foreach ($p in $parts[1..($parts.Count - 1)]) { $kv = $p -split '=', 2; $info.Modules[$kv[0]] = $kv[1] }
    [pscustomobject]$info
}

$script:BdPythonCache = @{}

function Find-BdPython {
    <# The first interpreter that is >= 3.10 and has Pillow and NumPy; BROKENDEV_PYTHON forces one. $null when none qualifies. Cached per BROKENDEV_PYTHON and PATH. #>
    $key = "$env:BROKENDEV_PYTHON|$env:PATH"
    if ($script:BdPythonCache.ContainsKey($key)) { return $script:BdPythonCache[$key] }
    $r = Find-BdPythonUncached
    $script:BdPythonCache[$key] = $r
    $r
}

function Find-BdPythonUncached {
    if ($env:BROKENDEV_PYTHON) {
        $i = Get-BdPythonInfo -Path $env:BROKENDEV_PYTHON
        if ($i -and $i.Version -ge [version]'3.10' -and $i.Modules['PIL'] -ne 'MISSING' -and $i.Modules['numpy'] -ne 'MISSING') { return $i }
        return $null
    }
    foreach ($p in (Get-BdPythonCandidates)) {
        $i = Get-BdPythonInfo -Path $p
        if ($i -and $i.Version -ge [version]'3.10' -and $i.Modules['PIL'] -ne 'MISSING' -and $i.Modules['numpy'] -ne 'MISSING') { return $i }
    }
    $null
}

function Test-BdPython {
    $i = Find-BdPython
    if ($i) {
        $opt = @('scipy', 'soundfile', 'mido' | Where-Object { $i.Modules[$_] -eq 'MISSING' })
        $detail = "Pillow $($i.Modules['PIL']), NumPy $($i.Modules['numpy'])"
        if ($opt.Count -gt 0) { $detail += "; optional music-tool modules missing: $($opt -join ', ')" }
        return New-BdCheck 'Python 3.10+ with pillow and numpy' $true $true $i.Version.ToString() $i.Path $detail
    }
    $seen = @(Get-BdPythonCandidates | ForEach-Object { $x = Get-BdPythonInfo -Path $_; if ($x) { "$($x.Path) $($x.Version) PIL=$($x.Modules['PIL']) numpy=$($x.Modules['numpy'])" } })
    New-BdCheck 'Python 3.10+ with pillow and numpy' $false $true '' '' ("no interpreter has both. seen: " + ($seen -join ' | ')) 'pip install pillow numpy  (or set BROKENDEV_PYTHON to an interpreter that has them)'
}

# ---------------------------------------------------------------------------------------------------- Node, .NET, PowerShell, 7-Zip

function Test-BdNode {
    $p = Find-BdExecutable -Name 'node.exe' -Candidates @("$env:ProgramFiles\nodejs\node.exe")
    if (-not $p) { return New-BdCheck 'Node.js 20+' $false $true '' '' 'node.exe not found' 'Install Node.js 20 or newer (winget install OpenJS.NodeJS)' }
    $r = Invoke-BdProcess -FilePath $p -Arguments @('--version') -TimeoutSec 30
    $v = Get-BdVersionFromText $r.Output
    $ok = ($v -and $v.Major -ge 20)
    New-BdCheck 'Node.js 20+' $ok $true ($(if ($v) { $v.ToString() } else { '' })) $p $(if ($ok) { '' } else { "version $($r.Output.Trim()) is older than 20" }) 'Install Node.js 20 or newer'
}

function Test-BdDotnet {
    $p = Find-BdExecutable -Name 'dotnet.exe' -Candidates @("$env:ProgramFiles\dotnet\dotnet.exe")
    if (-not $p) { return New-BdCheck '.NET 10 SDK' $false $true '' '' 'dotnet.exe not found' 'winget install Microsoft.DotNet.SDK.10' }
    $r = Invoke-BdProcess -FilePath $p -Arguments @('--list-sdks') -TimeoutSec 60
    $best = $null
    foreach ($line in ($r.Output -split "`r?`n")) { if ($line -match '^(\d+\.\d+\.\d+)') { $v = [version]$Matches[1]; if ($v.Major -ge 10 -and (-not $best -or $v -gt $best)) { $best = $v } } }
    New-BdCheck '.NET 10 SDK' ([bool]$best) $true ($(if ($best) { $best.ToString() } else { '' })) $p $(if ($best) { '' } else { 'no SDK with major version 10 or newer listed' }) 'winget install Microsoft.DotNet.SDK.10'
}

function Test-BdWindowsPowerShell {
    # The Nested pipeline loads a .NET Framework assembly by reflection: it only works in Windows PowerShell 5.1, not in pwsh.
    $p = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $p)) { return New-BdCheck 'Windows PowerShell 5.1' $false $true '' '' 'powershell.exe not found' 'Built into Windows 10/11' }
    $r = Invoke-BdProcess -FilePath $p -Arguments @('-NoProfile', '-Command', '$PSVersionTable.PSVersion.ToString()') -TimeoutSec 60
    $v = Get-BdVersionFromText $r.Output
    $ok = ($v -and $v.Major -eq 5 -and $v.Minor -ge 1)
    New-BdCheck 'Windows PowerShell 5.1' $ok $true ($(if ($v) { $v.ToString() } else { '' })) $p $(if ($ok) { '' } else { "reported '$($r.Output.Trim())'" })
}

function Find-Bd7Zip { Find-BdExecutable -Name '7z.exe' -Candidates @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") }

function Test-Bd7Zip {
    $p = Find-Bd7Zip
    if (-not $p) { return New-BdCheck '7-Zip' $false $true '' '' '7z.exe not found' 'winget install 7zip.7zip' }
    $r = Invoke-BdProcess -FilePath $p -Arguments @('i') -TimeoutSec 30
    $v = $null
    if ($r.Output -match '7-Zip[^\d]*(\d+\.\d+)') { $v = [version]$Matches[1] }
    New-BdCheck '7-Zip' $true $true ($(if ($v) { $v.ToString() } else { '' })) $p ''
}

function Test-BdDisk {
    <# Free space where BROKENDEV_HOME will live, and a warning when that is a network share (builds are many small files). #>
    param([long]$NeedBytes = 30GB)
    $h = Get-BdHome
    $probe = $h
    while ($probe -and -not (Test-Path -LiteralPath $probe)) { $probe = Split-Path -Parent $probe }
    if (-not $probe) { return New-BdCheck 'Disk space for BROKENDEV_HOME' $false $true '' $h "no existing parent folder of $h" 'Set BROKENDEV_HOME to a folder on a drive that exists' }
    $d = Get-BdDriveInfo -Path $probe
    $gb = if ($null -ne $d.FreeBytes) { [math]::Round($d.FreeBytes / 1GB, 1) } else { -1 }
    $ok = ($null -ne $d.FreeBytes -and $d.FreeBytes -ge $NeedBytes)
    $detail = "$gb GB free on $($d.Drive) ($($d.Detail)); a full restore needs about $([math]::Round($NeedBytes / 1GB)) GB"
    if ($d.IsNetwork) { $detail += '. NOTE: this is a network share, so the restore and builds are slower than on a local disk' }
    New-BdCheck 'Disk space for BROKENDEV_HOME' $ok $true "$gb GB" $h $detail 'Free space or set BROKENDEV_HOME to a roomier drive'
}

# ---------------------------------------------------------------------------------------------------- BrokenNes Workshop

function Get-BdWorkshopExe {
    <# The Workshop executable BrokenDev drives: BROKENDEV_WORKSHOP, else the one built by 'brokendev.ps1 build-workshop' under BROKENDEV_HOME. $null if there is none. #>
    if ($env:BROKENDEV_WORKSHOP) { if (Test-Path -LiteralPath $env:BROKENDEV_WORKSHOP) { return $env:BROKENDEV_WORKSHOP } else { return $null } }
    try { $bdHome = Get-BdHome } catch { return $null }   # an invalid BROKENDEV_HOME is reported by the doctor's own BROKENDEV_HOME check
    $loc = Join-Path $bdHome 'workshop-location.txt'
    if (Test-Path -LiteralPath $loc) {
        $p = ([System.IO.File]::ReadAllText($loc)).Trim()
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    $c = Join-Path $bdHome 'bnbuild\workshop\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe'
    if (Test-Path -LiteralPath $c) { return $c }
    $null
}

function Test-BdWorkshop {
    $p = Get-BdWorkshopExe
    if ($p) { return New-BdCheck 'BrokenNes Workshop (verify runner)' $true $false '' $p ('built ' + (Get-Item -LiteralPath $p).LastWriteTime.ToString('yyyy-MM-dd HH:mm')) }
    New-BdCheck 'BrokenNes Workshop (verify runner)' $false $false '' '' 'not built yet' 'brokendev.ps1 build-workshop'
}

# ---------------------------------------------------------------------------------------------------- project toolchains

function Test-BdFileTool {
    <# A toolchain file that ships inside a restored project. Reports "not restored" distinctly from "missing". #>
    param([string]$Name, [string]$Path, [string]$Restore, [bool]$Required = $true, [scriptblock]$VersionProbe = $null)
    if (-not (Test-Path -LiteralPath $Path)) {
        $archiveRestored = $false
        if ($Restore) { $archiveRestored = Test-Path -LiteralPath (Join-Path (Get-BdHome) $Restore) }
        $why = if ($archiveRestored) { 'file is missing from the restored project' } else { 'project not restored yet' }
        return New-BdCheck $Name $false $Required '' $Path $why "brokendev.ps1 restore $Restore"
    }
    $ver = ''
    if ($VersionProbe) { try { $ver = [string](& $VersionProbe $Path) } catch { $ver = '' } }
    New-BdCheck $Name $true $Required $ver $Path ''
}

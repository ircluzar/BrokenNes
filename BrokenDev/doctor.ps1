<#
.SYNOPSIS
  BrokenDev doctor: is this machine ready to restore, build and verify the BrokenDev projects?

.DESCRIPTION
  Checks the general prerequisites (Git Bash, Python 3.10+ with pillow and numpy, Node 20+, .NET 10 SDK, Windows PowerShell 5.1,
  7-Zip, disk space) and the toolchains that ship inside the restored projects (NESFab, Mesen, Project Nested and its .NET
  Framework runtime, the SNES assembler/compiler used by SuperVRUN). A project that is not restored yet is reported as
  "not restored", which is not a failure of the machine.

  Exit code: 0 = every REQUIRED check passed; 1 = at least one required check failed; 2 = bad usage.
  -Json prints the result as JSON (for scripts and the tests). -SkipProjects checks only the general prerequisites.

  Works in Windows PowerShell 5.1 and in PowerShell 7.

.EXAMPLE
  powershell -NoProfile -File BrokenDev\doctor.ps1
#>
[CmdletBinding()]
param(
    [switch]$Json,
    [switch]$SkipProjects
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'lib\Common.ps1')
. (Join-Path $here 'lib\Tools.ps1')
. (Join-Path $here 'lib\Archive.ps1')

function Get-BdDotNetFramework48 {
    # Project Nested's injector is a .NET Framework 4.8 assembly: Release >= 528040 means 4.8 or newer is installed.
    try {
        $k = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop
        $rel = [int]$k.Release
        return New-BdCheck '.NET Framework 4.8 (Project Nested injector)' ($rel -ge 528040) $false "release $rel" '' $(if ($rel -ge 528040) { '' } else { 'older than 4.8' }) 'Install .NET Framework 4.8 (built into Windows 10/11)'
    } catch {
        return New-BdCheck '.NET Framework 4.8 (Project Nested injector)' $false $false '' '' 'registry key not readable' 'Install .NET Framework 4.8'
    }
}

$checks = New-Object System.Collections.Generic.List[object]

# ---- general prerequisites -------------------------------------------------------------------
$homeInfo = $null
try { $homeInfo = Get-BdHome } catch { $checks.Add((New-BdCheck 'BROKENDEV_HOME' $false $true '' '' $_.Exception.Message 'Set BROKENDEV_HOME to a folder outside the repository')) }
if ($homeInfo) {
    $src = if ($env:BROKENDEV_HOME) { 'from BROKENDEV_HOME' } else { 'default' }
    $checks.Add((New-BdCheck 'BROKENDEV_HOME' $true $true '' $homeInfo "$src; exists: $(Test-Path -LiteralPath $homeInfo)"))
    $checks.Add((Test-BdDisk))
}
$checks.Add((Test-BdGitBash))
$checks.Add((Test-BdPython))
$checks.Add((Test-BdNode))
$checks.Add((Test-BdDotnet))
$checks.Add((Test-BdWindowsPowerShell))
$checks.Add((Test-Bd7Zip))
$checks.Add((Get-BdDotNetFramework48))
$checks.Add((Test-BdWorkshop))
# the other emulators 'compat' tests the games on (optional: 'brokendev.ps1 emulators install all')
if ($homeInfo) {
    try {
        . (Join-Path $here 'lib\Emulators.ps1')
        $ej = Read-BdEmulators
        $inst = @(); $miss = @(); $pre = @()
        foreach ($p in $ej.packages) {
            if ((Get-BdEmuPackageState $p).State -eq 'installed') { $inst += "$($p.id) $($p.version)" } else { $miss += [string]$p.id }
            foreach ($x in @(Test-BdEmuPrereqs $p)) { $pre += "$($p.id) needs $x" }
        }
        $ok = ($miss.Count -eq 0 -and $pre.Count -eq 0)
        $detail = "installed: $(if ($inst.Count) { $inst -join ', ' } else { 'none' })$(if ($miss.Count) { '; not installed: ' + ($miss -join ', ') })$(if ($pre.Count) { '; ' + ($pre -join '; ') })"
        $checks.Add((New-BdCheck 'Other emulators (compat)' $ok $false "$($inst.Count) of $(@($ej.packages).Count)" (Get-BdEmuHome) $detail 'brokendev.ps1 emulators install all'))
    } catch { $checks.Add((New-BdCheck 'Other emulators (compat)' $false $false '' '' $_.Exception.Message 'see BrokenDev\emulators.json')) }
}

# ---- archives and the toolchains that ship in them ---------------------------------------------
if (-not $SkipProjects -and $homeInfo) {
    $table = Get-BdArchiveTable
    foreach ($id in $table.Keys) {
        $spec = $table[$id]
        $zip = Get-BdArchiveFile $spec
        $zipOk = Test-Path -LiteralPath $zip
        $st = Get-BdRestoreState $spec
        $ws = Get-BdWorkspace -Id $id -Table $table
        $detail = "archive $(if ($zipOk) { 'present' } else { 'MISSING: ' + $zip }); restore state: $($st.State) ($($st.Detail))"
        $checks.Add((New-BdCheck "Project restored: $id" ($st.State -eq 'restored') $false '' $zip $detail "brokendev.ps1 restore $id"))
        $checks.Add((New-BdCheck "Workspace used: $id" ($ws.Kind -in 'live', 'restored') $false $ws.Kind $ws.Path $ws.Detail "put the live workspace beside the repository, or brokendev.ps1 restore $id"))
    }

    $roots = Get-BdArchiveRoots $table
    $nesfabProbe = { param($p) $r = Invoke-BdProcess -FilePath $p -Arguments @('--version') -TimeoutSec 30; ($r.Output -split "`r?`n" | Select-Object -First 1).Trim() }
    $mesenProbe  = { param($p) (Get-Item -LiteralPath $p).VersionInfo.FileVersion }

    $v = $roots['vrun']; $n = $roots['nested']; $sv = $roots['svrunrepo']; $s1 = $roots['smb1']
    $checks.Add((Test-BdFileTool 'NESFab 1.8 (VRUN, ISA=legal)' (Join-Path $v 'tools\nesfab\nesfab_legal.exe') 'vrun_nes_dev' $false $nesfabProbe))
    $checks.Add((Test-BdFileTool 'Mesen 2 (VRUN oracle)' (Join-Path $v 'tools\mesen\Mesen.exe') 'vrun_nes_dev' $false $mesenProbe))
    $checks.Add((Test-BdFileTool 'Project Nested injector (Project_Nested.exe)' (Join-Path $n 'Project Nested\bin\Release\Project_Nested.exe') 'vrun_nes_dev' $false))
    $checks.Add((Test-BdFileTool 'Nested pristine core (Project_Nested.pristine.smc)' (Join-Path $n 'tools\m30pipe\core\pristine\Project_Nested.pristine.smc') 'vrun_nes_dev' $false))
    $checks.Add((Test-BdFileTool 'NESFab (SMB1 remake, patched 1.8)' (Join-Path $s1 'tools\nesfab\nesfab.exe') 'smb1_topaz' $false $nesfabProbe))
    $checks.Add((Test-BdFileTool 'Mesen 2 (SMB1 remake)' (Join-Path $s1 'tools\mesen\Mesen.exe') 'smb1_topaz' $false $mesenProbe))
    $pvs = Join-Path $sv 'snes-stub\toolchain\pvsneslib\devkitsnes\bin'
    $checks.Add((Test-BdFileTool 'PVSnesLib C compiler 816-tcc (SuperVRUN)' (Join-Path $pvs '816-tcc.exe') 'supervrun_dev' $false))
    $checks.Add((Test-BdFileTool 'PVSnesLib assembler wla-65816 (SuperVRUN)' (Join-Path $pvs 'wla-65816.exe') 'supervrun_dev' $false))
    $checks.Add((Test-BdFileTool 'PVSnesLib linker wlalink (SuperVRUN)' (Join-Path $pvs 'wlalink.exe') 'supervrun_dev' $false))
    $checks.Add((Test-BdFileTool 'Mesen 2 (SuperVRUN)' (Join-Path $sv 'tools\Mesen2\Mesen.exe') 'supervrun_dev' $false $mesenProbe))

    # SMB1's builds read the original game's data; the ROM is the user's own and never goes into git.
    $orig = $env:SMB1_ORIG_ROM
    $origSrc = 'SMB1_ORIG_ROM'
    if (-not $orig) { $orig = Join-Path (Get-BdArchivesDir) 'SMB1 Topaz\smb1.nes'; $origSrc = 'default (SMB1 Topaz folder)' }
    $origOk = Test-Path -LiteralPath $orig
    $origDetail = if ($origOk) { "$origSrc; $((Get-Item -LiteralPath $orig).Length) bytes; sha256 $(Get-BdSha256 $orig)" } else { "$origSrc : not found. Needed to regenerate SMB1 data and for parity checks. The user's own copy; never commit it" }
    $checks.Add((New-BdCheck 'SMB1 original ROM (user-supplied, never in git)' $origOk $false '' $orig $origDetail 'Set SMB1_ORIG_ROM to your own copy of smb1.nes'))
}

# ---- output ------------------------------------------------------------------------------------
$required = @($checks | Where-Object { $_.Required })
$failedRequired = @($required | Where-Object { -not $_.Ok })
if ($Json) {
    [pscustomobject]@{ ok = ($failedRequired.Count -eq 0); home = $homeInfo; checks = $checks.ToArray() } | ConvertTo-Json -Depth 5
} else {
    Write-Host ''
    Write-Host 'BrokenDev doctor'
    Write-Host ('=' * 78)
    foreach ($c in $checks) {
        $tag = if ($c.Ok) { 'ok  ' } elseif ($c.Required) { 'FAIL' } else { 'todo' }
        $color = if ($c.Ok) { 'Green' } elseif ($c.Required) { 'Red' } else { 'Yellow' }
        $ver = if ($c.Version) { " $($c.Version)" } else { '' }
        Write-Host ("[{0}] {1}{2}" -f $tag, $c.Name, $ver) -ForegroundColor $color
        if ($c.Path)   { Write-Host "       $($c.Path)" -ForegroundColor DarkGray }
        if ($c.Detail) { Write-Host "       $($c.Detail)" -ForegroundColor DarkGray }
        if (-not $c.Ok -and $c.Fix) { Write-Host "       fix: $($c.Fix)" -ForegroundColor DarkGray }
    }
    Write-Host ('=' * 78)
    $optional = @($checks | Where-Object { -not $_.Required -and -not $_.Ok })
    if ($failedRequired.Count -eq 0) {
        Write-Host ("doctor: all {0} required checks passed; {1} optional item(s) outstanding (restore a project or build the Workshop to clear them)." -f $required.Count, $optional.Count) -ForegroundColor Green
    } else {
        Write-Host ("doctor: {0} of {1} required checks FAILED." -f $failedRequired.Count, $required.Count) -ForegroundColor Red
    }
}
if ($failedRequired.Count -eq 0) { exit 0 } else { exit 1 }

# BrokenDev shared helpers. Dot-source this file; it works in Windows PowerShell 5.1 and PowerShell 7.
#
# Conventions
#   - Nothing here touches the BrokenNes repository's tracked files; everything BrokenDev creates (restored projects,
#     toolchains, build output, logs) lives under BROKENDEV_HOME, which must be OUTSIDE the repository.
#   - No silent fallbacks: a missing prerequisite raises (or reports) with the exact thing that is missing.

Set-StrictMode -Version Latest

$script:BdLibDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:BdRoot    = Split-Path -Parent $script:BdLibDir          # ...\BrokenDev
$script:BdRepo    = Split-Path -Parent $script:BdRoot            # repository root

# The default working folder the user chose for this machine. Override with the BROKENDEV_HOME environment variable.
$script:BdDefaultHome = 'P:\BrokenDev\env'

function Get-BdRoot     { $script:BdRoot }
function Get-BdRepoRoot { $script:BdRepo }

function Get-BdHome {
    <# BROKENDEV_HOME, else the machine default. Never created here, never silently replaced by another folder. #>
    $h = $env:BROKENDEV_HOME
    if ($h) { $src = 'BROKENDEV_HOME' } else { $h = $script:BdDefaultHome; $src = 'default' }
    $full = [System.IO.Path]::GetFullPath($h)
    # The home must not be inside the repository: restored projects and builds must never reach git.
    $repoFull = [System.IO.Path]::GetFullPath($script:BdRepo).TrimEnd('\') + '\'
    if (($full.TrimEnd('\') + '\').StartsWith($repoFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "BROKENDEV_HOME ($full, from $src) is inside the repository ($repoFull). Pick a folder outside it."
    }
    $full.TrimEnd('\')
}

function Get-BdArchivesDir {
    <# Where the project archives (zips) live. BROKENDEV_ARCHIVES overrides; default is the parent of the home (P:\BrokenDev). #>
    if ($env:BROKENDEV_ARCHIVES) { return [System.IO.Path]::GetFullPath($env:BROKENDEV_ARCHIVES).TrimEnd('\') }
    $h = Get-BdHome
    Split-Path -Parent $h
}

# ---------------------------------------------------------------------------------------------------- logging

function Write-BdInfo  { param([string]$Message) Write-Host $Message }
function Write-BdOk    { param([string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-BdWarn  { param([string]$Message) Write-Host "WARN  $Message" -ForegroundColor Yellow }
function Write-BdFail  { param([string]$Message) Write-Host "FAIL  $Message" -ForegroundColor Red }

# ---------------------------------------------------------------------------------------------------- paths and placeholders

function Expand-BdPath {
    <#
      Replaces {home}, {repo}, {bd}, {out}, {root:<name>} and {env:<NAME>} in a manifest string.
      $Context is a hashtable: Roots (name -> folder), Out (the project's output folder), plus any extra {key} values.
      An unknown placeholder is an error, never an empty string.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [hashtable]$Context = @{})
    $rx = [regex]'\{([A-Za-z0-9_]+(?::[A-Za-z0-9_\-\.]+)?)\}'
    $rx.Replace($Text, {
        param($m)
        $key = $m.Groups[1].Value
        switch -Regex ($key) {
            '^home$' { return (Get-BdHome) }
            '^repo$' { return $script:BdRepo }
            '^bd$'   { return $script:BdRoot }
            '^archives$' { return (Get-BdArchivesDir) }
            '^out$'  {
                if ($Context.ContainsKey('Out')) { return [string]$Context['Out'] }
                throw "placeholder {out} used outside a project context in: $Text"
            }
            '^root:(.+)$' {
                $n = $Matches[1]
                if ($Context.ContainsKey('Roots') -and $Context['Roots'].ContainsKey($n)) { return [string]$Context['Roots'][$n] }
                throw "unknown root '{root:$n}' in: $Text"
            }
            '^env:(.+)$' {
                $v = [Environment]::GetEnvironmentVariable($Matches[1])
                if ([string]::IsNullOrEmpty($v)) { throw "environment variable $($Matches[1]) is not set (needed by: $Text)" }
                return $v
            }
            default {
                if ($Context.ContainsKey($key)) { return [string]$Context[$key] }
                throw "unknown placeholder {$key} in: $Text"
            }
        }
    })
}

function ConvertTo-BdBashPath {
    <# C:\a\b -> /c/a/b (Git Bash form). UNC and other drives are mapped through cygpath-style rules. #>
    param([Parameter(Mandatory)][string]$Path)
    $p = $Path -replace '\\', '/'
    if ($p -match '^([A-Za-z]):/?(.*)$') { return '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2] }
    $p
}

function Get-BdDriveInfo {
    <# Free bytes and whether the drive is a network share, for the drive that holds $Path. #>
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $result = [ordered]@{ Path = $full; Drive = $null; FreeBytes = $null; IsNetwork = $false; Detail = '' }
    if ($full.StartsWith('\\')) { $result.IsNetwork = $true; $result.Detail = 'UNC path'; return [pscustomobject]$result }
    $letter = $full.Substring(0, 1)
    $result.Drive = $letter + ':'
    try {
        $d = New-Object System.IO.DriveInfo($letter)
        $result.FreeBytes = $d.AvailableFreeSpace
        $result.IsNetwork = ($d.DriveType -eq [System.IO.DriveType]::Network)
        $result.Detail = [string]$d.DriveType
    } catch {
        $result.Detail = "drive $letter not readable: $($_.Exception.Message)"
    }
    [pscustomobject]$result
}

function Get-BdSha256 { param([Parameter(Mandatory)][string]$Path) (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-BdMd5    { param([Parameter(Mandatory)][string]$Path) (Get-FileHash -LiteralPath $Path -Algorithm MD5).Hash.ToLowerInvariant() }

function Save-BdJson {
    <# Atomic UTF-8 (no BOM) JSON write: a failure leaves the old file in place. #>
    param([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Path, [int]$Depth = 12)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $tmp = "$Path.tmp"
    $text = ConvertTo-Json -InputObject $Object -Depth $Depth
    [System.IO.File]::WriteAllText($tmp, $text + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Read-BdJson {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "file not found: $Path" }
    $raw = [System.IO.File]::ReadAllText($Path)
    try { $raw | ConvertFrom-Json } catch { throw "invalid JSON in ${Path}: $($_.Exception.Message)" }
}

function Get-BdProp {
    <# Strict-mode-safe property read: the value, or $Default when the property is absent. #>
    param($Object, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] } else { return $Default }
    }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    $Default
}

# ---------------------------------------------------------------------------------------------------- running processes

function ConvertTo-BdArgString {
    <# Windows command-line quoting for Windows PowerShell 5.1, which has no ProcessStartInfo.ArgumentList. #>
    param([string[]]$Arguments)
    ($Arguments | ForEach-Object {
        $a = [string]$_
        if ($a -eq '') { '""' }
        elseif ($a -notmatch '[\s"]') { $a }
        else {
            $b = $a -replace '(\\*)"', '$1$1\"'
            $b = $b -replace '(\\+)$', '$1$1'
            '"' + $b + '"'
        }
    }) -join ' '
}

function Stop-BdProcessTree {
    <# Stops ONE process tree by PID (one we started). Never by name. #>
    param([Parameter(Mandatory)][int]$ProcessId)
    & taskkill.exe /PID $ProcessId /T /F 2>&1 | Out-Null
}

function Invoke-BdProcess {
    <#
      Runs an executable with a hard timeout, capturing stdout+stderr into $LogFile (and returning the captured text in
      .Output). The process tree is killed by PID on a timeout. Returns [pscustomobject] ExitCode, TimedOut, Seconds, LogFile, Output.
      Env is a hashtable of extra environment variables for the child; PathPrepend are folders put in front of PATH.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = (Get-Location).Path,
        [int]$TimeoutSec = 600,
        [string]$LogFile,
        [hashtable]$Env = @{},
        [string[]]$PathPrepend = @(),
        [switch]$Quiet
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $WorkingDirectory
    if ($psi.PSObject.Properties['ArgumentList']) { foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add([string]$a) } }
    else { $psi.Arguments = ConvertTo-BdArgString $Arguments }
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[[string]$k] = [string]$Env[$k] }
    if ($PathPrepend.Count -gt 0) {
        $cur = $psi.EnvironmentVariables['PATH']
        $psi.EnvironmentVariables['PATH'] = (($PathPrepend | Where-Object { $_ }) -join ';') + ';' + $cur
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    # Both streams are drained asynchronously before waiting: a child that fills one pipe while we block on the other deadlocks.
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        $timedOut = $true
        Stop-BdProcessTree -ProcessId $p.Id
        try { [void]$p.WaitForExit(10000) } catch { }
    } else {
        $p.WaitForExit()   # flush async readers
    }
    $out = ''
    try { $out = $outTask.Result } catch { }
    $err = ''
    try { $err = $errTask.Result } catch { }
    $text = $out
    if ($err) { $text = $out + "`n--- stderr ---`n" + $err }
    if ($LogFile) {
        $ld = Split-Path -Parent $LogFile
        if ($ld -and -not (Test-Path -LiteralPath $ld)) { New-Item -ItemType Directory -Force -Path $ld | Out-Null }
        [System.IO.File]::WriteAllText($LogFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    $code = if ($timedOut) { -1 } else { $p.ExitCode }
    [pscustomobject]@{ ExitCode = $code; TimedOut = $timedOut; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); LogFile = $LogFile; Output = $text }
}

function Select-BdTail {
    param([string]$Text, [int]$Lines = 20)
    if (-not $Text) { return '' }
    (($Text -split "`r?`n") | Select-Object -Last $Lines) -join "`n"
}

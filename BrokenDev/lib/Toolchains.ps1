# BrokenDev pinned toolchains (toolchains.json): the toolchains that do not ship inside a project archive (GBDK-2020 for the Game Boy).
# Requires Common.ps1.
#
# Rules
#   - BrokenDev never downloads or unpacks a toolchain: the user approves the download; the zip stays in its _downloads folder.
#   - A toolchain is 'ready' only when its zip has the pinned size and sha256, its folder exists, every pinned file has its size and
#     sha256, and its version file says the pinned version. Anything else is reported with what is wrong, never silently accepted.
#   - The file hashes are cached per run (a build checks the toolchain once); the zip's hash is measured every time 'doctor' asks.

Set-StrictMode -Version Latest

$script:BdToolchainCache = @{}

function Get-BdToolchainsPath {
    <# BROKENDEV_TOOLCHAINS overrides the registry (the tests point it at synthetic ones). #>
    if ($env:BROKENDEV_TOOLCHAINS) { return $env:BROKENDEV_TOOLCHAINS }
    Join-Path (Get-BdRoot) 'toolchains.json'
}

function Test-BdToolchains {
    <# Structural validation of the registry. Returns error strings (empty = valid). Pure: reads nothing but the object. #>
    param([Parameter(Mandatory)]$J)
    $e = New-Object System.Collections.Generic.List[string]
    if ([int](Get-BdProp $J 'schema' 0) -ne 1) { $e.Add('toolchains schema must be 1') }
    $ids = @{}
    $list = @(Get-BdProp $J 'toolchains' @())
    if ($list.Count -eq 0) { $e.Add('toolchains is empty') }
    foreach ($t in $list) {
        $id = [string](Get-BdProp $t 'id' '')
        if (-not $id) { $e.Add('a toolchain has no id'); continue }
        if ($id -notmatch '^[a-z0-9][a-z0-9\-]*$') { $e.Add("toolchain id '$id' must be lower-case letters, digits and dashes") }
        if ($ids.ContainsKey($id)) { $e.Add("duplicate toolchain '$id'") }
        $ids[$id] = $true
        foreach ($f in 'title', 'version', 'source', 'zip', 'sha256', 'dir') { if (-not [string](Get-BdProp $t $f '')) { $e.Add("toolchain ${id}: '$f' is required") } }
        if ([string](Get-BdProp $t 'sha256' '') -notmatch '^[0-9a-f]{64}$') { $e.Add("toolchain ${id}: sha256 must be 64 lower-case hex digits") }
        if ([int64](Get-BdProp $t 'bytes' 0) -le 0) { $e.Add("toolchain ${id}: bytes must be the positive size of the zip") }
        foreach ($k in 'zip', 'dir') {
            $p = [string](Get-BdProp $t $k '')
            if ($p -and ([System.IO.Path]::IsPathRooted($p) -or $p -match '(^|[\\/])\.\.([\\/]|$)')) { $e.Add("toolchain ${id}: $k '$p' must be a path inside BROKENDEV_HOME (relative, no '..')") }
        }
        $files = @(Get-BdProp $t 'files' @())
        if ($files.Count -eq 0) { $e.Add("toolchain ${id}: files is empty (the pinned files are what proves the folder is the toolchain)") }
        foreach ($f in $files) {
            $fp = [string](Get-BdProp $f 'path' '')
            if (-not $fp -or $fp -match '(^|[\\/])\.\.([\\/]|$)' -or [System.IO.Path]::IsPathRooted($fp)) { $e.Add("toolchain ${id}: a file path '$fp' must be relative to its folder") }
            if ([string](Get-BdProp $f 'sha256' '') -notmatch '^[0-9a-f]{64}$') { $e.Add("toolchain ${id}: file $fp needs a sha256") }
            if ([int64](Get-BdProp $f 'bytes' 0) -le 0) { $e.Add("toolchain ${id}: file $fp needs its size") }
        }
    }
    @($e)
}

function Read-BdToolchains {
    param([switch]$NoValidate)
    $path = Get-BdToolchainsPath
    $j = Read-BdJson $path
    if (-not $NoValidate) {
        $errs = @(Test-BdToolchains -J $j)
        if ($errs.Count -gt 0) { throw ("toolchain registry $path is invalid:`n  " + ($errs -join "`n  ")) }
    }
    $j
}

function Get-BdToolchainSpec {
    param([Parameter(Mandatory)][string]$Id, $J = (Read-BdToolchains))
    $t = @($J.toolchains | Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    if (-not $t) { throw "unknown toolchain '$Id' (known: $((@($J.toolchains) | ForEach-Object { $_.id }) -join ', '))" }
    $t
}

function Get-BdToolchainState {
    <#
      Is the pinned toolchain there? Returns { Id; Version; State = ready | missing | changed; Dir; Zip; Problems (strings); Detail }.
      -SkipZip: do not hash the zip (a build checks the unpacked files; 'doctor' checks the zip too).
    #>
    param([Parameter(Mandatory)]$Spec, [switch]$SkipZip, [switch]$NoCache)
    $id = [string]$Spec.id
    $key = "$id|$([bool]$SkipZip)"
    if (-not $NoCache -and $script:BdToolchainCache.ContainsKey($key)) { return $script:BdToolchainCache[$key] }
    $homeDir = Get-BdHome
    $dir = Join-Path $homeDir (([string]$Spec.dir) -replace '/', '\')
    $zip = Join-Path $homeDir (([string]$Spec.zip) -replace '/', '\')
    $problems = New-Object System.Collections.Generic.List[string]
    $state = 'ready'
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { $state = 'missing'; $problems.Add("its folder $dir does not exist") }
    else {
        foreach ($f in @($Spec.files)) {
            $p = Join-Path $dir (([string]$f.path) -replace '/', '\')
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { $state = 'missing'; $problems.Add("$($f.path) is missing"); continue }
            $len = (Get-Item -LiteralPath $p).Length
            if ($len -ne [int64]$f.bytes) { if ($state -eq 'ready') { $state = 'changed' }; $problems.Add("$($f.path) is $len bytes, pinned $($f.bytes)"); continue }
            $h = Get-BdSha256 $p
            if ($h -ne [string]$f.sha256) { if ($state -eq 'ready') { $state = 'changed' }; $problems.Add("$($f.path) has sha256 $h, pinned $($f.sha256)") }
        }
        $vf = Get-BdProp $Spec 'versionFile' $null
        if ($vf) {
            $vp = Join-Path $dir (([string]$vf.path) -replace '/', '\')
            $first = $null
            if (Test-Path -LiteralPath $vp) { $sr = New-Object System.IO.StreamReader($vp); try { $first = $sr.ReadLine() } finally { $sr.Dispose() } }   # (ReadLines | Select -First 1 leaves the file open)
            if ($null -eq $first) { $problems.Add("its version file $($vf.path) is missing"); if ($state -eq 'ready') { $state = 'missing' } }
            elseif ($first.Trim() -ne [string]$vf.firstLine) { $problems.Add("$($vf.path) says '$($first.Trim())', pinned '$($vf.firstLine)'"); if ($state -eq 'ready') { $state = 'changed' } }
        }
    }
    if (-not $SkipZip) {
        if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { $problems.Add("the release zip $zip is missing (the proof of what was unpacked)"); if ($state -eq 'ready') { $state = 'changed' } }
        else {
            $len = (Get-Item -LiteralPath $zip).Length
            $h = Get-BdSha256 $zip
            if ($len -ne [int64]$Spec.bytes -or $h -ne [string]$Spec.sha256) { $problems.Add("the zip is $len bytes with sha256 $h, pinned $($Spec.bytes) bytes $($Spec.sha256)"); if ($state -eq 'ready') { $state = 'changed' } }
        }
    }
    $detail = if ($state -eq 'ready') { "$($Spec.title) $($Spec.version): every pinned file matches$(if (-not $SkipZip) { '; the zip matches its sha256' })" } else { $problems -join '; ' }
    $r = [pscustomobject]@{ Id = $id; Version = [string]$Spec.version; State = $state; Dir = $dir; Zip = $zip; Problems = @($problems); Detail = $detail }
    $script:BdToolchainCache[$key] = $r
    $r
}

function Get-BdToolchainDir {
    <# The folder of a ready toolchain ({toolchain:<id>} in a build step). Throws, saying what is wrong, when it is not ready. #>
    param([Parameter(Mandatory)][string]$Id)
    $st = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id $Id) -SkipZip
    if ($st.State -ne 'ready') { throw "toolchain $Id is $($st.State): $($st.Detail) (BrokenDev never downloads it: see toolchains.json)" }
    $st.Dir
}

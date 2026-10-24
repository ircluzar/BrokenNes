# BrokenDev emulators: the registry (emulators.json), installing the pinned releases portable into BROKENDEV_HOME\emulators, and finding
# what is installed. Requires Common, Tools.
#
# Rules
#   - Only the four pinned packages can be downloaded, and only from the release URL the registry pins; the host and repository must also be
#     in the fixed list below, so editing emulators.json alone cannot add a download source.
#   - A zip already in BROKENDEV_HOME\emulators\_downloads is used when its size and sha256 are the pinned ones; nothing is downloaded again.
#   - A zip with the right name but the wrong size or hash is an error (never silently replaced, never unpacked).
#   - An install is unpacked into a staging folder and moved into place only when 7-Zip succeeded and the exe is there; a marker
#     (.brokendev-install.json) records what was installed, so an interrupted install never looks finished.

Set-StrictMode -Version Latest

# The only release locations BrokenDev may download from (one per package). A registry URL outside these is refused.
$script:BdEmuAllowedUrls = @(
    '^https://github\.com/SourMesen/Mesen2/releases/download/[^/]+/[^/]+\.zip$',
    '^https://github\.com/TASEmulators/BizHawk/releases/download/[^/]+/[^/]+\.zip$',
    '^https://github\.com/TASEmulators/fceux/releases/download/[^/]+/[^/]+\.zip$',
    '^https://github\.com/snes9xgit/snes9x/releases/download/[^/]+/[^/]+\.zip$'
)
$script:BdEmuAdapters = @('workshop', 'mesen', 'bizhawk', 'fceux', 'snes9x')
$script:BdEmuMarker = '.brokendev-install.json'

function Get-BdEmulatorsPath {
    <# BROKENDEV_EMULATORS overrides the registry (the tests point it at synthetic ones). #>
    if ($env:BROKENDEV_EMULATORS) { return $env:BROKENDEV_EMULATORS }
    Join-Path (Get-BdRoot) 'emulators.json'
}

function Get-BdEmuHome { Join-Path (Get-BdHome) 'emulators' }
function Get-BdEmuDownloads { Join-Path (Get-BdEmuHome) '_downloads' }

function Test-BdEmuUrlAllowed {
    param([string]$Url)
    foreach ($rx in $script:BdEmuAllowedUrls) { if ($Url -match $rx) { return $true } }
    $false
}

function Test-BdEmulators {
    <# Structural validation of the registry. Returns an array of error strings (empty = valid). Pure: reads nothing but the object. #>
    param([Parameter(Mandatory)]$J)
    $e = New-Object System.Collections.Generic.List[string]
    if ([int](Get-BdProp $J 'schema' 0) -ne 1) { $e.Add('emulators schema must be 1') }
    $pk = @{}
    foreach ($p in @(Get-BdProp $J 'packages' @())) {
        $id = [string](Get-BdProp $p 'id' '')
        if (-not $id) { $e.Add('a package has no id'); continue }
        if ($id -notmatch '^[a-z0-9][a-z0-9\-]*$') { $e.Add("package id '$id' must be lower-case letters, digits and dashes") }
        if ($pk.ContainsKey($id)) { $e.Add("duplicate package '$id'") }
        $pk[$id] = $p
        foreach ($f in 'title', 'version', 'url', 'file', 'sha256', 'exe') { if (-not [string](Get-BdProp $p $f '')) { $e.Add("package ${id}: '$f' is required") } }
        $url = [string](Get-BdProp $p 'url' '')
        if ($url -and -not (Test-BdEmuUrlAllowed $url)) { $e.Add("package ${id}: url $url is not one of the pinned release locations BrokenDev may download from") }
        $file = [string](Get-BdProp $p 'file' '')
        if ($url -and $file -and -not $url.EndsWith('/' + $file)) { $e.Add("package ${id}: file '$file' is not the file the url names") }
        if ([string](Get-BdProp $p 'sha256' '') -notmatch '^[0-9a-f]{64}$') { $e.Add("package ${id}: sha256 must be 64 lower-case hex digits") }
        if ([int64](Get-BdProp $p 'bytes' 0) -le 0) { $e.Add("package ${id}: bytes must be the positive size of the zip") }
        if ([string](Get-BdProp $p 'version' '') -notmatch '^[0-9][0-9A-Za-z\.\-]*$') { $e.Add("package ${id}: version '$(Get-BdProp $p 'version' '')' is not a version") }
    }
    $ids = @{}; $refs = 0
    $emus = @(Get-BdProp $J 'emulators' @())
    if ($emus.Count -eq 0) { $e.Add('emulators is empty') }
    foreach ($m in $emus) {
        $id = [string](Get-BdProp $m 'id' '')
        if (-not $id) { $e.Add('an emulator has no id'); continue }
        if ($id -notmatch '^[a-z0-9][a-z0-9\-]*$') { $e.Add("emulator id '$id' must be lower-case letters, digits and dashes") }
        if ($ids.ContainsKey($id)) { $e.Add("duplicate emulator '$id'") }
        $ids[$id] = $true
        if (-not [string](Get-BdProp $m 'title' '')) { $e.Add("emulator ${id}: 'title' is required") }
        $cons = @(Get-BdProp $m 'consoles' @())
        if ($cons.Count -eq 0) { $e.Add("emulator ${id}: consoles is empty") }
        foreach ($c in $cons) { if (@('nes', 'snes', 'wms') -notcontains [string]$c) { $e.Add("emulator ${id}: console '$c' must be nes, snes or wms") } }
        $ad = [string](Get-BdProp $m 'adapter' '')
        if ($script:BdEmuAdapters -notcontains $ad) { $e.Add("emulator ${id}: adapter '$ad' must be one of: $($script:BdEmuAdapters -join ', ')") }
        $pkg = Get-BdProp $m 'package' $null
        if ($ad -eq 'workshop') { if ($pkg) { $e.Add("emulator ${id}: the workshop adapter (BrokenNes) has no package") } }
        elseif (-not $pkg) { $e.Add("emulator ${id}: 'package' is required") }
        elseif (-not $pk.ContainsKey([string]$pkg)) { $e.Add("emulator ${id}: package '$pkg' is not in packages") }
        if ($ad -eq 'bizhawk') {
            foreach ($f in 'core', 'system') { if (-not [string](Get-BdProp $m $f '')) { $e.Add("emulator ${id}: a bizhawk emulator needs '$f' (the core to pin and its system)") } }
            if ($cons.Count -ne 1) { $e.Add("emulator ${id}: a bizhawk emulator pins one core, so it runs one console") }
        }
        if (Get-BdProp $m 'reference' $false) { $refs++; if ($ad -ne 'workshop') { $e.Add("emulator ${id}: only BrokenNes (the workshop adapter) is the reference") } }
        $can = Get-BdProp $m 'can' $null
        if (-not $can) { $e.Add("emulator ${id}: 'can' (what its column can check) is required") }
        else { foreach ($f in 'memory', 'pictures') { if ($null -eq (Get-BdProp $can $f $null)) { $e.Add("emulator ${id}: can.$f must say true or false") } } }
    }
    if ($refs -ne 1) { $e.Add("exactly one emulator must be the reference (BrokenNes), found $refs") }
    @($e)
}

function Read-BdEmulators {
    param([switch]$NoValidate)
    $path = Get-BdEmulatorsPath
    $j = Read-BdJson $path
    if (-not $NoValidate) {
        $errs = @(Test-BdEmulators -J $j)
        if ($errs.Count -gt 0) { throw ("emulator registry $path is invalid:`n  " + ($errs -join "`n  ")) }
    }
    $j
}

function Get-BdEmuPackage {
    param([Parameter(Mandatory)][string]$Id, $J = (Read-BdEmulators))
    $p = @($J.packages | Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    if (-not $p) { throw "unknown emulator package '$Id' (known: $((@($J.packages | ForEach-Object { $_.id })) -join ', '))" }
    $p
}

function Get-BdEmulator {
    param([Parameter(Mandatory)][string]$Id, $J = (Read-BdEmulators))
    $m = @($J.emulators | Where-Object { $_.id -eq $Id }) | Select-Object -First 1
    if (-not $m) { throw "unknown emulator '$Id' (known: $((@($J.emulators | ForEach-Object { $_.id })) -join ', '))" }
    $m
}

function Get-BdEmuPackageDir { param([Parameter(Mandatory)]$Package) Join-Path (Get-BdEmuHome) ("{0}-{1}" -f $Package.id, $Package.version) }

function Get-BdEmuPackageState {
    <# installed | partial | absent, with the exe path. 'installed' = the marker matches the pinned package and the exe exists. #>
    param([Parameter(Mandatory)]$Package)
    $dir = Get-BdEmuPackageDir $Package
    $exe = Join-Path $dir ([string]$Package.exe)
    $marker = Join-Path $dir $script:BdEmuMarker
    if (-not (Test-Path -LiteralPath $dir)) { return [pscustomobject]@{ State = 'absent'; Dir = $dir; Exe = $exe; Detail = "$dir does not exist" } }
    if (-not (Test-Path -LiteralPath $marker)) { return [pscustomobject]@{ State = 'partial'; Dir = $dir; Exe = $exe; Detail = 'no install marker (unpacked by hand, or an interrupted install)' } }
    $mk = Read-BdJson $marker
    if ([string]$mk.sha256 -ne [string]$Package.sha256) { return [pscustomobject]@{ State = 'partial'; Dir = $dir; Exe = $exe; Detail = "installed from another zip (sha256 $([string]$mk.sha256))" } }
    if (-not (Test-Path -LiteralPath $exe)) { return [pscustomobject]@{ State = 'partial'; Dir = $dir; Exe = $exe; Detail = "the marker is there but $($Package.exe) is missing" } }
    [pscustomobject]@{ State = 'installed'; Dir = $dir; Exe = $exe; Detail = "installed $([string]$mk.installedAt)" }
}

function Test-BdEmuZip {
    <# Does the zip have the pinned size and sha256? Returns { Ok; Detail }. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Package)
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ Ok = $false; Detail = 'missing' } }
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -ne [int64]$Package.bytes) { return [pscustomobject]@{ Ok = $false; Detail = "size $len, pinned $($Package.bytes)" } }
    $sha = Get-BdSha256 $Path
    if ($sha -ne [string]$Package.sha256) { return [pscustomobject]@{ Ok = $false; Detail = "sha256 $sha, pinned $($Package.sha256)" } }
    [pscustomobject]@{ Ok = $true; Detail = "size and sha256 match ($len bytes)" }
}

function Get-BdEmuZip {
    <#
      The verified zip of a package in _downloads. Downloads the pinned URL only when the zip is not there (-NoDownload: an error instead).
      Never replaces a zip that is there with the wrong size or hash: that is reported, and the user decides.
    #>
    param([Parameter(Mandatory)]$Package, [switch]$NoDownload)
    if (-not (Test-BdEmuUrlAllowed ([string]$Package.url))) { throw "package $($Package.id): $($Package.url) is not a pinned release location; refusing to download it" }
    $dl = Get-BdEmuDownloads
    $zip = Join-Path $dl ([string]$Package.file)
    if (Test-Path -LiteralPath $zip) {
        $t = Test-BdEmuZip -Path $zip -Package $Package
        if (-not $t.Ok) { throw "package $($Package.id): $zip is there but does not match the pinned release ($($t.Detail)). Delete it yourself if it should be downloaded again; BrokenDev never replaces it." }
        Write-BdInfo "  $($Package.file): already downloaded, $($t.Detail)"
        return $zip
    }
    if ($NoDownload) { throw "package $($Package.id): $zip is not there and downloading was not allowed" }
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    $part = "$zip.part"
    if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
    Write-BdInfo "  downloading $($Package.url) ..."
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    $pp = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri ([string]$Package.url) -OutFile $part -UseBasicParsing -TimeoutSec 1800 } finally { $ProgressPreference = $pp }
    $t = Test-BdEmuZip -Path $part -Package $Package
    if (-not $t.Ok) { Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue; throw "package $($Package.id): the download does not match the pinned release ($($t.Detail)); nothing was installed" }
    Move-Item -LiteralPath $part -Destination $zip
    $zip
}

function Install-BdEmuPackage {
    <# Verifies (or downloads) the pinned zip and unpacks it portable into emulators\<id>-<version>. -Force unpacks again. Returns the state. #>
    param([Parameter(Mandatory)]$Package, [switch]$Force, [switch]$NoDownload)
    $st = Get-BdEmuPackageState $Package
    if ($st.State -eq 'installed' -and -not $Force) { Write-BdInfo "  $($Package.id) $($Package.version): already installed in $($st.Dir)"; return $st }
    $zip = Get-BdEmuZip -Package $Package -NoDownload:$NoDownload
    $sevenZip = Find-Bd7Zip
    if (-not $sevenZip) { throw '7-Zip not found (7z.exe). Install it: winget install 7zip.7zip' }
    $dir = $st.Dir
    $staging = Join-Path (Get-BdEmuHome) (".staging\{0}-{1}" -f $Package.id, $Package.version)
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $r = Invoke-BdProcess -FilePath $sevenZip -Arguments @('x', $zip, "-o$staging", '-y', '-bso0', '-bsp0') -TimeoutSec 1800 -LogFile (Join-Path (Get-BdHome) "logs\emulator-install-$($Package.id).log")
    if ($r.TimedOut -or $r.ExitCode -ne 0) { throw "7-Zip failed to unpack $zip (exit $($r.ExitCode)); staging left at $staging" }
    # A zip whose content sits in one top folder is unpacked from inside it.
    $src = $staging
    $kids = @(Get-ChildItem -LiteralPath $staging -Force)
    if ($kids.Count -eq 1 -and $kids[0].PSIsContainer -and -not (Test-Path -LiteralPath (Join-Path $staging ([string]$Package.exe)))) { $src = $kids[0].FullName }
    if (-not (Test-Path -LiteralPath (Join-Path $src ([string]$Package.exe)))) { throw "the zip has no $($Package.exe) where the registry says (looked in $src)" }
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    Move-Item -LiteralPath $src -Destination $dir
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $marker = [ordered]@{ package = [string]$Package.id; version = [string]$Package.version; url = [string]$Package.url; file = [string]$Package.file
                          bytes = [int64]$Package.bytes; sha256 = [string]$Package.sha256; exe = [string]$Package.exe; installedAt = (Get-Date).ToUniversalTime().ToString('o') }
    Save-BdJson -Object $marker -Path (Join-Path $dir $script:BdEmuMarker)
    $st = Get-BdEmuPackageState $Package
    if ($st.State -ne 'installed') { throw "install of $($Package.id) finished but the result is '$($st.State)': $($st.Detail)" }
    Write-BdOk "  $($Package.id) $($Package.version): installed in $dir"
    $st
}

function Resolve-BdEmuInstallTargets {
    <# 'all' -> every package; an emulator id -> its package; a package id -> it. Unknown names are an error. #>
    param([Parameter(Mandatory)][string]$Name, $J = (Read-BdEmulators))
    if ($Name -eq 'all') { return @($J.packages) }
    $m = @($J.emulators | Where-Object { $_.id -eq $Name }) | Select-Object -First 1
    if ($m) {
        if (-not (Get-BdProp $m 'package' $null)) { throw "emulator '$Name' is BrokenNes itself (the Workshop): it is built with 'brokendev.ps1 build-workshop', not installed" }
        return @(Get-BdEmuPackage -Id ([string]$m.package) -J $J)
    }
    $p = @($J.packages | Where-Object { $_.id -eq $Name }) | Select-Object -First 1
    if ($p) { return @($p) }
    throw "unknown emulator or package '$Name' (emulators: $((@($J.emulators | ForEach-Object { $_.id })) -join ', '); packages: $((@($J.packages | ForEach-Object { $_.id })) -join ', '))"
}

function Test-BdEmuPrereqs {
    <# System components a package needs that this machine lacks (empty = none). Reported, never installed. #>
    param([Parameter(Mandatory)]$Package)
    $miss = @()
    if ([string]$Package.id -eq 'bizhawk') {
        foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
            if (-not (Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32\$dll"))) { $miss += "Visual C++ 2015-2022 x64 runtime ($dll)" }
        }
        $rel = 0
        try { $rel = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release } catch { }
        if ($rel -lt 528040) { $miss += ".NET Framework 4.8 (release key $rel)" }
    }
    @($miss | Select-Object -Unique)
}

function Get-BdEmuExe {
    <# The exe of an emulator id, or $null when its package is not installed. BrokenNes: the Workshop. #>
    param([Parameter(Mandatory)]$Emu, $J = (Read-BdEmulators))
    if ([string]$Emu.adapter -eq 'workshop') { return (Get-BdWorkshopExe) }
    $st = Get-BdEmuPackageState (Get-BdEmuPackage -Id ([string]$Emu.package) -J $J)
    if ($st.State -eq 'installed') { return $st.Exe }
    $null
}

function Get-BdEmulatorRows {
    <# The registry as rows for 'emulators': id, consoles, version, installed or not, what its column can check. #>
    $j = Read-BdEmulators
    foreach ($m in $j.emulators) {
        if ([string]$m.adapter -eq 'workshop') {
            $ws = Get-BdWorkshopExe
            $state = if ($ws) { 'built' } else { 'not built' }
            $ver = 'this repository'; $path = $ws
        } else {
            $p = Get-BdEmuPackage -Id ([string]$m.package) -J $j
            $st = Get-BdEmuPackageState $p
            $state = $st.State; $ver = [string]$p.version; $path = $st.Dir
            $pre = @(Test-BdEmuPrereqs $p)
            if ($pre.Count -gt 0) { $state += ' (missing: ' + ($pre -join ', ') + ')' }
        }
        $can = @()
        foreach ($k in 'memory', 'pictures', 'audio', 'saves') { if (Get-BdProp $m.can $k $false) { $can += $k } }
        [pscustomobject]@{ Id = [string]$m.id; Title = [string]$m.title; Consoles = (@($m.consoles) -join ','); Version = $ver; State = $state; Can = ($can -join ','); Adapter = [string]$m.adapter; Path = $path; Reference = [bool](Get-BdProp $m 'reference' $false) }
    }
}

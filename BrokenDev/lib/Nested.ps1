# BrokenDev: the Nested module. Our own Project Nested fork (Project-Nested-core, branch nested-vrun-core) turned into one
# command: NES ROM -> SNES ROM, with the core assembled from source (tools/nestedasm) instead of the binary patch chain.
#
#   brokendev.ps1 nested core  [-Profile vrun]                         the core from source, checked against the profile's md5
#   brokendev.ps1 nested build <nes> [-Out f.smc] [-Profile vrun] [-Feedback none|brokennes] [-Base 50a5ca15] [-Post flash] [-SnesTitle T] [-Plans f.json]
#   brokendev.ps1 nested test  [-Quick]                                golden hashes: core md5 per step + the VRUN release ROM byte-identical
#
# The fork's source lives in the user's live VRUN_Nes_Dev workspace ({root:nestedcore}). Since 2026-11-10 the fork also carries
# the pipeline (tools/m30pipe: m30build.ps1, game profiles) and the injector's source (Project Nested\, .NET 10, net10.0-windows):
# these commands build the injector from source (dotnet build) and run the fork's pipeline on PowerShell 7.6+ (pwsh), never
# master's prebuilt .NET Framework exe. Nothing of either is copied into this repository.

function Get-BdNestedGolden { Read-BdJson -Path (Join-Path (Join-Path (Get-BdProjectsDir) 'nested-core') 'golden.json') }

function Get-BdNestedRoots {
    $r = Get-BdArchiveRoots
    foreach ($k in 'nestedcore', 'm30pipe') {
        if (-not $r.ContainsKey($k) -or -not (Test-Path -LiteralPath $r[$k])) { throw "nested: root '$k' not found (the live VRUN_Nes_Dev workspace with Nested_VRUN\Project-Nested-core; set BROKENDEV_LIVE_VRUN_NES_DEV)" }
    }
    $r
}

function Get-BdNestedPipe {
    <# The fork's own m30pipe (the pipeline that loads the injector built from the fork's source). #>
    param($Roots = (Get-BdNestedRoots))
    $pipe = Join-Path $Roots['nestedcore'] 'tools\m30pipe'
    if (-not (Test-Path -LiteralPath (Join-Path $pipe 'm30build.ps1'))) { throw "nested: $pipe has no m30build.ps1 (Project-Nested-core older than b21f48a?)" }
    $pipe
}

function Get-BdNestedPwsh {
    <# PowerShell 7.6+ (.NET 10), which the fork's .NET 10 injector needs: this process when it is one, else pwsh.exe on PATH. #>
    if ($PSVersionTable.PSEdition -eq 'Core' -and [Environment]::Version.Major -ge 10) { return (Get-Process -Id $PID).Path }
    $c = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if (-not $c) { throw 'nested: needs PowerShell 7.6+ (pwsh.exe) for the .NET 10 injector' }
    $c.Source
}

function Invoke-BdNestedInjector {
    <# Builds the injector from the fork's source (dotnet build -c Release) when its bin\Release\Project_Nested.dll is missing or older than a source file. #>
    param($Roots = (Get-BdNestedRoots))
    $src = Join-Path $Roots['nestedcore'] 'Project Nested'
    $proj = Join-Path $src 'Project Nested.csproj'
    $dll = Join-Path $src 'bin\Release\Project_Nested.dll'
    $newest = Get-ChildItem -LiteralPath $src -Recurse -File -Include *.cs, *.csproj, *.resx, *.config, *.settings |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ((Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).LastWriteTimeUtc -ge $newest.LastWriteTimeUtc) { return $dll }
    Write-BdInfo "nested: building the injector from $src (dotnet build -c Release)"
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $o = & dotnet build $proj -c Release -nologo -v q 2>&1
    $rc = $LASTEXITCODE; $ErrorActionPreference = $old
    if ($rc -ne 0 -or -not (Test-Path -LiteralPath $dll)) { $o | ForEach-Object { Write-BdInfo "  $_" }; throw "nested: injector build failed (exit $rc)" }
    Write-BdOk "nested: injector $dll"
    $dll
}

function Get-BdNestedPython {
    $py = Find-BdPython
    if (-not $py) { throw 'nested: no Python 3.10+ found' }
    $py.Path
}

function Invoke-BdNestedCore {
    <# Assembles the core of a nestedasm profile into BROKENDEV_HOME\work\nested\core\<profile>, with the manifest.json m30build.ps1 reads (-CoreDir). #>
    param([string]$Profile = 'vrun')
    $roots = Get-BdNestedRoots
    $py = Get-BdNestedPython
    $dir = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'nested\core') $Profile
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $core = Join-Path $dir 'Project_Nested.smc'
    $tool = Join-Path $roots['nestedcore'] 'tools\nestedasm\build.py'
    Write-BdInfo "nested: assembling the core (profile $Profile) from $($roots['nestedcore'])"
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $o = & $py $tool --profile $Profile --out $core 2>&1
    $rc = $LASTEXITCODE; $ErrorActionPreference = $old
    if ($rc -ne 0) { $o | ForEach-Object { Write-BdInfo "  $_" }; throw "nested: core build failed (exit $rc)" }
    $md5 = Get-BdMd5 -Path $core
    $profPath = Join-Path $roots['nestedcore'] "tools\nestedasm\profiles\$Profile.json"
    $want = ''
    if (Test-Path -LiteralPath $profPath) { $p = Read-BdJson -Path $profPath; if ($p.PSObject.Properties['expect_md5']) { $want = [string]$p.expect_md5 } }
    $steps = Join-Path $roots['nestedcore'] 'Assembly\Project\NestedVrun\Steps.asm'
    $commit = ''
    try { $commit = (& git -c safe.directory=* -C $roots['nestedcore'] rev-parse --short HEAD 2>$null) } catch { }
    # m30build.ps1 (Initialize-PipelineExe) reads final.md5/final.match and checks steps_file for staleness; steps = none, the
    # core comes from source. 'source' is ours (m30build ignores it).
    $manifest = [ordered]@{
        format = 'm30pipe-core-manifest/2'
        final = [ordered]@{ md5 = $md5; match = ($want -and $md5 -eq $want); expected_md5 = $want }
        steps_file = [ordered]@{ path = $steps; md5 = (Get-BdMd5 -Path $steps) }
        steps = @()
        source = [ordered]@{ kind = 'nestedasm'; profile = $Profile; tree = $roots['nestedcore']; commit = [string]$commit }
    }
    Save-BdJson -Path (Join-Path $dir 'manifest.json') -Object $manifest
    if ($want -and $md5 -ne $want) { throw "nested: core md5 $md5 is not the profile's $want" }
    Write-BdOk "nested: core $core md5 $md5$(if ($want) { ' (= profile)' })"
    [pscustomobject]@{ Dir = $dir; Core = $core; Md5 = $md5; Profile = $Profile; Commit = [string]$commit }
}

function Invoke-BdNestedBuild {
    <# NES ROM -> SNES ROM: the core from source, then tools\nested-build.ps1 (m30build + optional BrokenNes feedback) with -CoreDir. #>
    param([Parameter(Mandatory)][string]$Nes, [string]$Out = '', [string]$Profile = 'vrun', [string]$Feedback = 'none',
          [string]$Base = '', [string]$Post = '', [string]$SnesTitle = '', [string]$Plans = '', [string]$Nmi = '')
    if (-not (Test-Path -LiteralPath $Nes)) { throw "nested: no NES ROM at $Nes" }
    $Nes = (Resolve-Path -LiteralPath $Nes).ProviderPath
    $roots = Get-BdNestedRoots
    $pipe = Get-BdNestedPipe $roots
    $null = Invoke-BdNestedInjector $roots
    $core = Invoke-BdNestedCore -Profile $Profile
    $name = [System.IO.Path]::GetFileNameWithoutExtension($Nes)
    $work = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'nested\games') $name
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    if (-not $Out) { $Out = Join-Path (Join-Path (Join-Path (Get-BdHome) 'out') 'nested') ($name + '_SNES.smc') }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($Out))) | Out-Null
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path (Get-BdRoot) 'tools\nested-build.ps1'),
           '-Nes', $Nes, '-Out', $Out, '-Pipe', $pipe, '-Python', (Get-BdNestedPython), '-Work', $work,
           '-Feedback', $Feedback, '-CoreDir', $core.Dir)
    if ($Base) { $a += @('-BaseProfile', $Base) }
    if ($Post) { $a += @('-Post', $Post) }
    if ($SnesTitle) { $a += @('-SnesTitle', $SnesTitle) }
    if ($Nmi) { $a += @('-Nmi', $Nmi) }
    if ($Feedback -eq 'brokennes') {
        $ws = Get-BdWorkshopExe
        if (-not $ws) { throw 'nested: -Feedback brokennes needs the Workshop (brokendev.ps1 build-workshop)' }
        if (-not $Plans) { throw 'nested: -Feedback brokennes needs -Plans (a JSON list of { name, frames, input })' }
        $a += @('-Workshop', $ws, '-Plans', $Plans)
    }
    Push-Location $pipe
    try { & (Get-BdNestedPwsh) @a; $rc = $LASTEXITCODE } finally { Pop-Location }
    if ($rc -ne 0) { throw "nested: nested-build.ps1 failed (exit $rc)" }
    Write-BdOk "nested: $Out sha256 $(Get-BdSha256 -Path $Out) (core $($core.Md5))"
    [pscustomobject]@{ Out = $Out; Sha256 = (Get-BdSha256 -Path $Out); CoreMd5 = $core.Md5 }
}

function Test-BdNested {
    <#
      The golden hashes (projects\nested-core\golden.json):
        1. tools/nestedasm/test_nestedasm.py: the v1.8 tree = the pristine core, every NESTED_STEP = its pinned md5 (-Quick: 0 and 18);
        2. each release: its NES ROM (by sha256) through the fork's m30build.ps1 with its game profile, the core from source and the injector
           built from source (.NET 10) = the released SNES ROM.
    #>
    param([switch]$Quick)
    $roots = Get-BdNestedRoots
    $py = Get-BdNestedPython
    $g = Get-BdNestedGolden
    $pipe = Get-BdNestedPipe $roots
    $ok = $true
    try { $dll = Invoke-BdNestedInjector $roots; Write-BdOk "injector: built from source ($dll)" } catch { Write-BdFail "injector: $_"; return $false }
    $t = @((Join-Path $roots['nestedcore'] 'tools\nestedasm\test_nestedasm.py'))
    if ($Quick) { $t += '--quick' }
    Write-BdInfo 'nested test: core steps (test_nestedasm.py)'
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $o = & $py @t 2>$null
    $rc = $LASTEXITCODE; $ErrorActionPreference = $old
    $o | ForEach-Object { Write-BdInfo "  $_" }
    if ($rc -ne 0) { Write-BdFail 'core steps'; $ok = $false } else { Write-BdOk 'core steps: every pinned md5' }
    foreach ($r in @($g.releases)) {
        $core = Invoke-BdNestedCore -Profile ([string]$r.coreProfile)
        $nes = Expand-BdPath -Text ([string]$r.nes)
        if (-not (Test-Path -LiteralPath $nes) -or (Get-BdSha256 -Path $nes) -ne [string]$r.nesSha256) {
            Write-BdWarn "release $($r.id): SKIPPED: no NES ROM with sha256 $($r.nesSha256) at $nes"; continue
        }
        $out = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'nested\test') ($r.id + '.smc')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $pipe 'm30build.ps1'), '-Nes', $nes,
               '-Profile', [string]$r.gameProfile, '-Out', $out, '-Python', $py, '-CoreDir', $core.Dir, '-Overwrite')
        Push-Location $pipe
        try { & (Get-BdNestedPwsh) @a | Out-Null; $rc = $LASTEXITCODE } finally { Pop-Location }
        $sha = if (Test-Path -LiteralPath $out) { Get-BdSha256 -Path $out } else { '' }
        if ($sha -eq [string]$r.snesSha256) { Write-BdOk "release $($r.id): byte-identical (sha256 $sha)" }
        else { Write-BdFail "release $($r.id): sha256 '$sha' (m30build exit $rc), want $($r.snesSha256); see $out.build.log"; $ok = $false }
    }
    $ok
}

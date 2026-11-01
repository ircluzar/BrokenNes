# BrokenDev build runner: executes a manifest's build steps and publishes its outputs. Requires Common, Tools, Archive, Manifest.

Set-StrictMode -Version Latest

if (-not ('BdCrc32' -as [type])) {
    Add-Type -TypeDefinition @'
public static class BdCrc32 {
    private static readonly uint[] T = Make();
    private static uint[] Make() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1); t[i] = c; } return t; }
    public static uint Compute(byte[] d, int off, int len) { uint c = 0xFFFFFFFFu; for (int i = off; i < off + len; i++) c = T[(c ^ d[i]) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
}
'@
}

function Get-BdRomFacts {
    <# What a ROM file is, for the build record: size, hashes, and for iNES files the mapper and the PRG CRC32 the Nested profiles are keyed by. #>
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $facts = [ordered]@{ bytes = $bytes.Length; sha256 = (Get-BdSha256 $Path); md5 = (Get-BdMd5 $Path) }
    if ($bytes.Length -ge 16 -and $bytes[0] -eq 0x4E -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x53 -and $bytes[3] -eq 0x1A) {
        $prg = [int]$bytes[4] * 16384
        $facts['format'] = 'ines'
        $facts['mapper'] = ([int]$bytes[6] -shr 4) -bor ([int]$bytes[7] -band 0xF0)
        $facts['prgKB'] = $prg / 1024
        $facts['chrKB'] = [int]$bytes[5] * 8
        $facts['prgCrc32'] = ('{0:x8}' -f [BdCrc32]::Compute($bytes, 16, $prg))
    } elseif ($bytes.Length -ge 0x8000 -and ($bytes.Length % 0x8000) -eq 0) {
        # Header-less SNES image (LoROM/HiROM). A 512-byte copier header would make the length 512 bytes over a multiple of 32 KB.
        $facts['format'] = 'snes'
        $title = ''
        foreach ($off in 0x7FC0, 0xFFC0) {
            if ($bytes.Length -ge $off + 21) {
                $t = [System.Text.Encoding]::ASCII.GetString($bytes, $off, 21)
                if ($t -match '^[\x20-\x7E]{21}$') { $title = $t.TrimEnd(); break }
            }
        }
        $facts['snesTitle'] = $title
    }
    $facts
}

function Get-BdOutDir { param([Parameter(Mandatory)][string]$Id) Join-Path (Join-Path (Get-BdHome) 'out') $Id }

function Resolve-BdInputs {
    <# The external inputs a project declares (requires.inputs): {id, env, default, neverInGit}. Returns id -> resolved path; throws when one is absent. #>
    param($Manifest, [hashtable]$Ctx)
    $r = @{}
    foreach ($in in @($Manifest.requires.inputs)) {
        $id = [string]$in.id
        $val = [Environment]::GetEnvironmentVariable([string]$in.env)
        $src = "environment variable $($in.env)"
        if (-not $val -and (Get-BdProp $in 'default' $null)) { $val = Expand-BdPath -Text ([string]$in.default) -Context $Ctx; $src = 'manifest default' }
        if (-not $val -or -not (Test-Path -LiteralPath $val)) {
            throw "project $($Manifest.id) needs the input '$id' ($($in.description)). Not found (looked at $src : '$val'). Set $($in.env) to your own copy."
        }
        $r[$id] = [pscustomobject]@{ Path = (Resolve-Path -LiteralPath $val).Path; Env = [string]$in.env }
    }
    $r
}

function Get-BdShellCommand {
    <# Turns one manifest step into { FilePath; Arguments } for Invoke-BdProcess (placeholders already expanded). #>
    param($Step, [hashtable]$Ctx)
    $sh = [string]$Step.shell
    $file = if (Get-BdProp $Step 'file' $null) { Expand-BdPath -Text ([string]$Step.file) -Context $Ctx } else { '' }
    $stepArgs = @()
    foreach ($a in @(Get-BdProp $Step 'args' @())) { $stepArgs += (Expand-BdPath -Text ([string]$a) -Context $Ctx) }
    switch ($sh) {
        'bash' {
            $bash = Find-BdGitBash
            if (-not $bash) { throw 'Git Bash not found' }
            return @{ FilePath = $bash; Arguments = @('-c', (Expand-BdPath -Text ([string]$Step.run) -Context $Ctx)) }
        }
        'ps5' {
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            return @{ FilePath = $ps; Arguments = (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $file) + $stepArgs) }
        }
        'python' {
            if (-not $Ctx.ContainsKey('python')) { throw 'no suitable Python (3.10+ with pillow and numpy) was found' }
            return @{ FilePath = [string]$Ctx['python']; Arguments = (@($file) + $stepArgs) }
        }
        'exe' { return @{ FilePath = $file; Arguments = $stepArgs } }
    }
    throw "unknown shell '$sh'"
}

function Invoke-BdBuildStep {
    param($Step, [hashtable]$Ctx, [string]$LogDir, [hashtable]$BaseEnv, [switch]$DryRun)
    $cwd = Expand-BdPath -Text ([string]$Step.cwd) -Context $Ctx
    $cmd = Get-BdShellCommand -Step $Step -Ctx $Ctx
    $envx = @{}
    foreach ($k in $BaseEnv.Keys) { $envx[$k] = $BaseEnv[$k] }
    $se = Get-BdProp $Step 'env' $null
    if ($se) { foreach ($p in $se.PSObject.Properties) { $envx[$p.Name] = Expand-BdPath -Text ([string]$p.Value) -Context $Ctx } }
    $log = Join-Path $LogDir ("{0}.log" -f ([string]$Step.name -replace '[^A-Za-z0-9_\-\.]', '_'))
    if ($DryRun) {
        return [pscustomobject]@{ Name = [string]$Step.name; ExitCode = 0; TimedOut = $false; Seconds = 0; Log = $log; Cwd = $cwd; Command = ($cmd.FilePath + ' ' + (ConvertTo-BdArgString $cmd.Arguments)); DryRun = $true }
    }
    if (-not (Test-Path -LiteralPath $cwd)) { throw "step '$($Step.name)': working folder does not exist: $cwd (is the project restored?)" }
    # bash steps get the project's folders as forward-slash Windows paths (P:/BrokenDev/...): valid in Git Bash and for native tools alike,
    # with no backslash for the shell to eat. BD_ROOT_<NAME> for every archive root, plus BD_HOME, BD_OUT, BD_REPO, BD_WORK.
    if ([string]$Step.shell -eq 'bash') {
        $fs = { param($p) ([string]$p).Replace('\', '/') }
        $envx['BD_HOME'] = & $fs (Get-BdHome); $envx['BD_REPO'] = & $fs (Get-BdRepoRoot); $envx['BD_OUT'] = & $fs $Ctx['Out']
        $envx['BD_WORK'] = & $fs (Join-Path (Join-Path (Get-BdHome) 'work') $Ctx['Id'])
        foreach ($rk in $Ctx['Roots'].Keys) { $envx['BD_ROOT_' + ([string]$rk).ToUpperInvariant()] = & $fs $Ctx['Roots'][$rk] }
        if ($Ctx.ContainsKey('workshop')) { $envx['BD_WORKSHOP'] = & $fs $Ctx['workshop'] }
    }
    $pathPrepend = @()
    if ($Ctx.ContainsKey('pythondir')) { $pathPrepend += [string]$Ctx['pythondir'] }
    $cmdText = $cmd.FilePath + ' ' + (ConvertTo-BdArgString $cmd.Arguments)
    if ($cmdText.Length -gt 200) { $cmdText = $cmdText.Substring(0, 200) + '...' }
    Write-BdInfo ("  step {0}: {1}" -f $Step.name, $cmdText)
    $r = Invoke-BdProcess -FilePath $cmd.FilePath -Arguments $cmd.Arguments -WorkingDirectory $cwd -TimeoutSec ([int]$Step.timeoutSec) -LogFile $log -Env $envx -PathPrepend $pathPrepend
    [pscustomobject]@{ Name = [string]$Step.name; ExitCode = $r.ExitCode; TimedOut = $r.TimedOut; Seconds = $r.Seconds; Log = $log; Cwd = $cwd; Command = ''; DryRun = $false; Tail = (Select-BdTail $r.Output 12) }
}

function Invoke-BdProjectBuild {
    <#
      Builds one project: checks tools and inputs, runs every step (stopping at the first failure), requires every output to
      have been (re)written by this build, and publishes the outputs to BROKENDEV_HOME\out\<id>\ with a build.json record.
      Returns the build record. Throws on failure after writing a failed record.
    #>
    param([Parameter(Mandatory)][string]$Id, [switch]$DryRun)
    $m = Read-BdManifest -Id $Id
    $ctx = New-BdContext $m
    $out = [string]$ctx['Out']
    $logDir = Join-Path $out 'logs'

    $missing = @()
    foreach ($t in @($m.requires.tools)) {
        switch ([string]$t) {
            'gitbash'  { if (-not (Find-BdGitBash)) { $missing += 'Git Bash' } }
            'python'   { if (-not $ctx.ContainsKey('python')) { $missing += 'Python 3.10+ with pillow and numpy' } }
            'node'     { if (-not (Test-BdNode).Ok) { $missing += 'Node.js 20+' } }
            'dotnet'   { if (-not (Test-BdDotnet).Ok) { $missing += '.NET 10 SDK' } }
            'ps5'      { if (-not (Test-BdWindowsPowerShell).Ok) { $missing += 'Windows PowerShell 5.1' } }
            '7zip'     { if (-not (Find-Bd7Zip)) { $missing += '7-Zip' } }
            'workshop' { if (-not $ctx.ContainsKey('workshop')) { $missing += 'BrokenNes Workshop (run: brokendev.ps1 build-workshop)' } }
            default {
                # a pinned toolchain (toolchains.json): there and unchanged, or the build does not start
                if ([string]$t -match '^toolchain:(.+)$') {
                    $tid = $Matches[1]
                    try { $st = Get-BdToolchainState -Spec (Get-BdToolchainSpec -Id $tid) -SkipZip; if ($st.State -ne 'ready') { $missing += "toolchain $tid ($($st.State): $($st.Detail))" } }
                    catch { $missing += "toolchain ${tid}: $($_.Exception.Message)" }
                }
            }
        }
    }
    if ($missing.Count -gt 0) { throw "project ${Id}: missing tools: $($missing -join ', '). Run 'brokendev.ps1 doctor'." }

    # The project builds in its live workspace when there is one (the user's own folder: outputs land in its git-ignored build folders,
    # exactly like his own builds), else in the restored copy, which must then be completely restored.
    # A project with source.folder (a WideMess project) builds in that folder.
    $ws = Get-BdProjectWorkspace -Manifest $m
    if ($ws.Kind -eq 'absent' -and $m.source.PSObject.Properties['folder'] -and -not $DryRun) { throw "project ${Id}: $($ws.Detail)" }
    if ($ws.Kind -notin 'live', 'restored', 'folder' -and -not $DryRun) { throw "project ${Id}: archive '$($m.source.archive)' has no live workspace and its restored copy is $($ws.Kind) ($($ws.Detail)). Run 'brokendev.ps1 restore $Id'." }
    if (-not $DryRun) { Write-BdInfo ("  workspace: {0} ({1})" -f $ws.Kind, $ws.Path) }

    $inputs = @{}
    $baseEnv = @{}
    if (-not $DryRun) {
        $inputs = Resolve-BdInputs -Manifest $m -Ctx $ctx
        foreach ($k in $inputs.Keys) { $baseEnv[$inputs[$k].Env] = $inputs[$k].Path }
    }
    if ($ctx.ContainsKey('python')) { $baseEnv['BROKENDEV_PYTHON'] = [string]$ctx['python'] }

    # Remember the outputs' timestamps: a build that "succeeds" but leaves an older file in place has not built anything.
    $before = @{}
    foreach ($o in @($m.build.outputs)) {
        $p = Expand-BdPath -Text ([string]$o.path) -Context $ctx
        $before[[string]$o.id] = if (Test-Path -LiteralPath $p) { (Get-Item -LiteralPath $p).LastWriteTimeUtc } else { [datetime]::MinValue }
    }
    $started = (Get-Date).ToUniversalTime()

    if (-not $DryRun) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
    $record = [ordered]@{ project = $Id; title = [string]$m.title; startedAtUtc = $started.ToString('o'); ok = $false; workspace = [ordered]@{ kind = $ws.Kind; path = $ws.Path; source = $ws.Source }; steps = @(); outputs = @(); python = $(if ($ctx.ContainsKey('python')) { [string]$ctx['python'] } else { $null }); error = $null }
    # where the apps put this build's bug captures (D6): the game workspace's bugs\ (the desktop app reads it from here), none for a restored copy
    $record['bugs'] = $(if ($ws.Kind -in 'live', 'folder' -and $ws.Path) { Join-Path $ws.Path 'bugs' } else { $null })
    $failed = $null
    foreach ($s in @($m.build.steps)) {
        $r = Invoke-BdBuildStep -Step $s -Ctx $ctx -LogDir $logDir -BaseEnv $baseEnv -DryRun:$DryRun
        $record.steps += [ordered]@{ name = $r.Name; exitCode = $r.ExitCode; timedOut = $r.TimedOut; seconds = $r.Seconds; log = $r.Log }
        if ($DryRun) { Write-BdInfo ("  [dry-run] {0}`n      cwd {1}`n      {2}" -f $r.Name, $r.Cwd, $r.Command); continue }
        if ($r.TimedOut -or $r.ExitCode -ne 0) {
            $failed = "step '$($r.Name)' failed (exit $($r.ExitCode)$(if ($r.TimedOut) { ', timed out after ' + $s.timeoutSec + ' s' })). Log: $($r.Log)`n$($r.Tail)"
            break
        }
    }
    if ($DryRun) { return [pscustomobject]$record }

    if (-not $failed) {
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        foreach ($o in @($m.build.outputs)) {
            $p = Expand-BdPath -Text ([string]$o.path) -Context $ctx
            if (-not (Test-Path -LiteralPath $p)) { $failed = "output '$($o.id)' was not produced: $p"; break }
            $it = Get-Item -LiteralPath $p
            if ($it.LastWriteTimeUtc -le $before[[string]$o.id] -or $it.LastWriteTimeUtc -lt $started.AddSeconds(-2)) {
                $failed = "output '$($o.id)' ($p) was not rewritten by this build (timestamp $($it.LastWriteTimeUtc.ToString('o')) is older than the build start). A stale file would pass for a fresh build, so this is a failure."
                break
            }
            $dest = Join-Path $out ([string]$o.publishAs)
            Copy-Item -LiteralPath $p -Destination $dest -Force
            $facts = if ($o.id -in 'rom') { Get-BdRomFacts $dest } else { [ordered]@{ bytes = (Get-Item -LiteralPath $dest).Length; sha256 = (Get-BdSha256 $dest) } }
            $rec = [ordered]@{ id = [string]$o.id; published = $dest; source = $p }
            foreach ($k in $facts.Keys) { $rec[$k] = $facts[$k] }
            $record.outputs += $rec
        }
    }
    # A manifest may name the md5 of a ROM the user already has (build.reference.md5). Matching it is strong evidence the toolchain
    # reproduces the user's build; a mismatch is recorded, never hidden, but is not a failure (the source may have moved on).
    $ref = Get-BdProp (Get-BdProp $m.build 'reference' $null) 'md5' $null
    if ($ref -and -not $failed) {
        $romRec = $record.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1
        if ($romRec) {
            $record['referenceMd5'] = [string]$ref
            $record['referenceMatch'] = ([string]$romRec.md5 -eq [string]$ref)
            if ($record['referenceMatch']) { Write-BdOk "  reference: the ROM is byte-identical to the reference build ($ref)" } else { Write-BdWarn "the ROM md5 $($romRec.md5) differs from the reference $ref (not a failure: the source may differ from the reference build)" }
        }
    }
    $record.finishedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    if ($failed) { $record.error = $failed } else { $record.ok = $true }
    Save-BdJson -Object $record -Path (Join-Path $out 'build.json')
    if ($failed) { throw "project ${Id}: $failed" }
    [pscustomobject]$record
}

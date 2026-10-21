# BrokenDev developer tools: the editors and check scripts made while developing the games (the VRUN tile editor, the SFX configurator,
# the AudioPlayer configurator, the Nested Builder, the CTW room designer, the regression and puzzle checkers ...), listed in
# devtools.json and started against the LIVE workspaces. Requires Common, Tools, Archive, Manifest.
#
# Nothing here re-implements a tool: each entry says how the tool was always started (its own server or script, its own port), and
# BrokenDev only resolves the paths, starts it, opens the browser and records what it started. A server already listening on the
# tool's port is reused when it answers like the tool, and reported (never killed) when it does not.

Set-StrictMode -Version Latest

$script:BdToolKinds = @('server', 'script', 'page')

function Get-BdDevtoolsPath {
    <# BROKENDEV_DEVTOOLS overrides the registry (the tests point it at synthetic entries). #>
    if ($env:BROKENDEV_DEVTOOLS) { return $env:BROKENDEV_DEVTOOLS }
    Join-Path (Get-BdRoot) 'devtools.json'
}

function Test-BdDevtools {
    <# Structural validation of the registry. Returns an array of error strings (empty = valid). $ProjectIds: the known project ids. #>
    param([Parameter(Mandatory)]$J, [string[]]$ProjectIds = @())
    $e = New-Object System.Collections.Generic.List[string]
    if ([int](Get-BdProp $J 'schema' 0) -ne 1) { $e.Add("devtools schema must be 1") }
    $tools = @(Get-BdProp $J 'tools' @())
    if ($tools.Count -eq 0) { $e.Add('devtools.tools is empty') }
    $ids = @{}; $ports = @{}
    foreach ($t in $tools) {
        $id = [string](Get-BdProp $t 'id' '')
        if (-not $id) { $e.Add('a tool has no id'); continue }
        if ($id -notmatch '^[a-z0-9][a-z0-9\-]*$') { $e.Add("tool id '$id' must be lower-case letters, digits and dashes") }
        if ($ids.ContainsKey($id)) { $e.Add("duplicate tool id '$id'") }
        $ids[$id] = $true
        foreach ($f in 'title', 'project', 'edits', 'kind', 'cwd', 'from') { if (-not [string](Get-BdProp $t $f '')) { $e.Add("tool '$id': '$f' is required") } }
        $proj = [string](Get-BdProp $t 'project' '')
        if ($proj -and $ProjectIds.Count -gt 0 -and $ProjectIds -notcontains $proj) { $e.Add("tool '$id': project '$proj' is not a BrokenDev project (known: $($ProjectIds -join ', '))") }
        $kind = [string](Get-BdProp $t 'kind' '')
        if ($kind -and $script:BdToolKinds -notcontains $kind) { $e.Add("tool '$id': kind '$kind' must be one of: $($script:BdToolKinds -join ', ')") }
        $steps = @(Get-BdToolSteps $t)
        if ($steps.Count -eq 0) { $e.Add("tool '$id': needs 'run' (an argument list) or 'steps'") }
        foreach ($s in $steps) { if (@(Get-BdProp $s 'run' @()).Count -eq 0) { $e.Add("tool '$id': a step has an empty 'run'") } }
        if ($kind -eq 'server') {
            if ($steps.Count -ne 1) { $e.Add("tool '$id': a server has exactly one 'run'") }
            $port = [int](Get-BdProp $t 'port' 0)
            if ($port -le 0 -or $port -gt 65535) { $e.Add("tool '$id': a server needs its 'port'") }
            elseif ($ports.ContainsKey($port)) { $e.Add("tool '$id': port $port is also the port of '$($ports[$port])'") }
            else { $ports[$port] = $id }
            if (-not [string](Get-BdProp $t 'probe' '')) { $e.Add("tool '$id': a server needs a 'probe' path (a GET that answers when the tool is up)") }
        }
        if ($kind -eq 'page' -and -not [string](Get-BdProp $t 'open' '')) { $e.Add("tool '$id': a page needs 'open' (the file its steps write)") }
        foreach ($c in @(Get-BdProp $t 'okExitCodes' @())) { if ([string]$c -notmatch '^\d+$') { $e.Add("tool '$id': okExitCodes must be numbers, found '$c'") } }
        if ($kind -in 'script', 'page' -and [int](Get-BdProp $t 'timeoutSec' 0) -le 0) { $e.Add("tool '$id': timeoutSec must be a positive number") }
    }
    @($e)
}

function Get-BdToolSteps {
    param($Tool)
    $steps = Get-BdProp $Tool 'steps' $null
    if ($steps) { return @($steps) }
    $run = Get-BdProp $Tool 'run' $null
    if ($run) { return @([pscustomobject]@{ run = @($run); env = (Get-BdProp $Tool 'env' $null) }) }
    @()
}

function Read-BdDevtools {
    param([switch]$NoValidate)
    $j = Read-BdJson (Get-BdDevtoolsPath)
    if (-not $NoValidate) {
        $errs = @(Test-BdDevtools -J $j -ProjectIds (Get-BdProjectIds))
        if ($errs.Count -gt 0) { throw ("devtools registry $(Get-BdDevtoolsPath) is invalid:`n  " + ($errs -join "`n  ")) }
    }
    @($j.tools)
}

function Get-BdTool {
    param([Parameter(Mandatory)][string]$Id)
    $all = Read-BdDevtools
    $t = $all | Where-Object { $_.id -eq $Id } | Select-Object -First 1
    if (-not $t) { throw "unknown tool '$Id' (known: $((@($all | ForEach-Object { $_.id })) -join ', '))" }
    $t
}

function Find-BdPythonFor {
    <#
      The first Python (BROKENDEV_PYTHON when set, else every candidate) that is at least $MinVersion and has Pillow, NumPy and
      $Modules (among those Get-BdPythonInfo probes: scipy, soundfile, mido). $null when none has them.
    #>
    param([string[]]$Modules = @(), [string]$MinVersion = '3.10')
    $cands = if ($env:BROKENDEV_PYTHON) { @($env:BROKENDEV_PYTHON) } else { @(Get-BdPythonCandidates) }
    foreach ($p in $cands) {
        $i = Get-BdPythonInfo -Path $p
        if (-not $i -or $i.Version -lt [version]$MinVersion) { continue }
        $ok = $true
        foreach ($mod in @('PIL', 'numpy') + $Modules) { if (-not $i.Modules.ContainsKey($mod) -or $i.Modules[$mod] -eq 'MISSING') { $ok = $false } }
        if ($ok) { return $i }
    }
    $null
}

function New-BdToolContext {
    <#
      Placeholders for a tool: the project's (roots, python, out ...) plus {work} = BROKENDEV_HOME\work\tools\<id>, {node}, {bash}.
      A tool that names 'minPython' or 'pythonModules' gets the first Python that has them ({python}, and first on PATH for its children);
      when none has them, ctx['pythonMissing'] says what is missing.
    #>
    param([Parameter(Mandatory)]$Tool)
    $m = Read-BdManifest -Id ([string]$Tool.project)
    $ctx = New-BdContext $m
    $minPy = [string](Get-BdProp $Tool 'minPython' '')
    $mods = @(Get-BdProp $Tool 'pythonModules' @())
    if ($minPy -or $mods.Count -gt 0) {
        $want = if ($minPy) { $minPy } else { '3.10' }
        $py = Find-BdPythonFor -Modules $mods -MinVersion $want
        if ($py) { $ctx['python'] = $py.Path; $ctx['pythondir'] = Split-Path -Parent $py.Path }
        else {
            [void]$ctx.Remove('python'); [void]$ctx.Remove('pythondir')
            $ctx['pythonMissing'] = "a Python $want or newer with pillow, numpy$(if ($mods.Count) { ', ' + ($mods -join ', ') })"
        }
    }
    $ctx['work'] = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'tools') ([string]$Tool.id)
    $node = Find-BdExecutable -Name 'node.exe' -Candidates @("$env:ProgramFiles\nodejs\node.exe")
    if ($node) { $ctx['node'] = $node }
    $bash = Find-BdGitBash
    if ($bash) { $ctx['bash'] = $bash }
    $ctx
}

function Expand-BdToolArgs {
    <# One step's argument list with every placeholder expanded; a placeholder the machine cannot fill is an error naming it. #>
    param([Parameter(Mandatory)]$Step, [Parameter(Mandatory)][hashtable]$Ctx, [string]$ToolId)
    $out = @()
    foreach ($a in @($Step.run)) {
        $s = [string]$a
        foreach ($need in 'python', 'node', 'bash') {
            if ($s -match "\{$need\}" -and -not $Ctx.ContainsKey($need)) {
                $what = @{ python = $(if ($Ctx.ContainsKey('pythonMissing')) { [string]$Ctx['pythonMissing'] } else { 'a Python 3.10+ with pillow and numpy' }); node = 'Node.js'; bash = 'Git Bash' }[$need]
                throw "tool ${ToolId}: needs $what, which this machine does not have (run brokendev.ps1 doctor)"
            }
        }
        $out += (Expand-BdPath -Text $s -Context $Ctx)
    }
    $out
}

function Set-BdToolEnv {
    <# Sets a step's environment defaults in this process (only the ones the caller has not set) and returns what to undo. #>
    param($EnvSpec, [hashtable]$Ctx)
    $undo = @{}
    if (-not $EnvSpec) { return $undo }
    foreach ($p in $EnvSpec.PSObject.Properties) {
        if ([Environment]::GetEnvironmentVariable($p.Name)) { continue }   # the caller's own value wins
        $undo[$p.Name] = $null
        [Environment]::SetEnvironmentVariable($p.Name, (Expand-BdPath -Text ([string]$p.Value) -Context $Ctx))
    }
    $undo
}

function Restore-BdToolEnv { param([hashtable]$Undo) foreach ($k in $Undo.Keys) { [Environment]::SetEnvironmentVariable($k, $Undo[$k]) } }

function Test-BdPortOpen {
    param([Parameter(Mandatory)][int]$Port, [int]$TimeoutMs = 400)
    $c = New-Object System.Net.Sockets.TcpClient
    try { $t = $c.ConnectAsync('127.0.0.1', $Port); return ($t.Wait($TimeoutMs) -and $c.Connected) } catch { return $false } finally { $c.Close() }
}

function Get-BdPortOwner {
    <# The PID listening on 127.0.0.1:$Port, or $null. #>
    param([Parameter(Mandatory)][int]$Port)
    try { $c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1; if ($c) { return [int]$c.OwningProcess } } catch { }
    $null
}

function Test-BdToolProbe {
    <# Does the server on the tool's port answer the tool's probe (HTTP 200, and probeText when the entry names one)? #>
    param([Parameter(Mandatory)]$Tool)
    $url = "http://127.0.0.1:$([int]$Tool.port)$([string]$Tool.probe)"
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
        if ($r.StatusCode -ne 200) { return $false }
        $want = [string](Get-BdProp $Tool 'probeText' '')
        if ($want -and ([string]$r.Content) -notmatch [regex]::Escape($want)) { return $false }
        return $true
    } catch { return $false }
}

function Get-BdToolStateFile { param([Parameter(Mandatory)]$Tool) Join-Path (Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'tools') ([string]$Tool.id)) 'server.json' }

function Get-BdToolUrl { param([Parameter(Mandatory)]$Tool) "http://127.0.0.1:$([int]$Tool.port)$([string](Get-BdProp $Tool 'url' '/'))" }

# ---------------------------------------------------------------------------------------------------- preparations
# A named preparation fixes, from the BrokenDev side, what stops an old tool on this machine (a path of the old machine kept in the
# tool's own git-ignored state), without editing the game repositories.

function Invoke-BdToolPrepare {
    param([Parameter(Mandatory)]$Tool, [Parameter(Mandatory)][hashtable]$Ctx)
    $name = [string](Get-BdProp $Tool 'prepare' '')
    switch ($name) {
        '' { return }
        'nested-builder-state' { Update-BdNestedBuilderState -Ctx $Ctx; return }
        default { throw "tool $($Tool.id): unknown preparation '$name'" }
    }
}

function Update-BdNestedBuilderState {
    <#
      The Nested Builder keeps its tool paths and recent ROMs in m30pipe\out\ui\state.json (git-ignored). The copy that came with the
      workspace holds the old machine's paths: a Mesen at C:\PROJECTS\... that does not exist here (no harvest, no Play in Mesen), and
      last destinations under C:\Users\user\Desktop\... (a build would create that foreign folder). This points Mesen at the live VRUN
      workspace's Mesen when the stored one does not exist, drops destinations whose folder does not exist, and puts the live VRUN
      builds at the top of the recent list. The original file is kept once as state.json.oldmachine.
    #>
    param([Parameter(Mandatory)][hashtable]$Ctx)
    $pipe = [string]$Ctx['Roots']['m30pipe']; $vrun = [string]$Ctx['Roots']['vrun']
    $dir = Join-Path $pipe 'out\ui'
    $path = Join-Path $dir 'state.json'
    $st = $null
    if (Test-Path -LiteralPath $path) {
        $bak = "$path.oldmachine"
        if (-not (Test-Path -LiteralPath $bak)) { Copy-Item -LiteralPath $path -Destination $bak }
        $st = Read-BdJson $path
    }
    if (-not $st) { $st = [pscustomobject]@{} }
    $changes = @()
    $mesen = [string](Get-BdProp $st 'mesen_exe' '')
    $liveMesen = Join-Path $vrun 'tools\mesen\Mesen.exe'
    if ((-not $mesen -or -not (Test-Path -LiteralPath $mesen)) -and (Test-Path -LiteralPath $liveMesen)) {
        $st | Add-Member -NotePropertyName mesen_exe -NotePropertyValue $liveMesen -Force; $changes += "Mesen -> $liveMesen"
    }
    foreach ($k in 'dest_by_rom', 'dest_by_name') {
        $map = Get-BdProp $st $k $null
        if (-not $map) { continue }
        foreach ($p in @($map.PSObject.Properties)) {
            $d = Split-Path -Parent ([string]$p.Value)
            if (-not $d -or -not (Test-Path -LiteralPath $d)) { $map.PSObject.Properties.Remove($p.Name); $changes += "dropped the old destination $($p.Value)" }
        }
    }
    $recent = @(Get-BdProp $st 'recent' @())
    $keep = @($recent | Where-Object { $_ -and (Test-Path -LiteralPath ([string]$_.path)) })
    $now = [double][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $front = @()
    foreach ($n in 'vrun_game_nested.nes', 'vrun_game.nes') {
        $f = Join-Path $vrun "build\$n"
        if (Test-Path -LiteralPath $f) { $front += [pscustomobject]@{ path = $f; name = $n; ts = $now } }
    }
    $merged = @($front) + @($keep | Where-Object { $p0 = [string]$_.path; -not ($front | Where-Object { $_.path -ieq $p0 }) })
    if ((@($recent | ForEach-Object { [string]$_.path }) -join '|') -ne (@($merged | ForEach-Object { [string]$_.path }) -join '|')) { $changes += "recent ROMs: the live VRUN builds first, $(@($recent).Count - @($keep).Count) missing file(s) dropped" }
    $st | Add-Member -NotePropertyName recent -NotePropertyValue @($merged | Select-Object -First 12) -Force
    if ($changes.Count -gt 0) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Save-BdJson -Object $st -Path $path -Depth 6
        foreach ($c in $changes) { Write-BdInfo "  prepare: $c" }
    }
}

# ---------------------------------------------------------------------------------------------------- starting a tool

function Start-BdServerTool {
    <# Starts (or reuses) a server tool and returns { Url; Pid; Reused; Log }. Never kills anything it did not start. #>
    param([Parameter(Mandatory)]$Tool, [Parameter(Mandatory)][hashtable]$Ctx, [string[]]$Extra = @())
    $port = [int]$Tool.port
    $url = Get-BdToolUrl $Tool
    if (Test-BdPortOpen -Port $port) {
        if (Test-BdToolProbe $Tool) { return [pscustomobject]@{ Url = $url; Pid = (Get-BdPortOwner -Port $port); Reused = $true; Log = $null } }
        $owner = Get-BdPortOwner -Port $port
        $who = if ($owner) { $p = Get-Process -Id $owner -ErrorAction SilentlyContinue; "PID $owner ($(if ($p) { $p.ProcessName } else { '?' }))" } else { 'an unknown process' }
        throw "port $port (the port tool $($Tool.id) always used) is taken by $who, which does not answer like $($Tool.title). Close it yourself, then run this again: BrokenDev never stops a program it did not start."
    }
    $step = @(Get-BdToolSteps $Tool)[0]
    $argv = @(Expand-BdToolArgs -Step $step -Ctx $Ctx -ToolId $Tool.id) + $Extra
    $cwd = Expand-BdPath -Text ([string]$Tool.cwd) -Context $Ctx
    if (-not (Test-Path -LiteralPath $cwd)) { throw "tool $($Tool.id): its folder does not exist: $cwd" }
    $work = [string]$Ctx['work']
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $log = Join-Path $work 'server.log'; $errLog = Join-Path $work 'server.err.log'
    $undo = Set-BdToolEnv -EnvSpec (Get-BdProp $step 'env' $null) -Ctx $Ctx
    $pathBefore = $env:PATH
    if ($Ctx.ContainsKey('pythondir')) { $env:PATH = [string]$Ctx['pythondir'] + ';' + $env:PATH }   # children of the tool find the same Python
    try {
        # Started through cmd.exe by ShellExecute (no handle inheritance): a server started with redirected handles would inherit the
        # caller's own output pipe and keep a capturing caller waiting until the server exits. cmd writes the server's output to the logs.
        $inner = (ConvertTo-BdArgString $argv) + ' > "' + $log + '" 2> "' + $errLog + '"'
        $proc = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList ('/d /s /c "' + $inner + '"') -WorkingDirectory $cwd -WindowStyle Hidden -PassThru
    } finally { Restore-BdToolEnv $undo; $env:PATH = $pathBefore }
    $deadline = (Get-Date).AddSeconds([int](Get-BdProp $Tool 'startSec' 45))
    $up = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        if ((Test-BdPortOpen -Port $port) -and (Test-BdToolProbe $Tool)) { $up = $true; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $up) {
        $tail = ''
        foreach ($f in $log, $errLog) { if (Test-Path -LiteralPath $f) { $tail += (Select-BdTail ([System.IO.File]::ReadAllText($f)) 8) + "`n" } }
        if (-not $proc.HasExited) { Stop-BdProcessTree -ProcessId $proc.Id }
        throw "tool $($Tool.id) did not come up on port $port (log $log):`n$tail"
    }
    Save-BdJson -Object ([ordered]@{ tool = [string]$Tool.id; pid = $proc.Id; port = $port; url = $url; startedAt = (Get-Date).ToString('o'); command = $argv; cwd = $cwd; log = $log }) -Path (Get-BdToolStateFile $Tool)
    [pscustomobject]@{ Url = $url; Pid = $proc.Id; Reused = $false; Log = $log }
}

function Stop-BdServerTool {
    <# Stops the server BrokenDev started for this tool (the PID it recorded), only if that process still owns the tool's port. #>
    param([Parameter(Mandatory)]$Tool)
    $sf = Get-BdToolStateFile $Tool
    if (-not (Test-Path -LiteralPath $sf)) { Write-BdInfo "tool $($Tool.id): BrokenDev has no record of starting it"; return $false }
    $rec = Read-BdJson $sf
    $owner = Get-BdPortOwner -Port ([int]$Tool.port)
    $alive = Get-Process -Id ([int]$rec.pid) -ErrorAction SilentlyContinue
    if (-not $alive) { Remove-Item -LiteralPath $sf -Force; Write-BdInfo "tool $($Tool.id): not running (PID $($rec.pid) is gone)"; return $false }
    $ownsPort = ($owner -eq [int]$rec.pid) -or ($owner -and (Get-CimInstance Win32_Process -Filter "ProcessId=$owner" -ErrorAction SilentlyContinue).ParentProcessId -eq [int]$rec.pid)
    if (-not $ownsPort) { Write-BdWarn "tool $($Tool.id): PID $($rec.pid) no longer owns port $($Tool.port) (owner: $owner); not stopping anything"; return $false }
    Stop-BdProcessTree -ProcessId ([int]$rec.pid)
    Remove-Item -LiteralPath $sf -Force
    Write-BdOk "tool $($Tool.id): stopped PID $($rec.pid)"
    $true
}

function Invoke-BdLiveProcess {
    <#
      Runs one program in the foreground with its output shown as it comes and kept in $Log (stdout) / $Log.err (stderr), with a hard
      timeout (the tree is stopped by PID). Returns { ExitCode; TimedOut }.
    #>
    param([Parameter(Mandatory)][string[]]$Argv, [Parameter(Mandatory)][string]$Cwd, [Parameter(Mandatory)][string]$Log, [int]$TimeoutSec = 600)
    $errLog = "$Log.err"
    Remove-Item -LiteralPath $Log, $errLog -ErrorAction SilentlyContinue
    $sp = @{ FilePath = $Argv[0]; WorkingDirectory = $Cwd; RedirectStandardOutput = $Log; RedirectStandardError = $errLog; PassThru = $true; NoNewWindow = $true }
    if ($Argv.Count -gt 1) { $sp['ArgumentList'] = (ConvertTo-BdArgString ($Argv[1..($Argv.Count - 1)])) }
    $p = Start-Process @sp
    $null = $p.Handle   # keeps the exit code readable after the process ends (a Start-Process quirk)
    $pos = @{ $Log = 0L; $errLog = 0L }
    $show = {
        foreach ($f in @($Log, $errLog)) {
            if (-not (Test-Path -LiteralPath $f)) { continue }
            try {
                $fs = [System.IO.File]::Open($f, 'Open', 'Read', 'ReadWrite')
                try {
                    if ($fs.Length -gt $pos[$f]) {
                        [void]$fs.Seek($pos[$f], 'Begin'); $buf = New-Object byte[] ($fs.Length - $pos[$f]); $n = $fs.Read($buf, 0, $buf.Length); $pos[$f] += $n
                        Write-Host -NoNewline ([System.Text.Encoding]::UTF8.GetString($buf, 0, $n))
                    }
                } finally { $fs.Close() }
            } catch { }
        }
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $timedOut = $false
    while (-not $p.HasExited) {
        & $show
        if ((Get-Date) -gt $deadline) { $timedOut = $true; Stop-BdProcessTree -ProcessId $p.Id; break }
        Start-Sleep -Milliseconds 300
    }
    try { [void]$p.WaitForExit(10000) } catch { }
    & $show
    [pscustomobject]@{ ExitCode = $(if ($timedOut) { -1 } else { $p.ExitCode }); TimedOut = $timedOut }
}

function Invoke-BdScriptTool {
    <# Runs a script (or page) tool's steps in the foreground, output to the console and to work\tools\<id>\run*.log. Returns the exit code. #>
    param([Parameter(Mandatory)]$Tool, [Parameter(Mandatory)][hashtable]$Ctx, [string[]]$Extra = @())
    $cwd = Expand-BdPath -Text ([string]$Tool.cwd) -Context $Ctx
    if (-not (Test-Path -LiteralPath $cwd)) { throw "tool $($Tool.id): its folder does not exist: $cwd" }
    $work = [string]$Ctx['work']
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $steps = @(Get-BdToolSteps $Tool)
    $code = 0
    for ($i = 0; $i -lt $steps.Count; $i++) {
        $argv = @(Expand-BdToolArgs -Step $steps[$i] -Ctx $Ctx -ToolId $Tool.id)
        if ($i -eq $steps.Count - 1) { $argv += $Extra }   # extra arguments go to the last step
        $log = Join-Path $work ("run{0}.log" -f $(if ($steps.Count -gt 1) { $i + 1 } else { '' }))
        Write-BdInfo ("  {0}> {1}" -f $Tool.id, (ConvertTo-BdArgString $argv))
        $undo = Set-BdToolEnv -EnvSpec (Get-BdProp $steps[$i] 'env' $null) -Ctx $Ctx
        $pathBefore = $env:PATH
        if ($Ctx.ContainsKey('pythondir')) { $env:PATH = [string]$Ctx['pythondir'] + ';' + $env:PATH }
        try { $r = Invoke-BdLiveProcess -Argv $argv -Cwd $cwd -Log $log -TimeoutSec ([int]$Tool.timeoutSec) }
        finally { Restore-BdToolEnv $undo; $env:PATH = $pathBefore }
        $code = $r.ExitCode
        if ($r.TimedOut) { Write-BdFail "tool $($Tool.id): step $($i + 1) timed out after $($Tool.timeoutSec) s (log $log)"; return 1 }
        # a step may declare other exit codes that mean 'done' (screenshot.lua ends with emu.stop(1) once it has written its PNG)
        $okCodes = @(@(Get-BdProp $steps[$i] 'okExitCodes' @(Get-BdProp $Tool 'okExitCodes' @())) | ForEach-Object { [int]$_ }) + 0
        if ($okCodes -contains $code) { $code = 0 }
        if ($code -ne 0) { Write-BdFail "tool $($Tool.id): step $($i + 1) exited $code (log $log)"; return $code }
    }
    $code
}

function Invoke-BdTool {
    <# The 'tool <id>' verb. -NoBrowser prints the address instead of opening it; -Stop stops a server BrokenDev started. Returns an exit code. #>
    param([Parameter(Mandatory)][string]$Id, [switch]$NoBrowser, [switch]$Stop, [string[]]$Extra = @())
    $t = Get-BdTool -Id $Id
    if ($Stop) { if ([string]$t.kind -ne 'server') { throw "tool $Id is not a server" }; [void](Stop-BdServerTool $t); return 0 }
    $ctx = New-BdToolContext $t
    # a tool whose scripts call 'python' themselves (bash scripts) needs the right one even without {python} in its own command
    if ($ctx.ContainsKey('pythonMissing')) { throw "tool ${Id}: needs $($ctx['pythonMissing']), which this machine does not have (set BROKENDEV_PYTHON to one)" }
    $ws = Get-BdWorkspace -Id ([string](Read-BdManifest -Id ([string]$t.project)).source.archive)
    Write-BdInfo ("tool {0}: {1}  [workspace: {2} {3}]" -f $Id, $t.title, $ws.Kind, $ws.Path)
    $miss = @(Test-BdToolFiles -Tool $t -Ctx $ctx)
    if ($miss.Count -gt 0) { throw "tool ${Id} cannot start in this workspace ($($ws.Kind)): missing $($miss -join '; ')" }
    Invoke-BdToolPrepare -Tool $t -Ctx $ctx
    switch ([string]$t.kind) {
        'server' {
            $r = Start-BdServerTool -Tool $t -Ctx $ctx -Extra $Extra
            if ($r.Reused) { Write-BdOk "tool ${Id}: already running on $($r.Url) (PID $($r.Pid)); reused" } else { Write-BdOk "tool ${Id}: running on $($r.Url) (PID $($r.Pid), log $($r.Log))" }
            if ($NoBrowser) { Write-Host $r.Url } else { Start-Process $r.Url | Out-Null }
            Write-BdInfo "  stop it with: brokendev.ps1 tool $Id -Stop   (or close it the way you always did)"
            return 0
        }
        'script' { return (Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra $Extra) }
        'page' {
            $code = Invoke-BdScriptTool -Tool $t -Ctx $ctx -Extra $Extra
            if ($code -ne 0) { return $code }
            $page = Expand-BdPath -Text ([string]$t.open) -Context $ctx
            if (-not (Test-Path -LiteralPath $page)) { throw "tool ${Id}: its steps ran but did not write $page" }
            $uri = ([System.Uri]$page).AbsoluteUri
            Write-BdOk "tool ${Id}: $page"
            if ($NoBrowser) { Write-Host $uri } else { Start-Process $uri | Out-Null }
            return 0
        }
    }
    throw "tool ${Id}: unknown kind '$($t.kind)'"
}

function Test-BdToolFiles {
    <#
      The files a tool needs that are not where its entry says: its folder, and every program or script named in its steps (an argument
      ending in .py .mjs .js .sh .lua .ps1 .exe .html, relative to the tool's folder or absolute). Empty = the tool can start here.
    #>
    param([Parameter(Mandatory)]$Tool, [Parameter(Mandatory)][hashtable]$Ctx)
    $miss = @()
    $cwd = Expand-BdPath -Text ([string]$Tool.cwd) -Context $Ctx
    if (-not (Test-Path -LiteralPath $cwd -PathType Container)) { return @("folder $cwd") }
    foreach ($s in @(Get-BdToolSteps $Tool)) {
        try { $argv = @(Expand-BdToolArgs -Step $s -Ctx $Ctx -ToolId $Tool.id) } catch { $miss += $_.Exception.Message; continue }
        foreach ($a in $argv) {
            if ($a -notmatch '\.(py|mjs|js|sh|lua|ps1|exe|html)$' -or $a -match '\s') { continue }
            $p = if ([System.IO.Path]::IsPathRooted($a)) { $a } else { Join-Path $cwd $a }
            if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { $miss += "file $p" }
        }
    }
    @($miss)
}

function Get-BdToolRows {
    <# The registry as rows for 'tools' (optionally one project), with whether a server tool is up right now and whether its files are here. #>
    param([string]$Project = '')
    $rows = foreach ($t in (Read-BdDevtools)) {
        if ($Project -and [string]$t.project -ne $Project) { continue }
        $starts = switch ([string]$t.kind) {
            'server' { "web UI on port $($t.port)" + $(if (Test-BdPortOpen -Port ([int]$t.port)) { ' (running)' } else { '' }) }
            'page'   { 'generates a page, opens it' }
            default  { 'runs once' }
        }
        $miss = @()
        try { $miss = @(Test-BdToolFiles -Tool $t -Ctx (New-BdToolContext $t)) } catch { $miss = @($_.Exception.Message) }
        if ($miss.Count -gt 0) { $starts = 'MISSING FILES' }
        [pscustomobject]@{ Id = [string]$t.id; Project = [string]$t.project; Kind = [string]$t.kind; Starts = $starts; Title = [string]$t.title; Edits = [string]$t.edits; From = [string]$t.from }
    }
    @($rows)
}

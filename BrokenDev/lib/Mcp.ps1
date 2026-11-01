# BrokenDev: the BrokenNes MCP server (milestone D3, BrokenDev/mcp): build it, register it for Claude Code at USER scope, test it.
# Requires Common, Tools, Verify. Touches no Claude setting but the one 'brokennes' MCP server entry.

Set-StrictMode -Version Latest

$script:BdMcpName = 'brokennes'

function Get-BdMcpArtifacts { Join-Path (Get-BdHome) 'bnbuild\mcp' }

function Get-BdMcpExe {
    <# The built server: BROKENDEV_MCP, else the one 'brokendev.ps1 mcp build' made under BROKENDEV_HOME\bnbuild\mcp. $null if none. #>
    if ($env:BROKENDEV_MCP) { if (Test-Path -LiteralPath $env:BROKENDEV_MCP) { return $env:BROKENDEV_MCP } else { return $null } }
    try { $p = Join-Path (Get-BdMcpArtifacts) 'bin\BrokenNes.Mcp\release\BrokenNes.Mcp.exe' } catch { return $null }
    if (Test-Path -LiteralPath $p) { return $p }
    $null
}

function Invoke-BdBuildMcp {
    <# dotnet build -c Release of BrokenDev/mcp into BROKENDEV_HOME\bnbuild\mcp (outside the repository); returns the exe. #>
    param([string]$ArtifactsPath = '')
    $dotnet = Find-BdExecutable -Name 'dotnet.exe' -Candidates @("$env:ProgramFiles\dotnet\dotnet.exe")
    if (-not $dotnet) { throw 'dotnet.exe not found (.NET 10 SDK needed)' }
    $repo = Get-BdRepoRoot
    if (-not $ArtifactsPath) { $ArtifactsPath = Get-BdMcpArtifacts }
    New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null
    $log = Join-Path (Get-BdHome) 'logs\build-mcp.log'
    Write-BdInfo "building the MCP server into $ArtifactsPath ..."
    $r = Invoke-BdProcess -FilePath $dotnet -Arguments @('build', (Join-Path $repo 'BrokenDev\mcp\BrokenNes.Mcp.csproj'), '-c', 'Release', '--artifacts-path', $ArtifactsPath, '-v', 'q', '-nologo') -WorkingDirectory $repo -TimeoutSec 1800 -LogFile $log
    if ($r.TimedOut -or $r.ExitCode -ne 0) {
        $hint = if ("$($r.Output)" -match 'being used by another process|MSB3027|MSB3021') { ' (the server is running in a Claude Code session: close those sessions, then build again)' } else { '' }
        throw "dotnet build of the MCP server failed (exit $($r.ExitCode))$hint. Log: $log`n$(Select-BdTail $r.Output 25)"
    }
    $exe = Join-Path $ArtifactsPath 'bin\BrokenNes.Mcp\release\BrokenNes.Mcp.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "the build reported success but $exe does not exist" }
    $exe
}

function Get-BdMainCheckout {
    <# The repository's main checkout (a worktree's own folder is temporary): the parent of the common git folder. #>
    param([string]$Repo = (Get-BdRepoRoot))
    $git = Find-BdExecutable -Name 'git.exe' -Candidates @("$env:ProgramFiles\Git\cmd\git.exe")
    if (-not $git) { return $Repo }
    $common = (& $git -C $Repo rev-parse --path-format=absolute --git-common-dir 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $common) { return $Repo }
    $main = Split-Path -Parent ([System.IO.Path]::GetFullPath(($common | Select-Object -First 1).Trim()))
    if (Test-Path -LiteralPath (Join-Path $main 'BrokenDev\brokendev.ps1')) { return $main }
    $Repo
}

function Find-BdClaudeCli {
    <# Claude Code's CLI: 'claude' on PATH, else the newest one the Claude desktop app keeps (%APPDATA%\Claude\claude-code\<version>\<id>\claude.exe). #>
    $c = Get-Command claude -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $root = Join-Path $env:APPDATA 'Claude\claude-code'
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    $best = $null; $bestV = [version]'0.0'
    foreach ($d in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
        $v = $null
        if (-not [version]::TryParse($d.Name, [ref]$v)) { continue }
        $exe = Get-ChildItem -LiteralPath $d.FullName -Recurse -Filter 'claude.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exe -and $v -gt $bestV) { $best = $exe.FullName; $bestV = $v }
    }
    $best
}

function Get-BdClaudeUserConfig { Join-Path $env:USERPROFILE '.claude.json' }

function Install-BdMcp {
    <#
      Registers the server for Claude Code at USER scope (every session, in BrokenNes and in every game repo): with the official CLI,
      'claude mcp add --scope user brokennes -- <exe> --repo <main checkout>', when it is on this machine; else the documented user-level
      entry (mcpServers.brokennes in %USERPROFILE%\.claude.json), nothing else in that file changed. Returns what was written where.
    #>
    param([string]$Exe = '', [switch]$NoBuild, [switch]$DryRun)
    $ErrorActionPreference = 'Continue'   # the CLI writes notes to stderr; Windows PowerShell would turn them into errors
    if (-not $Exe) { $Exe = if ($NoBuild) { Get-BdMcpExe } else { Invoke-BdBuildMcp } }
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { throw 'the MCP server is not built (brokendev.ps1 mcp build)' }
    $repo = Get-BdMainCheckout
    $serverArgs = @('--repo', $repo)
    $cli = Find-BdClaudeCli
    $rec = [ordered]@{ name = $script:BdMcpName; scope = 'user'; command = $Exe; args = $serverArgs; how = ''; file = (Get-BdClaudeUserConfig); cli = $cli }
    if ($DryRun) { $rec.how = 'dry run: nothing written'; return [pscustomobject]$rec }
    if ($cli) {
        # replace an older registration of ours (only the 'brokennes' entry, only at user scope)
        & $cli mcp remove $script:BdMcpName --scope user *> $null
        $out = & $cli mcp add --scope user $script:BdMcpName '--' $Exe @serverArgs 2>&1
        if ($LASTEXITCODE -ne 0) { throw "claude mcp add failed (exit $LASTEXITCODE): $out" }
        $rec.how = "claude mcp add --scope user $($script:BdMcpName) -- `"$Exe`" $($serverArgs -join ' ')"
        $rec.output = "$out".Trim()
    } else {
        $f = Get-BdClaudeUserConfig
        $j = if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
        if (-not $j.PSObject.Properties['mcpServers']) { $j | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) }
        $entry = [pscustomobject]@{ type = 'stdio'; command = $Exe; args = $serverArgs; env = [pscustomobject]@{} }
        if ($j.mcpServers.PSObject.Properties[$script:BdMcpName]) { $j.mcpServers.$($script:BdMcpName) = $entry } else { $j.mcpServers | Add-Member -NotePropertyName $script:BdMcpName -NotePropertyValue $entry }
        [System.IO.File]::WriteAllText($f, ($j | ConvertTo-Json -Depth 64))
        $rec.how = "no claude CLI on this machine: wrote mcpServers.$($script:BdMcpName) in $f"
    }
    [pscustomobject]$rec
}

function Uninstall-BdMcp {
    <# Removes the 'brokennes' user-scope registration (the CLI's 'claude mcp remove --scope user', else the one key in .claude.json). #>
    $ErrorActionPreference = 'Continue'
    $cli = Find-BdClaudeCli
    if ($cli) {
        $out = & $cli mcp remove $script:BdMcpName --scope user 2>&1
        return [pscustomobject]@{ removed = ($LASTEXITCODE -eq 0); how = "claude mcp remove $($script:BdMcpName) --scope user"; output = "$out".Trim() }
    }
    $f = Get-BdClaudeUserConfig
    if (-not (Test-Path -LiteralPath $f)) { return [pscustomobject]@{ removed = $false; how = "no $f" } }
    $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
    if ($j.PSObject.Properties['mcpServers'] -and $j.mcpServers.PSObject.Properties[$script:BdMcpName]) {
        $j.mcpServers.PSObject.Properties.Remove($script:BdMcpName)
        [System.IO.File]::WriteAllText($f, ($j | ConvertTo-Json -Depth 64))
        return [pscustomobject]@{ removed = $true; how = "removed mcpServers.$($script:BdMcpName) from $f" }
    }
    [pscustomobject]@{ removed = $false; how = "no $($script:BdMcpName) entry in $f" }
}

function Get-BdMcpStatus {
    $ErrorActionPreference = 'Continue'
    $cli = Find-BdClaudeCli
    $o = [ordered]@{ built = (Get-BdMcpExe); cli = $cli; registration = $null }
    if ($cli) { $o.registration = ("$(& $cli mcp get $script:BdMcpName 2>&1)").Trim() }
    else {
        $f = Get-BdClaudeUserConfig
        if (Test-Path -LiteralPath $f) { $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json; if ($j.PSObject.Properties['mcpServers'] -and $j.mcpServers.PSObject.Properties[$script:BdMcpName]) { $o.registration = $j.mcpServers.$($script:BdMcpName) } }
    }
    [pscustomobject]$o
}

function Invoke-BdMcpTest {
    <# The scripted MCP clients: the client test and the planted-bug demo (each starts the server over stdio). Returns $true when both pass. #>
    param([string]$Exe = '')
    if (-not $Exe) { $Exe = Get-BdMcpExe }
    if (-not $Exe) { throw 'the MCP server is not built (brokendev.ps1 mcp build)' }
    $ok = $true
    foreach ($suite in '--client-test', '--demo') {
        Write-BdInfo "== $suite"
        & $Exe $suite --repo (Get-BdRepoRoot) | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { $ok = $false }
    }
    $ok
}

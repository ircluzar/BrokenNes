# Great Compatibility Catalog in BrokenDev (P1): 'brokendev.ps1 catalog view|progress|index|stats'.
# The catalog itself is tools\catalog (catalog.py on the share, docs/projects/compat-catalog-db.md); this only starts its view
# (tools\catalog\view.py, a local read-only web UI) and runs its CLI.

$script:BdCatalogPort = 8790

function Get-BdCatalogPython {
    @(@($env:BROKENDEV_PYTHON) + @(Get-BdPythonCandidates)) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

function Get-BdCatalogStateFile { Join-Path (Get-BdHome) 'work\tools\catalog-view\state.json' }

function Test-BdCatalogProbe {
    param([int]$Port)
    try { $r = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/probe" -TimeoutSec 3; return ($r.tool -eq 'brokennes-catalog-view') } catch { return $false }
}

function Invoke-BdCatalog {
    <# Returns an exit code. $Action: view (default) | progress | index | stats. -Stop stops a view BrokenDev started (its recorded PID). #>
    param([string]$Action = 'view', [switch]$NoBrowser, [switch]$Stop, [string]$Root = '')
    $py = Get-BdCatalogPython
    if (-not $py) { throw 'catalog: no Python found (set BROKENDEV_PYTHON)' }
    $dir = Join-Path $script:here 'tools\catalog'
    $rootArgs = if ($Root) { @('--root', $Root) } else { @() }
    if (-not $Action) { $Action = 'view' }
    switch ($Action.ToLowerInvariant()) {
        { $_ -in 'progress', 'index', 'stats' } {
            & $py (Join-Path $dir 'catalog.py') @rootArgs $_ | Out-Host
            return $LASTEXITCODE
        }
        'view' {
            $port = $script:BdCatalogPort
            $url = "http://127.0.0.1:$port/"
            $sf = Get-BdCatalogStateFile
            if ($Stop) {
                if (-not (Test-Path -LiteralPath $sf)) { Write-BdInfo 'catalog view: BrokenDev has no record of starting it'; return 0 }
                $rec = Read-BdJson $sf
                if (Get-Process -Id ([int]$rec.pid) -ErrorAction SilentlyContinue) { Stop-BdProcessTree -ProcessId ([int]$rec.pid); Write-BdOk "catalog view: stopped PID $($rec.pid)" }
                Remove-Item -LiteralPath $sf -Force
                return 0
            }
            if (Test-BdPortOpen -Port $port) {
                if (-not (Test-BdCatalogProbe -Port $port)) { throw "catalog view: port $port is taken by another program (owner PID $(Get-BdPortOwner -Port $port)); BrokenDev never stops a program it did not start" }
                Write-BdOk "catalog view: already running on $url; reused"
            } else {
                $work = Split-Path -Parent $sf
                New-Item -ItemType Directory -Force -Path $work | Out-Null
                $argv = @((Join-Path $dir 'view.py'), '--port', "$port") + $rootArgs
                $p = Start-Process -FilePath $py -ArgumentList (ConvertTo-BdArgString $argv) -WorkingDirectory $dir -WindowStyle Hidden -PassThru `
                    -RedirectStandardOutput (Join-Path $work 'server.log') -RedirectStandardError (Join-Path $work 'server.err.log')
                $deadline = (Get-Date).AddSeconds(20)
                while ((Get-Date) -lt $deadline -and -not $p.HasExited -and -not (Test-BdCatalogProbe -Port $port)) { Start-Sleep -Milliseconds 300 }
                if ($p.HasExited -or -not (Test-BdCatalogProbe -Port $port)) {
                    $err = Get-Content -LiteralPath (Join-Path $work 'server.err.log') -Raw -ErrorAction SilentlyContinue
                    if (-not $p.HasExited) { Stop-BdProcessTree -ProcessId $p.Id }
                    throw "catalog view did not come up on port ${port}: $err"
                }
                Save-BdJson -Object ([ordered]@{ pid = $p.Id; port = $port; url = $url; startedAt = (Get-Date).ToString('o') }) -Path $sf
                Write-BdOk "catalog view: running on $url (PID $($p.Id)); stop it with: brokendev.ps1 catalog view -Stop"
            }
            if ($NoBrowser) { Write-Host $url } else { Start-Process $url | Out-Null }
            return 0
        }
        default { throw "catalog: view | progress | index | stats (got '$Action')" }
    }
}

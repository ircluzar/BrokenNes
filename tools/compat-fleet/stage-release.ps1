# stage-release.ps1 - builds the pinned worker bundle onto the fleet share (runs on THIS PC).
#
#   stage-release.ps1 [-FleetRoot T:\BrokenDev\CompatFleet] [-Repo <BrokenNes checkout>] [-Version <name>] [-Pin] [-SkipEmulators]
#
# Lays out:
#   <FleetRoot>\releases\<version>\workshop\   BrokenNes.Workshop.exe, self-contained win-x64 (no .NET install needed on a box)
#   <FleetRoot>\releases\<version>\mcp\        BrokenNes.Mcp.exe, self-contained win-x64
#   <FleetRoot>\releases\<version>\runner\     this folder (worker.ps1, CompatQueue.psm1, tiers\)
#   <FleetRoot>\releases\<version>\manifest.json   sha256 of every file above + the emulator pins
#   <FleetRoot>\emulators\<name>-<ver>\        Mesen 2.1.1 and BizHawk 2.11.1 copied once from BROKENDEV_HOME (already pinned by sha256 there)
#   <FleetRoot>\install-worker.ps1             the one script a box runs
#   <FleetRoot>\current.json                   with -Pin: the version every install/update takes
# Version defaults to the repo's short commit (+"-dirty" when the tree has changes): a box always knows exactly what it runs.
param(
    [string]$FleetRoot = 'T:\BrokenDev\CompatFleet',
    [string]$Repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path,
    [string]$Version = '',
    [string]$EmulatorHome = 'T:\BrokenDev\env\emulators',
    [switch]$Pin,
    [switch]$SkipEmulators,
    [switch]$SkipBuild          # reuse an already staged build of this version (re-copies the runner + manifest only)
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Version) {
    $Version = (git -C $Repo rev-parse --short=10 HEAD).Trim()
    if ((git -C $Repo status --porcelain --untracked-files=no) ) { $Version += '-dirty' }
}
$rel = Join-Path $FleetRoot "releases\$Version"
New-Item -ItemType Directory -Force -Path $rel | Out-Null
Write-Host "staging $Version -> $rel"

if (-not $SkipBuild) {
    foreach ($p in @(@{ name = 'workshop'; proj = 'Workshop\BrokenNes.Workshop.csproj' }, @{ name = 'mcp'; proj = 'BrokenDev\mcp\BrokenNes.Mcp.csproj' })) {
        $out = Join-Path $rel $p.name
        # one build at a time, low priority: this PC is shared with other agents
        & dotnet publish (Join-Path $Repo $p.proj) -c Release -r win-x64 --self-contained true -o $out -p:DebugType=none -nologo -v:q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "publish of $($p.proj) failed ($LASTEXITCODE)" }
    }
}
$runner = Join-Path $rel 'runner'
New-Item -ItemType Directory -Force -Path $runner | Out-Null
Copy-Item -Recurse -Force (Join-Path $here '*') $runner -Exclude 'stage-release.ps1'
Copy-Item -Force (Join-Path $here 'install-worker.ps1') (Join-Path $FleetRoot 'install-worker.ps1')

$emus = @()
if (-not $SkipEmulators) {
    foreach ($e in 'mesen-2.1.1', 'bizhawk-2.11.1') {
        $src = Join-Path $EmulatorHome $e; $dst = Join-Path $FleetRoot "emulators\$e"
        if (-not (Test-Path $src)) { Write-Warning "$src missing: not staged"; continue }
        if (-not (Test-Path (Join-Path $dst '.staged'))) {
            & robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy $e failed ($LASTEXITCODE)" }
            Set-Content (Join-Path $dst '.staged') (Get-Date -Format o)
        }
        $pinInfo = $null; $pf = Join-Path $src '.brokendev-install.json'; if (Test-Path $pf) { $pinInfo = Get-Content -Raw $pf | ConvertFrom-Json }
        $emus += [ordered]@{ name = $e; path = "emulators\$e"; sha256 = $(if ($pinInfo) { $pinInfo.sha256 } else { '' }); exe = $(if ($pinInfo) { $pinInfo.exe } else { '' }) }
    }
}

$files = foreach ($f in (Get-ChildItem -Recurse -File $rel | Where-Object { $_.Name -ne 'manifest.json' })) {
    [ordered]@{ path = $f.FullName.Substring($rel.Length + 1); bytes = $f.Length; sha256 = (Get-FileHash -Algorithm SHA256 $f.FullName).Hash.ToLowerInvariant() }
}
$manifest = [ordered]@{ schema = 'compat-fleet-release/1'; version = $Version; stagedAt = (Get-Date).ToUniversalTime().ToString('o'); stagedBy = $env:COMPUTERNAME
                        workshop = 'workshop\BrokenNes.Workshop.exe'; mcp = 'mcp\BrokenNes.Mcp.exe'; runner = 'runner\worker.ps1'; emulators = $emus; files = @($files) }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $rel 'manifest.json')
if ($Pin) { @{ version = $Version; pinnedAt = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $FleetRoot 'current.json'); Write-Host "pinned $Version" }
Write-Host ("staged {0}: {1} files, {2:N0} MB" -f $Version, @($files).Count, ((@($files) | ForEach-Object { $_.bytes } | Measure-Object -Sum).Sum / 1MB))

exit 0

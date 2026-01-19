param(
    [ValidateSet("trace","counters","gcdump")][string]$Mode = "trace",
    [string]$OutputDir = "..\artifacts\profiling"
)

$ErrorActionPreference = "Stop"

function Ensure-DotNetTool {
    param([string]$Name)
    $installed = dotnet tool list -g | Select-String -Pattern "^$Name\s"
    if ($installed) {
        Write-Host "Updating $Name ..."
        dotnet tool update --global $Name | Out-Null
    }
    else {
        Write-Host "Installing $Name ..."
        dotnet tool install --global $Name | Out-Null
    }
}

Ensure-DotNetTool "dotnet-trace"
Ensure-DotNetTool "dotnet-counters"
Ensure-DotNetTool "dotnet-gcdump"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "Windows\\BrokenNes.Windows.csproj"

Write-Host "Building WinForms app (Release)..."
dotnet build $projectPath -c Release | Out-Null

$exe = Get-ChildItem -Path (Join-Path $repoRoot "Windows\\bin\\Release") -Filter "BrokenNes.Windows.exe" -Recurse | Select-Object -First 1
if (-not $exe) {
    throw "Could not find BrokenNes.Windows.exe under Windows\\bin\\Release."
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$OutputDir = Join-Path $repoRoot $OutputDir
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

switch ($Mode) {
    "trace" {
        $outFile = Join-Path $OutputDir "BrokenNes-winforms-$timestamp.nettrace"
        Write-Host "Running dotnet-trace (CPU + GC) -> $outFile"
        Write-Host "Play the game for 20-30 seconds, then close the app or press Ctrl+C."
        & dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0x1F000080018:5" --output $outFile -- $exe.FullName
        
        if (Test-Path $outFile) {
            Write-Host "`nConverting to speedscope format..."
            $speedscopeFile = $outFile -replace '\.nettrace$', '.speedscope.json'
            & dotnet-trace convert $outFile --format speedscope --output $speedscopeFile
            Write-Host "Speedscope file: $speedscopeFile (open at https://www.speedscope.app)"
            Write-Host "Original trace: $outFile (open with PerfView or Visual Studio)"
        }
    }
    "counters" {
        Write-Host "Running dotnet-counters (System.Runtime) refresh=1s. Ctrl+C to stop."
        & dotnet-counters monitor --refresh-interval 1 --counters "System.Runtime[gc-heap-size-bytes,gen-2-gc-count,threadpool-thread-count,contention-count]" -- $exe.FullName
    }
    "gcdump" {
        Write-Host "Launching app, collecting GC dump after 5s..."
        $proc = Start-Process -FilePath $exe.FullName -PassThru
        try {
            Start-Sleep -Seconds 5
            $dumpFile = Join-Path $OutputDir "BrokenNes-winforms-$timestamp.gcdump"
            & dotnet-gcdump collect -p $proc.Id -o $dumpFile
            Write-Host "GC dump saved to $dumpFile"
        }
        finally {
            if (-not $proc.HasExited) { $proc | Stop-Process }
        }
    }
}

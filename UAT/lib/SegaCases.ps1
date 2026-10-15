<#
  The Master System / Game Gear / SG-1000 case kind of UAT/run-suite.ps1 ('sega-run'), dot-sourced by it.

  A case runs the console layer exactly as the apps do: `BrokenNes.Workshop.exe --console-run --rom <rom> --frames N --input <pad script>` builds the session through
  ConsoleSessions.Create (the board session wrapped by the picture-chip bridge), with the cores the case names (cpu / ppu / apu, default: the console's own),
  optionally hot-swaps cores mid-run (`swap`: "frame:slot:id,..."), and prints the picture hash, size, distinct colours, audio length and peak. The ROM may be a loose
  file or a .7z of the Master System library ("archive.7z" takes the first ROM, "archive.7z#text" the first whose name contains the text; 7-Zip is needed).
  The honesty fields beside the hash are the same ones the NES cases keep: distinctColors says the picture is a picture, audioPeak that the game made sound.

  Programs nobody owns (the hand-assembled SG-1000 programs and the tone-and-scroll program of the hot-swap suite) are written by the Workshop itself into the
  manifest's 'gen' root before the run, so no ROM of any kind lives in the repository.
#>

function Resolve-SegaPath {
    param([string]$Path)
    [Environment]::ExpandEnvironmentVariables($Path)
}

function Initialize-SegaGeneratedPrograms {
    # Writes the generated programs once per run into the 'gen' root of every manifest that has one and uses it.
    param($Manifests)
    foreach ($m in $Manifests) {
        $roots = Get-Prop $m.Doc 'roots' $null
        $gen = Get-Prop $roots 'gen' $null
        if (-not $gen) { continue }
        $dir = Resolve-SegaPath ([string]$gen)
        if (-not [System.IO.Path]::IsPathRooted($dir)) { $dir = Join-Path $RepoRoot ($dir -replace '/', '\') }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        foreach ($flag in @('--write-sg-programs', '--write-swap-programs')) {
            $r = Invoke-Workshop -Arguments @('--smstest', $flag, $dir) -TimeoutMs 60000
            if ($r.ExitCode -ne 0) { Write-Host "  could not generate programs ($flag): exit $($r.ExitCode)" -ForegroundColor Yellow }
        }
    }
}

function Invoke-SegaRunCase {
    param($Case, [string]$RomPath)
    $a = @('--console-run', '--rom', $RomPath)
    foreach ($k in @('cpu', 'ppu', 'apu')) {
        $v = Get-Prop $Case $k $null
        if ($v) { $a += @("--$k", [string]$v) }
    }
    $a += @('--frames', "$([int](Get-Prop $Case 'frames' 600))", '--png-at', '0')   # no picture files (the run's default would drop a PNG into the current folder)
    $inp = Get-Prop $Case 'input' $null
    if ($inp) { $a += @('--input', [string]$inp) }
    $swap = Get-Prop $Case 'swap' $null
    if ($swap) { $a += @('--swap', [string]$swap) }

    $r = Invoke-Workshop -Arguments $a -TimeoutMs ($TimeoutSeconds * 1000)
    # the summary line:  frames=900 last=256x192 display-width=256 audio=1,470,000 stereo samples @ 44100 Hz battery=False hash=0123456789ABCDEF 150 fps colors=12 peak=3277
    $line = ($r.StdOut -split "`r?`n" | Where-Object { $_ -match '^frames=\d+ ' } | Select-Object -Last 1)
    $hash = ''; $size = ''; $colors = -1; $peak = -1; $battery = ''
    if ($line -match 'last=(\d+x\d+)') { $size = $Matches[1] }
    if ($line -match 'hash=([0-9A-F]{16})') { $hash = $Matches[1] }
    if ($line -match 'colors=(\d+)') { $colors = [int]$Matches[1] }
    if ($line -match 'peak=(\d+)') { $peak = [int]$Matches[1] }
    if ($line -match 'battery=(\w+)') { $battery = $Matches[1] }
    # a swap the session could not do in place is reported, not hidden: the case then fails against its golden
    $refused = @($r.StdOut -split "`r?`n" | Where-Object { $_ -match 'NOT in place' }).Count
    [pscustomobject]@{
        Raw      = $r
        Observed = [pscustomobject]@{
            exitCode        = $r.ExitCode
            frameBufferHash = $hash
            size            = $size
            distinctColors  = $colors
            audioPeak       = $peak
            battery         = $battery
            swapsRefused    = $refused
        }
        Display  = "exit={0} {1} {2} colours={3} peak={4}" -f $r.ExitCode, $(if ($hash) { $hash } else { '(no hash)' }), $size, $colors, $peak
    }
}

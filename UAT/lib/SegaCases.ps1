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

function Read-SegaSummary {
    # the summary line:  frames=900 last=256x192 display-width=256 audio=1,470,000 stereo samples @ 44100 Hz battery=False hash=0123456789ABCDEF 150 fps colors=12 peak=3277
    param($Run)
    $line = ($Run.StdOut -split "`r?`n" | Where-Object { $_ -match '^frames=\d+ ' } | Select-Object -Last 1)
    $o = [ordered]@{ hash = ''; size = ''; colors = -1; peak = -1; battery = '' }
    if ($line -match 'last=(\d+x\d+)') { $o.size = $Matches[1] }
    if ($line -match 'hash=([0-9A-F]{16})') { $o.hash = $Matches[1] }
    if ($line -match 'colors=(\d+)') { $o.colors = [int]$Matches[1] }
    if ($line -match 'peak=(\d+)') { $o.peak = [int]$Matches[1] }
    if ($line -match 'battery=(\w+)') { $o.battery = $Matches[1] }
    [pscustomobject]$o
}

function Invoke-SegaRoundTrip {
    # A case with a "roundtrip": { "load_frames": N, "load_input": "script" } is a battery save made in the game and read back by it:
    #   pass 1  the case's own script plays the game up to the point where it has saved (an inn, a menu, an editor's SAVE) and the session's save is written to a file;
    #   pass 2  a NEW session starts from that file and the load script walks the game's own CONTINUE / LOAD to the saved place;
    #   control the same load script with NO save file: the game must not end up on the same picture (it is the proof that the save file, not the script, made the picture).
    # The save's size, non-zero bytes and SHA1 and the three picture hashes are recorded.
    param($Case, [string]$RomPath, $Rt, [string[]]$Common)
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ('bn-uat-sega-rt-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        $sav = Join-Path $dir 'save.sav'
        $p1 = Invoke-Workshop -Arguments ($Common + @('--frames', "$([int](Get-Prop $Case 'frames' 600))", '--input', [string](Get-Prop $Case 'input' ''), '--battery-out', $sav)) -TimeoutMs ($TimeoutSeconds * 1000)
        $loadArgs = @('--frames', "$([int]$Rt.load_frames)", '--input', [string]$Rt.load_input)
        $p2 = $null; $p3 = $null
        $saveBytes = -1; $saveNonZero = -1; $saveSha1 = ''
        if (Test-Path -LiteralPath $sav) {
            $b = [System.IO.File]::ReadAllBytes($sav)
            $saveBytes = $b.Length; $saveNonZero = @($b | Where-Object { $_ -ne 0 }).Count
            $saveSha1 = (Get-FileHash -LiteralPath $sav -Algorithm SHA1).Hash.Substring(0, 12)
            $p2 = Invoke-Workshop -Arguments ($Common + $loadArgs + @('--battery', $sav)) -TimeoutMs ($TimeoutSeconds * 1000)
        }
        $p3 = Invoke-Workshop -Arguments ($Common + $loadArgs) -TimeoutMs ($TimeoutSeconds * 1000)
        [pscustomobject]@{ P1 = $p1; P2 = $p2; P3 = $p3; SaveBytes = $saveBytes; SaveNonZero = $saveNonZero; SaveSha1 = $saveSha1 }
    }
    finally { Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue }
}

function Invoke-SegaRunCase {
    param($Case, [string]$RomPath)
    $a = @('--console-run', '--rom', $RomPath)
    foreach ($k in @('cpu', 'ppu', 'apu')) {
        $v = Get-Prop $Case $k $null
        if ($v) { $a += @("--$k", [string]$v) }
    }
    $rt = Get-Prop $Case 'roundtrip' $null
    if ($rt) {
        $common = $a + @('--png-at', '0')
        $t = Invoke-SegaRoundTrip -Case $Case -RomPath $RomPath -Rt $rt -Common $common
        $s1 = Read-SegaSummary $t.P1
        $s2 = if ($t.P2) { Read-SegaSummary $t.P2 } else { [pscustomobject]@{ hash = ''; colors = -1; size = '' } }
        $s3 = Read-SegaSummary $t.P3
        # the pass-1 result stays the case's Raw (its exit code and time); the other two passes' time is added to it
        $ms = 0; foreach ($x in @($t.P1, $t.P2, $t.P3)) { if ($x) { $ms += $x.Ms } }
        $t.P1.Ms = $ms
        $ok = ($t.P1.ExitCode -eq 0) -and $t.P2 -and ($t.P2.ExitCode -eq 0) -and ($t.P3.ExitCode -eq 0)
        $differs = [bool]($s2.hash -and $s3.hash -and ($s2.hash -ne $s3.hash))
        return [pscustomobject]@{
            Raw      = $t.P1
            Observed = [pscustomobject]@{
                exitCode          = $(if ($ok) { 0 } else { 1 })
                frameBufferHash   = $s1.hash
                size              = $s1.size
                distinctColors    = $s1.colors
                saveBytes         = $t.SaveBytes
                saveNonZeroBytes  = $t.SaveNonZero
                saveSha1          = $t.SaveSha1
                loadedHash        = $s2.hash
                loadedColors      = $s2.colors
                noSaveHash        = $s3.hash
                loadDiffersFromNoSave = $differs
            }
            Display  = "save {0} bytes ({1} non-zero) {2}; saved run {3}; restart from the save {4}; restart without it {5}; differ={6}" -f $t.SaveBytes, $t.SaveNonZero, $t.SaveSha1, $s1.hash, $s2.hash, $s3.hash, $differs
        }
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

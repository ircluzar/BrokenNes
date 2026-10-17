# The gate of the NES differential replay (Mesen trace vs BrokenNes trace). Dot-source; works in Windows PowerShell 5.1 and 7.
#
# The two emulators write the same trace format, one record per frame (UAT\diff-trace.ps1 reads it; so does this). The gate reads both
# traces itself and decides per column:
#
#   picture and PPU control columns (ntbhash oamhash palhash chrhash fbhash ctrl mask stat t fx ...): must agree on every frame after the
#                                  boot window. No allowance.
#   APU register columns (p1per ... flg):  must agree after the boot window, EXCEPT isolated single-frame blips: a frame where the column
#                                  differs but agrees on the frame before and on the frame after. Those are listed, counted, and
#                                  tolerated up to a budget, because the two emulators read the APU at slightly different instants
#                                  of a frame (Mesen's APU state is brought up to date lazily, see the header of state_trace.lua) and
#                                  a game that writes its sound registers inside the NMI that the sample point straddles shows the
#                                  old value on one side for exactly one frame. A real defect does not stay one frame: it persists, or
#                                  recurs in a run, and then the gate fails. A blip budget that runs out fails too.
#   sample-skew columns (pc a x y sp p ramhash v w): an instruction-boundary or mid-write-sequence phase the two emulators cannot align;
#                                  information only (counted, never failed). v and w are the PPU address and its write toggle: if the
#                                  sample falls between the two $2006 writes of a vblank routine they read half-updated.
#
# Exit decisions are made by the caller from the returned object. (No Set-StrictMode here: the caller's mode stays as it is; the script tests
# run it under strict mode.)

$script:BdSkewColumns = @('pc', 'a', 'x', 'y', 'sp', 'p', 'ramhash', 'v', 'w')
$script:BdApuColumns = @('p1per', 'p1vol', 'p1len', 'p1dut', 'p1swp', 'p2per', 'p2vol', 'p2len', 'p2dut', 'p2swp', 'tper', 'tlen', 'tlin',
    'nper', 'nvol', 'nlen', 'nmod', 'dout', 'dcur', 'drem', 'fcs', 'st', 'en', 'flg')

function Read-BdTraceColumns {
    param([Parameter(Mandatory)][string]$Path)
    $names = $null; $rows = New-Object System.Collections.Generic.List[string[]]
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        if ($line.StartsWith('# columns:')) { $names = ($line.Substring(10).Trim()) -split '\|'; continue }
        if ($line.StartsWith('#') -or $line.Length -eq 0) { continue }
        $rows.Add($line -split '\|')
    }
    if (-not $names) { throw "trace $Path has no '# columns:' header (is it a trace file?)" }
    [pscustomobject]@{ Names = $names; Rows = $rows }
}

function Get-BdTraceGate {
    param(
        [Parameter(Mandatory)][string]$MesenTrace,
        [Parameter(Mandatory)][string]$BrokenNesTrace,
        [int]$BootFrames = 10,
        [int]$BlipLimit = -1     # distinct frames that may carry an isolated APU blip; -1 = max(5, 1% of the frames)
    )
    $ta = Read-BdTraceColumns $MesenTrace; $tb = Read-BdTraceColumns $BrokenNesTrace
    $idxA = @{}; for ($i = 0; $i -lt $ta.Names.Count; $i++) { $idxA[$ta.Names[$i]] = $i }
    $idxB = @{}; for ($i = 0; $i -lt $tb.Names.Count; $i++) { $idxB[$tb.Names[$i]] = $i }
    $n = [math]::Min($ta.Rows.Count, $tb.Rows.Count)
    if ($BlipLimit -lt 0) { $BlipLimit = [math]::Max(5, [int][math]::Floor($n * 0.01)) }

    $div = [ordered]@{}   # column -> frames that differ (only where both sides measured it)
    foreach ($c in $ta.Names) {
        if ($c -eq 'frame' -or -not $idxB.ContainsKey($c)) { continue }
        $ia = $idxA[$c]; $ib = $idxB[$c]; $list = New-Object System.Collections.Generic.List[int]
        for ($f = 0; $f -lt $n; $f++) {
            $x = $ta.Rows[$f][$ia]; $y = $tb.Rows[$f][$ib]
            if ($x -ne '-' -and $y -ne '-' -and $x -ne $y) { $list.Add($f) }
        }
        $div[$c] = $list
    }

    $gate = New-Object System.Collections.Generic.List[string]
    $boot = New-Object System.Collections.Generic.List[string]
    $blips = [ordered]@{}                                            # column -> isolated blip frames
    $blipFrames = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($c in $div.Keys) {
        if ($script:BdSkewColumns -contains $c) { continue }
        $late = @($div[$c] | Where-Object { $_ -ge $BootFrames }); $early = @($div[$c] | Where-Object { $_ -lt $BootFrames })
        if ($early.Count -gt 0) { $boot.Add("$c@" + ($early -join ',')) }
        if ($late.Count -eq 0) { continue }
        if ($script:BdApuColumns -contains $c) {
            $set = New-Object 'System.Collections.Generic.HashSet[int]'; foreach ($f in $div[$c]) { [void]$set.Add($f) }
            $isolated = New-Object System.Collections.Generic.List[int]; $hard = New-Object System.Collections.Generic.List[int]
            foreach ($f in $late) {
                if ($f -ge 1 -and $f -lt ($n - 1) -and -not $set.Contains($f - 1) -and -not $set.Contains($f + 1)) { $isolated.Add($f) } else { $hard.Add($f) }
            }
            if ($hard.Count -gt 0) { $gate.Add("$c differs on $($hard.Count) frame(s) that are not isolated blips, after the first $BootFrames (first at frame $($hard[0]))") }
            if ($isolated.Count -gt 0) { $blips[$c] = @($isolated); foreach ($f in $isolated) { [void]$blipFrames.Add($f) } }
        } else {
            $gate.Add("$c differs on $($late.Count) frame(s) after the first $BootFrames (first at frame $($late[0]))")
        }
    }
    if ($blipFrames.Count -gt $BlipLimit) { $gate.Add("$($blipFrames.Count) frames carry isolated APU blips, more than the budget of $BlipLimit") }
    if ($n -lt 10) { $gate.Add("only $n frames could be compared") }
    $census = [ordered]@{}; foreach ($c in $div.Keys) { $census[$c] = $div[$c].Count }
    $compared = @($div.Keys | Where-Object { $script:BdSkewColumns -notcontains $_ })
    [pscustomobject]@{
        Frames = $n; Census = $census; BootSkew = @($boot); GateFailures = @($gate); Blips = $blips; BlipFrames = $blipFrames.Count; BlipLimit = $BlipLimit
        ComparedColumns = $compared.Count; Pass = ($gate.Count -eq 0)
    }
}

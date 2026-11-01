<#
.SYNOPSIS
    Differential replay differ: aligns two frame-state traces and reports the first divergence.

.DESCRIPTION
    Consumes the shared cross-emulator trace format produced by
      - Mesen side     : sys0/simulations/VRUN/tools/state_trace.lua
      - BrokenNes side : Workshop --trace  (Workshop/TraceCli.cs)

    Format (UTF-8, LF; '#' lines are header/comment and ignored as data):

        <frame>|<pc>|<a>|<x>|<y>|<sp>|<p>|<ramhash>[|PPU...][|APU...][|audio...]

    THE COLUMN LIST IS NOT HARDCODED HERE. Each trace declares its own in a
    "# columns: frame|pc|..." header line, and this script compares the
    INTERSECTION of the two. That is what lets the CPU, PPU, APU and audio
    column blocks be turned on and off independently, and lets two traces
    produced by different revisions of the tools still diff over whatever they
    share. A trace with no columns line is read as the original 8-field v1.

    A column carrying "-" on either side is NOT compared for that frame: it
    means that emulator could not measure it, which is not a disagreement. The
    audio fingerprint columns are the standing example - Mesen 2.1.1's headless
    Lua API exposes no audio samples, so only BrokenNes can fill them. The
    verdict block names every column that went uncompared for this reason, so
    "IDENTICAL" never quietly means "we did not look".

    The framebuffer is deliberately not part of the record: NES palettes are not
    standardized, so two correct emulators legitimately produce different RGB.
    RAM + CPU state is the emulator-independent oracle.

    Divergence is reported per column AND rolled up per subsystem (cpu, work
    RAM, ppu, apu pulse1/pulse2/triangle/noise/dmc/frame-counter/$4015, audio
    pcm), because on a 40-column trace the first question is which chip
    disagrees, not which of forty columns.

    Designed to be usable as a CI gate: exit code 0 = identical over the compared
    range, 1 = divergence found, 2 = usage/parse error.

    Because the overwhelmingly most common cause of a "divergence" is a HARNESS
    artifact rather than an emulation defect, this differ also:
      * compares the two headers' rom-sha256 and power-on RAM hash and shouts if
        they disagree (a different power-on fill makes every frame differ for a
        reason that has nothing to do with emulation);
      * runs a frame-shift probe (-ProbeShift) that tests whether side B's trace
        matches side A shifted by +/-N frames, which is what an off-by-one frame
        boundary or an input applied a frame early looks like.

.PARAMETER A
    First trace file (conventionally the reference / Mesen side).

.PARAMETER B
    Second trace file (conventionally BrokenNes).

.PARAMETER Context
    How many frames of context to print before the first divergence. Default 5.

.PARAMETER Follow
    How many frames after the first divergence to print, used to classify the
    divergence as persistent or a one-frame blip. Default 12.

.PARAMETER ProbeShift
    Max +/- frame shift to test when looking for a constant frame-alignment
    offset between the two traces. 0 disables. Default 3.

.PARAMETER LabelA / .PARAMETER LabelB
    Display names for the two sides. Default: the file base names.

.PARAMETER Quiet
    Suppress the header/determinism section; print only the verdict and the
    divergence report.

.PARAMETER RamDumpA / .PARAMETER RamDumpB
    Directories holding raw dumps as produced by `--trace --ram-dump-at N,...`
    (BrokenNes) and VRUN_TRACE_RAMDUMP=N,... (Mesen). When both are given, the
    differ stops guessing from a hash and reports the actual differing
    ADDRESSES around the first divergence. This matters more than it sounds:
    the ramhash column is an all-or-nothing comparison of 2048 bytes taken at
    an instant two different emulators cannot align to better than a few CPU
    cycles, so on a ROM that touches RAM every frame it goes to 0% agreement
    while the states are in fact identical bar two or three in-flight scratch
    bytes. Differing BYTE COUNT, not hash equality, is the oracle that
    survives that.

    Both tracers write, at the same instant and into the same directory:
      ram_f<N>.bin    2048   work RAM
      ciram_f<N>.bin  2048   CIRAM / both nametables, raw, pre-mirroring
      oam_f<N>.bin     256   the PPU's own OAM, not the CPU-side shadow
      pal_f<N>.bin      32   palette RAM as INDICES, canonicalized
      chr_f<N>.bin       *   all of CHR RAM/ROM
      fb_f<N>.bin    61440   the rendered frame as NES palette indices, 0-63
                             per pixel, row major - NOT RGB, so it is
                             comparable between emulators with different
                             palettes
    Each is narrowed to the thing on screen it controls: nametable tile column
    and row (or the attribute byte and the 4x4 tile block it colours), sprite
    index and field, background/sprite palette and colour slot, CHR tile and
    bitplane row, and for the frame a differing-pixel count, bounding box and
    index-substitution census.

.PARAMETER RamMap
    A markdown RAM map (VRUN's docs/23-ram-map.md) used to put names on those
    addresses. Rows look like `| $0344 | 2 | mus_sq1_wait | ... |`. Names are
    printed with a health warning: a map measured against one build does not
    necessarily apply to the ROM under test.

.EXAMPLE
    ./diff-trace.ps1 mesen.txt brokennes.txt
    ./diff-trace.ps1 -A mesen.txt -B bn.txt -Context 10 -Follow 30
    ./diff-trace.ps1 -A ms.txt -B bn.txt -RamDumpA ramA -RamDumpB ramB `
                     -RamMap ../../sys0/simulations/VRUN/docs/23-ram-map.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$A,
    [Parameter(Mandatory = $true, Position = 1)][string]$B,
    [int]$Context = 5,
    [int]$Follow = 12,
    [int]$ProbeShift = 3,
    [string]$LabelA,
    [string]$LabelB,
    [switch]$Quiet,
    [string]$RamDumpA,
    [string]$RamDumpB,
    [string]$RamMap
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Field names in record order, after the leading frame number.
#
# This is only the FALLBACK. The column set is read from each trace's own
# "# columns: frame|pc|..." header line, so a tracer that grows new columns (the PPU
# block, the APU block, the audio fingerprint) needs no change here at all - and two
# traces produced by tools at different revisions still diff over whatever they share.
# A trace with no columns header is assumed to be v1 and gets this list.
$DEFAULT_FIELDS = @('pc', 'a', 'x', 'y', 'sp', 'p', 'ramhash')

# A column whose value is "-" means "this side could not produce it" (e.g. the audio
# fingerprint columns, which only BrokenNes can fill because Mesen 2.1.1's headless Lua
# API exposes no audio samples). Those are skipped per-frame rather than counted as a
# divergence - a tool that cannot measure something has not disagreed about it.
$ABSENT = '-'

# Columns that are hashes, not small integers. The constant-offset probe is meaningless
# for them (an xor of two hashes is noise), so it skips them.
$HASH_FIELDS = @('ramhash', 'ntbhash', 'oamhash', 'palhash', 'chrhash', 'fbhash')

# Which subsystem a column belongs to, for the grouped summary. Anything unlisted is
# reported under "other" rather than being silently dropped.
$FIELD_GROUP = @{
    pc = 'cpu'; a = 'cpu'; x = 'cpu'; y = 'cpu'; sp = 'cpu'; p = 'cpu'
    ramhash = 'work RAM'
    ntbhash = 'ppu'; oamhash = 'ppu'; palhash = 'ppu'; ctrl = 'ppu'; mask = 'ppu'; stat = 'ppu'
    v = 'ppu'; t = 'ppu'; fx = 'ppu'; w = 'ppu'; chrhash = 'ppu'; fbhash = 'ppu'
    p1per = 'apu pulse1'; p1vol = 'apu pulse1'; p1len = 'apu pulse1'; p1dut = 'apu pulse1'; p1swp = 'apu pulse1'
    p2per = 'apu pulse2'; p2vol = 'apu pulse2'; p2len = 'apu pulse2'; p2dut = 'apu pulse2'; p2swp = 'apu pulse2'
    tper = 'apu triangle'; tlen = 'apu triangle'; tlin = 'apu triangle'
    nper = 'apu noise'; nvol = 'apu noise'; nlen = 'apu noise'; nmod = 'apu noise'
    dout = 'apu dmc'; dcur = 'apu dmc'; drem = 'apu dmc'
    fcs = 'apu frame-counter'; st = 'apu $4015'; en = 'apu $4015'; flg = 'apu $4015'
    asmp = 'audio pcm'; arms = 'audio pcm'; apk = 'audio pcm'; azc = 'audio pcm'
}

# What each column actually is, printed beside a divergence so the reader does not have
# to go and find the tracer's header to know what broke.
$FIELD_DESC = @{
    p1per = 'pulse1 11-bit period register (the NOTE)'
    p1vol = 'pulse1 volume in force'; p1len = 'pulse1 length counter'
    p1dut = 'pulse1 duty select'; p1swp = 'pulse1 sweep enable/negate/shift'
    p2per = 'pulse2 11-bit period register (the NOTE)'
    p2vol = 'pulse2 volume in force'; p2len = 'pulse2 length counter'
    p2dut = 'pulse2 duty select'; p2swp = 'pulse2 sweep enable/negate/shift'
    tper = 'triangle 11-bit period register'; tlen = 'triangle length counter'
    tlin = 'triangle linear counter'
    nper = 'noise period in CPU cycles'; nvol = 'noise volume in force'
    nlen = 'noise length counter'; nmod = 'noise short-mode flag'
    dout = 'DMC output level 0..7f'; dcur = 'DMC current sample pointer'
    drem = 'DMC bytes remaining'
    fcs = 'frame sequencer step'
    st = 'synthesized $4015 (bits 0-4 lengths/DMC, 6 frame IRQ)'
    en = 'length enables + frame mode/inhibit + DMC IRQ enable'
    flg = 'length halts + constant-volume flags + DMC loop'
    asmp = 'PCM samples produced this frame'; arms = 'PCM RMS x 65535'
    apk = 'PCM peak x 65535'; azc = 'PCM zero crossings'
}

function Fail([string]$msg) {
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 2
}

function Read-Trace([string]$path, [string]$label) {
    if (-not (Test-Path -LiteralPath $path)) { Fail "trace file not found: $path" }
    $lines = [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $path))

    $header = New-Object System.Collections.Generic.List[string]
    $records = @{}                       # frame -> record object
    $order = New-Object System.Collections.Generic.List[int]
    $bad = New-Object System.Collections.Generic.List[string]
    $fatal = $null

    # Populated from the trace's own header the moment the columns line is seen, which is
    # always before the first record. Both tracers emit one; a trace without one predates
    # the header-driven format and is read as v1.
    $cols = $null

    $lineNo = 0
    foreach ($raw in $lines) {
        $lineNo++
        $line = $raw.TrimEnd("`r")       # tolerate a CRLF trace rather than silently mis-diffing it
        if ($line.Length -eq 0) { continue }
        if ($line[0] -eq '#') {
            $h = $line.Substring(1).Trim()
            $header.Add($h)
            if ($line -match 'FATAL|ABORTED|CRASHED') { $fatal = $line }
            # "columns: frame|pc|..." is the authoritative, machine-readable list and wins.
            # "format: <frame>|<pc>|..." is the human-readable prose line and is only a
            # fallback: on the Mesen side it is written per-branch and can predate columns
            # appended later in the header.
            $m = [regex]::Match($h, '^(columns|format):\s*(.+)$')
            if ($m.Success) {
                $kind = $m.Groups[1].Value
                if ($kind -eq 'columns' -or -not $cols) {
                    $spec = $m.Groups[2].Value.Trim() -replace '[<>]', ''
                    $parts = $spec.Split('|') | ForEach-Object { $_.Trim() }
                    if ($parts.Count -ge 2 -and $parts[0] -eq 'frame') {
                        if ($kind -eq 'columns' -or -not $cols) { $cols = @($parts[1..($parts.Count - 1)]) }
                    }
                }
            }
            continue
        }
        if (-not $cols) { $cols = $DEFAULT_FIELDS }

        $parts = $line.Split('|')
        if ($parts.Count -ne ($cols.Count + 1)) {
            $bad.Add("${label}:${lineNo}: header declares $($cols.Count + 1) fields (frame|$($cols -join '|')), got $($parts.Count): '$line'")
            continue
        }
        [int]$fr = 0
        if (-not [int]::TryParse($parts[0], [ref]$fr)) {
            $bad.Add("${label}:${lineNo}: frame '$($parts[0])' is not an integer")
            continue
        }
        if ($records.ContainsKey($fr)) {
            $bad.Add("${label}:${lineNo}: duplicate record for frame $fr")
            continue
        }
        $f = @{}
        for ($i = 0; $i -lt $cols.Count; $i++) { $f[$cols[$i]] = $parts[$i + 1].ToLowerInvariant() }
        $records[$fr] = [pscustomobject]@{
            frame  = $fr
            Fields = $f
            raw    = $line
        }
        $order.Add($fr)
    }
    if (-not $cols) { $cols = $DEFAULT_FIELDS }

    [pscustomobject]@{
        Path    = (Resolve-Path -LiteralPath $path).Path
        Label   = $label
        Header  = $header
        Columns = @($cols)
        Records = $records
        Frames  = $order
        Bad     = $bad
        Fatal   = $fatal
    }
}

# Pull "key: value" out of either tracer's header. The two tracers word their
# headers differently, so match on a regex over the whole header block.
function Get-HeaderValue($trace, [string]$pattern) {
    foreach ($h in $trace.Header) {
        $m = [regex]::Match($h, $pattern)
        if ($m.Success) { return $m.Groups[1].Value.Trim() }
    }
    return $null
}

function Format-Record($rec) {
    if ($null -eq $rec) { return '(absent)' }
    return $rec.raw
}

# Load a markdown RAM map into address -> name. Rows: | `$0344` | 2 | `mus_sq1_wait` | ... |
function Read-RamMap([string]$path) {
    $map = @{}
    if (-not $path) { return $map }
    if (-not (Test-Path -LiteralPath $path)) { Fail "RAM map not found: $path" }
    foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $path))) {
        $m = [regex]::Match($line, '^\s*\|\s*`?\$([0-9A-Fa-f]{4})`?\s*\|\s*(\d+)\s*\|\s*`?([^`|]+?)`?\s*\|')
        if (-not $m.Success) { continue }
        $addr = [Convert]::ToInt32($m.Groups[1].Value, 16)
        $size = [int]$m.Groups[2].Value
        $name = $m.Groups[3].Value.Trim()
        for ($k = 0; $k -lt [Math]::Max(1, $size); $k++) {
            if (-not $map.ContainsKey($addr + $k)) {
                $map[$addr + $k] = if ($size -gt 1) { "$name+$k" } else { $name }
            }
        }
    }
    return $map
}

function Get-RamDump([string]$dir, [int]$frame) {
    if (-not $dir) { return $null }
    $p = Join-Path $dir "ram_f$frame.bin"
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return [System.IO.File]::ReadAllBytes($p)
}

# The PPU dumps written alongside ram_f<N>.bin by both tracers at the same instant:
# ciram_f<N>.bin (2048), oam_f<N>.bin (256), pal_f<N>.bin (32), chr_f<N>.bin, and
# fb_f<N>.bin (61440 NES palette indices, one byte per pixel, row major).
function Get-PpuDump([string]$dir, [string]$kind, [int]$frame) {
    if (-not $dir) { return $null }
    $p = Join-Path $dir "${kind}_f$frame.bin"
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    return [System.IO.File]::ReadAllBytes($p)
}

# --- decoders: turn a raw offset into the thing on screen it controls -----------------
#
# This is the whole point of narrowing a PPU divergence. "ntbhash moved" is not
# actionable; "CIRAM page A, tile column 12 row 5" is - it is a place on the screen the
# owner can look at, and "attribute byte covering tiles (24,10)-(27,13)" tells them the
# colours of a 4x4 tile block moved rather than the tiles themselves.

function Describe-CiramOffset([int]$o) {
    # [int] casts throughout, not [Math]::Floor alone: Floor returns a Double, and .NET's
    # composite formatter rejects the hex specifier on a Double ("Format specifier was
    # invalid"), which turns a narrowing report into an exception at the worst moment.
    $page = [char](65 + [int][Math]::Floor($o / 0x400))   # A or B - the two physical 1KB halves
    $inner = $o % 0x400
    if ($inner -lt 0x3C0) {
        return ("CIRAM page {0}, nametable tile col {1,2} row {2,2}" -f $page, ($inner % 32), [int][Math]::Floor($inner / 32))
    }
    $ai = $inner - 0x3C0
    $ac = $ai % 8; $ar = [int][Math]::Floor($ai / 8)
    return ("CIRAM page {0}, ATTRIBUTE byte {1} - palette select for tiles ({2},{3})-({4},{5})" -f `
            $page, $ai, ($ac * 4), ($ar * 4), ($ac * 4 + 3), ($ar * 4 + 3))
}

function Describe-OamOffset([int]$o) {
    $field = @('Y (top-1)', 'tile index', 'attributes (palette/flip/priority)', 'X')[$o % 4]
    return ("sprite {0,2} {1}" -f [int][Math]::Floor($o / 4), $field)
}

function Describe-PaletteOffset([int]$o) {
    if ($o -eq 0) { return 'universal backdrop ($3f00)' }
    if ($o -lt 0x10) {
        $p = [int][Math]::Floor($o / 4); $c = $o % 4
        if ($c -eq 0) { return ("background palette $p, backdrop mirror (never rendered from)") }
        return ("BACKGROUND palette $p, colour $c")
    }
    $p = [int][Math]::Floor(($o - 0x10) / 4); $c = $o % 4
    if ($c -eq 0) { return ("sprite palette $p, backdrop mirror (never rendered from)") }
    return ("SPRITE palette $p, colour $c")
}

function Describe-ChrOffset([int]$o) {
    $tile = [int][Math]::Floor($o / 16)
    $plane = if (($o % 16) -lt 8) { 'plane 0 (low bit)' } else { 'plane 1 (high bit)' }
    # A CHR-RAM game bakes tiles at runtime, so naming the 1KB bank as well as the tile
    # says which bank-switch or which decompressor wrote the byte that moved.
    return ("CHR tile 0x{0:x3} ({0}) = 1KB bank {1} tile {2}, {3}, row {4}" -f `
            $tile, [int][Math]::Floor($o / 1024), ($tile % 64), $plane, ($o % 8))
}

# Standing NES address-space landmarks, used when no symbol covers an address.
function Get-RamRegion([int]$addr) {
    if ($addr -lt 0x100) { return 'zero page' }
    if ($addr -lt 0x200) { return 'HARDWARE STACK - bytes below SP are dead, expect noise here' }
    return 'work RAM'
}

function Compare-Record($ra, $rb) {
    # Returns the list of field names that differ, in record order. A field one side
    # emitted as '-' is not a disagreement: that side could not measure it.
    $d = @()
    foreach ($f in $script:FIELDS) {
        $va = $ra.Fields[$f]; $vb = $rb.Fields[$f]
        if ($va -eq $ABSENT -or $vb -eq $ABSENT) { continue }
        if ($va -ne $vb) { $d += $f }
    }
    return , $d
}

# ---------------------------------------------------------------- load

if (-not $LabelA) { $LabelA = [System.IO.Path]::GetFileNameWithoutExtension($A) }
if (-not $LabelB) { $LabelB = [System.IO.Path]::GetFileNameWithoutExtension($B) }
$w = [Math]::Max($LabelA.Length, $LabelB.Length)
$padA = $LabelA.PadRight($w)
$padB = $LabelB.PadRight($w)

$ta = Read-Trace $A $LabelA
$tb = Read-Trace $B $LabelB

if ($ta.Bad.Count -gt 0 -or $tb.Bad.Count -gt 0) {
    Write-Host "Malformed trace lines:" -ForegroundColor Red
    foreach ($m in $ta.Bad) { Write-Host "  $m" }
    foreach ($m in $tb.Bad) { Write-Host "  $m" }
    exit 2
}
if ($ta.Records.Count -eq 0) { Fail "$LabelA has no records ($($ta.Path))$(if($ta.Fatal){" - $($ta.Fatal)"})" }
if ($tb.Records.Count -eq 0) { Fail "$LabelB has no records ($($tb.Path))$(if($tb.Fatal){" - $($tb.Fatal)"})" }

# Contract check: frames must start at 0 and step by exactly 1.
foreach ($t in @($ta, $tb)) {
    for ($i = 0; $i -lt $t.Frames.Count; $i++) {
        if ($t.Frames[$i] -ne $i) {
            Fail "$($t.Label): frame numbering broken - record #$i carries frame $($t.Frames[$i]); the contract is 0-based and +1 per record"
        }
    }
}

$nA = $ta.Frames.Count
$nB = $tb.Frames.Count
$common = [Math]::Min($nA, $nB)

# Compare the INTERSECTION of the two column sets, in side A's order. Anything only one
# side emits is named explicitly below rather than quietly ignored - "not compared" and
# "compared and equal" are very different claims and the verdict must not blur them.
$FIELDS = @($ta.Columns | Where-Object { $tb.Columns -contains $_ })
$onlyA = @($ta.Columns | Where-Object { $tb.Columns -notcontains $_ })
$onlyB = @($tb.Columns | Where-Object { $ta.Columns -notcontains $_ })
if ($FIELDS.Count -eq 0) { Fail "the two traces share no columns ($($ta.Columns -join ',') vs $($tb.Columns -join ','))" }

# A column both sides declare but one of them fills with '-' on every frame is compared
# by name only; say so, because it looks identical to a genuine all-match otherwise.
$neverComparable = @()
foreach ($fn in $FIELDS) {
    $seen = $false
    for ($i = 0; $i -lt $common; $i++) {
        if ($ta.Records[$i].Fields[$fn] -ne $ABSENT -and $tb.Records[$i].Fields[$fn] -ne $ABSENT) { $seen = $true; break }
    }
    if (-not $seen) { $neverComparable += $fn }
}

# ---------------------------------------------------------------- header / determinism

if (-not $Quiet) {
    Write-Host "=== traces ===" -ForegroundColor Cyan
    Write-Host ("  {0} : {1}" -f $padA, $ta.Path)
    Write-Host ("  {0} : {1}" -f $padB, $tb.Path)
    Write-Host ("  {0} : {1} record(s), frames 0..{2}" -f $padA, $nA, ($nA - 1))
    Write-Host ("  {0} : {1} record(s), frames 0..{2}" -f $padB, $nB, ($nB - 1))
    if ($nA -ne $nB) {
        Write-Host ("  NOTE: differing lengths; comparing the common range 0..{0}" -f ($common - 1)) -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "=== columns ===" -ForegroundColor Cyan
    Write-Host ("  compared ({0}) : {1}" -f $FIELDS.Count, ($FIELDS -join ' '))
    if ($onlyA.Count -gt 0) {
        Write-Host ("  NOT compared   : {0} - only {1} emits {2}" -f ($onlyA -join ' '), $padA.Trim(), ($onlyA.Count -eq 1 ? 'it' : 'them')) -ForegroundColor Yellow
    }
    if ($onlyB.Count -gt 0) {
        Write-Host ("  NOT compared   : {0} - only {1} emits {2}" -f ($onlyB -join ' '), $padB.Trim(), ($onlyB.Count -eq 1 ? 'it' : 'them')) -ForegroundColor Yellow
    }
    if ($neverComparable.Count -gt 0) {
        Write-Host ("  NOT compared   : {0} - declared by both but one side emits '-' on every frame" -f ($neverComparable -join ' ')) -ForegroundColor Yellow
        Write-Host  "                   (the audio fingerprint columns are the expected case: Mesen 2.1.1's" -ForegroundColor DarkGray
        Write-Host  "                   headless Lua API exposes no audio samples, so only BrokenNes fills them.)" -ForegroundColor DarkGray
    }
    if ($onlyA.Count -eq 0 -and $onlyB.Count -eq 0 -and $neverComparable.Count -eq 0) {
        Write-Host "  every declared column is compared on both sides." -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "=== determinism cross-check (header, not data) ===" -ForegroundColor Cyan

    $checks = @(
        @{ name = 'rom-sha256';        pat = '^rom-sha256:\s*(.+)$' },
        @{ name = 'power-on ram hash'; pat = '(?:power-on-ramhash:|post-fill pre-boot RAM sha256\s*=)\s*([0-9a-fA-F]+)' },
        # The palette counterpart. A mismatch here makes EVERY pixel differ until the game
        # writes its own palette, which reads exactly like a rendering defect and is not one.
        @{ name = 'power-on palette';  pat = '^power-on-palhash:\s*([0-9a-fA-F]+)' },
        # Both tracers recover palette indices by inverting their own RGB table. That is only
        # sound while the two tables collide the SAME way; if they ever stop, fbhash silently
        # starts comparing two different quantities, so the classes are compared here by name.
        @{ name = 'fb palette classes'; pat = '^fb-palette-collisions:\s*(\{.+)$' },
        @{ name = 'input-script';      pat = '^input-script:\s*(.+)$' }
    )
    foreach ($c in $checks) {
        $va = Get-HeaderValue $ta $c.pat
        $vb = Get-HeaderValue $tb $c.pat
        if ($null -eq $va -and $null -eq $vb) {
            Write-Host ("  {0,-18} : (absent from both headers)" -f $c.name) -ForegroundColor DarkGray
            continue
        }
        # Power-on hashes are emitted at different truncations by the two tracers
        # (16 hex chars vs the full 64), so compare on the shorter prefix.
        $ca = $va; $cb = $vb
        if ($ca -and $cb -and $ca.Length -ne $cb.Length) {
            $len = [Math]::Min($ca.Length, $cb.Length)
            $ca = $ca.Substring(0, $len); $cb = $cb.Substring(0, $len)
        }
        if ($ca -eq $cb) {
            Write-Host ("  {0,-18} : MATCH  {1}" -f $c.name, $va) -ForegroundColor Green
        }
        else {
            Write-Host ("  {0,-18} : ***MISMATCH***" -f $c.name) -ForegroundColor Red
            Write-Host ("      {0} : {1}" -f $padA, $va)
            Write-Host ("      {0} : {1}" -f $padB, $vb)
            if ($c.name -eq 'power-on ram hash') {
                Write-Host "      A different power-on RAM fill makes every downstream frame differ for a" -ForegroundColor Red
                Write-Host "      reason that is NOT an emulation defect. Align the fills before reading" -ForegroundColor Red
                Write-Host "      anything into the divergence below." -ForegroundColor Red
            }
            if ($c.name -eq 'rom-sha256') {
                Write-Host "      The two sides ran DIFFERENT ROMs. Nothing below is meaningful." -ForegroundColor Red
            }
            if ($c.name -eq 'power-on palette') {
                Write-Host "      Palette RAM powers on indeterminate on real hardware, so neither fill is" -ForegroundColor Red
                Write-Host "      'right' - but until they match, EVERY pixel differs on every frame before the" -ForegroundColor Red
                Write-Host "      game writes its own palette, and palhash/fbhash below mean nothing." -ForegroundColor Red
                Write-Host "      Align them: BrokenNes --power-on-palette zeros / Mesen VRUN_TRACE_PALFILL=zeros" -ForegroundColor Red
            }
            if ($c.name -eq 'fb palette classes') {
                Write-Host "      The two emulators' 64-entry RGB tables no longer collide identically, so" -ForegroundColor Red
                Write-Host "      'lowest index wins' resolves the same pixel to different indices on the two" -ForegroundColor Red
                Write-Host "      sides. fbhash is comparing two different quantities - do not read a" -ForegroundColor Red
                Write-Host "      difference in it as a rendering defect until this line matches again." -ForegroundColor Red
            }
        }
    }
    if ($ta.Fatal) { Write-Host ("  {0}: {1}" -f $padA, $ta.Fatal) -ForegroundColor Red }
    if ($tb.Fatal) { Write-Host ("  {0}: {1}" -f $padB, $tb.Fatal) -ForegroundColor Red }
    Write-Host ""
}

# ---------------------------------------------------------------- compare

$firstDiv = -1
$firstFields = @()
for ($f = 0; $f -lt $common; $f++) {
    $d = Compare-Record $ta.Records[$f] $tb.Records[$f]
    if ($d.Count -gt 0) { $firstDiv = $f; $firstFields = $d; break }
}

# Per-field divergence census over the whole common range - tells you whether
# only the RAM hash drifts (state difference) or the CPU registers go too
# (control-flow difference).
$fieldCounts = @{}
foreach ($fn in $FIELDS) { $fieldCounts[$fn] = 0 }
$divFrames = 0
for ($f = 0; $f -lt $common; $f++) {
    $d = Compare-Record $ta.Records[$f] $tb.Records[$f]
    if ($d.Count -gt 0) {
        $divFrames++
        foreach ($fn in $d) { $fieldCounts[$fn]++ }
    }
}

# ---------------------------------------------------------------- frame-shift probe

$shiftReport = @()
if ($ProbeShift -gt 0) {
    foreach ($s in (-$ProbeShift..$ProbeShift)) {
        # side B frame (f) compared against side A frame (f + s)
        $lo = [Math]::Max(0, -$s)
        $hi = [Math]::Min($nB - 1, $nA - 1 - $s)
        if ($hi -lt $lo) { continue }
        $match = 0
        $firstBad = -1
        for ($f = $lo; $f -le $hi; $f++) {
            $d = Compare-Record $ta.Records[$f + $s] $tb.Records[$f]
            if ($d.Count -eq 0) { $match++ }
            elseif ($firstBad -lt 0) { $firstBad = $f }
        }
        $total = $hi - $lo + 1
        $shiftReport += [pscustomobject]@{
            Shift    = $s
            Matched  = $match
            Total    = $total
            Pct      = [Math]::Round(100.0 * $match / $total, 2)
            FirstBad = $firstBad
        }
    }
}

# ---------------------------------------------------------------- report

if ($firstDiv -lt 0) {
    Write-Host "=== VERDICT: IDENTICAL ===" -ForegroundColor Green
    Write-Host ("  {0} frame(s) compared (0..{1}); every field matched on every frame." -f $common, ($common - 1))
    Write-Host ("  Fields compared: {0}" -f ($FIELDS -join ', '))
    if ($onlyA.Count -gt 0 -or $onlyB.Count -gt 0 -or $neverComparable.Count -gt 0) {
        Write-Host ("  NOT compared   : {0}" -f (($onlyA + $onlyB + $neverComparable | Select-Object -Unique) -join ', ')) -ForegroundColor Yellow
        Write-Host  "  'IDENTICAL' therefore means identical over the compared columns only." -ForegroundColor Yellow
    }
    if ($nA -ne $nB) {
        Write-Host ("  (lengths differed: {0}={1}, {2}={3}; the tail beyond frame {4} was not compared)" -f $padA, $nA, $padB, $nB, ($common - 1)) -ForegroundColor Yellow
    }
    exit 0
}

# Not every column is worth the same as evidence, and reporting one number over all of them
# produces a headline that contradicts the truth underneath it. Two emulators cannot be aligned
# below a single instruction boundary, so at the sample instant the CPU registers legitimately
# sit mid-instruction apart - and `ramhash` is all-or-nothing across 2048 bytes taken at that
# same unalignable instant, which is exactly why the methodology says to gate on differing BYTE
# COUNT from the RAM dumps and never on the hash. A run whose settled state matches everywhere
# still scores ~100% "divergent" if those columns are counted, which is how a report came to
# carry "DIVERGENCE - 99.78% of frames" directly above a byte-narrowing that read 0 of 2048.
#
# So the headline leads with the settled-state columns - PPU memories, register state, APU state,
# the rendered frame - which ARE meaningful frame-to-frame, and reports the skew-prone columns
# separately instead of letting them set the verdict.
$SKEW_PRONE = @('pc','a','x','y','sp','p','ramhash')
$settledFields = @($FIELDS | Where-Object { $SKEW_PRONE -notcontains $_ -and $neverComparable -notcontains $_ })
$settledDivFrames = 0
$settledFirst = -1
for ($f = 0; $f -lt $common; $f++) {
    # Compare-Record deliberately returns ", $d" so a single-field difference stays an array;
    # enumerate it explicitly rather than piping, which would hand Where-Object the array itself.
    $diffs = Compare-Record $ta.Records[$f] $tb.Records[$f]
    $hit = $false
    foreach ($dn in $diffs) { if ($settledFields -contains $dn) { $hit = $true; break } }
    if ($hit) { $settledDivFrames++; if ($settledFirst -lt 0) { $settledFirst = $f } }
}

if ($settledDivFrames -eq 0) {
    Write-Host "=== VERDICT: SETTLED STATE IDENTICAL ===" -ForegroundColor Green
    Write-Host ("  Every settled-state column agrees on all {0} compared frame(s)." -f $common) -ForegroundColor Green
    Write-Host  "  PPU memories, PPU/APU register state and the rendered frame never differed."
}
else {
    Write-Host "=== VERDICT: DIVERGENCE ===" -ForegroundColor Red
    Write-Host ("  settled-state divergence : {0} of {1} frame(s) ({2}%)" -f $settledDivFrames, $common, ([Math]::Round(100.0 * $settledDivFrames / $common, 2))) -ForegroundColor Red
    Write-Host ("  first such frame         : {0}" -f $settledFirst)
}
Write-Host ""
Write-Host "  --- all columns, including the skew-prone ones -----------------------------" -ForegroundColor DarkGray
Write-Host ("  first divergent frame : {0}" -f $firstDiv)
Write-Host ("  field(s) that broke   : {0}" -f ($firstFields -join ', '))
Write-Host ("  frames matched before : {0} (frames 0..{1})" -f $firstDiv, ($firstDiv - 1))
Write-Host ("  frames compared       : {0}" -f $common)
Write-Host ("  divergent frames      : {0} of {1} ({2}%)" -f $divFrames, $common, ([Math]::Round(100.0 * $divFrames / $common, 2)))
Write-Host ("  NOTE: that figure counts {0}, which differ from sample skew alone." -f ($SKEW_PRONE -join ', ')) -ForegroundColor Yellow
Write-Host  "        Do not quote it as a divergence rate. For work RAM, dump both sides and" -ForegroundColor Yellow
Write-Host  "        compare BYTE COUNTS (-RamDumpA/-RamDumpB); the hash cannot settle it." -ForegroundColor Yellow
Write-Host ""

# Persistent vs blip: look at the run immediately after the first divergence.
$after = [Math]::Min($common - 1, $firstDiv + $Follow)
$runLen = 0
for ($f = $firstDiv; $f -le $after; $f++) {
    if ((Compare-Record $ta.Records[$f] $tb.Records[$f]).Count -gt 0) { $runLen++ } else { break }
}
$reconverged = ($firstDiv + $runLen) -le $after
if ($reconverged) {
    Write-Host ("  classification        : BLIP - diverges for {0} frame(s) then re-converges at frame {1}" -f $runLen, ($firstDiv + $runLen)) -ForegroundColor Yellow
}
else {
    Write-Host ("  classification        : PERSISTENT - still divergent {0} frame(s) later (through frame {1})" -f $runLen, $after) -ForegroundColor Red
}

Write-Host ""
Write-Host "  per-field divergence census over the full compared range:"
$lastGroup = $null
foreach ($fn in $FIELDS) {
    $g = if ($FIELD_GROUP.ContainsKey($fn)) { $FIELD_GROUP[$fn] } else { 'other' }
    if ($g -ne $lastGroup) { Write-Host ("    -- {0}" -f $g) -ForegroundColor DarkGray; $lastGroup = $g }
    $c = $fieldCounts[$fn]
    $bar = if ($c -gt 0) { '*' } else { ' ' }
    $desc = if ($c -gt 0 -and $FIELD_DESC.ContainsKey($fn)) { "  " + $FIELD_DESC[$fn] } else { '' }
    $col = if ($c -gt 0) { 'Red' } else { 'Gray' }
    Write-Host ("    {0} {1,-8} {2,6} frame(s){3}" -f $bar, $fn, $c, $desc) -ForegroundColor $col
}

# Divergence rolled up per subsystem. On a trace with 40+ columns the flat census above is
# too wide to read at a glance, and the first question is always "which chip disagrees",
# not "which of forty columns". A group that never diverges is worth seeing too - it is the
# only evidence that the column was compared and agreed.
Write-Host ""
Write-Host "  divergence by subsystem (frames on which ANY of that group's columns differed):"
$groupFrames = @{}
foreach ($fn in $FIELDS) {
    $g = if ($FIELD_GROUP.ContainsKey($fn)) { $FIELD_GROUP[$fn] } else { 'other' }
    if (-not $groupFrames.ContainsKey($g)) { $groupFrames[$g] = 0 }
}
for ($f = 0; $f -lt $common; $f++) {
    $d = Compare-Record $ta.Records[$f] $tb.Records[$f]
    if ($d.Count -eq 0) { continue }
    $hit = @{}
    foreach ($fn in $d) {
        $g = if ($FIELD_GROUP.ContainsKey($fn)) { $FIELD_GROUP[$fn] } else { 'other' }
        $hit[$g] = $true
    }
    foreach ($g in $hit.Keys) { $groupFrames[$g]++ }
}
foreach ($g in ($groupFrames.Keys | Sort-Object)) {
    $c = $groupFrames[$g]
    $col = if ($c -eq 0) { 'Green' } elseif ($c -lt ($common / 10)) { 'Yellow' } else { 'Red' }
    Write-Host ("    {0,-20} {1,6} / {2} frame(s)" -f $g, $c, $common) -ForegroundColor $col
}

# ---------------------------------------------------------------- constant-offset probe
#
# A register column that differs on EVERY frame by the SAME xor/delta is a representation
# difference between the two tracers, not an emulation difference. The one that actually
# bites here: Mesen's emu.getState() 'cpu.ps' reports the 6502's unused bit 5 clear, while
# BrokenNes keeps it set as hardware does - so p differs on 100% of frames, forever, for no
# emulation reason at all. Left undiagnosed that alone makes every frame look divergent.
$constant = @()
foreach ($fn in $FIELDS) {
    if ($HASH_FIELDS -contains $fn) { continue }      # xor of two hashes is noise
    if ($fieldCounts[$fn] -ne $common) { continue }   # must differ on every single frame
    $xors = @{}; $deltas = @{}
    $parsed = $true
    for ($f = 0; $f -lt $common; $f++) {
        $sa = $ta.Records[$f].Fields[$fn]; $sb = $tb.Records[$f].Fields[$fn]
        if ($sa -eq $ABSENT -or $sb -eq $ABSENT) { $parsed = $false; break }
        try {
            $va = [Convert]::ToInt32($sa, 16)
            $vb = [Convert]::ToInt32($sb, 16)
        }
        catch { $parsed = $false; break }
        $xors[($va -bxor $vb)] = $true
        $deltas[($vb - $va)] = $true
        if ($xors.Count -gt 1 -and $deltas.Count -gt 1) { break }
    }
    if (-not $parsed) { continue }
    if ($xors.Count -eq 1) {
        $constant += [pscustomobject]@{ Field = $fn; Kind = 'xor'; Value = ($xors.Keys | Select-Object -First 1) }
    }
    elseif ($deltas.Count -eq 1) {
        $constant += [pscustomobject]@{ Field = $fn; Kind = 'delta'; Value = ($deltas.Keys | Select-Object -First 1) }
    }
}
if ($constant.Count -gt 0) {
    Write-Host ""
    Write-Host "  *** CONSTANT-OFFSET FIELDS - almost certainly a tracer representation bug ***" -ForegroundColor Yellow
    foreach ($c in $constant) {
        if ($c.Kind -eq 'xor') {
            Write-Host ("    {0}: differs on all {1} frames, always by xor 0x{2:x2}" -f $c.Field, $common, $c.Value) -ForegroundColor Yellow
            if ($c.Field -eq 'p' -and $c.Value -eq 0x20) {
                Write-Host "      That is bit 5, the 6502's unused/always-set flag. One side is masking it off." -ForegroundColor Yellow
                Write-Host "      Fix the tracer (force it set on both sides); do not read this as an emulation bug." -ForegroundColor Yellow
            }
        }
        else {
            Write-Host ("    {0}: differs on all {1} frames, always by {2:+#;-#;0}" -f $c.Field, $common, $c.Value) -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "=== records around the first divergence ===" -ForegroundColor Cyan
$from = [Math]::Max(0, $firstDiv - $Context)
$to = [Math]::Min($common - 1, $firstDiv + $Follow)
for ($f = $from; $f -le $to; $f++) {
    $ra = $ta.Records[$f]
    $rb = $tb.Records[$f]
    $d = Compare-Record $ra $rb
    $mark = if ($f -eq $firstDiv) { '>>' } elseif ($d.Count -gt 0) { ' !' } else { '  ' }
    $color = if ($d.Count -gt 0) { 'Red' } else { 'DarkGray' }
    Write-Host ("{0} {1} {2}" -f $mark, $padA, (Format-Record $ra)) -ForegroundColor $color
    Write-Host ("{0} {1} {2}" -f $mark, $padB, (Format-Record $rb)) -ForegroundColor $color
    if ($d.Count -gt 0) {
        Write-Host ("   {0} differs: {1}" -f (' ' * $w), ($d -join ', ')) -ForegroundColor Yellow
    }
    Write-Host ""
}

# ---------------------------------------------------------------- harness-artifact probe

if ($shiftReport.Count -gt 0) {
    Write-Host "=== frame-alignment probe (harness-artifact check) ===" -ForegroundColor Cyan
    Write-Host ("  Tests whether {0} frame f equals {1} frame f+shift. A non-zero shift scoring far" -f $padB, $padA)
    Write-Host  "  better than shift 0 means the two harnesses disagree about where a frame ends or"
    Write-Host  "  when input is applied - a harness bug, not an emulation bug."
    foreach ($r in ($shiftReport | Sort-Object -Property Shift)) {
        $tag = if ($r.Shift -eq 0) { ' (aligned)' } else { '' }
        $hl = if ($r.Shift -ne 0 -and $r.Pct -gt 50) { 'Yellow' } else { 'Gray' }
        $sgn = if ($r.Shift -gt 0) { "+$($r.Shift)" } else { "$($r.Shift)" }
        Write-Host ("    shift {0,3} : {1,7} / {2,-7} matched ({3,6}%) first mismatch at frame {4}{5}" -f `
                $sgn, $r.Matched, $r.Total, $r.Pct, $r.FirstBad, $tag) -ForegroundColor $hl
    }
    $best = $shiftReport | Sort-Object -Property @{Expression = 'Matched'; Descending = $true } | Select-Object -First 1
    if ($best.Shift -ne 0 -and $best.Matched -gt 0) {
        Write-Host ("  >> best shift is {0}, not 0. Suspect a frame-boundary off-by-one in one tracer." -f $best.Shift) -ForegroundColor Yellow
    }
    else {
        Write-Host "  >> shift 0 is the best alignment; the divergence is not a whole-frame offset." -ForegroundColor Gray
    }

    # Per-COLUMN version of the same probe, for the columns whose value is a snapshot of
    # something the two tracers grab at slightly different moments. fbhash is the one that
    # earns this: the rendered frame is captured at Mesen's endFrame and at BrokenNes'
    # RunFrame() return, and if those ever pick different frames the column is off by one
    # while every other column is aligned - which a whole-record probe cannot see, because
    # the record never matches at any shift. A column whose best shift is not 0 is a
    # capture-point bug in one tracer, not an emulation difference.
    $perCol = @()
    foreach ($fn in $FIELDS) {
        if ($fieldCounts[$fn] -eq 0) { continue }         # already agrees everywhere
        $bestS = 0; $bestM = -1
        foreach ($s in (-$ProbeShift..$ProbeShift)) {
            $lo = [Math]::Max(0, -$s); $hi = [Math]::Min($nB - 1, $nA - 1 - $s)
            if ($hi -lt $lo) { continue }
            $m = 0
            for ($f = $lo; $f -le $hi; $f++) {
                $va = $ta.Records[$f + $s].Fields[$fn]; $vb = $tb.Records[$f].Fields[$fn]
                if ($va -eq $ABSENT -or $vb -eq $ABSENT) { continue }
                if ($va -eq $vb) { $m++ }
            }
            if ($m -gt $bestM) { $bestM = $m; $bestS = $s }
        }
        if ($bestS -ne 0 -and $bestM -gt 0) {
            $perCol += [pscustomobject]@{ Field = $fn; Shift = $bestS; Matched = $bestM }
        }
    }
    if ($perCol.Count -gt 0) {
        Write-Host "  >> per-column probe: these columns align better at a NON-ZERO shift, i.e. one" -ForegroundColor Yellow
        Write-Host "     tracer captures them a whole frame earlier or later than the other:" -ForegroundColor Yellow
        foreach ($c in $perCol) {
            Write-Host ("       {0,-8} best shift {1,3} ({2} frame(s) matched)" -f $c.Field, $c.Shift, $c.Matched) -ForegroundColor Yellow
        }
        Write-Host "     For fbhash that is the framebuffer capture point (VRUN_TRACE_FB_AT). Measured on" -ForegroundColor Yellow
        Write-Host "     game.nes over 900 frames of gameplay: endframe scores 899/900 at shift 0, while" -ForegroundColor Yellow
        Write-Host "     record scores 580/900 at shift 0 and 847/899 at shift +1 - Mesen's screen buffer" -ForegroundColor Yellow
        Write-Host "     at the record point still holds the PREVIOUS frame. endframe is the default and" -ForegroundColor Yellow
        Write-Host "     the correct one; a +1 here means the trace was taken with FB_AT=record." -ForegroundColor Yellow
    }
    Write-Host ""
}

# ---------------------------------------------------------------- byte-level narrowing

if ($RamDumpA -and $RamDumpB) {
    Write-Host "=== byte-level RAM narrowing ===" -ForegroundColor Cyan
    $names = Read-RamMap $RamMap
    if ($RamMap) {
        Write-Host ("  symbol names from {0} ({1} addresses covered)" -f $RamMap, $names.Count) -ForegroundColor DarkGray
        Write-Host "  CAUTION: a RAM map is measured against one specific build. Verify any symbol you" -ForegroundColor DarkGray
        Write-Host "  are about to act on against the ROM actually under test before believing it." -ForegroundColor DarkGray
    }
    $any = $false
    for ($f = [Math]::Max(0, $firstDiv - 1); $f -le $to; $f++) {
        $da = Get-RamDump $RamDumpA $f
        $db = Get-RamDump $RamDumpB $f
        if ($null -eq $da -or $null -eq $db) { continue }
        $any = $true
        $diffs = @()
        for ($i = 0; $i -lt [Math]::Min($da.Length, $db.Length); $i++) {
            if ($da[$i] -ne $db[$i]) { $diffs += $i }
        }
        $color = if ($diffs.Count -eq 0) { 'Green' } elseif ($diffs.Count -le 16) { 'Yellow' } else { 'Red' }
        Write-Host ("  frame {0}: {1} of {2} byte(s) differ" -f $f, $diffs.Count, $da.Length) -ForegroundColor $color
        foreach ($i in ($diffs | Select-Object -First 40)) {
            $nm = if ($names.ContainsKey($i)) { $names[$i] } else { "(unnamed - " + (Get-RamRegion $i) + ")" }
            Write-Host ("      `${0:x4}  {1} = {2:x2}   {3} = {4:x2}   {5}" -f `
                    $i, $padA, $da[$i], $padB, $db[$i], $nm)
        }
        if ($diffs.Count -gt 40) { Write-Host ("      ... and {0} more" -f ($diffs.Count - 40)) }
    }
    if (-not $any) {
        Write-Host "  no ram_f<N>.bin dumps found for any frame in the reported window." -ForegroundColor Yellow
        Write-Host "  Re-run both tracers with dumps at the frames named above:" -ForegroundColor Yellow
        Write-Host ("    BrokenNes: --trace ... --ram-dump-at {0},{1} --ram-dump-dir <dir>" -f ($firstDiv - 1), $firstDiv)
        Write-Host ("    Mesen:     VRUN_TRACE_RAMDUMP={0},{1} VRUN_TRACE_RAMDUMP_DIR=<dir>" -f ($firstDiv - 1), $firstDiv)
    }
    else {
        Write-Host "  A handful of differing bytes confined to the stack page, unallocated zero page, or" -ForegroundColor DarkGray
        Write-Host "  a counter that is off by one is the signature of the two emulators being SAMPLED a" -ForegroundColor DarkGray
        Write-Host "  few CPU cycles apart, not of the game state having forked. A fork shows tens to" -ForegroundColor DarkGray
        Write-Host "  hundreds of differing bytes and grows frame over frame." -ForegroundColor DarkGray
    }
    Write-Host ""

    # ------------------------------------------------------------ PPU narrowing
    #
    # Same idea as above, one level up: a moved ntbhash/oamhash/palhash/chrhash/fbhash
    # says only THAT the picture's inputs differ. These dumps say WHICH nametable cell,
    # WHICH sprite, WHICH palette entry, WHICH CHR tile and WHICH pixels - which is the
    # difference between a hash the owner cannot act on and a place on the screen to look.
    #
    # Unlike work RAM, these should be CLEAN. CIRAM, OAM, palette and CHR are written
    # during vblank and are long settled by the sample instant, so there is no
    # "sampled a few cycles apart" excuse for them: any differing byte here is real.
    $ppuKinds = @(
        @{ Kind = 'ciram'; Title = 'CIRAM / nametables'; Decode = ${function:Describe-CiramOffset} },
        @{ Kind = 'oam';   Title = 'OAM (sprites)';      Decode = ${function:Describe-OamOffset} },
        @{ Kind = 'pal';   Title = 'palette RAM';        Decode = ${function:Describe-PaletteOffset} },
        @{ Kind = 'chr';   Title = 'CHR (tile bitmaps)'; Decode = ${function:Describe-ChrOffset} }
    )

    $ppuAny = $false
    foreach ($f in ([Math]::Max(0, $firstDiv - 1)..$to)) {
        foreach ($k in $ppuKinds) {
            $da = Get-PpuDump $RamDumpA $k.Kind $f
            $db = Get-PpuDump $RamDumpB $k.Kind $f
            if ($null -eq $da -or $null -eq $db) { continue }
            if (-not $ppuAny) {
                Write-Host "=== byte-level PPU narrowing ===" -ForegroundColor Cyan
                Write-Host "  CIRAM/OAM/palette/CHR are written during vblank and are settled by the sample" -ForegroundColor DarkGray
                Write-Host "  instant, so unlike work RAM they have no sample-skew excuse: a differing byte" -ForegroundColor DarkGray
                Write-Host "  here is a real difference in what would be drawn." -ForegroundColor DarkGray
                $ppuAny = $true
            }
            if ($da.Length -ne $db.Length) {
                Write-Host ("  frame {0} {1}: SIZE MISMATCH {2} vs {3} bytes - not comparable" -f $f, $k.Title, $da.Length, $db.Length) -ForegroundColor Red
                continue
            }
            $diffs = @()
            for ($i = 0; $i -lt $da.Length; $i++) { if ($da[$i] -ne $db[$i]) { $diffs += $i } }
            $color = if ($diffs.Count -eq 0) { 'Green' } elseif ($diffs.Count -le 16) { 'Yellow' } else { 'Red' }
            Write-Host ("  frame {0} {1}: {2} of {3} byte(s) differ" -f $f, $k.Title, $diffs.Count, $da.Length) -ForegroundColor $color
            foreach ($i in ($diffs | Select-Object -First 24)) {
                Write-Host ("      +0x{0:x4}  {1} = {2:x2}   {3} = {4:x2}   {5}" -f `
                        $i, $padA, $da[$i], $padB, $db[$i], (& $k.Decode $i))
            }
            if ($diffs.Count -gt 24) { Write-Host ("      ... and {0} more" -f ($diffs.Count - 24)) }
        }

        # The framebuffer gets its own treatment: 61440 bytes of palette indices, where a
        # list of offsets is useless and a bounding box plus a per-index census is not.
        $fa = Get-PpuDump $RamDumpA 'fb' $f
        $fb = Get-PpuDump $RamDumpB 'fb' $f
        if ($null -ne $fa -and $null -ne $fb -and $fa.Length -eq $fb.Length) {
            if (-not $ppuAny) { Write-Host "=== byte-level PPU narrowing ===" -ForegroundColor Cyan; $ppuAny = $true }
            $n = 0; $minX = 256; $maxX = -1; $minY = 240; $maxY = -1
            $pairs = @{}
            $first = @()
            for ($i = 0; $i -lt $fa.Length; $i++) {
                if ($fa[$i] -eq $fb[$i]) { continue }
                $n++
                $x = $i % 256; $y = [Math]::Floor($i / 256)
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
                $key = "{0:x2}->{1:x2}" -f $fa[$i], $fb[$i]
                $pairs[$key] = 1 + ($(if ($pairs.ContainsKey($key)) { $pairs[$key] } else { 0 }))
                if ($first.Count -lt 8) { $first += [pscustomobject]@{ X = $x; Y = $y; A = $fa[$i]; B = $fb[$i] } }
            }
            $color = if ($n -eq 0) { 'Green' } elseif ($n -le 256) { 'Yellow' } else { 'Red' }
            Write-Host ("  frame {0} rendered frame (palette indices): {1} of {2} pixel(s) differ ({3}%)" -f `
                    $f, $n, $fa.Length, [Math]::Round(100.0 * $n / $fa.Length, 3)) -ForegroundColor $color
            if ($n -gt 0) {
                Write-Host ("      bounding box: x {0}..{1}  y {2}..{3}   ({4} tile col(s) x {5} tile row(s))" -f `
                        $minX, $maxX, $minY, $maxY, ([Math]::Floor($maxX / 8) - [Math]::Floor($minX / 8) + 1), ([Math]::Floor($maxY / 8) - [Math]::Floor($minY / 8) + 1))
                foreach ($p in $first) {
                    Write-Host ("      pixel ({0,3},{1,3})  {2} = index {3:x2}   {4} = index {5:x2}" -f `
                            $p.X, $p.Y, $padA, $p.A, $padB, $p.B)
                }
                Write-Host "      index substitutions (A->B, most common first):"
                foreach ($kv in ($pairs.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 8)) {
                    Write-Host ("        {0}  x{1}" -f $kv.Key, $kv.Value)
                }
                Write-Host "      A whole-screen difference with one or two substitutions is a palette-RAM or" -ForegroundColor DarkGray
                Write-Host "      power-on-palette difference, not a rendering one - check the palette rows above" -ForegroundColor DarkGray
                Write-Host "      and the two headers' power-on-palhash before reading it as a PPU defect." -ForegroundColor DarkGray
            }
        }
    }
    if ($ppuAny) { Write-Host "" }
    elseif ($any) {
        Write-Host "  (no ciram/oam/pal/chr/fb dumps in these directories - both tracers write them" -ForegroundColor DarkGray
        Write-Host "   alongside ram_f<N>.bin when PPU state is enabled, which is the default.)" -ForegroundColor DarkGray
        Write-Host ""
    }
}
else {
    Write-Host "Next steps: dump full RAM and PPU state from both sides at frames $($firstDiv - 1) and $firstDiv and diff" -ForegroundColor Cyan
    Write-Host "byte-by-byte, then re-run this with -RamDumpA/-RamDumpB/-RamMap. The same two flags also" -ForegroundColor Cyan
    Write-Host "pick up the ciram/oam/pal/chr/fb dumps and name the differing nametable cell, sprite," -ForegroundColor Cyan
    Write-Host "palette entry, CHR tile and screen pixels." -ForegroundColor Cyan
    Write-Host ("  BrokenNes: --trace ... --ram-dump-at {0},{1} --ram-dump-dir <dir>" -f ($firstDiv - 1), $firstDiv) -ForegroundColor Cyan
    Write-Host ("  Mesen:     VRUN_TRACE_RAMDUMP={0},{1} VRUN_TRACE_RAMDUMP_DIR=<dir>" -f ($firstDiv - 1), $firstDiv) -ForegroundColor Cyan
}

exit 1

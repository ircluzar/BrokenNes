# BrokenDev compatibility runs: one game, several emulators, the same input. Requires Common, Tools, Archive, Manifest, Build, Verify,
# Emulators.
#
# The adapter contract (every emulator, BrokenNes included, speaks it)
#
#   SPEC (written by BrokenDev, read by the adapter; UTF-8 text, one directive per line, '#' starts a comment)
#     console nes|snes
#     rom <path>                    the ROM (a copy in the run's scratch folder: nothing is ever written beside the built ROM)
#     out <dir>                     where result.txt and the pictures go
#     savedir <dir>                 where the emulator's battery save must live (a second run with the same folder is a fresh boot that finds it)
#     frames <N>                    stop after N completed frames
#     read <name> <domain> <hex>    a byte to record every frame. domain ram = the NES 2 KB internal RAM ($0000-$07FF);
#                                   wram = the SNES 128 KB work RAM ($00000-$1FFFF, bank $7E:0000 = 0)
#     input <frame> <buttons>       from frame index <frame> (0-based: held while the (frame+1)-th frame runs) until the next input line.
#                                   '-' = nothing; names joined with '+': NES A B Select Start Up Down Left Right; SNES B Y Select Start Up Down
#                                   Left Right A X L R
#     shot <frame>                  after <frame> completed frames, save the picture as shot_<frame, 5 digits>.png (or .bmp) in out
#
#   RESULT (<out>\result.txt, written by the adapter)
#     # bdcompat-result 1
#     emu <text>                    what ran (emulator, version, core)
#     reads <name> <name> ...       the spec's reads, in order (may be empty: an emulator that cannot read memory says so in a note)
#     t <frame> <v> <v> ...         one line per completed frame, decimal 0-255, flushed at least every 60 frames (BrokenDev watches the file
#                                   grow to tell a hung emulator from a slow one)
#     shot <frame> <file>           a picture in out
#     audio <frame> <0|1>           optional: sound was active in the frames since the previous audio line (only if the emulator can tell)
#     note <text>                   anything worth recording (memory domains used, the settings the emulator ran with)
#     end done <frame>              after the last frame, then the emulator exits
#     end crashed <frame> <text>    when the adapter itself sees the game or the emulator fail (a CPU jam, a Lua error)
#   No 'end' line: BrokenDev decides from the process: timeout (the run's wall-clock limit), hung (alive but the result stopped growing),
#   crashed (the process exited).

Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------------------------------- spec

$script:BdNesButtons = @('A', 'B', 'Select', 'Start', 'Up', 'Down', 'Left', 'Right')
$script:BdSnesButtons = @('B', 'Y', 'Select', 'Start', 'Up', 'Down', 'Left', 'Right', 'A', 'X', 'L', 'R')

function ConvertTo-BdInputSteps {
    <# "120:Start,132:,400:Right+B" -> @({Frame=120;Buttons=@('Start')}, ...), sorted. Unknown buttons for the console are an error. #>
    param([AllowEmptyString()][string]$Script, [ValidateSet('nes', 'snes')][string]$Console = 'nes')
    $known = if ($Console -eq 'nes') { $script:BdNesButtons } else { $script:BdSnesButtons }
    $steps = @()
    foreach ($part in @(([string]$Script) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if ($part -notmatch '^(\d+):(.*)$') { throw "bad input step '$part' (expected frame:Buttons)" }
        $f = [int]$Matches[1]
        $names = @($Matches[2] -split '\+' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $canon = @()
        foreach ($n in $names) {
            $k = $known | Where-Object { $_ -ieq $n } | Select-Object -First 1
            if (-not $k) { throw "button '$n' is not a $Console button (known: $($known -join ', '))" }
            $canon += $k
        }
        $steps += [pscustomobject]@{ Frame = $f; Buttons = $canon }
    }
    @($steps | Sort-Object Frame)
}

function New-BdCompatSpecText {
    <#
      The spec text for one run. $Run: Console, Rom, Out, SaveDir, Frames, Reads (@{Name; Domain; Addr}), Input (the "f:Buttons" script),
      Shots (frame numbers). Every value is checked: a spec an adapter could misread is never written.
    #>
    param([Parameter(Mandatory)]$Run)
    $c = [string]$Run.Console
    if (@('nes', 'snes') -notcontains $c) { throw "spec: console '$c' must be nes or snes" }
    $frames = [int]$Run.Frames
    if ($frames -le 0) { throw 'spec: frames must be positive' }
    $lines = @('# bdcompat-spec 1', "console $c", "rom $($Run.Rom)", "out $($Run.Out)", "savedir $($Run.SaveDir)", "frames $frames")
    $names = @{}
    foreach ($r in @($Run.Reads)) {
        $n = [string]$r.Name
        if ($n -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "spec: read name '$n' must be a word" }
        if ($names.ContainsKey($n)) { throw "spec: read '$n' twice" }
        $names[$n] = $true
        $d = [string]$r.Domain; $a = [int]$r.Addr
        $max = if ($d -eq 'ram') { 0x7FF } elseif ($d -eq 'wram') { 0x1FFFF } else { throw "spec: read ${n}: domain '$d' must be ram or wram" }
        if (($c -eq 'nes') -ne ($d -eq 'ram')) { throw "spec: read ${n}: domain $d does not belong to the $c" }
        if ($a -lt 0 -or $a -gt $max) { throw ("spec: read {0}: address {1:X} is outside {2} (0-{3:X})" -f $n, $a, $d, $max) }
        $lines += ('read {0} {1} {2:X}' -f $n, $d, $a)
    }
    foreach ($s in (ConvertTo-BdInputSteps -Script ([string]$Run.Input) -Console $c)) {
        $lines += ('input {0} {1}' -f $s.Frame, $(if (@($s.Buttons).Count -gt 0) { @($s.Buttons) -join '+' } else { '-' }))
    }
    foreach ($f in @($Run.Shots | ForEach-Object { [int]$_ } | Sort-Object -Unique)) {
        if ($f -le 0 -or $f -gt $frames) { throw "spec: shot $f is outside 1..$frames" }
        $lines += "shot $f"
    }
    ($lines -join "`n") + "`n"
}

function Read-BdCompatSpec {
    <# The spec text back as an object (the adapters written in PowerShell use it; the tests check the round trip). #>
    param([Parameter(Mandatory)][string]$Text)
    $s = [ordered]@{ Console = ''; Rom = ''; Out = ''; SaveDir = ''; Frames = 0; Reads = @(); Inputs = @(); Shots = @() }
    foreach ($raw in ($Text -split "`r?`n")) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $k, $v = $line -split '\s+', 2
        switch ($k) {
            'console' { $s.Console = $v }
            'rom' { $s.Rom = $v }
            'out' { $s.Out = $v }
            'savedir' { $s.SaveDir = $v }
            'frames' { $s.Frames = [int]$v }
            'read' { $p = $v -split '\s+'; $s.Reads += [pscustomobject]@{ Name = $p[0]; Domain = $p[1]; Addr = [Convert]::ToInt32($p[2], 16) } }
            'input' { $p = $v -split '\s+'; $s.Inputs += [pscustomobject]@{ Frame = [int]$p[0]; Buttons = $(if ($p[1] -eq '-') { @() } else { @($p[1] -split '\+') }) } }
            'shot' { $s.Shots += [int]$v }
            default { throw "spec: unknown directive '$k'" }
        }
    }
    [pscustomobject]$s
}

# ---------------------------------------------------------------------------------------------------- result

function ConvertFrom-BdCompatResult {
    <#
      Parses an adapter's result.txt. Returns { Emu; Reads; Frames (int[]); Values (frame -> int[]); Shots (frame -> file); Audio (frame -> 0|1);
      Notes; End ($null or { State; Frame; Text }); LastFrame; Errors }. Errors lists every malformed line: a result BrokenDev cannot read
      fully is never scored as if it were complete.
    #>
    param([AllowEmptyString()][string]$Text)
    # (Shots and Audio are plain hashtables keyed by frame: an [ordered] dictionary indexed with an int reads a POSITION, not a key)
    $r = [ordered]@{ Emu = ''; Reads = @(); Frames = New-Object System.Collections.Generic.List[int]; Values = @{}; Shots = @{}; Audio = @{}; Notes = @(); End = $null; LastFrame = 0; Errors = @() }
    $sawHeader = $false; $n = 0; $prev = 0
    foreach ($raw in (([string]$Text) -split "`r?`n")) {
        $n++
        $line = $raw.TrimEnd()
        if (-not $line) { continue }
        if ($line -match '^# bdcompat-result 1\s*$') { $sawHeader = $true; continue }
        if ($line.StartsWith('#')) { continue }
        $k, $v = $line -split '\s+', 2
        if ($null -eq $v) { $v = '' }
        try {
            switch ($k) {
                'emu' { $r.Emu = $v }
                'reads' { $r.Reads = @($v -split '\s+' | Where-Object { $_ }) }
                't' {
                    $p = @($v -split '\s+' | Where-Object { $_ -ne '' })
                    if ($p.Count -lt 1) { throw 'no frame' }
                    $f = [int]$p[0]
                    if ($f -le $prev) { throw "frame $f is not after frame $prev" }
                    $vals = @($p | Select-Object -Skip 1 | ForEach-Object { if ($_ -notmatch '^\d+$') { throw "value '$_' is not a number" }; $x = [int]$_; if ($x -gt 255) { throw "value $x is not a byte" }; $x })
                    if ($vals.Count -ne @($r.Reads).Count) { throw "$($vals.Count) values for $(@($r.Reads).Count) reads" }
                    $r.Frames.Add($f); $r.Values[$f] = $vals; $prev = $f; $r.LastFrame = $f
                }
                'shot' { $p = $v -split '\s+', 2; if ($p.Count -lt 2 -or $p[0] -notmatch '^\d+$') { throw 'expected: shot <frame> <file>' }; $r.Shots[[int]$p[0]] = $p[1] }
                'audio' { $p = $v -split '\s+'; if ($p.Count -lt 2 -or $p[0] -notmatch '^\d+$' -or $p[1] -notmatch '^[01]$') { throw 'expected: audio <frame> <0|1>' }; $r.Audio[[int]$p[0]] = [int]$p[1] }
                'note' { $r.Notes += $v }
                'end' {
                    $p = $v -split '\s+', 3
                    if (@('done', 'crashed') -notcontains $p[0]) { throw "end state '$($p[0])' must be done or crashed" }
                    if ($p.Count -lt 2 -or $p[1] -notmatch '^\d+$') { throw 'expected: end <state> <frame> [text]' }
                    $r.End = [pscustomobject]@{ State = $p[0]; Frame = [int]$p[1]; Text = $(if ($p.Count -gt 2) { $p[2] } else { '' }) }
                    if ([int]$p[1] -gt $r.LastFrame) { $r.LastFrame = [int]$p[1] }
                }
                default { throw "unknown line kind '$k'" }
            }
        } catch { $r.Errors += "line ${n}: $($_.Exception.Message): $line" }
    }
    if (-not $sawHeader) { $r.Errors += 'no "# bdcompat-result 1" header' }
    [pscustomobject]$r
}

# ---------------------------------------------------------------------------------------------------- running an emulator

function Invoke-BdWatchedProcess {
    <#
      Starts an emulator and watches its result file. Ends it (by PID, its whole tree) when the run outlives -TimeoutSec, or when the result
      file has not grown for -HangSec while the process is alive (hung: an emulator that stopped advancing frames). Returns
      { ExitCode; TimedOut; Hung; Seconds; Pid; LogFile }. -Env adds environment variables for the child only.
      The window is started minimised and without focus where the program honours it; stdout/stderr go to -LogFile through pipes, or with
      -OutputFile straight into that file (through cmd.exe redirection: a program that misbehaves with pipes, such as Mesen's test runner,
      gets a plain file handle), or nowhere with -NoRedirect.
    #>
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = (Split-Path -Parent $FilePath),
        [hashtable]$Env = @{},
        [Parameter(Mandatory)][string]$ResultFile,
        [int]$TimeoutSec = 600,
        [int]$HangSec = 60,
        [string]$LogFile = '',
        [string]$OutputFile = '',
        [switch]$NoRedirect
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Minimized
    if ($OutputFile) {
        $NoRedirect = $true
        $od = Split-Path -Parent $OutputFile
        if ($od -and -not (Test-Path -LiteralPath $od)) { New-Item -ItemType Directory -Force -Path $od | Out-Null }
        $psi.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
        $psi.CreateNoWindow = $true
        $psi.Arguments = '/d /s /c "' + (ConvertTo-BdArgString (@($FilePath) + $Arguments)) + ' > "' + $OutputFile + '" 2>&1"'
    } else {
        $psi.FileName = $FilePath
        if ($psi.PSObject.Properties['ArgumentList']) { foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add([string]$a) } }
        else { $psi.Arguments = ConvertTo-BdArgString $Arguments }
    }
    if (-not $NoRedirect) { $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true }
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[[string]$k] = [string]$Env[$k] }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $null; $errTask = $null
    if (-not $NoRedirect) { $outTask = $p.StandardOutput.ReadToEndAsync(); $errTask = $p.StandardError.ReadToEndAsync() }
    $timedOut = $false; $hung = $false
    $lastLen = -1L; $lastChange = $sw.Elapsed.TotalSeconds
    while (-not $p.WaitForExit(500)) {
        $len = 0L
        if (Test-Path -LiteralPath $ResultFile) { try { $len = (Get-Item -LiteralPath $ResultFile).Length } catch { $len = $lastLen } }
        if ($len -ne $lastLen) { $lastLen = $len; $lastChange = $sw.Elapsed.TotalSeconds }
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) { $timedOut = $true; break }
        if ($sw.Elapsed.TotalSeconds - $lastChange -gt $HangSec) { $hung = $true; break }
    }
    if ($timedOut -or $hung) { Stop-BdProcessTree -ProcessId $p.Id; try { [void]$p.WaitForExit(10000) } catch { } }
    else { $p.WaitForExit() }
    $text = ''
    if (-not $NoRedirect) {
        try { $text = $outTask.Result } catch { }
        try { $e2 = $errTask.Result; if ($e2) { $text += "`n--- stderr ---`n" + $e2 } } catch { }
    }
    if ($LogFile) {
        $ld = Split-Path -Parent $LogFile
        if ($ld -and -not (Test-Path -LiteralPath $ld)) { New-Item -ItemType Directory -Force -Path $ld | Out-Null }
        [System.IO.File]::WriteAllText($LogFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    $code = if ($timedOut -or $hung) { -1 } else { $p.ExitCode }
    if ($OutputFile) { $LogFile = $OutputFile; try { if (Test-Path -LiteralPath $OutputFile) { $text = [System.IO.File]::ReadAllText($OutputFile) } } catch { } }
    [pscustomobject]@{ ExitCode = $code; TimedOut = $timedOut; Hung = $hung; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Pid = $p.Id; LogFile = $LogFile; Output = $text }
}

# ---------------------------------------------------------------------------------------------------- pictures
# Every picture is measured the same way whatever made it: size, an FNV-1a hash of its RGB pixels, the number of distinct colours. Two
# emulators never share a NES palette (nor always a picture size: BizHawk clips 8 lines top and bottom), so pictures of different emulators
# are compared by STRUCTURE: the share of pixels that agree under the best colour-to-colour mapping, both ways (1.0 = the same picture up to
# palette; a different screen scores far lower). Sizes are aligned first: a 512-wide hi-res frame is halved, a taller frame is cropped
# equally top and bottom.

$script:BdPicSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
public static class BdPic {
    static Bitmap Load(string path) {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var tmp = new Bitmap(fs)) return new Bitmap(tmp);
    }
    static int[] Pixels(Bitmap b) {
        var r = new Rectangle(0, 0, b.Width, b.Height);
        var d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            var px = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), px, y * b.Width, b.Width);
            for (int i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
            return px;
        } finally { b.UnlockBits(d); }
    }
    public static string Stats(string path) {
        using (var b = Load(path)) {
            var px = Pixels(b);
            ulong h = 14695981039346656037UL;
            var seen = new HashSet<int>();
            foreach (int p in px) { h = (h ^ (byte)(p >> 16)) * 1099511628211UL; h = (h ^ (byte)(p >> 8)) * 1099511628211UL; h = (h ^ (byte)p) * 1099511628211UL; seen.Add(p); }
            return b.Width + " " + b.Height + " " + h.ToString("X16") + " " + seen.Count;
        }
    }
    public static void ToPng(string src, string dst) { using (var b = Load(src)) b.Save(dst, ImageFormat.Png); }
    static int[] Align(int[] px, int w, int h, int tw, int th) {
        // halve a hi-res width, then crop rows equally top and bottom (or columns left and right) down to tw x th
        if (w == tw * 2) { var q = new int[tw * h]; for (int y = 0; y < h; y++) for (int x = 0; x < tw; x++) q[y * tw + x] = px[y * w + x * 2]; px = q; w = tw; }
        int ox = (w - tw) / 2, oy = (h - th) / 2;
        var o = new int[tw * th];
        for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) o[y * tw + x] = px[(y + oy) * w + (x + ox)];
        return o;
    }
    static double OneWay(int[] a, int[] b) {
        var pairs = new Dictionary<long, int>(); var best = new Dictionary<int, int>();
        for (int i = 0; i < a.Length; i++) { long k = ((long)a[i] << 32) | (uint)b[i]; int c; pairs.TryGetValue(k, out c); pairs[k] = c + 1; }
        foreach (var kv in pairs) { int ca = (int)(kv.Key >> 32); int cur; best.TryGetValue(ca, out cur); if (kv.Value > cur) best[ca] = kv.Value; }
        long sum = 0; foreach (var v in best.Values) sum += v;
        return a.Length == 0 ? 0 : (double)sum / a.Length;
    }
    public static double Similarity(string pathA, string pathB) {
        using (var a = Load(pathA)) using (var b = Load(pathB)) {
            int wa = a.Width == 512 ? 256 : a.Width, wb = b.Width == 512 ? 256 : b.Width;
            int tw = Math.Min(wa, wb), th = Math.Min(a.Height, b.Height);
            var pa = Align(Pixels(a), a.Width, a.Height, tw, th); var pb = Align(Pixels(b), b.Width, b.Height, tw, th);
            return Math.Min(OneWay(pa, pb), OneWay(pb, pa));
        }
    }
}
'@

function Initialize-BdPic {
    if ('BdPic' -as [type]) { return }
    Add-Type -AssemblyName System.Drawing
    $refs = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location) | Where-Object { $_ } | Select-Object -Unique
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        # PowerShell 7 compiles against the runtime's reference set; System.Drawing.Common and the private Windows assemblies it is built on
        # (System.Private.Windows.Core / GdiPlus in .NET 9+) are not in it
        # A list given to -ReferencedAssemblies REPLACES PowerShell 7's default reference set: so it is that set ($PSHOME\ref) plus the
        # implementation assemblies that have no reference assembly there (System.Drawing.Common and what it is built on).
        $refDir = Join-Path $PSHOME 'ref'
        $have = @{}
        $refs = @()
        foreach ($f in @(Get-ChildItem -LiteralPath $refDir -Filter '*.dll' -ErrorAction SilentlyContinue)) { $refs += $f.FullName; $have[$f.BaseName] = $true }
        $impl = @([System.Drawing.Bitmap].Assembly)
        foreach ($n in [System.Drawing.Bitmap].Assembly.GetReferencedAssemblies()) { try { $impl += [System.Reflection.Assembly]::Load($n) } catch { } }
        foreach ($a in $impl) { $nm = $a.GetName().Name; if (-not $have.ContainsKey($nm) -and $a.Location) { $refs += $a.Location; $have[$nm] = $true } }
    }
    Add-Type -TypeDefinition $script:BdPicSource -ReferencedAssemblies $refs
}

function Get-BdPictureStats {
    <# { Width; Height; Hash; Colors } of a PNG or BMP. A BMP is converted to a PNG beside it first (the PNG is what the sheet shows); returns .Path. #>
    param([Parameter(Mandatory)][string]$Path)
    Initialize-BdPic
    if ($Path -match '\.bmp$') {
        $png = [System.IO.Path]::ChangeExtension($Path, '.png')
        [BdPic]::ToPng($Path, $png)
        Remove-Item -LiteralPath $Path -Force
        $Path = $png
    }
    $p = ([BdPic]::Stats($Path)) -split ' '
    [pscustomobject]@{ Path = $Path; Width = [int]$p[0]; Height = [int]$p[1]; Hash = $p[2]; Colors = [int]$p[3] }
}

function Get-BdPictureSimilarity {
    param([Parameter(Mandatory)][string]$A, [Parameter(Mandatory)][string]$B)
    Initialize-BdPic
    [math]::Round([BdPic]::Similarity($A, $B), 3)
}

# ---------------------------------------------------------------------------------------------------- symbols

function Read-BdSymbols {
    <#
      name -> address from a symbol file. .mlb (Mesen labels, NESFab writes them): 'R:XXXX:name@k:' = NES RAM; 'name@0' is also reachable as
      'name'. .sym (no$snes / WLA-DX): 'BBAAAAAA name' = a 24-bit SNES address. Any other line is ignored.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "symbol file not found: $Path" }
    $t = @{}
    if ($Path -match '\.mlb$') {
        foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
            if ($line -match '^R:([0-9A-Fa-f]+):([^:@]+)(?:@(\d+))?:') {
                $a = [Convert]::ToInt32($Matches[1], 16); $n = $Matches[2]; $k = $Matches[3]
                if ($k) { $t["$n@$k"] = $a }
                if (-not $k -or $k -eq '0') { if (-not $t.ContainsKey($n)) { $t[$n] = $a } }
            }
        }
    } elseif ($Path -match '\.sym$') {
        foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
            if ($line -match '^([0-9a-fA-F]{8})\s+(\S+)\s*$') { $t[$Matches[2]] = [Convert]::ToInt32($Matches[1], 16) }
        }
    } else { throw "unknown symbol file kind: $Path (.mlb or .sym)" }
    $t
}

function ConvertTo-BdWramOffset {
    <# A 24-bit SNES CPU address -> the work RAM offset it reaches ($7E:0000-$7F:FFFF, or the low 8 KB mirror in banks $00-$3F/$80-$BF). #>
    param([Parameter(Mandatory)][int]$Address)
    $bank = ($Address -shr 16) -band 0xFF; $a = $Address -band 0xFFFF
    if ($bank -eq 0x7E -or $bank -eq 0x7F) { return ((($bank - 0x7E) -shl 16) -bor $a) }
    if ((($bank -band 0x7F) -le 0x3F) -and $a -lt 0x2000) { return $a }
    throw ("address {0:X6} is not in SNES work RAM" -f $Address)
}

function Resolve-BdCompatReads {
    <#
      The compat section's reads as @{Name; Domain; Addr}. A read names a symbol (looked up in the section's symbol file) or a literal '$hex'.
      NES: domain ram, the symbol's address. SNES with a .mlb (a Nested game): domain wram, NES RAM address + symbols.nesRamInWram.
      SNES with a .sym: domain wram, the 24-bit address converted to a work RAM offset. A symbol that is not in the file is an error.
    #>
    param([Parameter(Mandatory)]$Compat, [Parameter(Mandatory)][string]$Console, [hashtable]$Ctx = @{})
    $symSpec = Get-BdProp $Compat 'symbols' $null
    $file = if ($symSpec) { Expand-BdPath -Text ([string](Get-BdProp $symSpec 'file' '')) -Context $Ctx } else { '' }
    $table = if ($file) { Read-BdSymbols -Path $file } else { @{} }
    $nesBase = [int](Get-BdProp $symSpec 'nesRamInWram' -1)
    $out = @()
    foreach ($p in (Get-BdProp $Compat 'reads' ([pscustomobject]@{})).PSObject.Properties) {
        $what = [string]$p.Value
        if ($what -match '^\$([0-9A-Fa-f]+)$') { $raw = [Convert]::ToInt32($Matches[1], 16) }
        elseif ($table.ContainsKey($what)) { $raw = [int]$table[$what] }
        else { throw "compat read '$($p.Name)': symbol '$what' is not in $file" }
        if ($Console -eq 'nes') { $dom = 'ram'; $addr = $raw -band 0xFFFF; if ($addr -gt 0x7FF) { throw ("compat read '{0}': {1:X4} is not NES RAM" -f $p.Name, $addr) } }
        elseif ($file -match '\.mlb$') {
            if ($nesBase -lt 0) { throw "compat: a NES symbol file on a SNES game needs symbols.nesRamInWram (where the recompiler keeps NES RAM in work RAM)" }
            $dom = 'wram'; $addr = $nesBase + ($raw -band 0x7FF)
        }
        else { $dom = 'wram'; $addr = if ($what -match '^\$') { $raw } else { ConvertTo-BdWramOffset -Address $raw } }
        $out += [pscustomobject]@{ Name = [string]$p.Name; Domain = $dom; Addr = [int]$addr; Symbol = $what }
    }
    @($out)
}

# ---------------------------------------------------------------------------------------------------- checks and verdicts

function Get-BdCheckKind {
    param([Parameter(Mandatory)]$Check)
    if ($null -ne (Get-BdProp $Check 'read' $null)) { return 'read' }
    if ($null -ne (Get-BdProp $Check 'changes' $null)) { return 'changes' }
    if ($null -ne (Get-BdProp $Check 'picture' $null)) { return 'picture' }
    if ($null -ne (Get-BdProp $Check 'pictureDiffers' $null)) { return 'pictureDiffers' }
    if ($null -ne (Get-BdProp $Check 'similar' $null)) { return 'similar' }
    ''
}

function Test-BdCompatSection {
    <# Structural validation of a manifest's compat section. Returns error strings (empty = valid). $VerifyIds: the verify scenario ids. #>
    param([Parameter(Mandatory)]$Compat, [string[]]$VerifyIds = @())
    $e = New-Object System.Collections.Generic.List[string]
    $reads = Get-BdProp $Compat 'reads' $null
    $readNames = @(); if ($reads) { $readNames = @($reads.PSObject.Properties | ForEach-Object { $_.Name }) }
    if ($readNames.Count -gt 0 -and -not (Get-BdProp $Compat 'symbols' $null)) {
        $lit = @($reads.PSObject.Properties | Where-Object { [string]$_.Value -notmatch '^\$[0-9A-Fa-f]+$' })
        if ($lit.Count -gt 0) { $e.Add('compat.symbols is required when a read names a symbol') }
    }
    $sc = @(Get-BdProp $Compat 'scenarios' @())
    if ($sc.Count -eq 0) { $e.Add('compat.scenarios is empty') }
    $ids = @{}
    foreach ($s in $sc) {
        $sid = [string](Get-BdProp $s 'id' '')
        if (-not $sid) { $e.Add('a compat scenario has no id'); continue }
        if ($ids.ContainsKey($sid)) { $e.Add("duplicate compat scenario '$sid'") }
        $ids[$sid] = $true
        $from = [string](Get-BdProp $s 'verify' '')
        if ($from) { if ($VerifyIds.Count -gt 0 -and $VerifyIds -notcontains $from) { $e.Add("compat scenario ${sid}: verify scenario '$from' does not exist") } }
        elseif ([int](Get-BdProp $s 'frames' 0) -le 0) { $e.Add("compat scenario ${sid}: needs 'verify' (a verify scenario to reuse) or 'frames'") }
        $checks = @(Get-BdProp $s 'checks' @())
        if ($checks.Count -eq 0) { $e.Add("compat scenario ${sid}: checks is empty (a run that checks nothing cannot fail)") }
        $cids = @{}
        foreach ($c in $checks) {
            $cid = [string](Get-BdProp $c 'id' '')
            if (-not $cid) { $e.Add("compat scenario ${sid}: a check has no id"); continue }
            if ($cids.ContainsKey($cid)) { $e.Add("compat scenario ${sid}: duplicate check '$cid'") }
            if (-not [string](Get-BdProp $c 'what' '')) { $e.Add("check ${sid}/${cid}: 'what' (the words the sheet shows) is required") }
            $kind = Get-BdCheckKind $c
            if (-not $kind) { $e.Add("check ${sid}/${cid}: needs one of read, changes, picture, pictureDiffers, similar") }
            if ($kind -in 'read', 'changes') {
                $rn = if ($kind -eq 'read') { [string]$c.read } else { [string]$c.changes }
                if ($readNames -notcontains $rn) { $e.Add("check ${sid}/${cid}: '$rn' is not one of compat.reads ($($readNames -join ', '))") }
                $w = @(Get-BdProp $c 'within' @())
                if ($w.Count -ne 2 -or [int]$w[0] -gt [int]$w[1]) { $e.Add("check ${sid}/${cid}: within must be [from, to]") }
                if ($kind -eq 'read' -and $null -eq (Get-BdProp $c 'is' $null) -and $null -eq (Get-BdProp $c 'not' $null)) { $e.Add("check ${sid}/${cid}: a read check needs 'is' or 'not'") }
                if ($kind -eq 'changes' -and [int](Get-BdProp $c 'min' 0) -le 0) { $e.Add("check ${sid}/${cid}: a changes check needs min > 0") }
                $after = [string](Get-BdProp $c 'after' '')
                if ($after -and -not $cids.ContainsKey($after)) { $e.Add("check ${sid}/${cid}: after '$after' must name an earlier check of the same scenario") }
            }
            if ($kind -eq 'picture' -and [int](Get-BdProp $c 'minColors' 0) -le 0) { $e.Add("check ${sid}/${cid}: a picture check needs minColors") }
            if ($kind -eq 'similar') { $m = [double](Get-BdProp $c 'min' 0); if ($m -le 0 -or $m -gt 1) { $e.Add("check ${sid}/${cid}: similar needs min in (0, 1]") } }
            if ($kind -eq 'pictureDiffers' -and @($c.pictureDiffers).Count -ne 2) { $e.Add("check ${sid}/${cid}: pictureDiffers needs two frames") }
            $cids[$cid] = $true
        }
    }
    @($e)
}

function Test-BdCompatCheck {
    <#
      Evaluates one check on one emulator's run. Pure: the parsed result (ConvertFrom-BdCompatResult), the picture stats by frame, what the
      emulator can do, the first frames of the checks before it, and (for 'similar') the reference's pictures.
      Returns { Id; What; Kind; Status = pass | fail | n/a; Detail; FirstFrame; Compare }.
        read     'is' (a value or a list) or 'not' within [from, to]; reached on some traced frame (default) or held on all of them ('hold')
        changes  the read changes at least 'min' times within [from, to] (a heartbeat: the game keeps running)
        picture  the picture at that frame has at least minColors colours
        pictureDiffers  the pictures at the two frames differ
        similar  the picture at that frame has at least 'min' structural similarity to BrokenNes' (a comparison: below it 'differs')
      'after' <check>: the window is relative to that check's first frame. 'compare': the first frame is compared with BrokenNes'.
      'soft': a failure is a difference, not a failure (for what may legitimately vary between emulators, e.g. a run that meets another
      random room and dies on the scripted path).
    #>
    param([Parameter(Mandatory)]$Check, $Result, [hashtable]$Shots = @{}, $Can, [hashtable]$Prior = @{}, [hashtable]$RefShots = @{})
    $r = Test-BdCompatCheckCore -Check $Check -Result $Result -Shots $Shots -Can $Can -Prior $Prior -RefShots $RefShots
    if ($r.Status -eq 'fail' -and [bool](Get-BdProp $Check 'soft' $false)) { $r.Status = 'differs'; $r.Detail = "(soft) $($r.Detail)" }
    $r
}

function Test-BdCompatCheckCore {
    param([Parameter(Mandatory)]$Check, $Result, [hashtable]$Shots = @{}, $Can, [hashtable]$Prior = @{}, [hashtable]$RefShots = @{})
    $kind = Get-BdCheckKind $Check
    $rec = [ordered]@{ Id = [string]$Check.id; What = [string]$Check.what; Kind = $kind; Status = 'n/a'; Detail = ''; FirstFrame = $null; Compare = [bool](Get-BdProp $Check 'compare' $false) -or $kind -eq 'similar' }
    $canMem = [bool](Get-BdProp $Can 'memory' $false); $canPic = [bool](Get-BdProp $Can 'pictures' $false)
    if ($kind -in 'read', 'changes') {
        if (-not $canMem) { $rec.Detail = 'this emulator cannot read game memory'; return [pscustomobject]$rec }
        if (-not $Result) { $rec.Status = 'fail'; $rec.Detail = 'no result'; return [pscustomobject]$rec }
        $name = if ($kind -eq 'read') { [string]$Check.read } else { [string]$Check.changes }
        $idx = [array]::IndexOf([string[]]@($Result.Reads), $name)
        if ($idx -lt 0) { $rec.Status = 'fail'; $rec.Detail = "the run recorded no '$name'"; return [pscustomobject]$rec }
        $w = @($Check.within); $lo = [int]$w[0]; $hi = [int]$w[1]
        $after = [string](Get-BdProp $Check 'after' '')
        if ($after) {
            if (-not $Prior.ContainsKey($after) -or $null -eq $Prior[$after]) { $rec.Status = 'fail'; $rec.Detail = "check '$after' never happened, so this one cannot"; return [pscustomobject]$rec }
            $lo += [int]$Prior[$after]; $hi += [int]$Prior[$after]
        }
        $frames = @($Result.Frames | Where-Object { $_ -ge $lo -and $_ -le $hi })
        if ($frames.Count -eq 0) { $rec.Status = 'fail'; $rec.Detail = "no frame in $lo..$hi (the run ended at frame $($Result.LastFrame))"; return [pscustomobject]$rec }
        if ($kind -eq 'changes') {
            $n = 0; $prev = $null
            foreach ($f in $frames) { $v = $Result.Values[$f][$idx]; if ($null -ne $prev -and $v -ne $prev) { $n++ }; $prev = $v }
            $min = [int]$Check.min
            $rec.Status = if ($n -ge $min) { 'pass' } else { 'fail' }
            $rec.Detail = "$name changed $n times in frames $lo..$hi (need $min)"
            return [pscustomobject]$rec
        }
        $isList = $null; $not = Get-BdProp $Check 'not' $null
        if ($null -ne (Get-BdProp $Check 'is' $null)) { $isList = @($Check.is | ForEach-Object { [int]$_ }) }
        $ok = { param($v) if ($null -ne $isList) { $isList -contains $v } else { $v -ne [int]$not } }
        $want = if ($null -ne $isList) { "$name = $($isList -join ' or ')" } else { "$name != $not" }
        if ([bool](Get-BdProp $Check 'hold' $false)) {
            $bad = @($frames | Where-Object { -not (& $ok $Result.Values[$_][$idx]) } | Select-Object -First 1)
            if ($bad.Count -eq 0) { $rec.Status = 'pass'; $rec.FirstFrame = $frames[0]; $rec.Detail = "$want on every frame of $lo..$hi" }
            else { $rec.Status = 'fail'; $rec.Detail = "$want should hold in $lo..$hi but frame $($bad[0]) has $($Result.Values[$bad[0]][$idx])" }
        } else {
            $hit = @($frames | Where-Object { & $ok $Result.Values[$_][$idx] } | Select-Object -First 1)
            if ($hit.Count -gt 0) { $rec.Status = 'pass'; $rec.FirstFrame = $hit[0]; $rec.Detail = "$want first at frame $($hit[0])" }
            else {
                $seen = @($frames | ForEach-Object { $Result.Values[$_][$idx] } | Select-Object -Unique | Select-Object -First 6)
                $rec.Status = 'fail'; $rec.Detail = "$want never happened in $lo..$hi (values seen: $($seen -join ', '))"
            }
        }
        return [pscustomobject]$rec
    }
    if (-not $canPic) { $rec.Detail = 'this emulator gives no pictures'; return [pscustomobject]$rec }
    switch ($kind) {
        'picture' {
            $f = [int]$Check.picture; $s = $Shots[$f]
            if (-not $s) { $rec.Status = 'fail'; $rec.Detail = "no picture at frame $f"; break }
            $min = [int]$Check.minColors
            $rec.Status = if ($s.Colors -ge $min) { 'pass' } else { 'fail' }
            $rec.Detail = "$($s.Colors) colours at frame $f (need $min)"
        }
        'pictureDiffers' {
            $f1 = [int]$Check.pictureDiffers[0]; $f2 = [int]$Check.pictureDiffers[1]
            if (-not $Shots[$f1] -or -not $Shots[$f2]) { $rec.Status = 'fail'; $rec.Detail = "no picture at frame $f1 or $f2"; break }
            $rec.Status = if ($Shots[$f1].Hash -ne $Shots[$f2].Hash) { 'pass' } else { 'fail' }
            $rec.Detail = "frame $f1 $($Shots[$f1].Hash), frame $f2 $($Shots[$f2].Hash)"
        }
        'similar' {
            $f = [int]$Check.similar; $s = $Shots[$f]; $ref = $RefShots[$f]
            if (-not $s) { $rec.Status = 'fail'; $rec.Detail = "no picture at frame $f"; break }
            if (-not $ref) { $rec.Detail = "BrokenNes has no picture at frame $f to compare with"; break }
            $sim = if ($s.PSObject.Properties['Similarity'] -and $null -ne $s.Similarity) { [double]$s.Similarity } else { Get-BdPictureSimilarity -A $s.Path -B $ref.Path }
            $min = [double]$Check.min
            $rec.Status = if ($sim -ge $min) { 'pass' } else { 'differs' }
            $rec.Detail = "structural similarity to BrokenNes at frame ${f}: $sim (want >= $min)"
        }
    }
    [pscustomobject]$rec
}

function Get-BdCompatVerdict {
    <#
      One emulator's verdict on one game, from its scenario records ({ Id; End = { State; Frame; Text }; Checks }), the reference's first
      frames (hashtable 'scenario/check' -> frame) and the frame tolerance. Pure.
        cannot be tested : nothing ran (the reason is in -NotRun), or every check was n/a for this emulator
        fails            : a run did not reach its last frame (crashed, hung, timeout), or a required check failed
        differs          : everything required passed, but a comparison with BrokenNes is outside the tolerance (a state reached more than
                           'frameTolerance' frames apart, a picture below its similarity bar)
        works            : everything passed
      Returns { Verdict; Why (strings); Passed; Failed; Differs; NotApplicable }.
    #>
    param($Records = @(), [hashtable]$RefFirst = @{}, [int]$Tolerance = 30, [string]$NotRun = '', [switch]$IsReference)
    $why = New-Object System.Collections.Generic.List[string]
    if ($NotRun) { return [pscustomobject]@{ Verdict = 'cannot be tested'; Why = @($NotRun); Passed = 0; Failed = 0; Differs = 0; NotApplicable = 0 } }
    $pass = 0; $fail = 0; $diff = 0; $na = 0; $ended = $false
    foreach ($r in @($Records)) {
        $st = if ($r.End) { [string]$r.End.State } else { 'crashed' }
        if ($st -ne 'done') { $ended = $true; $why.Add("$($r.Id): $st at frame $(if ($r.End) { $r.End.Frame } else { '?' })$(if ($r.End -and $r.End.Text) { ': ' + $r.End.Text })") }
        foreach ($c in @($r.Checks)) {
            switch ([string]$c.Status) {
                'n/a' { $na++ }
                'fail' { $fail++; $why.Add("$($r.Id)/$($c.Id) FAILED: $($c.What): $($c.Detail)") }
                'differs' { $diff++; $why.Add("$($r.Id)/$($c.Id) differs: $($c.Detail)") }
                'pass' {
                    $key = "$($r.Id)/$($c.Id)"
                    if ($c.Compare -and -not $IsReference -and $null -ne $c.FirstFrame -and $RefFirst.ContainsKey($key) -and $null -ne $RefFirst[$key]) {
                        $d = [int]$c.FirstFrame - [int]$RefFirst[$key]
                        if ([math]::Abs($d) -gt $Tolerance) { $diff++; $why.Add("$key differs: $($c.What) at frame $($c.FirstFrame), BrokenNes at $($RefFirst[$key]) ($d frames, tolerance $Tolerance)") }
                        else { $pass++ }
                    } else { $pass++ }
                }
            }
        }
    }
    $verdict = if ($ended -or $fail -gt 0) { 'fails' } elseif ($pass + $diff -eq 0) { 'cannot be tested' } elseif ($diff -gt 0) { 'differs' } else { 'works' }
    if ($verdict -eq 'cannot be tested') { $why.Add('no check could be evaluated on this emulator') }
    if ($verdict -in 'works', 'differs' -and $na -gt 0) { $why.Add("$na check(s) not checkable on this emulator") }
    [pscustomobject]@{ Verdict = $verdict; Why = @($why); Passed = $pass; Failed = $fail; Differs = $diff; NotApplicable = $na }
}

# ---------------------------------------------------------------------------------------------------- adapters

# Every adapter is its own file; one that does not load (a syntax error) costs only its own column, never the whole verb.
$script:BdAdapterLoadErrors = @{}
foreach ($a in (Get-ChildItem -LiteralPath (Join-Path $script:BdLibDir 'adapters') -Filter '*.ps1' -ErrorAction SilentlyContinue)) {
    $tokens = $null; $perr = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($a.FullName, [ref]$tokens, [ref]$perr)
    if ($perr -and @($perr).Count -gt 0) { $script:BdAdapterLoadErrors[$a.BaseName] = "line $($perr[0].Extent.StartLineNumber): $($perr[0].Message)"; continue }
    try { . $a.FullName } catch { $script:BdAdapterLoadErrors[$a.BaseName] = $_.Exception.Message }
}

function Get-BdAdapterFunction {
    param([Parameter(Mandatory)][string]$Adapter, [string]$Prefix = 'Invoke-BdAdapter')
    $name = $Prefix + $Adapter.Substring(0, 1).ToUpperInvariant() + $Adapter.Substring(1)
    if (Get-Command $name -CommandType Function -ErrorAction SilentlyContinue) { return $name }
    $null
}

function Get-BdAdapterLoadError { param([string]$Adapter) if ($script:BdAdapterLoadErrors.ContainsKey($Adapter)) { return $script:BdAdapterLoadErrors[$Adapter] } '' }

# ---------------------------------------------------------------------------------------------------- the compat run

function Get-BdCompatScenarios {
    <# The compat section's scenarios with everything an adapter needs: frames, input and pictures (from the verify scenario they name, plus their own), saves chain, checks. #>
    param([Parameter(Mandatory)]$Manifest)
    $compat = $Manifest.compat
    $verify = @($Manifest.verify.scenarios)
    foreach ($s in @($compat.scenarios)) {
        $v = $null
        $from = [string](Get-BdProp $s 'verify' '')
        if ($from) { $v = $verify | Where-Object { $_.id -eq $from } | Select-Object -First 1; if (-not $v) { throw "compat scenario $($s.id): no verify scenario '$from'" } }
        $frames = [int](Get-BdProp $s 'frames' $(if ($v) { $v.frames } else { 0 }))
        $input = if ($null -ne (Get-BdProp $s 'input' $null)) { [string]$s.input } elseif ($v) { [string](Get-BdProp $v 'input' '') } else { '' }
        $shots = @(@(Get-BdProp $v 'checkpoints' @()) + @(Get-BdProp $s 'shots' @()) + $frames)
        foreach ($c in @($s.checks)) {
            foreach ($k in 'picture', 'similar') { $f = Get-BdProp $c $k $null; if ($null -ne $f) { $shots += [int]$f } }
            $pd = Get-BdProp $c 'pictureDiffers' $null; if ($pd) { $shots += @($pd | ForEach-Object { [int]$_ }) }
        }
        $shots = @($shots | ForEach-Object { [int]$_ } | Where-Object { $_ -ge 1 -and $_ -le $frames } | Sort-Object -Unique)
        [pscustomobject]@{
            Id = [string]$s.id; Verify = $from; Frames = $frames; Input = $input; Shots = $shots
            PowerOnRam = [string](Get-BdProp $s 'powerOnRam' $(Get-BdProp $v 'powerOnRam' ''))
            Ntsc = [bool](Get-BdProp $s 'ntscFrameTiming' $(Get-BdProp $v 'ntscFrameTiming' $false))
            Saves = [string](Get-BdProp $s 'saves' ''); What = [string](Get-BdProp $s 'what' ''); Checks = @($s.checks)
        }
    }
}

function Get-BdUninitialisedReads {
    <# Mesen's test runner prints '[CPU] Uninitialized memory read: $XXXX' for RAM read before any write: the addresses, with the symbol each falls in. #>
    param([string]$Output, [hashtable]$Symbols = @{}, [string]$Console = 'nes')
    $addrs = @(([regex]::Matches([string]$Output, 'Uninitialized memory read: \$([0-9A-Fa-f]+)') | ForEach-Object { [Convert]::ToInt32($_.Groups[1].Value, 16) }) | Select-Object -Unique | Sort-Object)
    $byAddr = @{}
    foreach ($k in $Symbols.Keys) { $a = [int]$Symbols[$k]; if (-not $byAddr.ContainsKey($a) -or $k.Length -lt $byAddr[$a].Length) { $byAddr[$a] = $k } }
    $keys = @($byAddr.Keys | Sort-Object)
    foreach ($a in $addrs) {
        $name = ''
        if ($Console -eq 'nes' -and $keys.Count -gt 0) {
            $below = @($keys | Where-Object { $_ -le $a } | Select-Object -Last 1)
            if ($below.Count -gt 0 -and ($a - $below[0]) -lt 64) { $name = $byAddr[$below[0]]; if ($a -ne $below[0]) { $name += '+' + ($a - $below[0]) } }
        }
        [pscustomobject]@{ Addr = ('${0:X4}' -f $a); Symbol = $name }
    }
}

function Get-BdReadChanges {
    <# A read's value changes over a run, as 'frame=value' (capped): what compat.json keeps of the per-frame trace. #>
    param($Result, [int]$Index, [int]$Max = 120)
    $out = @(); $prev = $null
    foreach ($f in $Result.Frames) { $v = $Result.Values[$f][$Index]; if ($v -ne $prev) { $out += "$f=$v"; $prev = $v; if ($out.Count -ge $Max) { $out += '...'; break } } }
    $out -join ' '
}

function Invoke-BdCompatEmulator {
    <# Runs every scenario of a project on one emulator and evaluates its checks. Returns the emulator's record. #>
    param([Parameter(Mandatory)]$Manifest, [Parameter(Mandatory)]$Emu, $J, [Parameter(Mandatory)]$Scenarios, [Parameter(Mandatory)]$Reads, [Parameter(Mandatory)][string]$Rom,
          [Parameter(Mandatory)][string]$OutDir, [Parameter(Mandatory)][string]$WorkDir, $Reference = $null, [hashtable]$Symbols = @{}, [int]$TimeoutSec = 600)
    $id = [string]$Emu.id
    $console = [string]$Manifest.console
    $rec = [ordered]@{ id = $id; title = [string]$Emu.title; reference = [bool](Get-BdProp $Emu 'reference' $false); adapter = [string]$Emu.adapter; version = ''
                       can = $Emu.can; verdict = ''; why = @(); counts = $null; settings = $null; notRun = ''; scenarios = @(); firstFrames = @{} }
    $pkg = $null
    if (Get-BdProp $Emu 'package' $null) { $pkg = Get-BdEmuPackage -Id ([string]$Emu.package) -J $J; $rec.version = [string]$pkg.version } else { $rec.version = 'this repository' }
    if (@($Emu.consoles) -notcontains $console) { $rec.notRun = "$($Emu.title) does not run the $console" }
    $exe = $null
    if (-not $rec.notRun) {
        $exe = Get-BdEmuExe -Emu $Emu -J $J
        if (-not $exe) { $rec.notRun = $(if ($pkg) { "not installed (brokendev.ps1 emulators install $($Emu.id))" } else { 'the Workshop is not built (brokendev.ps1 build-workshop)' }) }
    }
    if (-not $rec.notRun -and $pkg) { $pre = @(Test-BdEmuPrereqs $pkg); if ($pre.Count -gt 0) { $rec.notRun = "this machine lacks: $($pre -join ', ')" } }
    $fn = Get-BdAdapterFunction -Adapter ([string]$Emu.adapter)
    if (-not $rec.notRun -and -not $fn) {
        $le = Get-BdAdapterLoadError -Adapter ([string]$Emu.adapter)
        $rec.notRun = if ($le) { "the $($Emu.adapter) adapter does not load (lib\adapters\$($Emu.adapter).ps1, $le)" } else { "the $($Emu.adapter) adapter is missing (lib\adapters\$($Emu.adapter).ps1)" }
    }
    if ($rec.notRun) {
        $v = Get-BdCompatVerdict -NotRun $rec.notRun
        $rec.verdict = $v.Verdict; $rec.why = $v.Why; $rec.counts = $v
        return [pscustomobject]$rec
    }
    $emuOut = Join-Path $OutDir $id; $emuWork = Join-Path $WorkDir $id
    foreach ($d in $emuOut, $emuWork) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }; New-Item -ItemType Directory -Force -Path $d | Out-Null }
    $prior = @{}
    $refShotsByScenario = @{}
    if ($Reference) { foreach ($rs in @($Reference.scenarios)) { $h = @{}; foreach ($sh in @($rs.shotStats)) { $h[[int]$sh.Frame] = $sh }; $refShotsByScenario[[string]$rs.id] = $h } }
    $records = @()
    foreach ($sc in $Scenarios) {
        $sOut = Join-Path $emuOut $sc.Id; $sWork = Join-Path $emuWork $sc.Id
        New-Item -ItemType Directory -Force -Path $sOut, $sWork | Out-Null
        $romCopy = Join-Path $sWork (Split-Path -Leaf $Rom)
        Copy-Item -LiteralPath $Rom -Destination $romCopy -Force
        $chain = if ($sc.Saves) { $sc.Saves } else { $sc.Id }
        $saveDir = Join-Path $emuWork "saves-$chain"
        New-Item -ItemType Directory -Force -Path $saveDir | Out-Null
        $specText = New-BdCompatSpecText -Run @{ Console = $console; Rom = $romCopy; Out = $sOut; SaveDir = $saveDir; Frames = $sc.Frames; Reads = $Reads; Input = $sc.Input; Shots = $sc.Shots }
        $specPath = Join-Path $sOut 'spec.txt'
        [System.IO.File]::WriteAllText($specPath, $specText, (New-Object System.Text.UTF8Encoding($false)))
        $call = @{ Emu = $Emu; Package = $pkg; Exe = $exe; SpecPath = $specPath; Spec = (Read-BdCompatSpec -Text $specText); Scratch = $sWork; TimeoutSec = $TimeoutSec }
        if ([string]$Emu.adapter -eq 'workshop') {
            if ($sc.PowerOnRam) { $call['PowerOnRam'] = $sc.PowerOnRam }
            $cores = Get-BdProp $Manifest.verify 'cores' $null; if ($cores) { $call['Cores'] = $cores }
            if ($sc.Ntsc) { $call['Ntsc'] = $true }
        }
        $srec = [ordered]@{ id = $sc.Id; what = $sc.What; frames = $sc.Frames; input = $sc.Input; saves = $chain; end = $null; exitCode = $null; seconds = $null
                            emu = ''; notes = @(); shots = @(); shotStats = @(); audio = $null; saveFiles = @(); uninitialisedReads = @(); changes = [ordered]@{}; checks = @(); adapterError = '' }
        $r = $null
        Write-BdInfo ("  {0,-16} {1,-12} {2} frames ..." -f $id, $sc.Id, $sc.Frames)
        try { $r = & $fn @call } catch { $srec.adapterError = $_.Exception.Message }
        # an adapter that finds it cannot run here at all (an outdated Workshop, a missing component) says CANNOT-RUN: the column is
        # 'cannot be tested', not a failure of the game
        if ($srec.adapterError -like 'CANNOT-RUN:*') {
            $rec.notRun = $srec.adapterError.Substring(11).Trim()
            $v = Get-BdCompatVerdict -NotRun $rec.notRun
            $rec.verdict = $v.Verdict; $rec.why = $v.Why; $rec.counts = $v
            return [pscustomobject]$rec
        }
        if ($r) { $srec.exitCode = $r.ExitCode; $srec.seconds = $r.Seconds; if ($r.PSObject.Properties['Settings']) { $rec.settings = $r.Settings } }
        $resPath = Join-Path $sOut 'result.txt'
        $res = $null
        if (Test-Path -LiteralPath $resPath) { $res = ConvertFrom-BdCompatResult -Text ([System.IO.File]::ReadAllText($resPath)) }
        if ($res) {
            $srec.emu = $res.Emu; $srec.notes = @($res.Notes)
            if ($res.Errors.Count -gt 0) { $srec.notes += @($res.Errors | Select-Object -First 5 | ForEach-Object { "RESULT FILE ERROR: $_" }) }
            for ($i = 0; $i -lt @($res.Reads).Count; $i++) { $srec.changes[[string]$res.Reads[$i]] = Get-BdReadChanges -Result $res -Index $i }
            if ($res.Audio.Count -gt 0) { $on = @($res.Audio.Values | Where-Object { $_ -eq 1 }).Count; $srec.audio = [ordered]@{ windows = $res.Audio.Count; active = $on } }
        }
        # the run's end: the adapter's own word, else what happened to the process
        $end = $null
        if ($res -and $res.End) { $end = [ordered]@{ state = $res.End.State; frame = $res.End.Frame; text = $res.End.Text } }
        elseif ($srec.adapterError) { $end = [ordered]@{ state = 'crashed'; frame = $(if ($res) { $res.LastFrame } else { 0 }); text = "adapter: $($srec.adapterError)" } }
        elseif ($r -and $r.TimedOut) { $end = [ordered]@{ state = 'timeout'; frame = $(if ($res) { $res.LastFrame } else { 0 }); text = "still running after $TimeoutSec s" } }
        elseif ($r -and $r.Hung) { $end = [ordered]@{ state = 'hung'; frame = $(if ($res) { $res.LastFrame } else { 0 }); text = 'the emulator stopped advancing frames (its result file stopped growing)' } }
        else { $end = [ordered]@{ state = 'crashed'; frame = $(if ($res) { $res.LastFrame } else { 0 }); text = "the emulator exited ($(if ($r) { $r.ExitCode } else { '?' })) without finishing$(if ($r -and $r.Output) { ': ' + ((Select-BdTail $r.Output 2) -replace '\s+', ' ') })" } }
        if ($end.state -eq 'done' -and [int]$end.frame -lt $sc.Frames) { $end.state = 'crashed'; $end.text = "the adapter said done at frame $($end.frame) of $($sc.Frames)" }
        if ($res -and $res.Errors.Count -gt 0 -and $end.state -eq 'done') { $end.state = 'crashed'; $end.text = "the result file has $($res.Errors.Count) malformed line(s): $($res.Errors[0])" }
        $srec.end = $end
        # pictures: measured the same way for every emulator; similarity against BrokenNes' picture of the same frame
        $shotH = @{}
        $refShots = if ($refShotsByScenario.ContainsKey($sc.Id)) { $refShotsByScenario[$sc.Id] } else { @{} }
        if ($res) {
            foreach ($f in @($res.Shots.Keys | Sort-Object)) {
                $p = Join-Path $sOut ([string]$res.Shots[$f])
                if (-not (Test-Path -LiteralPath $p)) { continue }
                try { $st = Get-BdPictureStats -Path $p } catch { $srec.notes += "picture $f unreadable: $($_.Exception.Message)"; continue }
                $sim = $null
                if ($refShots.ContainsKey([int]$f)) { try { $sim = Get-BdPictureSimilarity -A $st.Path -B $refShots[[int]$f].Path } catch { } }
                $o = [pscustomobject]@{ Frame = [int]$f; Path = $st.Path; File = (($id + '/' + $sc.Id + '/' + (Split-Path -Leaf $st.Path))); Width = $st.Width; Height = $st.Height; Hash = $st.Hash; Colors = $st.Colors; Similarity = $sim }
                $shotH[[int]$f] = $o; $srec.shotStats += $o
            }
        }
        if ($rec.reference) { $refShots = $shotH }   # BrokenNes' own pictures are the reference: a 'similar' check on them compares them with themselves
        $srec.shots = @($srec.shotStats | ForEach-Object { [ordered]@{ frame = $_.Frame; file = $_.File; width = $_.Width; height = $_.Height; hash = $_.Hash; colors = $_.Colors; similarityToBrokenNes = $_.Similarity } })
        $srec.saveFiles = @(Get-ChildItem -LiteralPath $saveDir -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ name = $_.FullName.Substring($saveDir.Length + 1); bytes = $_.Length } })
        if ($r -and $r.Output) { $srec.uninitialisedReads = @(Get-BdUninitialisedReads -Output $r.Output -Symbols $Symbols -Console $console) }
        # checks, in order (a check may be relative to an earlier one's first frame)
        $local = @{}
        foreach ($c in $sc.Checks) {
            $cr = Test-BdCompatCheck -Check $c -Result $res -Shots $shotH -Can $Emu.can -Prior $local -RefShots $refShots
            $local[[string]$c.id] = $cr.FirstFrame
            $prior["$($sc.Id)/$($c.id)"] = $cr.FirstFrame
            $srec.checks += $cr
        }
        $records += [pscustomobject]$srec
        $bad = @($srec.checks | Where-Object { $_.Status -eq 'fail' }).Count
        Write-BdInfo ("  {0,-16} {1,-12} {2} at frame {3} in {4} s; checks: {5} pass, {6} fail, {7} n/a" -f $id, $sc.Id, $end.state, $end.frame, $srec.seconds, @($srec.checks | Where-Object { $_.Status -eq 'pass' }).Count, $bad, @($srec.checks | Where-Object { $_.Status -eq 'n/a' }).Count)
    }
    $rec.scenarios = $records
    $rec.firstFrames = $prior
    [pscustomobject]$rec
}

function ConvertTo-BdPairs {
    <# key -> value pairs of a hashtable, an ordered dictionary or an object (an adapter's Settings may be any of them). #>
    param($O)
    if ($null -eq $O) { return @() }
    if ($O -is [System.Collections.IDictionary]) { return @($O.Keys | ForEach-Object { [pscustomobject]@{ Key = [string]$_; Value = $O[$_] } }) }
    @($O.PSObject.Properties | ForEach-Object { [pscustomobject]@{ Key = $_.Name; Value = $_.Value } })
}

function Invoke-BdCompat {
    <#
      'compat <project>': the project's compat scenarios on BrokenNes (always first: the reference) and on every other emulator that runs its
      console (or the -EmuIds given), each from the same input; then every check on every run, a verdict per emulator, and the record:
      out\<project>\compat\compat.json, compat.html (the contact sheet) and the pictures. Returns the record (its .ok: no emulator 'fails').
    #>
    param([Parameter(Mandatory)][string]$Project, [string[]]$EmuIds = @(), [string[]]$Scenarios = @(), [int]$TimeoutSec = 600)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $m = Read-BdManifest -Id $Project
    $compat = Get-BdProp $m 'compat' $null
    if (-not $compat) { throw "project $Project has no 'compat' section in its manifest" }
    $verifyIds = @($m.verify.scenarios | ForEach-Object { [string]$_.id })
    $errs = @(Test-BdCompatSection -Compat $compat -VerifyIds $verifyIds)
    if ($errs.Count -gt 0) { throw ("compat section of $Project is invalid:`n  " + ($errs -join "`n  ")) }
    $ctx = New-BdContext $m
    $out = [string]$ctx['Out']
    $bp = Join-Path $out 'build.json'
    if (-not (Test-Path -LiteralPath $bp)) { throw "project $Project has not been built (no $bp). Run: brokendev.ps1 build $Project" }
    $build = Read-BdJson $bp
    if (-not $build.ok) { throw "the last build of $Project failed; fix it before compat" }
    $romOut = $build.outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1
    if (-not $romOut -or -not (Test-Path -LiteralPath $romOut.published)) { throw "the built ROM of $Project is missing" }
    $rom = [string]$romOut.published
    $reads = @(Resolve-BdCompatReads -Compat $compat -Console ([string]$m.console) -Ctx $ctx)
    $symFile = ''; $symbols = @{}
    $ss = Get-BdProp $compat 'symbols' $null
    if ($ss) { $symFile = Expand-BdPath -Text ([string]$ss.file) -Context $ctx; $symbols = Read-BdSymbols -Path $symFile }
    $scen = @(Get-BdCompatScenarios -Manifest $m)
    if ($Scenarios.Count -gt 0) {
        foreach ($s in $Scenarios) { if (@($scen | ForEach-Object { $_.Id }) -notcontains $s) { throw "unknown compat scenario '$s' (known: $((@($scen | ForEach-Object { $_.Id })) -join ', '))" } }
        # a scenario that shares a save chain needs the ones before it in that chain (a fresh boot needs the save)
        $keep = @{}
        foreach ($s in $scen) { if ($Scenarios -contains $s.Id) { $keep[$s.Id] = $true; if ($s.Saves) { foreach ($t in $scen) { if ($t.Saves -eq $s.Saves -and [array]::IndexOf(@($scen), $t) -lt [array]::IndexOf(@($scen), $s)) { $keep[$t.Id] = $true } } } } }
        $scen = @($scen | Where-Object { $keep.ContainsKey($_.Id) })
    }
    $J = Read-BdEmulators
    $console = [string]$m.console
    $chosen = @()
    if ($EmuIds.Count -gt 0) { foreach ($e in $EmuIds) { $chosen += (Get-BdEmulator -Id $e -J $J) } }
    else { $chosen = @($J.emulators | Where-Object { @($_.consoles) -contains $console }) }
    $ref = $J.emulators | Where-Object { Get-BdProp $_ 'reference' $false } | Select-Object -First 1
    $chosen = @($ref) + @($chosen | Where-Object { $_.id -ne $ref.id })
    $compatDir = Join-Path $out 'compat'
    $workDir = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') 'compat') $Project
    New-Item -ItemType Directory -Force -Path $compatDir, $workDir | Out-Null
    $tol = [int](Get-BdProp $compat 'frameTolerance' 30)
    $emuRecs = @(); $refRec = $null
    foreach ($e in $chosen) {
        $er = Invoke-BdCompatEmulator -Manifest $m -Emu $e -J $J -Scenarios $scen -Reads $reads -Rom $rom -OutDir $compatDir -WorkDir $workDir -Reference $refRec -Symbols $symbols -TimeoutSec $TimeoutSec
        $isRef = [bool](Get-BdProp $e 'reference' $false)
        if (-not $er.notRun) {
            $refFirst = if ($refRec) { $refRec.firstFrames } else { @{} }
            $v = Get-BdCompatVerdict -Records $er.scenarios -RefFirst $refFirst -Tolerance $tol -IsReference:$isRef
            $er.verdict = $v.Verdict; $er.why = $v.Why; $er.counts = [ordered]@{ passed = $v.Passed; failed = $v.Failed; differs = $v.Differs; notApplicable = $v.NotApplicable }
        }
        if ($isRef) { $refRec = $er }
        $emuRecs += [pscustomobject]$er
        $color = switch ($er.verdict) { 'works' { 'Green' } 'differs' { 'Yellow' } 'fails' { 'Red' } default { 'DarkGray' } }
        Write-Host ("  => {0,-16} {1}" -f $er.id, $er.verdict.ToUpperInvariant()) -ForegroundColor $color
        foreach ($w in @($er.why | Select-Object -First 6)) { Write-Host "       $w" -ForegroundColor $color }
    }
    $record = [ordered]@{
        schema = 1; project = $Project; title = [string]$m.title; console = $console; kind = [string]$m.kind
        rom = $rom; romSha256 = [string]$romOut.sha256; symbols = $symFile
        reads = @($reads | ForEach-Object { [ordered]@{ name = $_.Name; domain = $_.Domain; addr = ('{0:X}' -f $_.Addr); symbol = $_.Symbol } })
        frameTolerance = $tol; ranAtUtc = (Get-Date).ToUniversalTime().ToString('o'); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        ok = (@($emuRecs | Where-Object { $_.verdict -eq 'fails' }).Count -eq 0)
        scenarios = @($scen | ForEach-Object { [ordered]@{ id = $_.Id; what = $_.What; verify = $_.Verify; frames = $_.Frames; input = $_.Input; shots = $_.Shots; saves = $_.Saves; powerOnRamOnBrokenNes = $_.PowerOnRam; checks = $_.Checks } })
        emulators = @($emuRecs | ForEach-Object { ConvertTo-BdCompatJsonEmu $_ })
    }
    Save-BdJson -Object $record -Path (Join-Path $compatDir 'compat.json') -Depth 14
    Write-BdCompatSheet -Record ([pscustomobject]$record) -EmuRecs $emuRecs -Path (Join-Path $compatDir 'compat.html')
    Write-BdOk "  compat record: $(Join-Path $compatDir 'compat.json')"
    Write-BdOk "  contact sheet: $(Join-Path $compatDir 'compat.html')"
    [pscustomobject]$record
}

function ConvertTo-BdCompatJsonEmu {
    <# An emulator's record as it goes into compat.json (no file-system objects, no parsed traces). #>
    param($E)
    [ordered]@{
        id = $E.id; title = $E.title; version = $E.version; reference = $E.reference; adapter = $E.adapter; verdict = $E.verdict; why = @($E.why); counts = $E.counts
        canCheck = $E.can; notRun = $E.notRun; settings = $E.settings
        scenarios = @(@($E.scenarios) | ForEach-Object {
            [ordered]@{ id = $_.id; end = $_.end; exitCode = $_.exitCode; seconds = $_.seconds; emu = $_.emu; notes = @($_.notes); saves = $_.saves
                        audio = $_.audio; saveFiles = @($_.saveFiles); uninitialisedReads = @($_.uninitialisedReads | ForEach-Object { "$($_.Addr)$(if ($_.Symbol) { ' ' + $_.Symbol })" })
                        changes = $_.changes; shots = @($_.shots)
                        checks = @($_.checks | ForEach-Object { [ordered]@{ id = $_.Id; what = $_.What; kind = $_.Kind; status = $_.Status; detail = $_.Detail; firstFrame = $_.FirstFrame; compare = $_.Compare } }) }
        })
    }
}

function ConvertTo-BdHtml { param([AllowNull()][AllowEmptyString()][string]$Text) if ($null -eq $Text) { return '' } [System.Net.WebUtility]::HtmlEncode($Text) }

function Write-BdCompatSheet {
    <#
      The contact sheet: one row per picture (scenario @ frame), BrokenNes first then each emulator, each cell the picture with its frame, colours,
      similarity to BrokenNes and the recorded game state at that frame; above it the verdicts, below it every check by emulator. One HTML file
      beside the pictures (relative links), no script, readable in light and dark.
    #>
    param([Parameter(Mandatory)]$Record, [Parameter(Mandatory)]$EmuRecs, [Parameter(Mandatory)][string]$Path)
    $H = { param($t) ConvertTo-BdHtml ([string]$t) }
    $sb = New-Object System.Text.StringBuilder
    $css = @'
:root { --bg:#fbfaf7; --fg:#1d1d1b; --muted:#6b6a65; --line:#dedcd4; --card:#ffffff; --works:#1f7a3a; --differs:#9a6700; --fails:#b42318; --na:#6b6a65; }
@media (prefers-color-scheme: dark) { :root { --bg:#161614; --fg:#ecebe6; --muted:#a3a29b; --line:#34332f; --card:#1f1f1c; --works:#5bc27a; --differs:#e3b341; --fails:#f47067; --na:#a3a29b; } }
body { background:var(--bg); color:var(--fg); font:14px/1.45 system-ui, Segoe UI, sans-serif; margin:0; padding:16px; }
h1 { font-size:20px; margin:0 0 4px; } h2 { font-size:16px; margin:24px 0 8px; }
.muted { color:var(--muted); } code { font-size:12px; }
table { border-collapse:collapse; } th, td { border:1px solid var(--line); padding:6px; vertical-align:top; text-align:left; }
th { background:var(--card); position:sticky; top:0; }
.wrap { overflow-x:auto; max-width:100%; }
img { image-rendering:pixelated; width:256px; height:auto; display:block; border:1px solid var(--line); }
.cell { font-size:12px; min-width:256px; } .v { font-weight:600; text-transform:uppercase; font-size:12px; }
.works { color:var(--works); } .differs { color:var(--differs); } .fails { color:var(--fails); } .na, .cannot { color:var(--na); }
.pass { color:var(--works); } .fail { color:var(--fails); } .differs-c { color:var(--differs); }
ul { margin:4px 0; padding-left:18px; }
'@
    [void]$sb.Append("<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><meta name=`"viewport`" content=`"width=device-width, initial-scale=1`"><title>Compat $(& $H $Record.project)</title><style>$css</style></head><body>")
    [void]$sb.Append("<h1>$(& $H $Record.title)</h1><div class=`"muted`">compat of <code>$(& $H $Record.project)</code> ($(& $H $Record.console)), ROM sha256 <code>$(& $H ([string]$Record.romSha256).Substring(0, [math]::Min(16, ([string]$Record.romSha256).Length)))</code>, $(& $H $Record.ranAtUtc), frame tolerance $($Record.frameTolerance). BrokenNes is the reference column.</div>")
    $cls = { param($v) switch ($v) { 'works' { 'works' } 'differs' { 'differs' } 'fails' { 'fails' } default { 'cannot' } } }
    [void]$sb.Append('<h2>Verdicts</h2><div class="wrap"><table><tr><th>Emulator</th><th>Verdict</th><th>Why</th><th>Ran with</th></tr>')
    foreach ($e in $EmuRecs) {
        $why = if (@($e.why).Count -gt 0) { '<ul>' + ((@($e.why) | Select-Object -First 12 | ForEach-Object { "<li>$(& $H $_)</li>" }) -join '') + '</ul>' } else { '' }
        $set = if ($e.settings) { '<ul>' + ((ConvertTo-BdPairs $e.settings | ForEach-Object { "<li>$(& $H $_.Key): $(& $H ([string]$_.Value))</li>" }) -join '') + '</ul>' } else { '' }
        [void]$sb.Append("<tr><td><b>$(& $H $e.title)</b><br><span class=`"muted`">$(& $H $e.id) $(& $H $e.version)</span></td><td class=`"v $(& $cls $e.verdict)`">$(& $H $e.verdict)</td><td>$why</td><td class=`"muted`" style=`"font-size:12px`">$set</td></tr>")
    }
    [void]$sb.Append('</table></div>')
    $ran = @($EmuRecs | Where-Object { -not $_.notRun })
    [void]$sb.Append('<h2>Pictures</h2><div class="wrap"><table><tr><th>Scenario @ frame</th>')
    foreach ($e in $ran) { [void]$sb.Append("<th>$(& $H $e.title) <span class=`"v $(& $cls $e.verdict)`">$(& $H $e.verdict)</span></th>") }
    [void]$sb.Append('</tr>')
    foreach ($s in @($Record.scenarios)) {
        foreach ($f in @($s.shots)) {
            [void]$sb.Append("<tr><td><b>$(& $H $s.id)</b> @ $f</td>")
            foreach ($e in $ran) {
                $es = @($e.scenarios) | Where-Object { $_.id -eq $s.id } | Select-Object -First 1
                $shot = if ($es) { @($es.shotStats) | Where-Object { $_.Frame -eq [int]$f } | Select-Object -First 1 } else { $null }
                if (-not $shot) {
                    $why = if ($es -and $es.end -and $es.end.state -ne 'done') { "$($es.end.state) at frame $($es.end.frame)" } else { 'no picture' }
                    [void]$sb.Append("<td class=`"cell muted`">$(& $H $why)</td>"); continue
                }
                $state = @()
                if ($es.changes) {
                    foreach ($k in @($es.changes.Keys)) {
                        $val = $null
                        foreach ($pair in ([string]$es.changes[$k] -split ' ')) { if ($pair -match '^(\d+)=(\d+)$') { if ([int]$Matches[1] -le [int]$f) { $val = $Matches[2] } else { break } } }
                        if ($null -ne $val) { $state += "$k=$val" }
                    }
                }
                $sim = if ($null -ne $shot.Similarity) { " &middot; like BrokenNes $($shot.Similarity)" } else { '' }
                [void]$sb.Append("<td class=`"cell`"><img src=`"$(& $H $shot.File)`" alt=`"$(& $H $e.id) $(& $H $s.id) frame $f`" loading=`"lazy`"><div>$($shot.Width)x$($shot.Height), $($shot.Colors) colours$sim</div><div class=`"muted`">$(& $H ($state -join ' '))</div></td>")
            }
            [void]$sb.Append('</tr>')
        }
    }
    [void]$sb.Append('</table></div>')
    [void]$sb.Append('<h2>Checks</h2><div class="wrap"><table><tr><th>Scenario / check</th>')
    foreach ($e in $ran) { [void]$sb.Append("<th>$(& $H $e.title)</th>") }
    [void]$sb.Append('</tr>')
    foreach ($s in @($Record.scenarios)) {
        foreach ($c in @($s.checks)) {
            [void]$sb.Append("<tr><td><b>$(& $H $s.id)/$(& $H $c.id)</b><br><span class=`"muted`">$(& $H $c.what)</span></td>")
            foreach ($e in $ran) {
                $es = @($e.scenarios) | Where-Object { $_.id -eq $s.id } | Select-Object -First 1
                $cr = if ($es) { @($es.checks) | Where-Object { $_.Id -eq $c.id } | Select-Object -First 1 } else { $null }
                if (-not $cr) { [void]$sb.Append('<td class="muted">-</td>'); continue }
                $k = switch ($cr.Status) { 'pass' { 'pass' } 'fail' { 'fail' } 'differs' { 'differs-c' } default { 'na' } }
                [void]$sb.Append("<td style=`"font-size:12px`"><span class=`"v $k`">$(& $H $cr.Status)</span> $(& $H $cr.Detail)</td>")
            }
            [void]$sb.Append('</tr>')
        }
        [void]$sb.Append("<tr><td><b>$(& $H $s.id)</b> run end</td>")
        foreach ($e in $ran) {
            $es = @($e.scenarios) | Where-Object { $_.id -eq $s.id } | Select-Object -First 1
            $t = if ($es -and $es.end) { "$($es.end.state) at frame $($es.end.frame) ($($es.seconds) s)$(if ($es.end.text) { ': ' + $es.end.text })" } else { '-' }
            $extra = @()
            if ($es -and $es.audio) { $extra += "sound active in $($es.audio.active) of $($es.audio.windows) 30-frame windows" }
            if ($es -and @($es.saveFiles).Count -gt 0) { $extra += 'saves: ' + ((@($es.saveFiles) | ForEach-Object { "$($_.name) ($($_.bytes) B)" }) -join ', ') }
            if ($es -and @($es.uninitialisedReads).Count -gt 0) { $extra += "read before written: $((@($es.uninitialisedReads) | Select-Object -First 12 | ForEach-Object { "$($_.Addr) $($_.Symbol)".Trim() }) -join ', ')$(if (@($es.uninitialisedReads).Count -gt 12) { ' ...' })" }
            [void]$sb.Append("<td style=`"font-size:12px`">$(& $H $t)$(if ($extra.Count) { '<br><span class="muted">' + (& $H ($extra -join '; ')) + '</span>' })</td>")
        }
        [void]$sb.Append('</tr>')
    }
    [void]$sb.Append('</table></div>')
    $notRun = @($EmuRecs | Where-Object { $_.notRun })
    if ($notRun.Count -gt 0) { [void]$sb.Append('<h2>Not run</h2><ul>' + (($notRun | ForEach-Object { "<li><b>$(& $H $_.title)</b>: $(& $H $_.notRun)</li>" }) -join '') + '</ul>') }
    [void]$sb.Append('<p class="muted">Made by BrokenDev compat (BrokenDev\lib\Compat.ps1). compat.json beside this page holds every value shown here.</p></body></html>')
    [System.IO.File]::WriteAllText($Path, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------------------------------- play

function Invoke-BdPlay {
    <#
      'play <project> -Emu <id>': opens the built ROM in that emulator for hand testing: a copy of the ROM and the emulator's saves live in
      BROKENDEV_HOME\work\play\<project>\<emulator> (the saves persist from one play to the next; nothing is written beside the built ROM).
      BrokenNes: the desktop app through the 'run' verb.
    #>
    param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$EmuId)
    $J = Read-BdEmulators
    $e = Get-BdEmulator -Id $EmuId -J $J
    $m = Read-BdManifest -Id $Project
    if (@($e.consoles) -notcontains [string]$m.console) { throw "$($e.title) does not run the $($m.console) ($Project is a $($m.console) game)" }
    if ([string]$e.adapter -eq 'workshop') { Write-BdInfo 'BrokenNes: opening the built ROM in the desktop app (the run verb)'; Invoke-BdProjectRun -Id $Project; return }
    $pkg = Get-BdEmuPackage -Id ([string]$e.package) -J $J
    $exe = Get-BdEmuExe -Emu $e -J $J
    if (-not $exe) { throw "$($e.title) is not installed (brokendev.ps1 emulators install $EmuId)" }
    $pre = @(Test-BdEmuPrereqs $pkg); if ($pre.Count -gt 0) { throw "this machine lacks: $($pre -join ', ')" }
    $fn = Get-BdAdapterFunction -Adapter ([string]$e.adapter) -Prefix 'Start-BdPlay'
    if (-not $fn) { throw "the $($e.adapter) adapter has no play launcher (Start-BdPlay$($e.adapter))" }
    $bp = Join-Path (Get-BdOutDir $Project) 'build.json'
    if (-not (Test-Path -LiteralPath $bp)) { throw "project $Project has not been built. Run: brokendev.ps1 build $Project" }
    $rom = (Read-BdJson $bp).outputs | Where-Object { $_.id -eq 'rom' } | Select-Object -First 1
    if (-not $rom -or -not (Test-Path -LiteralPath $rom.published)) { throw "the built ROM of $Project is missing" }
    $work = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work\play') $Project) $EmuId
    $scratch = Join-Path $work 'scratch'; $saves = Join-Path $work 'saves'
    # The saves belong to one build: FCEUX's mapper-30 save is the whole PRG flash and is RUN at the next power-on, so a save made by an older
    # build would replace the new program. A new ROM moves the old saves aside (kept, never deleted) and starts with an empty folder.
    $stamp = Join-Path $saves '.brokendev-rom-sha256'
    if ((Test-Path -LiteralPath $saves) -and (Test-Path -LiteralPath $stamp) -and ([System.IO.File]::ReadAllText($stamp).Trim() -ne [string]$rom.sha256)) {
        $old = [System.IO.File]::ReadAllText($stamp).Trim()
        $aside = Join-Path $work ("saves-{0}-{1}" -f $old.Substring(0, [math]::Min(12, $old.Length)), (Get-Date).ToString('yyyyMMdd-HHmmss'))
        Move-Item -LiteralPath $saves -Destination $aside
        Write-BdWarn "the ROM changed since the last play: the saves of the older build were moved to $aside"
    }
    New-Item -ItemType Directory -Force -Path $scratch, $saves | Out-Null
    [System.IO.File]::WriteAllText($stamp, [string]$rom.sha256)
    $copy = Join-Path $scratch (Split-Path -Leaf ([string]$rom.published))
    Copy-Item -LiteralPath ([string]$rom.published) -Destination $copy -Force
    $p = & $fn -Emu $e -Package $pkg -Exe $exe -Rom $copy -SaveDir $saves -Scratch $scratch
    Write-BdOk "$($e.title): $Project started (PID $($p.Id)); saves in $saves"
}

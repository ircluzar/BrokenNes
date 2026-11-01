# BrokenDev adapter: Snes9x 1.63, the standalone Windows x64 build (snes9x-x64.exe). Dot-sourced by lib\Compat.ps1.
#
# The standalone Windows build has no scripting and no command-line movie or dump switches (its S9xParseArg, win32\wconfig.cpp, knows only
# -removeregistrykeys -restore -hidemenu -fullscreen -cartb; the shared S9xParseArgs adds sound/region/-snapshot/-conf switches). So the run
# is driven through the program's own menu commands, sent as window messages to the window of the process this adapter started (by PID):
#
#   1. Everything lives in <Scratch>\snes9x: a COPY of snes9x-x64.exe (Snes9x reads and REWRITES snes9x.conf in the folder of its exe and
#      writes stdout.txt, stderr.txt and Valid.Ext there: win32\win32.cpp S9xGetDirectory(DEFAULT_DIR) = GetModuleFileName's folder), its
#      snes9x.conf, the movies, the raw screenshots and save states. The battery save folder (Dir:SRAM) is the spec's savedir. The install
#      folder is never written. The config sets AddToRegistry = FALSE: Snes9x's default (TRUE) writes HKCU file associations at start-up
#      (wsnes9x.cpp LoadExts: if (GUI.AddToRegistry) { RegisterProgid(); RegisterExts(); }).
#   2. Input: a .smv movie (format: movie.cpp/movie.h, version 5, "start from power-on" = MOVIE_OPT_FROM_RESET, the battery save embedded)
#      opened with File > Movie > Play (WM_COMMAND 40113 = ID_FILE_MOVIE_PLAY; the IDD_OPENMOVIE dialog: path box 1208, read-only box
#      1242, OK). S9xMovieOpen then power-on resets (S9xReset) and loads the first 128 KB of SRAM from the movie.
#   3. Frames: Snes9x is paused (File > Pause, WM_COMMAND 40026) and stepped ONE frame at a time with its Frame Advance command (WM_COMMAND
#      40074 = ID_FRAME_ADVANCE), each step preceded by its Save Screenshot command (WM_COMMAND 40151 = ID_SAVESCREENSHOT). The screenshot
#      is written at the end of that very frame (gfx.cpp S9xEndScreenRefresh -> S9xDoScreenshot), so the picture file appearing proves the
#      frame ran: the next step is sent only then (commands posted earlier would be pumped before the frame and merge into one step).
#   4. Memory (when the spec has reads): after each frame, Save State slot 0 (WM_COMMAND 44000 = ID_FILE_SAVE0, sent synchronously) and the
#      128 KB work RAM is read from the state's "RAM" block (snapshot.cpp: Memory.RAM, 0x20000 bytes = SNES $7E0000-$7FFFFF). Saving a
#      state does not disturb the emulation (measured: identical pictures with and without the per-frame states).
#   5. The end: WM_CLOSE; Snes9x saves the battery RAM on WM_DESTROY into Dir:SRAM (the savedir) and exits.
#
# Frame alignment. A Snes9x frame (one S9xMainLoop call, wsnes9x.cpp main loop) runs from one vblank start (V=225) to the next; the first one
# after power-on starts at V=0, like BrokenNes' RunFrame (power-on to the first vblank start). So "after N frames" is the same point in both
# and picture N is the same picture. Movie input: S9xMovieUpdate applies the next sample at the start of each frame after the first
# (cpuexec.cpp, when SCAN_KEYS_FLAG is set), i.e. at the vblank start that ended the previous frame; sample 0 is applied by S9xMovieOpen.
# So sample k is held during frame k+1, exactly like the spec's input k in BrokenNes. The auto-joypad read is where the two differ: Snes9x
# does it two lines AFTER the vblank start (V = ScreenHeight + 3), with the new sample; BrokenNes latches it AT the vblank start that ends a
# frame, with that frame's input. Hence two alignments:
#   manual      sample k = input k            a game that reads the pads by hand ($4016/$4017) sees what BrokenNes' game sees
#   autojoypad  sample k = input k-1 (k>=1)   a game that reads the auto-joypad registers ($4218-$421F) sees what BrokenNes' game sees
# The adapter picks one from the game itself (-InputAlign probe, the default): before the run it plays the game without input and reads
# NMITIMEN ($4200, Snes9x's FillRAM copy in the save state's FIL block) after each frame; the auto-joypad enable bit (bit 0) seen by the
# frame of the first button press (+30) means autojoypad, never seen means manual. Measured on 2026-10-22: Super VRUN ($4200 = $81) and the
# Project Nested VRUN ($4200 = $20) each replay BrokenNes' run exactly with their alignment (the read variables on every frame, the
# pictures) and diverge (another first room) with the other one.

Set-StrictMode -Version Latest

$script:BdS9xCmd = @{ MoviePlay = 40113; FrameAdvance = 40074; Screenshot = 40151; SaveSlot0 = 44000; Pause = 40026 }
# The SNES joypad word as Snes9x stores it in movies (controls.cpp MovieSetJoypad -> joypad[i].buttons, written to $4218/$4219 as is).
$script:BdS9xBits = [ordered]@{ B = 0x8000; Y = 0x4000; Select = 0x2000; Start = 0x1000; Up = 0x0800; Down = 0x0400; Left = 0x0200; Right = 0x0100
                                A = 0x0080; X = 0x0040; L = 0x0020; R = 0x0010 }

$script:BdS9xNativeSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class BdS9xNative {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeoutStr(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint SMTO_ABORTIFHUNG = 2;
    const uint WM_COMMAND = 0x0111, WM_SETTEXT = 0x000C, WM_CLOSE = 0x0010, BM_SETCHECK = 0x00F1;

    // Starts a GUI program with the given show command (7 = SW_SHOWMINNOACTIVE: minimised, never takes the focus). Returns the PID.
    public static int Start(string exe, string args, string cwd, short show) {
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO)); si.dwFlags = 1; si.wShowWindow = show;
        PROCESS_INFORMATION pi;
        var cmd = new StringBuilder("\"" + exe + "\" " + args);
        if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, cwd, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string TextOf(IntPtr h) { var sb = new StringBuilder(1024); GetWindowText(h, sb, 1024); return sb.ToString(); }

    public static IntPtr[] TopWindows(int pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == (uint)pid) list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }
    public static IntPtr FindTop(int pid, string cls) {
        foreach (var h in TopWindows(pid)) if (ClassOf(h) == cls) return h;
        return IntPtr.Zero;
    }
    // Every dialog box of the process with its texts ("title | text | text"), for error reports.
    public static string[] Dialogs(int pid) {
        var o = new List<string>();
        foreach (var h in TopWindows(pid)) {
            if (ClassOf(h) != "#32770") continue;
            var parts = new List<string>(); parts.Add(TextOf(h));
            EnumChildWindows(h, (c, l) => { string t = TextOf(c); if (t.Length > 0 && ClassOf(c) == "Static") parts.Add(t); return true; }, IntPtr.Zero);
            o.Add(String.Join(" | ", parts.ToArray()));
        }
        return o.ToArray();
    }
    public static IntPtr FindDialogWith(int pid, int ctrlId) {
        foreach (var h in TopWindows(pid)) if (ClassOf(h) == "#32770" && GetDlgItem(h, ctrlId) != IntPtr.Zero) return h;
        return IntPtr.Zero;
    }

    public static bool PostCommand(IntPtr h, int id) { return PostMessage(h, WM_COMMAND, (IntPtr)id, IntPtr.Zero); }
    public static bool PostClose(IntPtr h) { return PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
    // Synchronous: returns when the window's thread has handled the message (false = timeout or hung).
    public static bool Send(IntPtr h, uint msg, int w, IntPtr l, uint timeoutMs) { IntPtr r; return SendMessageTimeout(h, msg, (IntPtr)w, l, SMTO_ABORTIFHUNG, timeoutMs, out r) != IntPtr.Zero; }
    public static bool SendCommand(IntPtr h, int id, uint timeoutMs) { return Send(h, WM_COMMAND, id, IntPtr.Zero, timeoutMs); }
    public static bool Ping(IntPtr h, uint timeoutMs) { return Send(h, 0, 0, IntPtr.Zero, timeoutMs); }
    public static bool SetText(IntPtr h, string s, uint timeoutMs) { IntPtr r; return SendMessageTimeoutStr(h, WM_SETTEXT, IntPtr.Zero, s, SMTO_ABORTIFHUNG, timeoutMs, out r) != IntPtr.Zero; }
    public static bool SetCheck(IntPtr h, bool on, uint timeoutMs) { return Send(h, BM_SETCHECK, on ? 1 : 0, IntPtr.Zero, timeoutMs); }
    public static bool Click(IntPtr dlg, int id) { return PostMessage(dlg, WM_COMMAND, (IntPtr)(id & 0xFFFF), GetDlgItem(dlg, id)); }

    // Waits for a file to exist: 0 = there, 1 = timeout, 2 = the process is gone.
    public static int WaitForFile(string path, int timeoutMs, int pid) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        while (true) {
            if (File.Exists(path)) return 0;
            if (sw.ElapsedMilliseconds > timeoutMs) return 1;
            if ((++n & 63) == 0) { try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) { if (p.HasExited) return 2; } } catch (ArgumentException) { return 2; } }
            if (sw.ElapsedMilliseconds < 50) Thread.Sleep(0); else Thread.Sleep(1);
        }
    }

    // A file another process may still hold open: retried for a moment.
    public static byte[] ReadShared(string path) {
        for (int i = 0; ; i++) {
            try { using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { var b = new byte[f.Length]; int o = 0; while (o < b.Length) { int k = f.Read(b, o, b.Length - o); if (k <= 0) break; o += k; } return b; } }
            catch (IOException) { if (i > 200) throw; Thread.Sleep(5); }
        }
    }

    // Blocks of a Snes9x freeze file (snapshot.cpp: "#!s9xsnp:NNNN\n", then blocks "XXX:NNNNNN:" + data, a size over 999999 written as
    // "XXX:------:" with the size big-endian in bytes 6-9; the file is gzip-compressed). Every name asked for must be there.
    public static Dictionary<string, byte[]> FreezeBlocks(string path, string[] names) {
        byte[] raw = ReadShared(path);
        byte[] d = raw;
        if (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B) {
            using (var ms = new MemoryStream(raw)) using (var gz = new GZipStream(ms, CompressionMode.Decompress)) using (var o = new MemoryStream()) { gz.CopyTo(o); d = o.ToArray(); }
        }
        int pos = Array.IndexOf(d, (byte)'\n');
        if (pos < 0 || pos > 64) throw new InvalidDataException("not a Snes9x freeze file (no header line): " + path);
        string head = Encoding.ASCII.GetString(d, 0, pos);
        if (!head.StartsWith("#!s9xsnp")) throw new InvalidDataException("not a Snes9x freeze file (header '" + head + "'): " + path);
        pos++;
        var want = new HashSet<string>(names);
        var res = new Dictionary<string, byte[]>();
        while (pos + 11 <= d.Length && res.Count < want.Count) {
            string bn = Encoding.ASCII.GetString(d, pos, 3);
            string sz = Encoding.ASCII.GetString(d, pos + 4, 6);
            if (d[pos + 3] != (byte)':' || d[pos + 10] != (byte)':') throw new InvalidDataException("bad block header at " + pos + " in " + path);
            int len;
            if (sz == "------") len = (d[pos + 6] << 24) | (d[pos + 7] << 16) | (d[pos + 8] << 8) | d[pos + 9];
            else len = int.Parse(sz);
            int start = pos + 11;
            if (start + len > d.Length) throw new InvalidDataException("block " + bn + " runs past the end of " + path);
            if (want.Contains(bn)) { var b = new byte[len]; Buffer.BlockCopy(d, start, b, 0, len); res[bn] = b; }
            pos = start + len;
        }
        foreach (var n in names) if (!res.ContainsKey(n)) throw new InvalidDataException("no " + n + " block in " + path);
        return res;
    }

    static uint[] crcTable;
    public static uint Crc32(byte[] b, int offset, int count) {
        if (crcTable == null) { var t = new uint[256]; for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; } crcTable = t; }
        uint crc = 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++) crc = crcTable[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // Bytes of a and b that differ in [from, to).
    public static int CountDiff(byte[] a, byte[] b, int from, int to) {
        int n = 0;
        for (int i = from; i < to && i < a.Length && i < b.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }
}
'@

function Initialize-BdS9xNative {
    if ('BdS9xNative' -as [type]) { return }
    Add-Type -TypeDefinition $script:BdS9xNativeSource -Language CSharp
}

# ---------------------------------------------------------------------------------------------------- the movie

function Get-BdS9xJoypadWord {
    param([string[]]$Buttons)
    $w = 0
    foreach ($b in @($Buttons)) {
        if (-not $b) { continue }
        $k = @($script:BdS9xBits.Keys | Where-Object { $_ -ieq $b }) | Select-Object -First 1
        if (-not $k) { throw "snes9x: '$b' is not a SNES button" }
        $w = $w -bor $script:BdS9xBits[$k]
    }
    $w
}

function Get-BdS9xMovieSamples {
    <#
      The joypad-1 word of every movie sample 0..Samples-1 (see the frame alignment note at the top of this file). -Align manual: sample k =
      the spec's input for frame index k. -Align autojoypad: sample k = input k-1 for k >= 1, sample 0 = input 0. $Inputs: the spec's input
      lines ({Frame; Buttons}), any order.
    #>
    param([Parameter(Mandatory)][int]$Samples, $Inputs, [ValidateSet('manual', 'autojoypad')][string]$Align = 'autojoypad')
    $steps = @(@($Inputs) | Where-Object { $null -ne $_ } | Sort-Object { [int]$_.Frame })
    $words = New-Object 'int[]' $Samples
    $si = 0; $cur = 0
    for ($k = 0; $k -lt $Samples; $k++) {
        $f = if ($Align -eq 'manual') { $k } else { [math]::Max(0, $k - 1) }
        while ($si -lt $steps.Count -and [int]$steps[$si].Frame -le $f) { $cur = Get-BdS9xJoypadWord -Buttons @($steps[$si].Buttons); $si++ }
        $words[$k] = $cur
    }
    , $words
}

function New-BdS9xMovie {
    <#
      Writes a Snes9x .smv (version 5, movie.cpp) that starts from power-on (MOVIE_OPT_FROM_RESET) with joypad 1 recorded and a joypad
      plugged in both ports. Layout: the 64-byte header (little-endian: magic "SMV\x1A", version 5, id, rerecords 0, MaxFrame,
      controller mask 0x01, options 0x01, 0, sync flags DATA_EXISTS|HASROMINFO, savestate offset, controller data offset, MaxSample,
      port types JOYPAD/JOYPAD, port ids 0,-1,-1,-1 / 1,-1,-1,-1), no metadata, the 30-byte ROM info (3 zero bytes, CRC32, 23-byte name),
      the 128 KB SRAM a from-reset movie starts with, 0xCC up to a multiple of 16, then MaxSample+1 samples of 2 bytes.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][int[]]$Words, [Parameter(Mandatory)][byte[]]$Sram, [uint32]$RomCrc = 0, [string]$RomName = '')
    if ($Sram.Length -ne 0x20000) { throw 'snes9x movie: the SRAM block must be 128 KB' }
    $maxSample = $Words.Length - 1
    if ($maxSample -lt 1) { throw 'snes9x movie: at least two samples' }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $saveOffset = 64 + 30
    $dataOffset = $saveOffset + 0x20000
    if ($dataOffset % 16) { $dataOffset += 16 - ($dataOffset % 16) }
    $bw.Write([uint32]0x1a564d53)                 # "SMV\x1A"
    $bw.Write([uint32]5)                          # SMV_VERSION
    $bw.Write([uint32]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))
    $bw.Write([uint32]0)                          # rerecords
    $bw.Write([uint32]$maxSample)                 # MaxFrame (no polling-only samples: one sample per frame)
    $bw.Write([byte]0x01)                         # controllers mask: joypad 1
    $bw.Write([byte]0x01)                         # MOVIE_OPT_FROM_RESET
    $bw.Write([byte]0x00)
    $bw.Write([byte]0x41)                         # MOVIE_SYNC_DATA_EXISTS | MOVIE_SYNC_HASROMINFO
    $bw.Write([uint32]$saveOffset)
    $bw.Write([uint32]$dataOffset)
    $bw.Write([uint32]$maxSample)                 # MaxSample
    $bw.Write([byte]1); $bw.Write([byte]1)        # port types: CTL_JOYPAD, CTL_JOYPAD
    $bw.Write([byte[]]@(0, 0xFF, 0xFF, 0xFF, 1, 0xFF, 0xFF, 0xFF))
    $bw.Write((New-Object 'byte[]' 18))           # to 64
    $bw.Write((New-Object 'byte[]' 3))
    $bw.Write([uint32]$RomCrc)
    $nb = New-Object 'byte[]' 23
    $an = [System.Text.Encoding]::ASCII.GetBytes($RomName)
    [Array]::Copy($an, $nb, [math]::Min(23, $an.Length))
    $bw.Write($nb)
    $bw.Write($Sram)
    while ($ms.Length -lt $dataOffset) { $bw.Write([byte]0xCC) }
    foreach ($w in $Words) { $bw.Write([uint16]$w) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

function Get-BdS9xRomInfo {
    <#
      CRC32 (of the image without a 512-byte copier header, as Snes9x prints it), the internal title and the battery RAM size of a LoROM or
      HiROM image (the header with a valid checksum complement wins; title and map mode break ties).
    #>
    param([Parameter(Mandatory)][string]$Path)
    Initialize-BdS9xNative
    $b = [System.IO.File]::ReadAllBytes($Path)
    $off = if (($b.Length % 1024) -eq 512) { 512 } else { 0 }
    $crc = [BdS9xNative]::Crc32($b, $off, $b.Length - $off)
    $best = $null; $bestScore = -1
    foreach ($h in 0x7FC0, 0xFFC0, 0x40FFC0) {
        $at = $off + $h
        if ($at + 0x20 -gt $b.Length) { continue }
        $score = 0
        $sum = $b[$at + 0x1E] -bor ($b[$at + 0x1F] -shl 8); $cmp = $b[$at + 0x1C] -bor ($b[$at + 0x1D] -shl 8)
        if (($sum -bxor $cmp) -eq 0xFFFF) { $score += 4 }
        $title = [System.Text.Encoding]::ASCII.GetString($b, $at, 21)
        if ($title -match '^[\x20-\x7E]{21}$') { $score += 2 }
        $map = $b[$at + 0x15] -band 0x0F
        if (($h -eq 0x7FC0 -and $map -in 0, 2, 3) -or ($h -ne 0x7FC0 -and $map -in 1, 5)) { $score += 1 }
        if ($score -gt $bestScore) { $bestScore = $score; $best = $at }
    }
    $name = ''; $sram = 0
    if ($null -ne $best) {
        $name = ([System.Text.Encoding]::ASCII.GetString($b, $best, 21) -replace '[^\x20-\x7E]', ' ').TrimEnd()
        $n = [int]$b[$best + 0x18]
        if ($n -ge 1 -and $n -le 9) { $sram = 1024 -shl $n }
    }
    [pscustomobject]@{ Crc32 = $crc; Name = $name; SramBytes = $sram; HeaderBytes = $off }
}

# ---------------------------------------------------------------------------------------------------- the configuration

function New-BdS9xConfigText {
    <#
      A snes9x.conf. Snes9x keeps every key it does not find at its default and rewrites the full file at start-up. Directories are absolute.
      -Mode compat: sound muted and not synced, no frame skip, no pausing when the window is inactive, no rewind, no on-screen texts, no
      screenshot inside save states, movies read-only. -Mode play: Snes9x's own defaults except the folders, the registry and the display
      driver.
    #>
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$SaveDir, [ValidateSet('compat', 'play')][string]$Mode = 'compat')
    foreach ($p in $Root, $SaveDir) { if ($p -match '#') { throw "snes9x: the folder '$p' has a '#', which starts a comment in snes9x.conf" } }
    $d = @{}
    foreach ($n in 'roms', 'shots', 'movies', 'states', 'misc') { $d[$n] = Join-Path $Root $n }
    $s = [ordered]@{}
    # OpenGL: Direct3D (the default) needs the DirectX 9 runtime; without it Snes9x opens an error box at start-up (seen on this machine).
    $s['Display\Win'] = [ordered]@{ 'OutputMethod' = '2'; 'Fullscreen:Enabled' = 'FALSE'; 'Fullscreen:FullscreenOnOpen' = 'FALSE' }
    # Never write file associations into HKCU (Snes9x's default does at every start-up).
    $s['Settings\Win'] = [ordered]@{ 'AddToRegistry' = 'FALSE'; 'ConfirmSaveLoad' = 'FALSE' }
    $s['Settings\Win\Files'] = [ordered]@{ 'Dir:Roms' = $d.roms; 'Dir:Screenshots' = $d.shots; 'Dir:Movies' = $d.movies; 'Dir:SPCs' = $d.misc
                                           'Dir:Savestates' = $d.states; 'Dir:SRAM' = $SaveDir; 'Dir:Cheats' = $d.misc; 'Dir:Patches' = $d.misc
                                           'Dir:Bios' = $d.misc; 'Dir:SatData' = $d.misc; 'Dir:Lock' = 'TRUE' }
    if ($Mode -eq 'compat') {
        $s['Display'] = [ordered]@{ 'MessagesInImage' = 'FALSE'; 'FrameRate' = 'OFF'; 'DisplayInput' = 'FALSE'; 'DisplayFrameCount' = 'FALSE' }
        $s['Display\Win']['Vsync'] = 'FALSE'
        $s['Display\Win']['Window:Maximized'] = 'FALSE'
        # AutoSaveDelay 0: no timed battery saves (gfx.cpp: a zero delay clears SRAMModified), so the savedir is written once, when Snes9x closes
        # StretchScreenshots 0 (read by the shared snes9x.cpp loader, not written back by the Windows build): pictures at the native size,
        # a hi-res frame 512x224 rather than doubled in height (screenshot.cpp)
        $s['Settings'] = [ordered]@{ 'FrameSkip' = '0'; 'TurboFrameSkip' = '0'; 'AutoMaxSkipFramesAtOnce' = '0'; 'SnapshotScreenshots' = 'OFF'; 'MovieTruncateAtEnd' = 'FALSE'
                                     'AutoSaveDelay' = '0'; 'StretchScreenshots' = '0' }
        foreach ($k in 'PauseWhenInactive=FALSE', 'RewindBufferSize=0', 'FrameAdvanceSkipsNonInput=FALSE', 'MovieDefaultReadOnly=TRUE', 'CustomRomOpenDialog=FALSE') {
            $kk, $vv = $k -split '=', 2; $s['Settings\Win'][$kk] = $vv
        }
        $s['Sound'] = [ordered]@{ 'Sync' = '0'; 'Mute' = 'TRUE' }
        $s['Sound\Win'] = [ordered]@{ 'MuteFrameAdvance' = 'TRUE' }
        $s['Controls\Win'] = [ordered]@{ 'Input:Background' = 'OFF'; 'Input:BackgroundKeyHotkeys' = 'OFF' }
    }
    $lines = @('# written by BrokenDev (lib\adapters\snes9x.ps1); Snes9x rewrites this file with every key at start-up')
    foreach ($sec in $s.Keys) {
        $lines += ''; $lines += "[$sec]"
        foreach ($k in $s[$sec].Keys) { $lines += "$k = $($s[$sec][$k])" }
    }
    ($lines -join "`r`n") + "`r`n"
}

function Initialize-BdS9xFolder {
    <# <Scratch>\<Name> with a fresh copy of the exe (Snes9x writes its config and logs beside its exe) and the folders the config names. #>
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Scratch, [Parameter(Mandatory)][string]$SaveDir, [string]$Mode = 'compat', [string]$Name = 'snes9x')
    $root = Join-Path $Scratch $Name
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    $emu = Join-Path $root 'emu'
    foreach ($p in @($emu) + @('roms', 'shots', 'movies', 'states', 'misc' | ForEach-Object { Join-Path $root $_ }) + @($SaveDir)) {
        if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Force -Path $p | Out-Null }
    }
    $exeCopy = Join-Path $emu (Split-Path -Leaf $Exe)
    Copy-Item -LiteralPath $Exe -Destination $exeCopy -Force
    $conf = Join-Path $emu 'snes9x.conf'
    [System.IO.File]::WriteAllText($conf, (New-BdS9xConfigText -Root $root -SaveDir $SaveDir -Mode $Mode), (New-Object System.Text.UTF8Encoding($false)))
    [pscustomobject]@{ Root = $root; Emu = $emu; Exe = $exeCopy; Conf = $conf; Shots = (Join-Path $root 'shots'); Movies = (Join-Path $root 'movies'); States = (Join-Path $root 'states') }
}

# ---------------------------------------------------------------------------------------------------- one Snes9x process

function Start-BdS9xSession {
    <#
      Starts the exe of a prepared folder on the ROM (minimised, without focus), waits for the ROM to be loaded and pauses Snes9x at once:
      File > Pause (a toggle; LoadROM leaves Snes9x running), sent synchronously; at most one more frame runs before Snes9x waits in its
      message loop. Proof: a screenshot request then produces no picture (no frame runs while paused). The request stays pending and is
      taken by the next frame, which every step asks for anyway. Returns the session (Process, Pid, Main window, Png and State paths).
    #>
    param([Parameter(Mandatory)]$Folder, [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][uint32]$HangMs, [int]$StartSec = 60)
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Rom)
    $procId = [BdS9xNative]::Start($Folder.Exe, ('"' + $Rom + '"'), $Folder.Emu, 7)
    $p = [System.Diagnostics.Process]::GetProcessById($procId)
    $null = $p.Handle
    $s = [pscustomobject]@{ Process = $p; Pid = $procId; Main = [IntPtr]::Zero; Png = (Join-Path $Folder.Shots "${stem}000.png"); State = (Join-Path $Folder.States "$stem.000"); HangMs = $HangMs }
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            if ($p.HasExited) { throw "Snes9x exited at start-up (code $($p.ExitCode))" }
            $m = [BdS9xNative]::FindTop($procId, 'Snes9x: WndClass')
            if ($m -ne [IntPtr]::Zero -and ([BdS9xNative]::TextOf($m)).StartsWith("$stem - ")) { $s.Main = $m; break }
            $dl = @([BdS9xNative]::Dialogs($procId))
            if ($dl.Count -gt 0) { throw "Snes9x opened a dialog at start-up: $($dl -join ' // ')" }
            if ($sw.Elapsed.TotalSeconds -gt $StartSec) { throw "Snes9x did not load the ROM within $StartSec s" }
            Start-Sleep -Milliseconds 10
        }
        $paused = $false
        for ($try = 0; $try -lt 3 -and -not $paused; $try++) {
            if (-not [BdS9xNative]::SendCommand($s.Main, $script:BdS9xCmd.Pause, $HangMs)) { throw 'Snes9x did not answer File > Pause' }
            Start-Sleep -Milliseconds 100
            if (Test-Path -LiteralPath $s.Png) { Remove-Item -LiteralPath $s.Png -Force }
            if (-not [BdS9xNative]::SendCommand($s.Main, $script:BdS9xCmd.Screenshot, $HangMs)) { throw 'Snes9x did not answer Save Screenshot' }
            $paused = ([BdS9xNative]::WaitForFile($s.Png, 700, $procId) -eq 1)
        }
        if (-not $paused) { throw 'Snes9x could not be paused' }
    } catch { Stop-BdS9xSession -Session $s; throw }
    $s
}

function Stop-BdS9xSession {
    <# Ends the process by its PID (never by name) when it is still there. #>
    param($Session)
    if (-not $Session -or -not $Session.Process) { return }
    if (-not $Session.Process.HasExited) { Stop-BdProcessTree -ProcessId $Session.Pid; try { [void]$Session.Process.WaitForExit(10000) } catch { } }
}

function Invoke-BdS9xStep {
    <#
      One frame: a screenshot request (synchronous), Frame Advance (posted), then the picture proves the frame ran and Snes9x answering a
      message proves it is back in its message loop with the frame complete. Returns ok, hung or gone. The picture is left at $Session.Png.
    #>
    param([Parameter(Mandatory)]$Session)
    if (Test-Path -LiteralPath $Session.Png) { Remove-Item -LiteralPath $Session.Png -Force }
    if (-not [BdS9xNative]::SendCommand($Session.Main, $script:BdS9xCmd.Screenshot, $Session.HangMs)) { return 'hung' }
    if (-not [BdS9xNative]::PostCommand($Session.Main, $script:BdS9xCmd.FrameAdvance)) { return 'gone' }
    $w = [BdS9xNative]::WaitForFile($Session.Png, [int]$Session.HangMs, $Session.Pid)
    if ($w -eq 1) { return 'hung' }
    if ($w -eq 2) { return 'gone' }
    if (-not [BdS9xNative]::Ping($Session.Main, $Session.HangMs)) { return 'hung' }
    'ok'
}

function Read-BdS9xState {
    <# Save State slot 0 (handled synchronously), then the named blocks of the state file. #>
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string[]]$Blocks)
    if (Test-Path -LiteralPath $Session.State) { Remove-Item -LiteralPath $Session.State -Force }
    if (-not [BdS9xNative]::SendCommand($Session.Main, $script:BdS9xCmd.SaveSlot0, $Session.HangMs)) { throw 'Snes9x did not answer Save State' }
    if (-not (Test-Path -LiteralPath $Session.State)) { throw "Snes9x wrote no save state ($($Session.State))" }
    [BdS9xNative]::FreezeBlocks($Session.State, $Blocks)
}

function Open-BdS9xMovie {
    <# File > Movie > Play with this movie, read-only, no input display. Snes9x then power-on resets and stays paused. #>
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Path)
    $procId = $Session.Pid
    [void][BdS9xNative]::PostCommand($Session.Main, $script:BdS9xCmd.MoviePlay)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $dlg = [IntPtr]::Zero
    while ($dlg -eq [IntPtr]::Zero) {
        $dlg = [BdS9xNative]::FindDialogWith($procId, 1208)
        if ($sw.Elapsed.TotalSeconds -gt 20) { throw "the movie dialog did not open (dialogs: $(@([BdS9xNative]::Dialogs($procId)) -join ' // '))" }
        if ($dlg -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 10 }
    }
    [void][BdS9xNative]::SetText([BdS9xNative]::GetDlgItem($dlg, 1208), $Path, 5000)
    [void][BdS9xNative]::SetCheck([BdS9xNative]::GetDlgItem($dlg, 1242), $true, 5000)
    $di = [BdS9xNative]::GetDlgItem($dlg, 1220); if ($di -ne [IntPtr]::Zero) { [void][BdS9xNative]::SetCheck($di, $false, 5000) }
    $ok = [BdS9xNative]::GetDlgItem($dlg, 1)
    if (-not [BdS9xNative]::IsWindowEnabled($ok)) { throw "Snes9x refused the movie $Path (dialog: $(@([BdS9xNative]::Dialogs($procId)) -join ' // '))" }
    [void][BdS9xNative]::Click($dlg, 1)
    $sw.Restart()
    while ([BdS9xNative]::IsWindow($dlg)) { if ($sw.Elapsed.TotalSeconds -gt 20) { throw 'the movie dialog did not close' }; Start-Sleep -Milliseconds 10 }
    if (-not [BdS9xNative]::Ping($Session.Main, $Session.HangMs)) { throw 'Snes9x stopped answering after opening the movie' }
    $dl = @([BdS9xNative]::Dialogs($procId))
    if ($dl.Count -gt 0) { throw "Snes9x could not play the movie: $($dl -join ' // ')" }
    # still paused: the screenshot request left pending by Start-BdS9xSession would be taken by the first frame that runs
    if (Test-Path -LiteralPath $Session.Png) { Remove-Item -LiteralPath $Session.Png -Force }
    if (-not [BdS9xNative]::SendCommand($Session.Main, $script:BdS9xCmd.Screenshot, $Session.HangMs)) { throw 'Snes9x did not answer Save Screenshot' }
    if ([BdS9xNative]::WaitForFile($Session.Png, 300, $procId) -ne 1) { throw 'Snes9x ran a frame on its own after opening the movie (it should be paused)' }
}

function Get-BdS9xInputAlign {
    <#
      The probe (see "Frame alignment" at the top of this file), in a Snes9x process of its own (<Scratch>\snes9x-probe, its battery folder a
      scratch copy of the savedir's save), ended afterwards: nothing the game does during the probe reaches the run. Plays the game from
      power-on without input up to frame $UpTo and reads NMITIMEN after every frame. Returns { Align; Why; Frames }.
    #>
    param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Scratch, [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][byte[]]$Sram,
          [string]$SrmPath = '', [Parameter(Mandatory)][int]$UpTo, [Parameter(Mandatory)][uint32]$HangMs, $Info, [double]$Deadline = 600, $Clock)
    $pf = Initialize-BdS9xFolder -Exe $Exe -Scratch $Scratch -SaveDir (Join-Path (Join-Path $Scratch 'snes9x-probe') 'saves') -Mode 'compat' -Name 'snes9x-probe'
    $saves = Join-Path $pf.Root 'saves'
    if ($SrmPath -and (Test-Path -LiteralPath $SrmPath)) { Copy-Item -LiteralPath $SrmPath -Destination $saves -Force }
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Rom)
    $movie = Join-Path $pf.Movies "$stem.smv"
    New-BdS9xMovie -Path $movie -Words (New-Object 'int[]' ($UpTo + 3)) -Sram $Sram -RomCrc $Info.Crc32 -RomName $Info.Name
    $s = $null
    try {
        $s = Start-BdS9xSession -Folder $pf -Rom $Rom -HangMs $HangMs
        Open-BdS9xMovie -Session $s -Path $movie
        $v = 0
        for ($fr = 1; $fr -le $UpTo; $fr++) {
            if ($Clock -and $Clock.Elapsed.TotalSeconds -gt $Deadline) { throw 'TIMEOUT' }
            $st = Invoke-BdS9xStep -Session $s
            if ($st -ne 'ok') { throw "the input probe stopped at frame ${fr}: Snes9x $st" }
            $v = [int]((Read-BdS9xState -Session $s -Blocks @('FIL'))['FIL'][0x4200])
            if ($v -band 1) {
                return [pscustomobject]@{ Align = 'autojoypad'; Frames = $fr; Why = ('the game turned the auto-joypad read on by frame {0} (NMITIMEN $4200 = ${1:X2}, read from Snes9x''s save state in a probe run without input)' -f $fr, $v) }
            }
        }
        [pscustomobject]@{ Align = 'manual'; Frames = $UpTo; Why = ('the game left the auto-joypad read off through frame {0} (NMITIMEN $4200 = ${1:X2}, read from Snes9x''s save state in a probe run without input): it reads the pads by hand' -f $UpTo, $v) }
    } finally { Stop-BdS9xSession -Session $s }
}

# ---------------------------------------------------------------------------------------------------- the run

function Get-BdS9xWorkFolder {
    <#
      Where a compat run's per-frame files live: the run's scratch folder when it is on a local disk, else a local one under %TEMP%
      (brokendev-snes9x\<a hash of the scratch path>; the adapter deletes it after the run). BROKENDEV_SNES9X_WORK names another base folder.
    #>
    param([Parameter(Mandatory)][string]$Scratch)
    $base = $env:BROKENDEV_SNES9X_WORK
    if (-not $base) {
        $d = Get-BdDriveInfo -Path $Scratch
        if (-not $d.IsNetwork) { return $Scratch }
        $base = Join-Path ([System.IO.Path]::GetTempPath()) 'brokendev-snes9x'
    }
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $h = -join ($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Scratch.ToLowerInvariant())) | Select-Object -First 6 | ForEach-Object { $_.ToString('x2') })
    $md5.Dispose()
    Join-Path $base $h
}

function Get-BdS9xRegistryState {
    <#
      The per-user file associations Snes9x writes when its AddToRegistry setting is TRUE (its default): read only, so a run can prove it
      wrote nothing. (A first probe on 2026-10-22, before the setting was known, did write them: see docs\projects\brokendev-emulators.md.)
    #>
    $h = [ordered]@{}
    foreach ($k in 'HKCU:\Software\Classes\Snes9x.Win32\DefaultIcon', 'HKCU:\Software\Classes\Snes9x.Win32\shell\open\command', 'HKCU:\Software\Classes\Applications\snes9x-x64.exe\shell\open\command') {
        $v = $null
        try { $v = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).'(default)' } catch { }
        $h[$k] = [string]$v
    }
    $h
}

function Invoke-BdAdapterSnes9x {
    # The registry guard around the run (a change is reported, never repaired: that is the user's to decide)
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SpecPath, [Parameter(Mandatory)]$Spec,
          [Parameter(Mandatory)][string]$Scratch, [int]$TimeoutSec = 600, [int]$HangSec = 120,
          [ValidateSet('probe', 'autojoypad', 'manual')][string]$InputAlign = 'probe')
    $regBefore = Get-BdS9xRegistryState
    $r = Invoke-BdAdapterSnes9xCore @PSBoundParameters
    $regAfter = Get-BdS9xRegistryState
    $changed = @($regBefore.Keys | Where-Object { $regBefore[$_] -ne $regAfter[$_] })
    if ($r -and $r.PSObject.Properties['Settings'] -and $r.Settings) {
        $r.Settings['registryCheck'] = if ($changed.Count -eq 0) { 'the Snes9x file associations in HKCU are unchanged by the run' } else { "CHANGED by the run: $($changed -join ', ')" }
    }
    if ($changed.Count -gt 0) { Write-BdWarn "snes9x: the run changed HKCU file associations: $($changed -join ', ') (now: $(@($changed | ForEach-Object { $regAfter[$_] }) -join ' | '))" }
    $r
}

function Invoke-BdAdapterSnes9xCore {
    <#
      Runs a spec on Snes9x (see the top of this file) and writes <Spec.Out>\result.txt as it goes. -InputAlign: probe (default: the game's
      auto-joypad bit decides), autojoypad or manual (see "Frame alignment" above). Returns ExitCode, TimedOut, Hung, Seconds, Pid, LogFile,
      Output and Settings (what Snes9x ran with).
    #>
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SpecPath, [Parameter(Mandatory)]$Spec,
          [Parameter(Mandatory)][string]$Scratch, [int]$TimeoutSec = 600, [int]$HangSec = 120,
          [ValidateSet('probe', 'autojoypad', 'manual')][string]$InputAlign = 'probe')
    Initialize-BdS9xNative
    if ([string]$Spec.Console -ne 'snes') { throw "snes9x runs the SNES only (the spec says '$($Spec.Console)')" }
    $frames = [int]$Spec.Frames
    if ($frames -le 0) { throw 'snes9x: the spec has no frames' }
    $rom = [string]$Spec.Rom
    if (-not (Test-Path -LiteralPath $rom)) { throw "snes9x: ROM not found: $rom" }
    $out = [string]$Spec.Out
    $saveDir = [string]$Spec.SaveDir
    if (-not (Test-Path -LiteralPath $out)) { New-Item -ItemType Directory -Force -Path $out | Out-Null }
    $reads = @($Spec.Reads)
    foreach ($r in $reads) { if ([string]$r.Domain -ne 'wram') { throw "snes9x: read $($r.Name): domain '$($r.Domain)' (only wram)" } }
    $shotSet = @{}; foreach ($s in @($Spec.Shots)) { $shotSet[[int]$s] = $true }
    $version = if ($Package) { [string]$Package.version } else { '1.63' }
    $pressed = @(@($Spec.Inputs) | Where-Object { $null -ne $_ -and @($_.Buttons | Where-Object { $_ }).Count -gt 0 } | Sort-Object { [int]$_.Frame })
    $hangMs = [uint32]([math]::Max(5, $HangSec) * 1000)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # Every frame writes and reads a picture and a save state: on a network share (BROKENDEV_HOME is P: on the first machine) that is 9
    # frames/s instead of 40 (measured). Those transient files go to a local folder; the log stays in the run's scratch, the battery save
    # in the savedir, the kept pictures in the out folder.
    $realScratch = $Scratch
    $Scratch = Get-BdS9xWorkFolder -Scratch $realScratch
    $f = Initialize-BdS9xFolder -Exe $Exe -Scratch $Scratch -SaveDir $saveDir -Mode 'compat'
    $logPath = Join-Path $realScratch 'snes9x-adapter.log'
    $log = New-Object System.Collections.Generic.List[string]
    function Add-Log([string]$m) { $log.Add(('{0,7:0.00}s {1}' -f $sw.Elapsed.TotalSeconds, $m)) }

    # the battery save a run starts with: the savedir's (else Snes9x's fresh battery RAM: 0x60 bytes, memmap.cpp SRAMInitialValue)
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($rom)
    $info = Get-BdS9xRomInfo -Path $rom
    $sramFull = New-Object 'byte[]' 0x80000
    for ($i = 0; $i -lt $sramFull.Length; $i++) { $sramFull[$i] = 0x60 }
    $srmPath = Join-Path $saveDir "$stem.srm"
    $sramFrom = "none in savedir: Snes9x's fresh battery RAM (0x60 bytes)"
    if (Test-Path -LiteralPath $srmPath) {
        $sb = [System.IO.File]::ReadAllBytes($srmPath)
        [Array]::Copy($sb, $sramFull, [math]::Min($sb.Length, $sramFull.Length))
        $sramFrom = "$stem.srm from savedir ($($sb.Length) bytes)"
    } else { $srmPath = '' }
    $sram = New-Object 'byte[]' 0x20000
    [Array]::Copy($sramFull, $sram, 0x20000)

    $settings = [ordered]@{
        exe = "$($f.Exe) (a copy of ${Exe}: Snes9x reads and rewrites snes9x.conf beside its exe)"
        config = $f.Conf
        region = 'auto from the ROM header (memmap.cpp InitROM)'
        pictureSize = 'native: 256x224 (239 with overscan, 512 wide in hi-res, doubled height interlaced); StretchScreenshots 0'
        colours = 'Snes9x screenshot PNG: 5 bits per channel shifted left 3 (0-248)'
        sound = 'muted (Sound Mute TRUE, Sync 0), XAudio2 driver'
        frameSkip = '0 (every frame rendered)'
        pauseWhenInactive = 'FALSE'
        powerOnWram = '0x55 everywhere (cpu.cpp S9xReset)'
        battery = "$sramFrom; the movie sets the first 128 KB at power-on; no timed saves (AutoSaveDelay 0); written to the savedir when Snes9x closes"
        input = 'a .smv movie from power-on (File > Movie > Play, read-only)'
        inputAlign = $InputAlign
        frames = 'Frame Advance (WM_COMMAND 40074) one frame at a time, each confirmed by its screenshot'
        pictures = 'Save Screenshot (WM_COMMAND 40151) before every frame; the PNG of a requested frame is kept'
        memory = $(if ($reads.Count -gt 0) { 'Save State slot 0 (WM_COMMAND 44000) after every frame, its RAM block (128 KB WRAM)' } else { 'not requested' })
        display = 'OpenGL (OutputMethod 2), window minimised without focus'
        registry = 'AddToRegistry FALSE (nothing written)'
        workFolder = $(if ($Scratch -ne $realScratch) { "$Scratch (local: the run's scratch is on a network share; deleted after the run)" } else { $Scratch })
    }

    # result.txt: written as the run goes (BrokenDev watches it grow)
    $resPath = Join-Path $out 'result.txt'
    $res = New-Object System.IO.StreamWriter($resPath, $false, (New-Object System.Text.UTF8Encoding($false)))
    $res.WriteLine('# bdcompat-result 1')
    $res.WriteLine("emu Snes9x $version (standalone Windows x64 build, snes9x-x64.exe), driven by its own menu commands")
    $res.WriteLine(('reads ' + (@($reads | ForEach-Object { [string]$_.Name }) -join ' ')).TrimEnd())
    $res.WriteLine('note frames: Snes9x is stepped one frame at a time from power-on with its own Frame Advance, each step confirmed by the picture it wrote for that frame: frame numbers and pictures are exact')
    $res.WriteLine('note pictures: Snes9x''s own screenshot at the end of the frame, native size, 5-bit colour channels shifted left 3')
    if ($reads.Count -gt 0) { $res.WriteLine('note memory: wram from Snes9x''s own save state (slot 0) after every frame, its RAM block (the 128 KB work RAM)') }
    else { $res.WriteLine('note memory: no reads requested (Snes9x gives wram through its save states)') }
    $res.WriteLine('note power-on: Snes9x fills work RAM with 0x55 (BrokenNes: zeros); battery RAM ' + $sramFrom)
    $res.WriteLine('note settings: region from the ROM header, sound muted, no frame skip, no pause when inactive, OpenGL display')
    $res.Flush()

    $s = $null; $timedOut = $false; $hung = $false; $endLine = ''; $last = 0; $exitCode = -1; $shotsSaved = 0
    try {
        # the input alignment (see the top of this file)
        $align = $InputAlign
        $alignWhy = 'given by the caller'
        if ($align -eq 'probe') {
            if ($pressed.Count -eq 0) { $align = 'autojoypad'; $alignWhy = 'no button is pressed in the spec: either alignment gives the same run' }
            else {
                $upTo = [math]::Min([math]::Max([int]$pressed[0].Frame + 30, 60), 900)
                try { $pr = Get-BdS9xInputAlign -Exe $Exe -Scratch $Scratch -Rom $rom -Sram $sram -SrmPath $srmPath -UpTo $upTo -HangMs $hangMs -Info $info -Deadline $TimeoutSec -Clock $sw }
                catch { if ($_.Exception.Message -eq 'TIMEOUT') { $timedOut = $true }; throw }
                $align = $pr.Align; $alignWhy = $pr.Why
            }
            Add-Log "input alignment: $align ($alignWhy)"
        }
        $settings.inputAlign = "$align ($alignWhy)"
        if ($align -eq 'manual') {
            $res.WriteLine("note input: a .smv movie from power-on, sample k = the spec input for frame index k (held during the same frame as in BrokenNes) because $alignWhy. A game reading the auto-joypad registers would see each input one frame earlier than in BrokenNes")
        } else {
            $res.WriteLine("note input: a .smv movie from power-on, sample k = the spec input for frame index k-1, so the auto-joypad read at the end of frame k sees the buttons BrokenNes latches there; $alignWhy. A game reading `$4016/`$4017 by hand during a frame would see each input one frame later than in BrokenNes")
        }
        $res.Flush()

        # the run: a fresh Snes9x, paused, the movie (power-on reset), then every frame
        $words = Get-BdS9xMovieSamples -Samples ($frames + 3) -Inputs $Spec.Inputs -Align $align
        $movie = Join-Path $f.Movies "$stem.smv"
        New-BdS9xMovie -Path $movie -Words $words -Sram $sram -RomCrc $info.Crc32 -RomName $info.Name
        $s = Start-BdS9xSession -Folder $f -Rom $rom -HangMs $hangMs
        Add-Log "started PID $($s.Pid), window '$([BdS9xNative]::TextOf($s.Main))' minimised=$([BdS9xNative]::IsIconic($s.Main)); paused"
        if ($info.SramBytes -gt 0x20000) {
            # battery RAM above the 128 KB the movie sets: what the game wrote there in the frames Snes9x ran before the pause stays
            $sra = (Read-BdS9xState -Session $s -Blocks @('SRA'))['SRA']
            $nd = [BdS9xNative]::CountDiff($sra, $sramFull, 0x20000, [math]::Min($info.SramBytes, $sra.Length))
            Add-Log "battery RAM above 128 KB at the pause: $nd bytes differ from what Snes9x loaded"
            if ($nd -gt 0) { $res.WriteLine("note battery: the game wrote $nd bytes of its battery RAM above 128 KB in the frames Snes9x ran before the pause; the movie resets only the first 128 KB") }
        }
        Open-BdS9xMovie -Session $s -Path $movie
        Add-Log ("movie {0} open ({1} samples, {2} alignment, ROM CRC32 {3:x8}, battery: {4}); power-on, paused" -f $movie, $words.Length, $align, $info.Crc32, $sramFrom)

        for ($fr = 1; $fr -le $frames; $fr++) {
            if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) { $timedOut = $true; break }
            $st = Invoke-BdS9xStep -Session $s
            if ($st -eq 'hung') {
                $hung = $true
                $dl = @([BdS9xNative]::Dialogs($s.Pid))
                Add-Log ("frame ${fr}: no picture or no answer within $HangSec s" + $(if ($dl.Count) { "; Snes9x shows: $($dl -join ' // ')" } else { '' }))
                break
            }
            if ($st -eq 'gone') { Add-Log "frame ${fr}: Snes9x exited"; $endLine = "end crashed $last Snes9x exited during frame $fr"; break }
            if ($shotSet.ContainsKey($fr)) {
                $name = 'shot_{0:D5}.png' -f $fr
                Move-Item -LiteralPath $s.Png -Destination (Join-Path $out $name) -Force
                $res.WriteLine("shot $fr $name"); $shotsSaved++
            }
            if ($reads.Count -gt 0) {
                $ram = (Read-BdS9xState -Session $s -Blocks @('RAM'))['RAM']
                if ($ram.Length -lt 0x20000) { throw "the save state's RAM block has $($ram.Length) bytes" }
                $vals = @($reads | ForEach-Object { [int]$ram[[int]$_.Addr] })
                $res.WriteLine("t $fr " + ($vals -join ' '))
            } else {
                $res.WriteLine("t $fr")
            }
            $last = $fr
            if (($fr % 60) -eq 0 -or $shotSet.ContainsKey($fr)) { $res.Flush() }
        }
        Add-Log "stepped to frame $last ($shotsSaved pictures kept)"
        if (-not $timedOut -and -not $hung -and -not $endLine -and $last -eq $frames) {
            # a clean exit: Snes9x saves the battery RAM into Dir:SRAM (the savedir) on WM_DESTROY
            [void][BdS9xNative]::PostClose($s.Main)
            if ($s.Process.WaitForExit(30000)) { $exitCode = $s.Process.ExitCode; $endLine = "end done $frames"; Add-Log "exited $exitCode" }
            else { Add-Log 'did not exit within 30 s of WM_CLOSE'; $endLine = "end crashed $frames Snes9x did not close" }
        }
    } catch {
        Add-Log "error: $($_.Exception.Message)"
        if (-not $endLine -and -not $timedOut -and -not $hung) { $endLine = "end crashed $last $($_.Exception.Message -replace '\s+', ' ')" }
    } finally {
        Stop-BdS9xSession -Session $s
        if ($endLine -and -not $timedOut -and -not $hung) { $res.WriteLine($endLine) }
        $res.Flush(); $res.Dispose()
        foreach ($n in 'stdout.txt', 'stderr.txt') {
            $fp = Join-Path $f.Emu $n
            if (-not (Test-Path -LiteralPath $fp)) { continue }
            # one 'saved.' line per screenshot and state: counted, not copied
            $lines = @([System.IO.File]::ReadAllLines($fp))
            $saved = @($lines | Where-Object { $_ -match ' saved\.$' }).Count
            $log.Add("--- $n ($saved 'saved.' lines left out) ---")
            foreach ($l in $lines) { if ($l -notmatch ' saved\.$') { $log.Add($l) } }
        }
        [System.IO.File]::WriteAllText($logPath, ($log -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
        if ($Scratch -ne $realScratch -and (Test-Path -LiteralPath $Scratch)) {
            foreach ($try in 1..5) { try { Remove-Item -LiteralPath $Scratch -Recurse -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 } }
        }
    }
    if ($s -and $s.Process.HasExited -and $exitCode -eq -1 -and -not $timedOut -and -not $hung) { try { $exitCode = $s.Process.ExitCode } catch { } }
    if ($timedOut -or $hung) { $exitCode = -1 }
    [pscustomobject]@{ ExitCode = $exitCode; TimedOut = $timedOut; Hung = $hung; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Pid = $(if ($s) { $s.Pid } else { 0 })
                       LogFile = $logPath; Output = ($log -join "`n"); Settings = $settings }
}

# ---------------------------------------------------------------------------------------------------- play (hand testing)

function Start-BdPlaySnes9x {
    <#
      Opens the ROM in Snes9x for hand testing and returns the started process (not waited for). A copy of the exe and its snes9x.conf live in
      <Scratch>\snes9x (Snes9x writes its config and logs beside its exe), battery saves go to $SaveDir, every other folder (screenshots,
      states, movies...) into <Scratch>\snes9x. Snes9x's own defaults otherwise (sound on, normal speed, pause when inactive), except: no
      registry entries, OpenGL display.
    #>
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Rom, [Parameter(Mandatory)][string]$SaveDir,
          [Parameter(Mandatory)][string]$Scratch)
    Initialize-BdS9xNative
    if (-not (Test-Path -LiteralPath $Rom)) { throw "snes9x: ROM not found: $Rom" }
    $f = Initialize-BdS9xFolder -Exe $Exe -Scratch $Scratch -SaveDir $SaveDir -Mode 'play'
    $procId = [BdS9xNative]::Start($f.Exe, ('"' + $Rom + '"'), $f.Emu, 1)   # SW_SHOWNORMAL
    $p = [System.Diagnostics.Process]::GetProcessById($procId)
    $null = $p.Handle
    $p
}

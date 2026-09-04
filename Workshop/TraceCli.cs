using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenNes half of a cross-emulator deterministic replay tracer. Emits one record per frame in a
/// shared, emulator-independent format so a differ can find the exact frame where BrokenNes and a
/// reference emulator (Mesen) stop agreeing.
///
/// The RGB framebuffer is deliberately NOT part of the record. NES palettes are not standardized,
/// so two *correct* emulators legitimately produce different RGB. What v2 adds instead is every
/// emulator-INDEPENDENT input to the picture - CIRAM, OAM, palette RAM as indices, the PPU
/// registers and scroll latches, CHR - plus the rendered frame reduced back to NES palette INDICES
/// (0-63 per pixel) by inverting the emulator's own RGB table. Those must agree between two
/// correct emulators even though their RGB does not.
///
/// Record format (UTF-8, LF, lower-case hex, no 0x prefixes), emitted at the END of each frame -
/// i.e. immediately after the RunFrame() that advanced that frame returns - starting at frame 0:
///
///   v1 (what --ppu-state off still emits, byte for byte):
///     &lt;frame&gt;|&lt;pc&gt;|&lt;a&gt;|&lt;x&gt;|&lt;y&gt;|&lt;sp&gt;|&lt;p&gt;|&lt;ramhash&gt;
///
///   v2 (default) appends twelve PPU columns to exactly that prefix:
///     ...|&lt;ntbhash&gt;|&lt;oamhash&gt;|&lt;palhash&gt;|&lt;ctrl&gt;|&lt;mask&gt;|&lt;stat&gt;|&lt;v&gt;|&lt;t&gt;|&lt;fx&gt;|&lt;w&gt;|&lt;chrhash&gt;|&lt;fbhash&gt;
///
///   --apu-state (default on) then appends twenty-four APU columns, in hardware units so the
///   other emulator can produce the same digits:
///     ...|&lt;p1per&gt;|&lt;p1vol&gt;|&lt;p1len&gt;|&lt;p1dut&gt;|&lt;p1swp&gt;|&lt;p2per&gt;|&lt;p2vol&gt;|&lt;p2len&gt;|&lt;p2dut&gt;|&lt;p2swp&gt;
///        |&lt;tper&gt;|&lt;tlen&gt;|&lt;tlin&gt;|&lt;nper&gt;|&lt;nvol&gt;|&lt;nlen&gt;|&lt;nmod&gt;|&lt;dout&gt;|&lt;dcur&gt;|&lt;drem&gt;|&lt;fcs&gt;|&lt;st&gt;|&lt;en&gt;|&lt;flg&gt;
///
///   --audio-trace (default on) then appends four columns fingerprinting the PCM the mixer
///   actually produced this frame - BrokenNes-only, the Mesen side emits '-' for all four:
///     ...|&lt;asmp&gt;|&lt;arms&gt;|&lt;apk&gt;|&lt;azc&gt;
///
///   The column list is written into the header as "# columns: ...", and the differ reads it
///   from there, so the two blocks can be enabled independently without breaking the diff.
///
///   frame   decimal, 0-based, +1 per record; pc 4 hex digits; a,x,y,sp,p 2 hex digits each
///   ramhash first 16 hex chars of SHA-256 over the 2048 bytes of work RAM ($0000-$07FF),
///           hashed in address order
///   ntbhash FNV-1a-64 over the 2048 bytes of CIRAM (nametable RAM) in address order - the raw 2KB
///           the mapper mirrors, NOT the $2000-$2FFF view
///   oamhash FNV-1a-64 over the PPU's own 256-byte OAM (not the CPU-side $0200 shadow)
///   palhash FNV-1a-64 over the 32 palette-RAM bytes, canonicalized - see PALETTE CANON
///   ctrl    PPUCTRL ($2000), 2 hex;  mask  PPUMASK ($2001), 2 hex
///   stat    PPUSTATUS ($2002) masked to 0xe0. Only vblank/sprite-0/overflow are real state; the
///           low five bits are PPU open bus and are not comparable between emulators.
///   v,t     current / temporary VRAM address, 4 hex, masked to 0x7fff;  fx  fine X, 1 hex
///   w       the $2005/$2006 write toggle, 1 hex: bit0 = the $2005 latch, bit1 = the $2006 latch.
///           Hardware has ONE shared toggle, so a correct pair reads 0 or 3. BrokenNes' PPU_FIX
///           keeps two independent latches (scrollLatch / addrLatch), so 1 and 2 are reachable and
///           mean exactly that. Split into two bits on purpose so the differ can name which one.
///   chrhash FNV-1a-64 over all of CHR (RAM or ROM) in address order, or '-' when disabled
///   fbhash  FNV-1a-64 over 61440 bytes - the last rendered frame as NES palette indices, row
///           major, one byte per pixel - or '-' when disabled or unavailable
///
///   FNV-1a-64: h = 0xcbf29ce484222325; per byte h = (h ^ b) * 0x100000001b3 mod 2^64; 16 hex.
///   It replaces SHA-256 for the new columns because the Mesen side hashes it in pure Lua and
///   64-bit multiply-xor is two orders of magnitude cheaper there than a SHA-256 round.
///
/// PALETTE CANON - both tracers apply this identically before hashing or dumping palette RAM:
///   each byte &amp; 0x3f, then $10/$14/$18/$1c are overwritten with $00/$04/$08/$0c. Those four cells
///   are hardware mirrors that no renderer ever reads; Mesen keeps them in sync with their $3f00
///   counterparts, BrokenNes leaves them at their power-on value, so without this they differ
///   forever for no visual reason.
///
/// FRAME INDICES: BrokenNes renders through a fixed 64-entry RGB table (PPU_*.PaletteBytes) with no
///   emphasis or grayscale post-processing, so the palette index behind a pixel is recoverable by
///   inverting that table. The table is read out of the live PPU core by REFLECTION rather than
///   copied here, so it cannot drift. Inversion is ambiguous only where the table itself is, and
///   measured on both sides the collision classes are IDENTICAL - {0d,0e,0f,1d,1e,1f,2e,2f,3e,3f}
///   all black, {20,30} both white - so "lowest index wins" gives the same answer on both. Those
///   classes are printed into the header; a future palette that breaks the property shows up there
///   instead of making the diff quietly lie.
///
/// APU COLUMNS - what they are and, more importantly, what they are NOT:
///   p1per/p2per/tper are 11-bit period REGISTERS (the note being played); p1vol/p2vol/nvol are
///   the volume actually in force (constant-volume parameter or envelope decay, resolved, because
///   the two emulators store those in different fields and their envelope dividers differ by one);
///   nper is the noise period in CPU CYCLES from the NTSC table, not the 4-bit register index;
///   dcur/drem are the DMC's live pointer and remaining length; st is a SYNTHESIZED $4015 (reading
///   the real register would clear the frame IRQ flag and perturb the run being traced).
///
///   The sub-instruction phase of every channel - duty position, triangle sequence position, the
///   noise LFSR, all timer divider counters, and the per-cycle channel output level - is
///   deliberately absent. Mesen 2.1.1's emu.getState() reports the APU from wherever NesApu::Run()
///   last caught it up. Measured over 501 frame-boundary samples on game.nes: a mean 1582 CPU
///   cycles behind the CPU, max 2505. Forcing a catch-up moves exactly 15 of the 105 apu.* keys -
///   precisely those - and leaves the other 90 untouched. Every column above comes from those 90.
///   No side-effect-free catch-up exists in that Lua API, so those 15 are unreachable, not merely
///   skipped, and comparing them would show a divergence on nearly every frame for a harness
///   reason. See ApuSampler for the full measurement.
///
/// AUDIO COLUMNS: asmp = samples this frame (~2de at 44.1kHz / 60.0988fps), arms = RMS * 65535,
///   apk = peak * 65535, azc = zero crossings seeded from the previous frame's last sample. These
///   are BrokenNes-only: Mesen 2.1.1's headless Lua API exposes no audio samples and its WAV
///   recorder is GUI-only. Exact PCM equality across emulators would not be a fair test anyway -
///   mixers, resampling and DC/low-pass filters all differ legitimately - so these are coarse
///   loudness/brightness statistics, usable as a same-emulator regression gate and to tell
///   BrokenNes' APU cores apart. Measured discrimination on game.nes over 620 frames: APU_FMC is
///   bit-identical to APU_FIX on every frame, APU_LOW matches on 157/620, APU_HI on 27/620 with
///   four times the mean zero-crossing rate.
///
/// Lines starting with '#' are header and must be ignored by the differ.
///
/// Usage:
///   --trace --rom &lt;path.nes&gt; [--cpu FIX] [--ppu FMC] [--apu FMC] [--frames N]
///           [--input "60:Start,66:,120:Right+A,180:"] [--ntsc-frame-timing on|off]
///           [--power-on-ram fceux|zeros|ones] [--strict] [--ppu-state on|off]
///           [--ppu-chr on|off] [--ppu-frame on|off] --out trace.txt
///
/// Exit codes match RomTestCli so scripts can treat every Workshop CLI uniformly:
///   0 ran clean | 1 emulator crashed | 2 usage/IO error | 3 a requested core did not apply
///   4 unsupported mapper | 5 unexpected exception
/// </summary>
internal static class TraceCli
{
    private const string Usage =
        "Usage: --trace --rom <path.nes> --out <trace.txt> [--cpu ID] [--ppu ID] [--apu ID]\n" +
        "               [--frames N] [--input \"frame:Buttons,...\"] [--ntsc-frame-timing on|off]\n" +
        "  --out           destination trace file, or '-' for stdout.\n" +
        "  --frames        frames to run and therefore records to emit (default 1800).\n" +
        "  --input script  comma-separated frame:buttons steps, buttons joined by '+'. Empty button\n" +
        "                  list releases everything. Held set applies to player 1 from that frame\n" +
        "                  INCLUSIVE until the next step. Buttons: A,B,Select,Start,Up,Down,Left,Right.\n" +
        "                  Example: \"60:Start,66:,120:Right+A,180:\"\n" +
        "  --ntsc-frame-timing  default 'on'. See the determinism notes in TraceCli.cs.\n" +
        "  --power-on-ram  fceux (default, the emulator's own fill) | zeros | ones. Only bend this\n" +
        "                  when the emulator on the other side of the diff cannot produce FCEUX's\n" +
        "                  pattern; whichever is used is recorded in the header.\n" +
        "  --ram-dump-at   comma-separated frame numbers. Writes the raw 2048 bytes of work RAM at\n" +
        "                  the END of each named frame - the same instant the ramhash column is\n" +
        "                  taken - as <dir>/ram_f<N>.bin. A hash tells you THAT two frames differ;\n" +
        "                  this is how you find out WHICH bytes, which is what names the variable.\n" +
        "                  With --ppu-state on it also writes ciram_f<N>.bin (2048), oam_f<N>.bin\n" +
        "                  (256), pal_f<N>.bin (32, canonicalized) and, when enabled, chr_f<N>.bin\n" +
        "                  and fb_f<N>.bin (61440 palette indices). Same names on the Mesen side.\n" +
        "  --ram-dump-dir  where those go (default: alongside --out).\n" +
        "  --ppu-write-log <file>  log every CPU write to $2000-$2007 as\n" +
        "                  frame|scanline|dot|reg|value|maskAfter. A per-frame hash cannot see a\n" +
        "                  mid-frame $2001/$2005 write - the register is back to its old value by the\n" +
        "                  end of the frame - so this is the only way to compare raster-timed writes\n" +
        "                  against another emulator.\n" +
        "  --ppu-write-frames a-b  restrict that log to a frame range (default: all frames).\n" +
        "  --power-on-palette  zeros (default) | keep. Palette RAM powers on indeterminate on real\n" +
        "                  hardware, so no emulator's fill is 'right' - but they must MATCH or every\n" +
        "                  frame before the game writes its own palette differs, and with it every\n" +
        "                  pixel of fbhash. 'zeros' is what FCEUX does and what PPU_FIX documents\n" +
        "                  itself as doing; 'keep' shows what this build actually powers on with,\n" +
        "                  which under --ppu FIX is the FMC core's cosmetic ramp carried across by\n" +
        "                  the hot-swap. Recorded in the header either way.\n" +
        "  --ppu-state     default 'on'. Off emits the v1 8-column record byte for byte.\n" +
        "  --ppu-chr       default 'on'. Hash all of CHR each frame (the chrhash column).\n" +
        "  --ppu-frame     default 'on'. Hash the rendered frame as NES palette indices (fbhash).\n" +
        "                  Needs the PPU core's RGB table to be invertible; if it is not, the column\n" +
        "                  becomes '-' and the header says why rather than emitting something wrong.\n" +
        "  --strict        pin every SpeedConfig accuracy-for-speed opt-out to its most accurate\n" +
        "                  setting for this run only (idle-loop skip, blank-scanline skip, adaptive\n" +
        "                  CPU batching, zero-page direct access, the PPU render caches, the APU\n" +
        "                  silent-skip fast-forward). Changes no default and no other consumer. The\n" +
        "                  resulting configuration is echoed field-by-field into the header either\n" +
        "                  way, so a trace always carries the settings it was produced under.\n" +
        "  --speed-set     Name=on|off (or Name=N for the int knobs), repeatable, applied AFTER\n" +
        "                  --strict. This is the bisect lever: --strict answers 'do the shortcuts\n" +
        "                  cost accuracy', only flipping them ONE AT A TIME answers 'which one'.\n" +
        "                  e.g. --strict --speed-set CpuIdleLoopSkip=on\n" +
        "  --apu-state     default 'on'. Appends 24 APU columns - both pulses, triangle, noise,\n" +
        "                  DMC, the frame sequencer and a synthesized $4015 - expressed in HARDWARE\n" +
        "                  units so they diff digit-for-digit against Mesen's emu.getState(). The\n" +
        "                  sub-instruction phase fields (duty position, noise LFSR, timer dividers,\n" +
        "                  per-cycle output level) are deliberately absent; see the APU sample-point\n" +
        "                  block in the header for the measurement that says why.\n" +
        "  --audio-trace   default 'on'. Appends 4 columns fingerprinting the PCM the APU actually\n" +
        "                  produced this frame (count, RMS, peak, zero crossings). Needs no audio\n" +
        "                  device. BrokenNes-ONLY: Mesen 2.1.1's headless Lua API exposes no audio\n" +
        "                  samples and no way to start its WAV recorder, so these columns are a\n" +
        "                  regression and cross-APU-CORE oracle, not a cross-EMULATOR one.";

    private const int WorkRamSize = 2048; // $0000-$07FF, the NES's 2KB of internal work RAM

    /// <summary>
    /// The SpeedConfig opt-outs <c>--strict</c> pins, each with the value that is its most
    /// accurate setting - not simply "every bool false".
    ///
    /// That distinction is load-bearing: SpeedConfig mixes two unrelated kinds of flag. The
    /// ApuFeat_* group are ACCURACY FEATURES whose accurate setting is <c>true</c> (absolute-cycle
    /// frame sequencer, the immediate quarter+half tick on a $4017 write, sweep mute prediction,
    /// the DMC channel and its IRQ), so a blanket clear would make "strict" markedly *less*
    /// accurate than the default. They are listed here pinned ON precisely so a future default
    /// change cannot quietly remove them from a trace either.
    ///
    /// Field names are resolved by reflection and a miss is a hard error, so a rename in
    /// SpeedConfig.cs makes this table fail loudly instead of silently pinning nothing.
    /// </summary>
    private static readonly (string Field, object Value, string Why)[] StrictPins =
    {
        // --- CPU: real 6502 work that the fast path elides or reorders -----------------------
        ("CpuIdleLoopSkip",              false, "fast-forwards $2002 spin loops instead of executing them"),
        ("CpuIdleLoopSkipApuStatus",     false, "same, for $4015 spin loops (APU IRQ flags can appear mid-burst)"),
        ("CpuIdleLoopSkipAdaptive",      false, "ramps the skip burst size on long stable loops"),
        ("CpuIdleLoopDetect",            false, "detection is side-effect-free, but it is the skip's input; off is the quiet state"),
        ("CpuAdaptiveBatching",          false, "resizes the CPU batch between PPU/APU catch-ups, moving where reads see stale state"),
        ("CpuZeroPageDirect",            false, "reads/writes bus.ram[] directly, bypassing Bus.Read/Write and its side effects"),
        ("CpuBranchHotness",             false, "instrumentation only; pinned so a changed default cannot alter timing via overhead"),
        ("CpuFastOamDmaStall",           false, "INERT - the $4014 stall is unconditional now (Bus.WriteSlow); pinned for the record"),
        // --- PPU: render shortcuts that skip or reorder fetches -------------------------------
        ("PpuSkipBlankScanlines",        false, "skips whole scanlines detected as blank"),
        ("PpuTileBatching",              false, "prefetches 33 tiles of metadata per scanline ahead of rendering them"),
        ("PpuPatternCache",              false, "serves background pattern rows from a cache instead of re-reading CHR"),
        ("PpuSpritePatternCache",        false, "same cache for sprite rows"),
        ("PpuPaletteCache",              false, "serves palette entries from a pre-expanded RGBA cache"),
        ("PpuSpriteFastPath",            false, "preloads three RGBA entries per sprite row"),
        ("PpuDeferAttributeFetch",       false, "defers the attribute fetch until non-zero tile bits are known"),
        ("PpuUnsafeScanline",            false, "pointer-based scanline renderer with no bounds checks"),
        // --- APU: sample-generation shortcuts -------------------------------------------------
        ("ApuSilentChannelSkip",         false, "fast-forwards the frame sequencer in bulk while all channels are silent"),
        ("ApuSkipEnvelopeOnConstantVolume", false, "skips envelope decay while the constant-volume flag is set"),
        ("ApuOpt_NewHotPaths",           false, "umbrella for the batched/inlined APU hot paths (already off by default)"),
        ("ApuOpt_BatchSampleMix",        false, "batched GenerateAudioSamplesBatch"),
        ("ApuOpt_BlockSilenceFill",      false, "block-based WriteSilenceSamples"),
        ("ApuOpt_InlinePulseOutput",     false, "inlined ComputePulseOutput"),
        ("ApuOpt_SingleDmcFetch",        false, "drops one of two TryDmcFetch calls"),
        // --- APU accuracy FEATURES: pinned ON, not off ----------------------------------------
        ("ApuFeat_FrameSequencer",       true,  "absolute-cycle frame sequencer"),
        ("ApuFeat_4017ImmediateTick",    true,  "immediate quarter+half tick on a $4017 write with bit7 set"),
        ("ApuFeat_SweepMutePrediction",  true,  "mutes a pulse channel whose sweep target is invalid"),
        ("ApuFeat_DmcChannel",           true,  "DMC delta counter, sample fetch and loop"),
        ("ApuFeat_DmcIrq",               true,  "DMC IRQ"),
        ("ApuFeat_LutMixing",            true,  "nonlinear pulse/TND mixing LUT"),
    };

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();

        string? romPath = null, cpu = null, ppu = null, apu = null, outPath = null, inputScript = null;
        int frames = 1800;
        bool ntscFrameTiming = true;
        bool strict = false;
        var speedOverrides = new List<string>();
        bool ppuState = true, ppuChr = true, ppuFrame = true, powerOnPaletteZeros = true;
        bool apuState = true, audioTrace = true;
        PowerOnRam powerOnRam = PowerOnRam.Fceux;
        var ramDumpFrames = new HashSet<int>();
        string? ramDumpDir = null;
        // --ppu-write-log: a raw log of every CPU write that reaches $2000-$2007, stamped with the
        // scanline and dot it landed on. Per-frame hashes cannot answer "did the game turn rendering
        // off halfway down the screen" - the register is back to its old value by the time the frame
        // ends, so the evidence is gone. See Bus.PpuRegisterWriteObserver.
        string? ppuWriteLogPath = null;
        int ppuWriteFrom = 0, ppuWriteTo = int.MaxValue;

        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom": romPath = args[++i]; break;
                    case "--cpu": cpu = args[++i]; break;
                    case "--ppu": ppu = args[++i]; break;
                    case "--apu": apu = args[++i]; break;
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--input": inputScript = args[++i]; break;
                    case "--out": outPath = args[++i]; break;
                    case "--ntsc-frame-timing": ntscFrameTiming = ParseOnOff(args[++i]); break;
                    case "--power-on-ram": powerOnRam = ParsePowerOnRam(args[++i]); break;
                    case "--ram-dump-at":
                        foreach (var t in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            ramDumpFrames.Add(int.Parse(t));
                        break;
                    case "--ram-dump-dir": ramDumpDir = args[++i]; break;
                    case "--ppu-write-log": ppuWriteLogPath = args[++i]; break;
                    case "--ppu-write-frames":
                    {
                        var parts = args[++i].Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        ppuWriteFrom = int.Parse(parts[0]);
                        ppuWriteTo = parts.Length > 1 ? int.Parse(parts[1]) : ppuWriteFrom;
                        break;
                    }
                    case "--strict": strict = true; break;
                    case "--speed-set": speedOverrides.Add(args[++i]); break;
                    case "--power-on-palette": powerOnPaletteZeros = ParseZerosKeep(args[++i]); break;
                    case "--ppu-state": ppuState = ParseOnOff(args[++i]); break;
                    case "--ppu-chr": ppuChr = ParseOnOff(args[++i]); break;
                    case "--ppu-frame": ppuFrame = ParseOnOff(args[++i]); break;
                    case "--apu-state": apuState = ParseOnOff(args[++i]); break;
                    case "--audio-trace": audioTrace = ParseOnOff(args[++i]); break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}\n{Usage}");
                        return 2;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            Console.Error.WriteLine($"Bad arguments: {ex.Message}\n{Usage}");
            return 2;
        }

        if (romPath == null || outPath == null) { Console.Error.WriteLine(Usage); return 2; }
        if (frames < 0) { Console.Error.WriteLine("--frames must be >= 0"); return 2; }

        if (ramDumpFrames.Count > 0)
        {
            // Default next to --out, which is where the trace being investigated already lives.
            ramDumpDir ??= (outPath == "-" ? "." : (Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? "."));
            try { Directory.CreateDirectory(ramDumpDir); }
            catch (Exception ex) { Console.Error.WriteLine($"Failed to create --ram-dump-dir: {ex.Message}"); return 2; }
        }

        List<RomTestCli.InputStep> script;
        try { script = RomTestCli.ParseInputScript(inputScript); }
        catch (FormatException fe) { Console.Error.WriteLine($"Bad --input script: {fe.Message}\n{Usage}"); return 2; }

        byte[] romBytes;
        string romFullPath;
        try
        {
            romFullPath = Path.GetFullPath(romPath);
            romBytes = File.ReadAllBytes(romFullPath);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to read ROM: {ex.Message}"); return 2; }

        string romSha = Convert.ToHexString(SHA256.HashData(romBytes)).ToLowerInvariant();

        TextWriter writer;
        StreamWriter? fileWriter = null;
        try
        {
            if (outPath == "-")
            {
                // Console.Out defaults to "\r\n" on Windows, which would break the LF contract the
                // moment anyone pipes this to a file. Redirected stdout is already UTF-8 without a
                // BOM in .NET, so the newline is the only thing that needs correcting.
                Console.Out.NewLine = "\n";
                writer = Console.Out;
            }
            else
            {
                // Explicit UTF-8 *without* BOM and explicit "\n": the contract says UTF-8 + LF, and
                // .NET's Windows default is UTF-8-with-BOM + CRLF, which would make every line of
                // this file differ from the Mesen side's for no emulation reason at all.
                fileWriter = new StreamWriter(outPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    NewLine = "\n",
                };
                writer = fileWriter;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to open --out file: {ex.Message}"); return 2; }

        try
        {
            var nes = new NES { RomName = Path.GetFileName(romFullPath), RomPath = romFullPath };
            nes.LoadROM(romBytes);

            var cpuApply = RomTestCli.ApplyCore("CPU", cpu, nes.SetCpuCore, nes.GetCpuCoreId);
            var ppuApply = RomTestCli.ApplyCore("PPU", ppu, nes.SetPpuCore, nes.GetPpuCoreId);
            // Captured BEFORE the swap so the header can say whether the APU core was actually
            // replaced. That matters more than it sounds: Bus.SetApuCoreById reapplies the whole
            // $4000-$4017 latch array to the incoming core, and at this point - before frame 0 -
            // that array is all zeros, so the swap performs about twenty register writes the ROM
            // never made. Three of them ($4000/$4004/$400C) set an envelope start flag, and
            // $4011 = 0 forces the DMC output level. See the apu-core-swap header block.
            string apuBefore = nes.GetApuCoreId();
            var apuApply = RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);
            bool apuSwapped = !string.Equals(apuBefore, apuApply.Effective, StringComparison.Ordinal);

            // --- Determinism knobs, pinned explicitly rather than inherited ---------------------
            //
            // Everything below is set here on purpose so the trace's provenance is a property of
            // this tool, not of whatever the emulator's defaults happen to be next month. Each is
            // echoed into the header so the other side of the diff can see what it is comparing to.
            //
            // NtscAccurateFrameRate: BrokenNes' default frame budget is CpuFrequency/60 = 29829.55
            // CPU cycles, but real NTSC is 89341.5 PPU dots = 29780.5 CPU cycles per frame. That
            // ~49-cycle-per-frame surplus makes BrokenNes' frame boundary drift away from any
            // cycle-accurate reference within a handful of frames, which would show up in the diff
            // as an emulation defect when it is really a pacing choice. So --trace defaults this ON.
            var speed = nes.GetSpeedConfig();
            if (speed != null) speed.NtscAccurateFrameRate = ntscFrameTiming;
            // --strict: pin every accuracy-for-speed opt-out to its most accurate setting, so the
            // diff measures what this emulator can do rather than what its fast path does. Applied
            // to THIS NES instance's SpeedConfig only - no default moves, and no other consumer
            // (AccuracyCoin, benchmarks, the golden suite) is touched.
            if (strict && speed != null)
            {
                foreach (var (name, value, _) in StrictPins)
                {
                    var fi = typeof(SpeedConfig).GetField(name);
                    if (fi == null)
                    {
                        // Loud, not silent: a renamed field would otherwise leave --strict quietly
                        // pinning nothing and every measurement taken with it would be a lie.
                        Console.Error.WriteLine($"--strict: SpeedConfig has no field '{name}'. TraceCli.StrictPins is out of date with SpeedConfig.cs.");
                        return 2;
                    }
                    fi.SetValue(speed, value);
                }
            }
            // --speed-set Name=value, applied last so it can override --strict. This is the lever
            // that turns "--strict changed nothing" into an actual finding: with only an all-or-
            // nothing switch, a null result is indistinguishable from the shortcuts cancelling one
            // another out, and a positive result names no culprit. Flipping one flag at a time
            // against an otherwise-fixed configuration is the only way to attribute either.
            var overridden = new HashSet<string>(StringComparer.Ordinal);
            if (speed != null)
            {
                foreach (var spec in speedOverrides)
                {
                    int eq = spec.IndexOf('=');
                    if (eq <= 0)
                    {
                        Console.Error.WriteLine($"--speed-set expects Name=value, got '{spec}'");
                        return 2;
                    }
                    string name = spec[..eq].Trim(), raw = spec[(eq + 1)..].Trim();
                    var fi = typeof(SpeedConfig).GetField(name);
                    if (fi == null || !fi.IsPublic || fi.IsStatic)
                    {
                        Console.Error.WriteLine($"--speed-set: SpeedConfig has no public field '{name}'.");
                        return 2;
                    }
                    try
                    {
                        if (fi.FieldType == typeof(bool)) fi.SetValue(speed, ParseOnOff(raw));
                        else if (fi.FieldType == typeof(int)) fi.SetValue(speed, int.Parse(raw));
                        else { Console.Error.WriteLine($"--speed-set: field '{name}' is {fi.FieldType.Name}; only bool and int are settable."); return 2; }
                    }
                    catch (FormatException fe)
                    {
                        Console.Error.WriteLine($"--speed-set {name}: {fe.Message}");
                        return 2;
                    }
                    overridden.Add(name);
                }
            }
            // Event-scheduled stepping is an optimization path with different interrupt polling;
            // it is off by default but pin it so a changed default cannot silently alter a trace.
            nes.EnableEventScheduler = false;
            // RedScreen is the default and the deterministic one: ImagineFix runs a freeze detector
            // that mutates CPU state and picks among candidate fixes with a seeded-by-clock Random.
            nes.SetCrashBehavior(NES.CrashBehavior.RedScreen);

            // Reusable buffers: a 1800-frame trace hashes ~3.7 MB of RAM, and there is no reason to
            // allocate a fresh array 1800 times to do it.
            var ramSnapshot = new byte[WorkRamSize];
            Span<byte> digest = stackalloc byte[32];
            var line = new StringBuilder(64);

            // Power-on RAM override. BrokenNes fills work RAM with FCEUX's default pattern, which is
            // deliberate and load-bearing for .fm2 portability (see Bus.cs:206-221) - so it stays the
            // default here. But Mesen's ramPowerOnState offers all-zeros / all-ones / random and has
            // no FCEUX-pattern option, so on that pairing the two sides genuinely cannot agree at
            // frame 0 unless one of them bends. This flag is the cheap place to bend: it changes no
            // default, it is recorded in the header, and it beats leaving the whole trace unusable.
            if (powerOnRam != PowerOnRam.Fceux)
            {
                byte fill = powerOnRam == PowerOnRam.Ones ? (byte)0xFF : (byte)0x00;
                for (int i = 0; i < WorkRamSize; i++) nes.PokeSystemRam(i, fill);
            }

            // Power-on RAM, sampled before a single frame runs and hashed the same way the per-frame
            // ramhash is. This is the single most valuable header field: if the two sides' frame-0
            // records disagree, this line says immediately whether the cause is a different power-on
            // fill (boring, fixable in the other emulator's settings) or genuinely different
            // execution (interesting). It is a header comment, so the differ ignores it as data.
            for (int i = 0; i < WorkRamSize; i++) ramSnapshot[i] = nes.PeekSystemRam(i);
            SHA256.HashData(ramSnapshot, digest);
            var powerOn = new StringBuilder(16);
            AppendHashPrefix(powerOn, digest);

            // Power-on PALETTE RAM, the exact analogue of the work-RAM fill above and just as
            // load-bearing once the trace compares pixels.
            //
            // Measured on game.nes: at frame 10 CIRAM, OAM and CHR agree byte for byte with Mesen
            // while all 32 palette bytes and therefore all 61440 pixels differ - purely because the
            // game has not written its palette yet and the two emulators power on with different
            // contents. Real hardware powers up indeterminate, so neither fill is "right", but they
            // have to MATCH or the picture comparison is dead on arrival.
            //
            // BrokenNes' value here is not even PPU_FIX's own: PPU_FIX zero-fills in its ctor, but
            // --ppu FIX hot-swaps FROM the default core, and PpuSharedState carries that core's
            // cosmetic InitializeDefaultPalette() ramp (0f 00 10 30 0f 06 16 26 ...) straight across.
            // Zeroing is what FCEUX does and what PPU_FIX documents itself as doing.
            string powerOnPaletteHash = "(not sampled)";
            if (nes.GetPpuState() is PpuSharedState ppuPowerOn)
            {
                if (powerOnPaletteZeros)
                {
                    Array.Clear(ppuPowerOn.palette, 0, ppuPowerOn.palette.Length);
                    // Round-trips every other field unchanged - the snapshot was taken one line ago.
                    nes.SetPpuState(ppuPowerOn);
                }
                var canon = new byte[32];
                for (int i = 0; i < 32; i++) canon[i] = (byte)(i < ppuPowerOn.palette.Length ? ppuPowerOn.palette[i] & 0x3F : 0);
                canon[0x10] = canon[0x00]; canon[0x14] = canon[0x04]; canon[0x18] = canon[0x08]; canon[0x1C] = canon[0x0C];
                var pb = new StringBuilder(16);
                AppendFnv(pb, canon, 32);
                powerOnPaletteHash = pb.ToString();
            }

            // Built before the header so the header can report exactly what it will and will not be
            // able to sample - an "fbhash disabled because..." line in the header beats a column of
            // '-' that the reader has to go and explain to themselves.
            var ppuSampler = ppuState ? new PpuSampler(nes, ppuChr, ppuFrame, ppuApply.Effective) : null;
            var apuSampler = (apuState || audioTrace)
                ? new ApuSampler(nes, apuState, audioTrace, apuApply.Effective, apuSwapped)
                : null;

            WriteHeader(writer, romFullPath, romBytes.Length, romSha, frames, inputScript, script,
                        cpuApply, ppuApply, apuApply, ntscFrameTiming, powerOnRam, powerOn.ToString(),
                        strict, speed, overridden, ppuSampler, apuSampler);

            // Written from here rather than threaded through WriteHeader's already long parameter
            // list. The differ regex-scans the whole '#' block, so position does not matter.
            if (ppuSampler != null)
            {
                w_PowerOnPalette(writer, powerOnPaletteZeros, powerOnPaletteHash);
            }

            if (!cpuApply.Applied || !ppuApply.Applied || !apuApply.Applied)
            {
                writer.WriteLine("# ABORTED: a requested core did not apply");
                writer.Flush();
                Console.Error.WriteLine("A requested core did not apply:");
                foreach (var c in new[] { cpuApply, ppuApply, apuApply })
                    Console.Error.WriteLine($"  {c.Kind}: requested={c.Requested ?? "(default)"} effective={c.Effective} applied={c.Applied}");
                return 3;
            }

            if (nes.IsCrashed())
            {
                writer.WriteLine($"# ABORTED: emulator crashed before frame 0: {nes.GetCrashInfo()}");
                writer.Flush();
                Console.Error.WriteLine($"Crashed before frame 0: {nes.GetCrashInfo()}");
                return 1;
            }

            var held = new bool[8];
            int nextStep = 0;
            int emitted = 0;
            int crashFrame = -1;

            // --ppu-write-log plumbing. The observer is attached only when asked for, so a normal
            // trace pays nothing but the one null test Bus.WriteSlow already does.
            StreamWriter? ppuWriteLog = null;
            int curFrame = 0;
            if (ppuWriteLogPath != null)
            {
                ppuWriteLog = new StreamWriter(ppuWriteLogPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
                ppuWriteLog.WriteLine("# frame|scanline|dot|reg|value|maskAfter");
                var log = ppuWriteLog;
                nes.SetPpuRegisterWriteObserver((reg, value, sl, dot, mask) =>
                {
                    if (curFrame < ppuWriteFrom || curFrame > ppuWriteTo) return;
                    log.WriteLine($"{curFrame}|{sl}|{dot}|{reg:x4}|{value:x2}|{mask:x2}");
                });
            }

            for (int f = 0; f < frames; f++)
            {
                curFrame = f;
                // Identical to RomTestCli's loop: a while (not if) so multiple steps on the same
                // frame all resolve, and the step's held set is in effect for the frame it names.
                while (nextStep < script.Count && script[nextStep].Frame <= f)
                {
                    held = (bool[])script[nextStep].Held.Clone();
                    nextStep++;
                }
                nes.SetInputs(held, null);

                nes.RunFrame();

                // END of frame: sampled immediately after the call that advanced this frame
                // returned, which is the boundary the other emulator is told to sample at too.
                var (pc, a, x, y, p, sp) = nes.GetCpuRegs();
                for (int i = 0; i < WorkRamSize; i++) ramSnapshot[i] = nes.PeekSystemRam(i);
                SHA256.HashData(ramSnapshot, digest);

                line.Clear();
                line.Append(f).Append('|')
                    .Append(pc.ToString("x4")).Append('|')
                    .Append(a.ToString("x2")).Append('|')
                    .Append(x.ToString("x2")).Append('|')
                    .Append(y.ToString("x2")).Append('|')
                    // SP is a ushort in ICPU.GetRegisters() but the cores keep it masked to
                    // 0x00FF (CPU_FIX.cs:452,457), so the low byte is the real 6502 S register.
                    .Append(((byte)sp).ToString("x2")).Append('|')
                    .Append(p.ToString("x2")).Append('|');
                AppendHashPrefix(line, digest);
                // Same instant, same RunFrame() return: the PPU columns are not sampled a frame
                // later or earlier than the CPU/RAM ones they sit beside.
                ppuSampler?.AppendColumns(line);
                // Same instant again. The APU columns must be drained/read on EVERY frame, not only
                // when something is being investigated, or the audio fingerprint would be measuring
                // a ring buffer of unknown age instead of this frame's output.
                apuSampler?.AppendColumns(line);
                writer.WriteLine(line.ToString());
                emitted++;

                if (ramDumpFrames.Contains(f))
                {
                    var path = Path.Combine(ramDumpDir!, $"ram_f{f}.bin");
                    File.WriteAllBytes(path, ramSnapshot);
                    ppuSampler?.WriteDumps(ramDumpDir!, f);
                    Console.Error.WriteLine($"ram-dump: frame {f} -> {path}{(ppuSampler != null ? " (+ ciram/oam/pal" + (ppuChr ? "/chr" : "") + (ppuSampler.FrameEnabled ? "/fb" : "") + ")" : "")}");
                }

                if (nes.IsCrashed()) { crashFrame = f; break; }
            }

            if (ppuWriteLog != null)
            {
                nes.SetPpuRegisterWriteObserver(null);
                ppuWriteLog.Flush();
                ppuWriteLog.Dispose();
                Console.Error.WriteLine($"ppu-write-log: frames {ppuWriteFrom}..{ppuWriteTo} -> {ppuWriteLogPath}");
            }

            if (crashFrame >= 0)
            {
                writer.WriteLine($"# CRASHED at frame {crashFrame}: {nes.GetCrashInfo()}");
                writer.Flush();
                Console.Error.WriteLine($"Emulator crashed at frame {crashFrame}: {nes.GetCrashInfo()}");
                Console.Error.WriteLine($"Wrote {emitted} record(s) to {(outPath == "-" ? "stdout" : outPath)} before the crash.");
                return 1;
            }

            // A pixel whose RGB is not in the core's own palette table means the inversion behind
            // fbhash did not hold for this run, so say so in the trace itself rather than letting a
            // reader assume the column meant what the header promised.
            if (ppuSampler is { UnmappedPixels: > 0 })
            {
                writer.WriteLine($"# WARNING: {ppuSampler.UnmappedPixels} framebuffer pixel(s) had an RGB value absent from the");
                writer.WriteLine("#   PPU core's own 64-entry palette table and were encoded as index ff. fbhash is NOT");
                writer.WriteLine("#   a reliable palette-index comparison for this run.");
                Console.Error.WriteLine($"WARNING: {ppuSampler.UnmappedPixels} unmapped framebuffer pixel(s); fbhash is unreliable for this run.");
            }

            apuSampler?.WriteSummary(writer);

            writer.Flush();
            if (outPath != "-")
            {
                Console.WriteLine($"Trace  : {outPath}");
                Console.WriteLine($"ROM    : {Path.GetFileName(romFullPath)} sha256={romSha}");
                Console.WriteLine($"Cores  : {cpuApply.Effective} / {ppuApply.Effective} / {apuApply.Effective}");
                Console.WriteLine($"Frames : {emitted} record(s), frame 0..{emitted - 1}");
                Console.Out.Flush();
            }
            return 0;
        }
        catch (Cartridge.UnsupportedMapperException ume)
        {
            Console.Error.WriteLine($"Unsupported mapper {ume.MapperId} ({ume.MapperName}).");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 5;
        }
        finally
        {
            fileWriter?.Dispose();
        }
    }

    // ---- header --------------------------------------------------------------

    private static void WriteHeader(
        TextWriter w, string romFullPath, int romLength, string romSha, int frames,
        string? rawScript, List<RomTestCli.InputStep> script,
        RomTestCli.CoreApplyReport cpu, RomTestCli.CoreApplyReport ppu, RomTestCli.CoreApplyReport apu,
        bool ntscFrameTiming, PowerOnRam powerOnRam, string powerOnRamHash,
        bool strict, SpeedConfig? speed, HashSet<string> speedOverridden, PpuSampler? ppuSampler,
        ApuSampler? apuSampler)
    {
        string version = typeof(TraceCli).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        // v2 == the twelve PPU columns are present. A v1 trace is still a valid v2 prefix, so a
        // differ that understands v2 reads both and an old v1 trace stays diffable forever.
        w.WriteLine($"# brokennes-trace {(ppuSampler != null ? "v2" : "v1")}");
        w.WriteLine($"# emulator: BrokenNes Workshop {version}");
        w.WriteLine($"# cores: cpu={cpu.Effective} ppu={ppu.Effective} apu={apu.Effective}");
        w.WriteLine($"# rom: {romFullPath}");
        w.WriteLine($"# rom-bytes: {romLength}");
        w.WriteLine($"# rom-sha256: {romSha}");
        w.WriteLine($"# frames: {frames}");
        w.WriteLine($"# input-script: {(string.IsNullOrWhiteSpace(rawScript) ? "(none)" : rawScript)}");
        foreach (var s in script)
            w.WriteLine($"#   step frame {s.Frame} -> {(s.Names.Count == 0 ? "(release all)" : string.Join("+", s.Names))}");

        // The determinism block is the whole reason this is a header and not just data: an
        // unexplained frame-0 mismatch is almost always one of these lines disagreeing.
        w.WriteLine("# determinism: region=ntsc (BrokenNes has no PAL mode at the NES level; the only");
        w.WriteLine("#   palMode flags live inside unused APU cores and default to NTSC)");
        switch (powerOnRam)
        {
            case PowerOnRam.Zeros:
                w.WriteLine("# determinism: power-on-ram=zeros (OVERRIDE - all 2048 bytes forced to 00");
                w.WriteLine("#   after construction; matches Mesen ramPowerOnState=AllZeros)");
                break;
            case PowerOnRam.Ones:
                w.WriteLine("# determinism: power-on-ram=ones (OVERRIDE - all 2048 bytes forced to ff");
                w.WriteLine("#   after construction; matches Mesen ramPowerOnState=AllOnes)");
                break;
            default:
                w.WriteLine("# determinism: power-on-ram=fceux-default-pattern");
                w.WriteLine("#   repeating 8 bytes 00 00 00 00 ff ff ff ff over all 2048 bytes");
                w.WriteLine("#   (Bus.InitializeRamPowerOnPattern, Windows/NesEmulator/board/Bus.cs:222,");
                w.WriteLine("#   called from the Bus ctor at Bus.cs:198 - matches FCEUX RAMInitOption=0)");
                break;
        }
        w.WriteLine($"# power-on-ramhash: {powerOnRamHash}");
        w.WriteLine("#   same hash function as the ramhash column, taken before frame 0 runs. If the");
        w.WriteLine("#   two emulators' frame-0 records disagree, compare THIS first: a mismatch here");
        w.WriteLine("#   is a power-on fill difference, not an emulation difference.");
        w.WriteLine($"# determinism: ntsc-frame-timing={(ntscFrameTiming ? "on" : "off")} " +
                    $"({(ntscFrameTiming ? "89342/89341 alternating PPU dots per frame" : "fixed 29829+33/60 CPU cycles per frame")})");
        WriteSpeedConfigBlock(w, strict, speed, speedOverridden, cpu.Effective, ppu.Effective, apu.Effective);
        w.WriteLine("# determinism: event-scheduler=off");
        w.WriteLine("# determinism: crash-behavior=redscreen (no ImagineFix; that path is RNG-driven)");
        w.WriteLine("# determinism: rewind=off auto-save-state=off cheats=off overclock=off frame-skip=off");
        w.WriteLine("#   (BrokenNes has no rewind, no cheat engine and no overclock; RunFrame always");
        w.WriteLine("#   renders - 'no frameskip' is unconditional, see NES.cs UpdateFrameBuffer call)");
        // The APU is CLOCKED regardless (Bus.StepAPU runs every CPU cycle whether or not anyone is
        // listening), so audio samples exist headlessly; what is absent is an output device and the
        // timing feedback one would create. That distinction matters now that --audio-trace reads
        // the samples: draining the ring buffer is a pure read, it cannot alter emulation.
        w.WriteLine("# determinism: audio=generated-but-not-played (headless; no audio device, no timing");
        w.WriteLine("#   feedback. The APU is clocked every CPU cycle either way; --audio-trace only drains");
        w.WriteLine("#   the sample ring, which no other component reads.)");
        w.WriteLine("# sample-point: end of frame, immediately after RunFrame() returns");
        string apuCols = apuSampler?.ColumnSuffix ?? string.Empty;
        if (ppuSampler == null)
        {
            w.WriteLine($"# columns: frame|pc|a|x|y|sp|p|ramhash{apuCols}");
            w.WriteLine("#   ramhash = first 16 hex chars of sha256 over $0000-$07FF in address order");
            w.WriteLine("# not-compared: PPU state (--ppu-state off). Any defect that never reaches work RAM -");
            w.WriteLine("#   i.e. the entire visual half of 'does it play the same' - is invisible in this trace.");
            w.WriteLine("# not-compared: framebuffer (NES palettes are not standardized)");
            apuSampler?.WriteHeaderBlock(w);
            return;
        }

        w.WriteLine($"# columns: frame|pc|a|x|y|sp|p|ramhash|ntbhash|oamhash|palhash|ctrl|mask|stat|v|t|fx|w|chrhash|fbhash{apuCols}");
        w.WriteLine("#   ramhash = first 16 hex chars of sha256 over $0000-$07FF in address order");
        w.WriteLine("#   ntbhash|oamhash|palhash|chrhash|fbhash = fnv-1a-64 (h=cbf29ce484222325, per byte");
        w.WriteLine("#     h=(h^b)*100000001b3 mod 2^64, 16 hex) over, respectively: 2048 bytes of CIRAM in");
        w.WriteLine("#     address order (the raw 2KB the mapper mirrors, NOT the $2000-$2fff view); the PPU's");
        w.WriteLine("#     own 256-byte OAM, not the CPU-side $0200 shadow; 32 canonicalized palette bytes;");
        w.WriteLine($"#     all {ppuSampler.ChrSize} bytes of CHR; and 61440 palette-index pixels.");
        w.WriteLine("#   palette canon: each byte & 0x3f, then $10/$14/$18/$1c := $00/$04/$08/$0c. Those four");
        w.WriteLine("#     are write-mirrors nothing renders from - Mesen keeps them in sync, PPU_FIX leaves");
        w.WriteLine("#     them at power-on - so unfolded they would differ on every frame for no visual reason.");
        w.WriteLine("#   ctrl=$2000&0xbc mask=$2001 stat=$2002&0xe0. The masked-off bits are not comparable");
        w.WriteLine("#     state: $2002's low 5 bits are PPU open bus; $2000 bit 6 is the EXT-bus direction");
        w.WriteLine("#     bit Mesen does not expose; $2000 bits 1-0 are the nametable select, which hardware");
        w.WriteLine("#     keeps in t (bits 10-11), not in the $2000 latch - they are compared via the t column.");
        w.WriteLine("#   v,t = 4 hex masked to 0x7fff; fx = fine X, 1 hex");
        w.WriteLine("#   w = write toggle, 1 hex: bit0 the $2005 latch, bit1 the $2006 latch. Hardware has ONE");
        w.WriteLine("#     shared toggle so a correct pair is 0 or 3; PPU_FIX keeps two independent latches");
        w.WriteLine("#     (scrollLatch/addrLatch) and a $2002 read clears only the $2005 one, so 1 and 2 are");
        w.WriteLine("#     reachable here and mean exactly that. Two bits on purpose, so a differ can say which.");
        w.WriteLine("# ppu-sample-point: identical to the CPU/RAM one - same RunFrame() return, no offset.");
        w.WriteLine("#   CIRAM/OAM/palette/CHR are written during vblank and are stable by this instant, so");
        w.WriteLine("#   they should match exactly. ctrl/mask/stat/v/t/fx/w are LIVE mid-frame registers and");
        w.WriteLine("#   differ for the same boundary reasons work RAM does - two emulators cannot align below");
        w.WriteLine("#   one instruction. In particular PPU_FIX assigns v=t at the end of the pre-render line");
        w.WriteLine("#   and never increments v during rendering (RenderBackground works on a local copy), so");
        w.WriteLine("#   at scanline 0 it reads v==t where hardware reads t plus the pre-render prefetch's two");
        w.WriteLine("#   coarse-X increments. Read v/t as diagnostics, not as a pass/fail gate.");
        w.WriteLine("# ppu-baseline: measured game.nes vs Mesen 2.1.1, 900 frames, gameplay input script,");
        w.WriteLine("#   CPU_FIX/PPU_FIX/APU_FIX: ntbhash oamhash palhash ctrl mask stat t fx w all 0/900");
        w.WriteLine("#   divergent; chrhash 1/900 (frame 8, ONE byte - a CHR-RAM write caught mid-flight);");
        w.WriteLine("#   fbhash 1/900 (frame 0, 25 pixels - Mesen's first frame starts ~25 dots late at");
        w.WriteLine("#   power-on); v 895/900 for the structural reason above. For contrast, on the same run");
        w.WriteLine("#   ramhash differed on 620/900 and pc on 841/900 - i.e. the CPU-side noise is sample");
        w.WriteLine("#   skew, and the picture is the same. A NEW non-zero count in the first group is a");
        w.WriteLine("#   real regression in what would be drawn.");
        w.WriteLine("#   Honest caveat on that baseline: over those 900 frames stat, w and fx took ONE value");
        w.WriteLine("#   each on both sides (00, 0, 0). stat is 00 because every flag is cleared at");
        w.WriteLine("#   pre-render dot 1, before the sample instant - it would still catch a PPU that failed");
        w.WriteLine("#   to clear one. w is 0 because this ROM never leaves a half-written $2005/$2006 across");
        w.WriteLine("#   a frame boundary, so PPU_FIX's split scroll/addr latch is simply not exercised here;");
        w.WriteLine("#   fx is 0 because the game does not use fine-X scroll. Those three agreeing is weak");
        w.WriteLine("#   evidence. ctrl (3 values), mask (2) and t (6) do vary and agreed on every frame, and");
        w.WriteLine("#   ntbhash/oamhash/palhash/chrhash/fbhash vary constantly - that is the strong evidence.");
        w.WriteLine("# not-compared: RGB framebuffer. Compared instead as NES palette INDICES (0-63/pixel),");
        w.WriteLine("#   recovered by inverting the PPU core's own 64-entry RGB table, so two emulators with");
        w.WriteLine("#   different palettes still have to agree.");
        w.WriteLine($"# fb-palette-inversion: {(ppuSampler.FrameEnabled ? "ON" : "OFF - " + (ppuSampler.DisabledReason ?? "unknown"))}");
        w.WriteLine($"# fb-palette-collisions: {ppuSampler.CollisionClasses}");
        w.WriteLine("#   (indices that share an RGB and so cannot be told apart; lowest wins. This is only");
        w.WriteLine("#   safe because the Mesen side's table collides IDENTICALLY - compare the two headers.)");
        w.WriteLine($"# chr: {(ppuSampler.ChrSize > 0 ? ppuSampler.ChrSize + " bytes" : "not hashed (--ppu-chr off or no CHR)")}");
        apuSampler?.WriteHeaderBlock(w);
    }

    /// <summary>
    /// Echo the ENTIRE effective SpeedConfig, field by field, whether or not --strict was passed.
    ///
    /// A trace is evidence, and evidence about an emulator's accuracy is worthless without the
    /// settings it was produced under: SpeedConfig ships several shortcuts ON by default, so the
    /// "obvious" reading of a divergence - that the emulator gets something wrong - can equally be
    /// that it was told not to bother. Printing the whole struct rather than just the pinned subset
    /// means a reader can tell a strict trace from a fast-path one without trusting a flag in a
    /// filename, and can see a default that has moved since the trace was taken.
    /// </summary>
    private static void WriteSpeedConfigBlock(
        TextWriter w, bool strict, SpeedConfig? speed, HashSet<string> speedOverridden,
        string cpuCore, string ppuCore, string apuCore)
    {
        w.WriteLine($"# determinism: strict-accuracy={(strict ? "ON (--strict)" : "off (emulator defaults)")}");
        if (speed == null) { w.WriteLine("# speed-config: (unavailable - no Bus)"); return; }

        if (strict)
        {
            w.WriteLine("#   --strict pins every accuracy-for-speed opt-out in SpeedConfig to its most");
            w.WriteLine("#   accurate setting for this run only. It changes no default and no other");
            w.WriteLine("#   consumer: AccuracyCoin, the benchmarks and the golden-hash suite are tuned");
            w.WriteLine("#   against the defaults and keep seeing them.");
        }
        else
        {
            w.WriteLine("#   This trace ran on the emulator's FAST PATH. Several shortcuts below default");
            w.WriteLine("#   to on. Re-run with --strict before concluding that a divergence is a defect.");
        }

        // Which code actually reads any of this - the difference between a --strict trace that
        // means something and one that is a no-op. Measured, not assumed; re-check with
        //   grep -rln SpeedConfig Windows/NesEmulator
        w.WriteLine("# speed-config: reach - as of this build the ONLY readers of SpeedConfig are");
        w.WriteLine("#   NES.RunFrame + Bus, and the CPU_SPD / CPU_EIL / PPU_SPD / PPU_EIL /");
        w.WriteLine("#   APU_SPD / APU_SPD2 / APU_EIL cores. CPU_FIX, PPU_FIX, APU_FIX, CPU_FMC,");
        w.WriteLine("#   PPU_FMC and APU_FMC contain no reference to it at all, so on those cores");
        w.WriteLine("#   --strict can only reach the frame loop's own batching, not the core.");
        w.WriteLine($"#   cores in force here: cpu={cpuCore} ppu={ppuCore} apu={apuCore}");
        w.WriteLine("#   (re-verify with: grep -rln SpeedConfig Windows/NesEmulator)");

        var defaults = new SpeedConfig();
        var pinned = new Dictionary<string, string>();
        foreach (var (name, _, why) in StrictPins) pinned[name] = why;

        w.WriteLine("# speed-config: effective values (name = value  [state]  - why it is a trade-off)");
        foreach (var fi in typeof(SpeedConfig).GetFields())
        {
            if (!fi.IsPublic || fi.IsStatic) continue;
            object? cur = fi.GetValue(speed);
            object? def = fi.GetValue(defaults);
            bool moved = !Equals(cur, def);
            bool isPin = pinned.ContainsKey(fi.Name);

            // --speed-set wins over --strict, so it is reported first: a reader chasing a bisect
            // needs to see the one flag that was deliberately moved, not the 30 that were not.
            string state =
                speedOverridden.Contains(fi.Name) ? "*** --speed-set OVERRIDE, default " + def + " ***"
                : strict && isPin && moved ? "PINNED by --strict, default " + def
                : strict && isPin ? "pinned by --strict (already the default)"
                : moved ? "SET BY --trace, default " + def
                : "emulator default";

            string why = isPin ? "  - " + pinned[fi.Name] : string.Empty;
            w.WriteLine($"#   {fi.Name,-34} = {cur,-5}  [{state}]{why}");
        }
        if (strict)
        {
            w.WriteLine("#   Fields absent from the pin list above are either not accuracy trade-offs");
            w.WriteLine("#   (ApuFeat_SoftClip is audio character, and no audio is generated here) or are");
            w.WriteLine("#   tuning numbers that only matter while the flag they tune is on, in which case");
            w.WriteLine("#   --strict has already turned that flag off.");
        }
    }

    /// <summary>
    /// The palette-RAM counterpart of the power-on-ramhash block: the single most likely
    /// explanation for a picture that differs from frame 0 and then stops differing the moment the
    /// game writes its own palette.
    /// </summary>
    private static void w_PowerOnPalette(TextWriter w, bool zeroed, string hash)
    {
        w.WriteLine($"# determinism: power-on-palette={(zeroed ? "zeros (OVERRIDE - all 32 bytes forced to 00 before frame 0)" : "keep (whatever this build powers on with)")}");
        w.WriteLine("#   Palette RAM powers on indeterminate on real hardware, so no fill is 'right' - but the");
        w.WriteLine("#   two sides must MATCH or every frame before the game writes its own palette differs,");
        w.WriteLine("#   and with it every pixel of fbhash. Measured on game.nes at frame 10: CIRAM, OAM and");
        w.WriteLine("#   CHR agreed byte for byte with Mesen while all 32 palette bytes and all 61440 pixels");
        w.WriteLine("#   differed, entirely because of this. zeros is FCEUX's behaviour and is what PPU_FIX");
        w.WriteLine("#   documents itself as doing - note that under --ppu FIX it does NOT do it, because the");
        w.WriteLine("#   core is hot-swapped in and PpuSharedState carries the previous core's cosmetic");
        w.WriteLine("#   InitializeDefaultPalette() ramp (0f 00 10 30 0f 06 16 26 ...) across with it.");
        w.WriteLine("#   It does not perturb execution: measured on game.nes over 900 frames of gameplay,");
        w.WriteLine("#   'zeros' vs 'keep' changed pc and ramhash on 0/900 frames and CIRAM/OAM/CHR on 0/900.");
        w.WriteLine("#   It moves only palhash (19/900) and fbhash (14/900), i.e. only the picture before the");
        w.WriteLine("#   game's first palette write - so a v1 trace is bit-identical either way.");
        w.WriteLine($"# power-on-palhash: {hash}");
        w.WriteLine("#   fnv-1a-64 over the 32 canonicalized palette bytes the ROM's first instruction sees.");
        w.WriteLine("#   Emit the same line from the other tracer and compare it directly: if these differ,");
        w.WriteLine("#   nothing about the picture before the game's first palette write is meaningful.");
    }

    // ---- PPU sampling --------------------------------------------------------

    /// <summary>
    /// Everything that determines the picture, sampled at exactly the instant the CPU/RAM record is
    /// taken, in a form a different emulator can be asked for too.
    ///
    /// Deliberately NOT the RGB framebuffer: two correct emulators ship different palettes, so RGB
    /// equality is neither necessary nor sufficient. Everything here is either raw NES memory
    /// (CIRAM, OAM, palette indices, CHR) or a documented register, and the frame is compared as
    /// palette INDICES recovered from the emulator's own RGB table.
    ///
    /// This type never mutates the emulator - GetPpuState() hands back clones and GetFrameBuffer()
    /// is read-only - so switching it on cannot change what a trace records in the other columns.
    /// </summary>
    private sealed class PpuSampler
    {
        public const int CiramSize = 2048;
        public const int OamSize = 256;
        public const int PaletteSize = 32;
        public const int ScreenPixels = 256 * 240;

        private readonly NES nes;
        private readonly bool wantChr;
        private readonly int chrSize;
        private readonly byte[] chrBuf;
        private readonly byte[] palBuf = new byte[PaletteSize];
        private readonly byte[] fbIndex;

        /// <summary>RGB (0x00rrggbb) -> lowest NES palette index producing it, or null if the core's
        /// table could not be read. See DisabledReason for what stopped it.</summary>
        private readonly Dictionary<int, byte>? rgbToIndex;

        public string? DisabledReason { get; }
        public string CollisionClasses { get; } = "(palette table unavailable)";
        public int ChrSize => chrSize;
        public bool FrameEnabled => rgbToIndex != null;
        /// <summary>Pixels whose RGB was not in the core's own palette table. Must stay 0; anything
        /// else means the framebuffer picked up a colour the renderer cannot have produced from a
        /// palette index, and fbhash is then not comparable.</summary>
        public long UnmappedPixels { get; private set; }

        public PpuSampler(NES nes, bool wantChr, bool wantFrame, string ppuCoreId)
        {
            this.nes = nes;
            this.wantChr = wantChr;
            chrSize = wantChr ? Math.Max(0, nes.GetChrSize()) : 0;
            chrBuf = new byte[chrSize];
            fbIndex = new byte[ScreenPixels];

            if (!wantFrame) { DisabledReason = "disabled with --ppu-frame off"; return; }

            // Read the live core's own 64-entry RGB table by reflection. Copying the numbers into
            // this file would make the inversion silently wrong the day someone retunes a palette,
            // and PPU_FIX.cs is not this tool's to edit.
            byte[]? pal = null;
            try
            {
                var t = typeof(NES).Assembly.GetType("NesEmulator." + ppuCoreId);
                var fi = t?.GetField("PaletteBytes",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                pal = fi?.GetValue(null) as byte[];
            }
            catch (Exception ex) { DisabledReason = $"reflection on NesEmulator.{ppuCoreId}.PaletteBytes threw {ex.GetType().Name}"; return; }

            if (pal == null) { DisabledReason = $"NesEmulator.{ppuCoreId} has no static byte[] PaletteBytes"; return; }
            if (pal.Length != 192) { DisabledReason = $"NesEmulator.{ppuCoreId}.PaletteBytes is {pal.Length} bytes, expected 192 (64 rgb triples)"; return; }

            var map = new Dictionary<int, byte>(64);
            var classes = new Dictionary<int, List<int>>(64);
            for (int i = 0; i < 64; i++)
            {
                int rgb = (pal[i * 3] << 16) | (pal[i * 3 + 1] << 8) | pal[i * 3 + 2];
                if (!map.ContainsKey(rgb)) map[rgb] = (byte)i;   // lowest index wins
                if (!classes.TryGetValue(rgb, out var l)) classes[rgb] = l = new List<int>();
                l.Add(i);
            }
            rgbToIndex = map;
            var col = classes.Where(kv => kv.Value.Count > 1)
                             .OrderBy(kv => kv.Value[0])
                             .Select(kv => "{" + string.Join(",", kv.Value.Select(i => i.ToString("x2"))) + "}->" + kv.Value[0].ToString("x2"));
            CollisionClasses = classes.Values.All(v => v.Count == 1) ? "(none - table is injective)" : string.Join(" ", col);
        }

        /// <summary>The twelve v2 columns, appended to an already-built v1 record.</summary>
        public void AppendColumns(StringBuilder line)
        {
            var st = nes.GetPpuState() as PpuSharedState;
            if (st == null)
            {
                // Only reachable if a PPU core returns something other than PpuSharedState. Emit
                // sentinels rather than zeros: a zero would read as real, comparable state.
                line.Append("|-|-|-|--|--|--|----|----|-|-|-|-");
                return;
            }

            AppendFnv(line.Append('|'), st.vram, CiramSize);
            AppendFnv(line.Append('|'), st.oam, OamSize);
            CanonicalizePalette(st.palette, palBuf);
            AppendFnv(line.Append('|'), palBuf, PaletteSize);

            // PPUCTRL masked to 0xbc. Bit 6 is the EXT-bus direction bit, which Mesen does not
            // expose (no game sets it, and masking it on both sides is honest where inventing a 0
            // would not be). Bits 1-0 are the nametable select, which on hardware is NOT stored in
            // the $2000 latch at all - a $2000 write drops it into t bits 10-11 and that is where
            // the PPU reads it from. Comparing BrokenNes' raw $2000 byte against a value rebuilt
            // from t made every $2006 write (which moves t without touching $2000) look like a
            // PPUCTRL divergence; measured on game.nes that was the ONLY reason ctrl ever differed
            // in 900 frames. Nothing is lost - t is its own column and carries those two bits.
            line.Append('|').Append(((byte)(st.PPUCTRL & 0xBC)).ToString("x2"))
                .Append('|').Append(st.PPUMASK.ToString("x2"))
                // Low five bits of $2002 are PPU open bus, not state; masking them off is what
                // makes this column mean the same thing on an emulator that models open bus and
                // one that does not.
                .Append('|').Append(((byte)(st.PPUSTATUS & 0xE0)).ToString("x2"))
                .Append('|').Append(((ushort)(st.v & 0x7FFF)).ToString("x4"))
                .Append('|').Append(((ushort)(st.t & 0x7FFF)).ToString("x4"))
                .Append('|').Append((st.fineX & 0x07).ToString("x1"))
                // bit0 = the $2005 toggle, bit1 = the $2006 toggle. Hardware shares one; PPU_FIX
                // does not, and this is where that shows.
                .Append('|').Append(((st.scrollLatch ? 1 : 0) | (st.addrLatch ? 2 : 0)).ToString("x1"));

            if (wantChr && chrSize > 0) { ReadChr(); AppendFnv(line.Append('|'), chrBuf, chrSize); }
            else line.Append("|-");

            if (BuildFrameIndices()) AppendFnv(line.Append('|'), fbIndex, ScreenPixels);
            else line.Append("|-");
        }

        /// <summary>Raw dumps at the same instant, so a moved hash can be narrowed to a byte.</summary>
        public void WriteDumps(string dir, int frame)
        {
            var st = nes.GetPpuState() as PpuSharedState;
            if (st == null) return;
            File.WriteAllBytes(Path.Combine(dir, $"ciram_f{frame}.bin"), Trim(st.vram, CiramSize));
            File.WriteAllBytes(Path.Combine(dir, $"oam_f{frame}.bin"), Trim(st.oam, OamSize));
            CanonicalizePalette(st.palette, palBuf);
            File.WriteAllBytes(Path.Combine(dir, $"pal_f{frame}.bin"), palBuf);
            if (wantChr && chrSize > 0) { ReadChr(); File.WriteAllBytes(Path.Combine(dir, $"chr_f{frame}.bin"), chrBuf); }
            // fbIndex was filled by AppendColumns for this same frame moments ago - reuse it
            // rather than re-inverting. Rebuilding would double-count UnmappedPixels on exactly
            // the dump frames, which is where an unmapped pixel matters most and where an
            // inflated count would send the reader looking for a second problem that isn't there.
            if (frameBuiltThisSample) File.WriteAllBytes(Path.Combine(dir, $"fb_f{frame}.bin"), fbIndex);
        }

        private static byte[] Trim(byte[] src, int n) =>
            src.Length == n ? src : src.Take(n).Concat(new byte[Math.Max(0, n - src.Length)]).ToArray();

        /// <summary>
        /// See PALETTE CANON in the file header. $10/$14/$18/$1c are write-mirrors of $00/$04/$08/
        /// $0c that nothing renders from; Mesen keeps them in sync, BrokenNes' PPU_FIX redirects the
        /// write and leaves the mirror at its power-on value. Without folding them the palhash
        /// column would differ on every frame of every trace for a reason with no picture behind it.
        /// </summary>
        private static void CanonicalizePalette(byte[] src, byte[] dst)
        {
            for (int i = 0; i < PaletteSize; i++) dst[i] = (byte)(i < src.Length ? src[i] & 0x3F : 0);
            dst[0x10] = dst[0x00]; dst[0x14] = dst[0x04]; dst[0x18] = dst[0x08]; dst[0x1C] = dst[0x0C];
        }

        private void ReadChr()
        {
            for (int i = 0; i < chrSize; i++) chrBuf[i] = nes.PeekChr(i);
        }

        /// <summary>
        /// Invert the core's RGB table over the finished frame. BrokenNes writes RGBA with no
        /// emphasis or grayscale stage, so every pixel came from exactly one palette-table entry.
        /// </summary>
        /// <summary>Set by the last BuildFrameIndices() call, so WriteDumps can tell whether
        /// fbIndex holds this frame's picture instead of re-deriving (and re-counting) it.</summary>
        private bool frameBuiltThisSample;

        private bool BuildFrameIndices()
        {
            frameBuiltThisSample = false;
            if (rgbToIndex == null) return false;
            var fb = nes.GetFrameBuffer();
            if (fb == null || fb.Length < ScreenPixels * 4) return false;
            for (int i = 0, o = 0; i < ScreenPixels; i++, o += 4)
            {
                int rgb = (fb[o] << 16) | (fb[o + 1] << 8) | fb[o + 2];
                if (rgbToIndex.TryGetValue(rgb, out byte idx)) fbIndex[i] = idx;
                else { fbIndex[i] = 0xFF; UnmappedPixels++; }
            }
            frameBuiltThisSample = true;
            return true;
        }
    }

    /// <summary>
    /// FNV-1a, 64-bit. Chosen over SHA-256 for the v2 columns because the Mesen half of this diff
    /// hashes the same bytes in pure Lua, where a SHA-256 round costs two orders of magnitude more
    /// than an xor and a 64-bit multiply - and CHR alone is 32 KB per frame. Not cryptographic, and
    /// it does not need to be: the adversary here is an emulator bug, not a forger.
    /// </summary>
    private static void AppendFnv(StringBuilder sb, byte[] data, int count)
    {
        ulong h = 0xcbf29ce484222325UL;
        int n = Math.Min(count, data.Length);
        for (int i = 0; i < n; i++) { h ^= data[i]; h *= 0x100000001b3UL; }
        // Trailing zeros if the source array is short - keeps the column a fixed 16 hex chars
        // instead of hashing a different number of bytes than the header claims.
        for (int i = n; i < count; i++) { h ^= 0; h *= 0x100000001b3UL; }
        sb.Append(h.ToString("x16"));
    }

    /// <summary>
    /// Samples the sound hardware once per frame, at the same instant as the CPU/RAM columns.
    ///
    /// TWO INDEPENDENT LEVELS, and the difference between them is the whole point:
    ///
    ///   STATE (--apu-state, 24 columns) is a CROSS-EMULATOR oracle. Every value is defined in
    ///   hardware terms - an 11-bit period register, a length counter, a volume in force, a DMC
    ///   pointer - so Mesen can produce the identical number from emu.getState() and the two can be
    ///   compared digit for digit. This is the level that can say "BrokenNes is playing the wrong
    ///   note on pulse 2 from frame 431".
    ///
    ///   AUDIO (--audio-trace, 4 columns) is a BrokenNes-only regression / cross-core oracle. It
    ///   fingerprints the PCM the mixer actually produced. Mesen 2.1.1's headless Lua API exposes
    ///   no audio samples at all (verified: the emu table has getScreenBuffer but no audio
    ///   equivalent, and the WAV recorder is GUI-only with no command-line switch), so there is
    ///   nothing on the other side to compare it to. It is still the only thing in the whole
    ///   toolchain that can catch a core that emits silence, clipping, or a mixer regression.
    ///
    /// WHAT THE STATE COLUMNS DELIBERATELY OMIT, and the measurement behind it: on Mesen 2.1.1
    /// emu.getState() reports the APU from wherever NesApu::Run() last caught it up, which at an
    /// arbitrary instruction boundary is BEHIND the CPU. Measured over 501 frame-boundary samples on
    /// game.nes: mean 1582 CPU cycles behind, max 2505. Forcing a catch-up changes exactly 15 of the
    /// 105 apu.* keys - dutyPos, sequencePosition, the noise LFSR, every timer.timer, every
    /// timer.lastOutput, dmc.bitsRemaining - and leaves the other 90 untouched. Every column below
    /// is drawn from those 90. Including the other 15 would be comparing two different instants and
    /// would show a divergence on nearly every frame for a pure harness reason.
    /// </summary>
    private sealed class ApuSampler
    {
        private readonly NES nes;
        private readonly IApuStateProbe? probe;
        private readonly bool audioEnabled;

        /// <summary>Null when the active APU core implements no probe - reported, never faked.</summary>
        public string? StateDisabledReason { get; }
        public bool StateEnabled => probe != null;
        public bool AudioEnabled => audioEnabled;
        public string ApuCore { get; }

        // Running audio statistics, for the end-of-run summary in the header/stderr. A trace whose
        // audio was silent on every single frame is a finding, not a clean run.
        public long TotalSamples { get; private set; }
        public int SilentFrames { get; private set; }
        public int MaxFrameSamples { get; private set; }
        public int FramesSampled { get; private set; }

        // Seeds the zero-crossing count so a crossing that straddles the frame boundary is counted
        // exactly once overall rather than zero times.
        private float lastSample;

        /// <summary>True when --apu named a core other than the one already active.</summary>
        public bool CoreWasSwapped { get; }

        public ApuSampler(NES nes, bool stateEnabled, bool audioEnabled, string apuCoreId, bool coreWasSwapped)
        {
            this.nes = nes;
            this.audioEnabled = audioEnabled;
            ApuCore = apuCoreId;
            CoreWasSwapped = coreWasSwapped;
            if (stateEnabled)
            {
                probe = nes.GetApuStateProbe();
                if (probe == null)
                    StateDisabledReason = $"APU core {apuCoreId} does not implement IApuStateProbe " +
                                          "(only APU_FIX does today - it is the accuracy target)";
            }
            else
            {
                StateDisabledReason = "--apu-state off";
            }
        }

        /// <summary>The pipe-prefixed column names this sampler will append, for the header line.</summary>
        public string ColumnSuffix
        {
            get
            {
                var sb = new StringBuilder();
                if (StateEnabled)
                    sb.Append("|p1per|p1vol|p1len|p1dut|p1swp|p2per|p2vol|p2len|p2dut|p2swp")
                      .Append("|tper|tlen|tlin|nper|nvol|nlen|nmod|dout|dcur|drem|fcs|st|en|flg");
                if (audioEnabled) sb.Append("|asmp|arms|apk|azc");
                return sb.ToString();
            }
        }

        public void AppendColumns(StringBuilder line)
        {
            if (StateEnabled)
            {
                var s = probe!.ProbeApuState();
                line.Append('|').Append(s.Pulse1Period.ToString("x3"))
                    .Append('|').Append(s.Pulse1Volume.ToString("x1"))
                    .Append('|').Append(s.Pulse1Length.ToString("x2"))
                    .Append('|').Append(s.Pulse1Duty.ToString("x1"))
                    .Append('|').Append(s.Pulse1SweepBits().ToString("x2"))
                    .Append('|').Append(s.Pulse2Period.ToString("x3"))
                    .Append('|').Append(s.Pulse2Volume.ToString("x1"))
                    .Append('|').Append(s.Pulse2Length.ToString("x2"))
                    .Append('|').Append(s.Pulse2Duty.ToString("x1"))
                    .Append('|').Append(s.Pulse2SweepBits().ToString("x2"))
                    .Append('|').Append(s.TrianglePeriod.ToString("x3"))
                    .Append('|').Append(s.TriangleLength.ToString("x2"))
                    .Append('|').Append(s.TriangleLinear.ToString("x2"))
                    .Append('|').Append(s.NoisePeriod.ToString("x3"))
                    .Append('|').Append(s.NoiseVolume.ToString("x1"))
                    .Append('|').Append(s.NoiseLength.ToString("x2"))
                    .Append('|').Append(s.NoiseMode ? '1' : '0')
                    .Append('|').Append(s.DmcOutput.ToString("x2"))
                    .Append('|').Append(s.DmcCurrentAddr.ToString("x4"))
                    .Append('|').Append(s.DmcBytesRemaining.ToString("x4"))
                    .Append('|').Append(s.FrameStep.ToString("x1"))
                    .Append('|').Append(s.StatusBits().ToString("x2"))
                    .Append('|').Append(s.EnableBits().ToString("x2"))
                    .Append('|').Append(s.FlagBits().ToString("x2"));
            }

            if (!audioEnabled) return;

            // GetAudioBuffer() drains the APU's sample ring. At 44100 Hz over an NTSC frame that is
            // ~734 samples, comfortably inside the 2048 this returns, so one call per frame takes
            // exactly this frame's audio and leaves the ring empty for the next one. Nothing else in
            // the emulator reads the ring, so draining it cannot perturb the run being traced.
            float[] buf = nes.GetAudioBuffer();
            int n = buf.Length;
            double sumSq = 0;
            double peak = 0;
            int crossings = 0;
            float prev = lastSample;
            for (int i = 0; i < n; i++)
            {
                float v = buf[i];
                sumSq += (double)v * v;
                double av = v < 0 ? -v : v;
                if (av > peak) peak = av;
                if ((v < 0f) != (prev < 0f)) crossings++;
                prev = v;
            }
            lastSample = prev;

            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0.0;
            FramesSampled++;
            TotalSamples += n;
            if (n > MaxFrameSamples) MaxFrameSamples = n;
            if (peak == 0.0) SilentFrames++;

            line.Append('|').Append(Math.Min(n, 0xFFF).ToString("x3"))
                .Append('|').Append(Fixed16(rms).ToString("x4"))
                .Append('|').Append(Fixed16(peak).ToString("x4"))
                .Append('|').Append(Math.Min(crossings, 0xFFF).ToString("x3"));
        }

        // Amplitudes are nominally in [-1,1] but the mixer's DC high-pass can transiently overshoot,
        // so clamp rather than wrap - a wrapped 0xffff -> 0x0000 would read as sudden silence.
        private static int Fixed16(double v)
        {
            int q = (int)Math.Round(v * 65535.0);
            return q < 0 ? 0 : (q > 0xFFFF ? 0xFFFF : q);
        }

        public void WriteHeaderBlock(TextWriter w)
        {
            w.WriteLine($"# apu-core: {ApuCore}");
            if (CoreWasSwapped)
            {
                // Measured, not theorised. Mesen's write log for this ROM shows only $4010=00,
                // $4017=40, $4015=0f in the first nine frames - no $4000/$4004/$400C and no $4011.
                // Yet a swapped BrokenNes run reports all three envelope decay levels at 15 and
                // falling, and a DMC output level of 0 where APU_FIX powers on at 64. Both are the
                // signature of the latch replay below, and both clear by frame 4.
                w.WriteLine("# apu-core-swap: *** THIS RUN SWAPPED THE APU CORE BEFORE FRAME 0 ***");
                w.WriteLine("#   Bus.SetApuCoreById reapplies the whole $4000-$4017 latch array to the incoming");
                w.WriteLine("#   core so a mid-game hot swap inherits the current register state. Applied before");
                w.WriteLine("#   frame 0 that array is still all zeros, so the swap performs ~23 register writes");
                w.WriteLine("#   the ROM never made. Two of them have visible consequences here:");
                w.WriteLine("#     $4000/$4004/$400C = 00 set the envelope START flag on pulse1, pulse2 and noise,");
                w.WriteLine("#       so all three decay levels load 15 and decay to 0 over frames 0-3. Expect");
                w.WriteLine("#       p1vol/p2vol/nvol to disagree with a reference emulator on exactly those");
                w.WriteLine("#       frames, in lockstep. That is the harness, not the APU.");
                w.WriteLine("#     $4011 = 00 forces the DMC output level to 0. APU_FIX powers on at 64, which");
                w.WriteLine("#       hardware and Mesen do not; the swap happens to hide that.");
                w.WriteLine("#   Run with the APU core already active to trace an unperturbed power-on.");
            }
            if (!StateEnabled)
            {
                w.WriteLine($"# apu-state: OFF - {StateDisabledReason}");
                w.WriteLine("#   No APU column is emitted. A wrong note, a stuck channel or a dead DMC is");
                w.WriteLine("#   invisible in this trace; do not read a clean diff as 'the audio matches'.");
            }
            else
            {
                w.WriteLine("# apu-state: ON");
                w.WriteLine("#   p1per|p2per|tper = the 11-bit period REGISTER (Mesen apu.squareN.realPeriod and");
                w.WriteLine("#     apu.triangle.timer.period), not an internal divider reload.");
                w.WriteLine("#   p1vol|p2vol|nvol = the volume actually in force: the constant-volume parameter");
                w.WriteLine("#     when the constant flag is set, otherwise the envelope decay level. Emitted this");
                w.WriteLine("#     way on purpose - the two emulators keep those two numbers in different fields,");
                w.WriteLine("#     and the envelope DIVIDER convention differs by one, so only the resolved volume");
                w.WriteLine("#     is comparable.");
                w.WriteLine("#   p1len|p2len|tlen|nlen = length counters; tlin = triangle linear counter 0..7f.");
                w.WriteLine("#   p1dut|p2dut = duty select 0..3. The duty POSITION is not emitted - see below.");
                w.WriteLine("#   p1swp|p2swp = sweep, packed as $4001 lays it out MINUS the 3-bit period field:");
                w.WriteLine("#     bit7 enable, bit3 negate, bits2-0 shift. The period field is omitted because");
                w.WriteLine("#     BrokenNes stores it already incremented by one and Mesen's convention could not");
                w.WriteLine("#     be confirmed; its only observable effect is on the period register, which IS");
                w.WriteLine("#     compared every frame.");
                w.WriteLine("#   nper = noise period in CPU CYCLES from the NTSC table (4,8,...,4068). Mesen stores");
                w.WriteLine("#     this minus one internally; its tracer adds it back. nmod = $400e bit 7.");
                w.WriteLine("#   dout = DMC output level 0..7f; dcur = the running sample pointer; drem = bytes left.");
                w.WriteLine("#   fcs = frame sequencer step; st = SYNTHESIZED $4015, bits 0-4 and 6 only.");
                w.WriteLine("#     Synthesized, not read: a real $4015 read clears the frame IRQ flag and would");
                w.WriteLine("#     change the behaviour of the very run being traced. Bit 5 is open bus. Bit 7");
                w.WriteLine("#     (DMC IRQ) is masked off on both sides because Mesen 2.1.1 exposes no DMC IRQ");
                w.WriteLine("#     flag - that is a real hole in the comparison, not something covered elsewhere.");
                w.WriteLine("#   en = bits0-3 length-counter enables, bit5 5-step mode, bit6 frame IRQ inhibit,");
                w.WriteLine("#     bit7 DMC IRQ enable. bit4 is reserved 0: Mesen exposes no DMC channel-enable.");
                w.WriteLine("#   flg = bits0-3 length halts, bits4-6 constant-volume flags, bit7 DMC loop.");
                w.WriteLine("# apu-sample-point: identical to the CPU/RAM one - same RunFrame() return.");
                w.WriteLine("# apu-not-compared: the sub-instruction phase of every channel - duty position,");
                w.WriteLine("#   triangle sequence position, the noise LFSR, all timer divider counters, and the");
                w.WriteLine("#   per-cycle channel output level. MEASURED REASON, not a guess: Mesen 2.1.1's");
                w.WriteLine("#   emu.getState() reports the APU from wherever NesApu::Run() last caught it up.");
                w.WriteLine("#   Over 501 frame-boundary samples on game.nes it was a mean 1582 CPU cycles behind");
                w.WriteLine("#   the CPU, max 2505. Forcing a catch-up moves exactly 15 of the 105 apu.* keys -");
                w.WriteLine("#   precisely the ones listed above - and leaves the other 90 untouched. Every column");
                w.WriteLine("#   emitted here comes from those 90. There is no side-effect-free way to force the");
                w.WriteLine("#   catch-up from Lua, so those 15 are unreachable rather than merely omitted.");
            }

            if (!audioEnabled)
            {
                w.WriteLine("# audio-trace: OFF (--audio-trace off)");
                return;
            }
            w.WriteLine("# audio-trace: ON - asmp|arms|apk|azc, from the PCM the mixer actually produced.");
            w.WriteLine("#   asmp = samples produced this frame (44100 Hz / 60.0988 fps => ~2de = 734).");
            w.WriteLine("#   arms = RMS * 65535 clamped; apk = peak |sample| * 65535 clamped;");
            w.WriteLine("#   azc  = zero crossings, seeded from the previous frame's last sample.");
            w.WriteLine("# audio-trace: *** BROKENNES-ONLY - THE MESEN SIDE CANNOT PRODUCE THESE ***");
            w.WriteLine("#   Mesen 2.1.1's headless Lua API exposes no audio samples (the emu table has");
            w.WriteLine("#   getScreenBuffer but no audio equivalent) and its WAV recorder is GUI-only with no");
            w.WriteLine("#   command-line switch, so its tracer emits '-' for all four. Exact PCM equality");
            w.WriteLine("#   across emulators would not be a fair test anyway - the mixers, the resampling and");
            w.WriteLine("#   the DC/low-pass filters all differ legitimately - which is why these are coarse");
            w.WriteLine("#   loudness/brightness statistics rather than samples. Use them as a same-emulator");
            w.WriteLine("#   regression gate and to compare BrokenNes' APU cores against each other.");
        }

        public void WriteSummary(TextWriter w)
        {
            if (!audioEnabled || FramesSampled == 0) return;
            w.WriteLine($"# audio-summary: {TotalSamples} sample(s) over {FramesSampled} frame(s), " +
                        $"mean {(double)TotalSamples / FramesSampled:F1}/frame, max {MaxFrameSamples}, " +
                        $"{SilentFrames} silent frame(s)");
            if (SilentFrames == FramesSampled)
                w.WriteLine("#   *** EVERY FRAME WAS SILENT *** the APU produced no non-zero sample at all.");
        }
    }

    // ---- helpers -------------------------------------------------------------

    // First 16 hex chars == first 8 bytes of the digest. Written out rather than
    // Convert.ToHexString(...).Substring(0,16) so no 64-char string is built 1800 times.
    private static void AppendHashPrefix(StringBuilder sb, ReadOnlySpan<byte> digest)
    {
        const string Hex = "0123456789abcdef";
        for (int i = 0; i < 8; i++)
        {
            sb.Append(Hex[digest[i] >> 4]);
            sb.Append(Hex[digest[i] & 0x0F]);
        }
    }

    private enum PowerOnRam { Fceux, Zeros, Ones }

    private static PowerOnRam ParsePowerOnRam(string v) => v.ToLowerInvariant() switch
    {
        "fceux" or "pattern" or "default" => PowerOnRam.Fceux,
        "zeros" or "zero" or "00" => PowerOnRam.Zeros,
        "ones" or "one" or "ff" => PowerOnRam.Ones,
        _ => throw new FormatException($"expected fceux|zeros|ones, got '{v}'"),
    };

    private static bool ParseOnOff(string v) => v.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => throw new FormatException($"expected on|off, got '{v}'"),
    };

    private static bool ParseZerosKeep(string v) => v.ToLowerInvariant() switch
    {
        "zeros" or "zero" or "00" => true,
        "keep" or "native" or "as-is" => false,
        _ => throw new FormatException($"expected zeros|keep, got '{v}'"),
    };
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NesEmulator;

namespace BrokenNes.Workshop;

/// <summary>
/// BrokenNes half of a cross-emulator deterministic replay tracer. Emits one record per frame in a
/// shared, emulator-independent format so a differ can find the exact frame where BrokenNes and a
/// reference emulator (Mesen) stop agreeing.
///
/// The framebuffer is deliberately NOT part of the record. NES palettes are not standardized, so
/// two *correct* emulators legitimately produce different RGB; CPU registers plus work RAM are the
/// only oracle both sides can agree on.
///
/// Record format (UTF-8, LF, lower-case hex, no 0x prefixes), emitted at the END of each frame -
/// i.e. immediately after the RunFrame() that advanced that frame returns - starting at frame 0:
///
///   &lt;frame&gt;|&lt;pc&gt;|&lt;a&gt;|&lt;x&gt;|&lt;y&gt;|&lt;sp&gt;|&lt;p&gt;|&lt;ramhash&gt;
///
///   frame   decimal, 0-based, +1 per record; pc 4 hex digits; a,x,y,sp,p 2 hex digits each
///   ramhash first 16 hex chars of SHA-256 over the 2048 bytes of work RAM ($0000-$07FF),
///           hashed in address order
///
/// Lines starting with '#' are header and must be ignored by the differ.
///
/// Usage:
///   --trace --rom &lt;path.nes&gt; [--cpu FIX] [--ppu FMC] [--apu FMC] [--frames N]
///           [--input "60:Start,66:,120:Right+A,180:"] [--ntsc-frame-timing on|off] --out trace.txt
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
        "  --ntsc-frame-timing  default 'on'. See the determinism notes in TraceCli.cs.";

    private const int WorkRamSize = 2048; // $0000-$07FF, the NES's 2KB of internal work RAM

    public static int Run(string[] args)
    {
        RomTestCli.EnsureConsole();

        string? romPath = null, cpu = null, ppu = null, apu = null, outPath = null, inputScript = null;
        int frames = 1800;
        bool ntscFrameTiming = true;

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
            var apuApply = RomTestCli.ApplyCore("APU", apu, nes.SetApuCore, nes.GetApuCoreId);

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

            // Power-on RAM, sampled before a single frame runs and hashed the same way the per-frame
            // ramhash is. This is the single most valuable header field: if the two sides' frame-0
            // records disagree, this line says immediately whether the cause is a different power-on
            // fill (boring, fixable in the other emulator's settings) or genuinely different
            // execution (interesting). It is a header comment, so the differ ignores it as data.
            for (int i = 0; i < WorkRamSize; i++) ramSnapshot[i] = nes.PeekSystemRam(i);
            SHA256.HashData(ramSnapshot, digest);
            var powerOn = new StringBuilder(16);
            AppendHashPrefix(powerOn, digest);

            WriteHeader(writer, romFullPath, romBytes.Length, romSha, frames, inputScript, script,
                        cpuApply, ppuApply, apuApply, ntscFrameTiming, powerOn.ToString());

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

            for (int f = 0; f < frames; f++)
            {
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
                writer.WriteLine(line.ToString());
                emitted++;

                if (nes.IsCrashed()) { crashFrame = f; break; }
            }

            if (crashFrame >= 0)
            {
                writer.WriteLine($"# CRASHED at frame {crashFrame}: {nes.GetCrashInfo()}");
                writer.Flush();
                Console.Error.WriteLine($"Emulator crashed at frame {crashFrame}: {nes.GetCrashInfo()}");
                Console.Error.WriteLine($"Wrote {emitted} record(s) to {(outPath == "-" ? "stdout" : outPath)} before the crash.");
                return 1;
            }

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
        bool ntscFrameTiming, string powerOnRamHash)
    {
        string version = typeof(TraceCli).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        w.WriteLine("# brokennes-trace v1");
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
        w.WriteLine("# determinism: power-on-ram=fceux-default-pattern");
        w.WriteLine("#   repeating 8 bytes 00 00 00 00 ff ff ff ff over all 2048 bytes");
        w.WriteLine("#   (Bus.InitializeRamPowerOnPattern, Windows/NesEmulator/board/Bus.cs:222,");
        w.WriteLine("#   called from the Bus ctor at Bus.cs:198 - matches FCEUX RAMInitOption=0)");
        w.WriteLine($"# power-on-ramhash: {powerOnRamHash}");
        w.WriteLine("#   same hash function as the ramhash column, taken before frame 0 runs. If the");
        w.WriteLine("#   two emulators' frame-0 records disagree, compare THIS first: a mismatch here");
        w.WriteLine("#   is a power-on fill difference, not an emulation difference.");
        w.WriteLine($"# determinism: ntsc-frame-timing={(ntscFrameTiming ? "on" : "off")} " +
                    $"({(ntscFrameTiming ? "89342/89341 alternating PPU dots per frame" : "fixed 29829+33/60 CPU cycles per frame")})");
        w.WriteLine("# determinism: event-scheduler=off");
        w.WriteLine("# determinism: crash-behavior=redscreen (no ImagineFix; that path is RNG-driven)");
        w.WriteLine("# determinism: rewind=off auto-save-state=off cheats=off overclock=off frame-skip=off");
        w.WriteLine("#   (BrokenNes has no rewind, no cheat engine and no overclock; RunFrame always");
        w.WriteLine("#   renders - 'no frameskip' is unconditional, see NES.cs UpdateFrameBuffer call)");
        w.WriteLine("# determinism: audio=not-generated (headless; no audio device, no timing feedback)");
        w.WriteLine("# sample-point: end of frame, immediately after RunFrame() returns");
        w.WriteLine("# columns: frame|pc|a|x|y|sp|p|ramhash");
        w.WriteLine("#   ramhash = first 16 hex chars of sha256 over $0000-$07FF in address order");
        w.WriteLine("# not-compared: framebuffer (NES palettes are not standardized)");
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

    private static bool ParseOnOff(string v) => v.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => throw new FormatException($"expected on|off, got '{v}'"),
    };
}

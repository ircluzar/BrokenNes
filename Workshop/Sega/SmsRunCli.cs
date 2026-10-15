using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop.Sega;

/// <summary>
/// <c>--sms-run &lt;rom&gt; [--frames N] [--input "f:Btn+Btn,f:"] [--input2 ...] [--png-at f1,f2] [--out dir] [--tag name] [--hash] [--hash-at f1,f2] [--region ntsc|pal]
/// [--model sms1|sms2|markiii|sg1000|sc3000] [--console sms|gg] [--wav out.wav] [--trace-z80 N] [--trace-vdp] [--dump-vram file] [--battery file] [--battery-out file]
/// [--mesen dir]</c>: runs a Master System / Game Gear / SG-1000 ROM headless on the real session and reports what it did.
/// <para>Buttons are <see cref="PadButtons"/> names (<c>A</c> = button 1, <c>B</c> = button 2, <c>Select</c> = Pause, <c>Start</c> = Game Gear Start, <c>Mode</c> = Reset). <c>"60:Start,70:"</c> holds Start from frame 60
/// until frame 70 (frames are counted from 0 and an entry holds until the next).</para>
/// <para><c>--mesen dir</c> compares each <c>--png-at</c> frame with <c>dir/&lt;tag&gt;_f&lt;frame&gt;.png</c>, a screenshot the Mesen capture script (<c>Workshop/Sega/mesen/capture.lua</c>) took at the same frame, and prints the differing pixels.</para>
/// </summary>
internal static class SmsRunCli
{
    public static int Run(string[] args)
    {
        string? Opt(string name) { int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Flag(string name) => args.Any(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        string? romPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Opt("rom");
        if (romPath == null)
        {
            Console.WriteLine("usage: --sms-run <rom> [--frames N] [--input spec] [--input2 spec] [--png-at f1,f2] [--out dir] [--tag name] [--hash] [--hash-at f1,f2] [--region ntsc|pal]");
            Console.WriteLine("                     [--model sms1|sms2|markiii|sg1000|sc3000] [--console sms|gg] [--wav f] [--trace-z80 N] [--trace-vdp] [--dump-vram f] [--battery f] [--battery-out f] [--mesen dir]");
            return 2;
        }
        byte[] rom = SmsRomIo.Load(romPath, out string inner);
        var detected = SegaRomFormat.Detect(rom, inner);
        var console = Opt("console") is { } c ? Consoles.FromKey(c) : detected ?? ConsoleKind.MasterSystem;
        if (console is not (ConsoleKind.MasterSystem or ConsoleKind.GameGear)) { Console.WriteLine($"not a Master System / Game Gear ROM ({detected})"); return 2; }
        int frames = int.Parse(Opt("frames") ?? "600");
        var shots = ParseFrames(Opt("png-at") ?? "");
        var hashAt = ParseFrames(Opt("hash-at") ?? "");
        var ramAt = ParseFrames(Opt("ram-at") ?? "");
        var fmAt = ParseFrames(Opt("fm-regs-at") ?? "");
        int diffDetail = int.Parse(Opt("diff-detail") ?? "6");
        int firstDiffFrame = -1, diffsPrinted = 0;
        string outDir = Opt("out") ?? ".", tag = Opt("tag") ?? Path.GetFileNameWithoutExtension(inner);
        Directory.CreateDirectory(outDir);
        string? mesenDir = Opt("mesen"), mesenDirB = Opt("mesen-b");
        var script1 = SmsRomIo.ParseInput(Opt("input") ?? ""); var script2 = SmsRomIo.ParseInput(Opt("input2") ?? "");
        RegionChoice? region = Opt("region") is { } r ? Enum.Parse<RegionChoice>(r, true) : null;
        MasterSystemModel? model = Opt("model") is { } m ? Enum.Parse<MasterSystemModel>(m, true) : null;

        // --mapper <kind>: run this ROM on that cartridge wiring instead of the one the image or the table of known cartridges gives it (a way to find out what a game needs)
        if (Opt("mapper") is { } mapperName)
            SmsCartridge.KnownCartridges[Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(SegaRomFormat.StripCopierHeader(rom)))] = Enum.Parse<SmsMapperKind>(mapperName, true);
        string apu = Opt("apu") ?? "SMS";      // --apu SMS-FM: the Japanese console's sound unit (PSG and YM2413)
        var session = console == ConsoleKind.GameGear
            ? (SmsSession)new GgSession(rom, "Z80", "GG", apu == "SMS" ? "GG" : apu, inner, region)
            : new SmsSession(rom, console, "Z80", "SMS", apu, inner, model, region);
        var board = session.Board;
        // the oracle starts its VDP at a random scanline; its capture script records the line, and this emulator starts at the same one so the two run in step (frame for frame, cycle for cycle on the Z80 side)
        string? startLine = Opt("start-line");
        if (startLine == null && mesenDir != null && File.Exists(Path.Combine(mesenDir, tag + ".phase.txt")))
            startLine = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(mesenDir, tag + ".phase.txt")), @"scanline=(\d+)").Groups[1].Value;
        if (!string.IsNullOrEmpty(startLine)) board.StartLine = int.Parse(startLine);
        // --tune Name=value[,Name=value]: a timing field of the board or the VDP (WriteLatencyMc, VintFlagMc, ...), to find or check a constant
        foreach (var kv in (Opt("tune") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')))
        {
            bool done = false;
            foreach (object target in new object[] { board, board.Vdp })
            {
                if (done) break;
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                if (target.GetType().GetField(kv[0], flags) is { } fi && fi.FieldType == typeof(int)) { fi.SetValue(target, int.Parse(kv[1])); done = true; }
                else if (target.GetType().GetProperty(kv[0], flags) is { CanWrite: true } pi && pi.PropertyType == typeof(int)) { pi.SetValue(target, int.Parse(kv[1])); done = true; }
            }
            if (!done) throw new ArgumentException($"no tunable field '{kv[0]}'");
        }
        if (Opt("start-cycle") is { } sc) board.StartCycle = int.Parse(sc);
        if (!string.IsNullOrEmpty(startLine) || Opt("start-cycle") != null || Opt("tune") != null) { board.Reset(); Console.WriteLine($"VDP starts at line {board.StartLine} cycle {board.StartCycle}"); }
        Console.WriteLine(session.Description);
        if (Opt("battery") is { } bat && File.Exists(bat)) { session.ImportSave(File.ReadAllBytes(bat)); Console.WriteLine($"battery loaded: {bat}"); }

        // tracing
        int traceN = int.Parse(Opt("trace-z80") ?? "0");
        var trace = new Queue<string>();
        if (traceN > 0)
            board.InstructionHook = pc =>
            {
                var c = board.Cpu;
                trace.Enqueue($"{pc:X4} {board.Peek(pc):X2} {board.Peek((ushort)(pc + 1)):X2} {board.Peek((ushort)(pc + 2)):X2}  AF={c.AF:X4} BC={c.BC:X4} DE={c.DE:X4} HL={c.HL:X4} SP={c.SP:X4} IX={c.IX:X4} IY={c.IY:X4} IFF={(c.IFF1 ? 1 : 0)} line={board.Vdp.Line}");
                if (trace.Count > traceN) trace.Dequeue();
            };
        if (Opt("trace-io") is { } tio)
        {
            // --trace-io <first>-<last>: every port access made in frames first..last (counted from 1), as "frame line cycle R/W port value"
            var range = tio.Split('-'); int first = int.Parse(range[0]), lastF = range.Length > 1 ? int.Parse(range[1]) : first;
            int shown = 0, max = int.Parse(Opt("trace-io-max") ?? "400");
            var only = Opt("trace-io-ports")?.Split(',').Select(h => Convert.ToInt32(h, 16)).ToHashSet();   // --trace-io-ports 7F,06: only those ports
            board.PortHook = (write, port, value, line, cyc) =>
            {
                long fr = board.Frames + 1;
                if (fr < first || fr > lastF || shown >= max) return;
                if (only != null && !only.Contains(port & 0xFF)) return;
                shown++;
                Console.WriteLine($"IO f{fr} l{line} c{cyc} {(write ? "W" : "R")} {port & 0xFF:X2} {value:X2}");
            };
        }
        StreamWriter? watchLog = null;
        if (Opt("watch-pc") is { } wpcs)
        {
            // --watch-pc <addr>[,<addr>]: each execution of those instruction addresses as "frame pc cycles line" to <out>/<tag>.watch.txt (the oracle's MESEN_WATCH writes the same, with its own cycle count)
            watchLog = new StreamWriter(Path.Combine(outDir, $"{tag}.watch.txt"));
            var set = wpcs.Split(',').Select(h => Convert.ToUInt16(h, 16)).ToHashSet();
            board.InstructionHook = pc => { if (set.Contains(pc)) watchLog.WriteLine($"{board.Frames + 1} {pc:X4} {board.Cycles} {board.Vdp.Line} A={board.Cpu.A:X2} BC={board.Cpu.BC:X4} DE={board.Cpu.DE:X4} HL={board.Cpu.HL:X4} [HL]={board.Peek(board.Cpu.HL):X2}"); };
        }
        if (Opt("count-pc") is { } cpc)
        {
            // --count-pc <addr> [--count-every N]: print the Z80 cycle count at every Nth execution of an instruction address (compare with the oracle's exec callback)
            ushort target = Convert.ToUInt16(cpc, 16); int every = int.Parse(Opt("count-every") ?? "4000"); long hits = 0;
            board.InstructionHook = pc => { if (pc == target && ++hits % every == 1) Console.WriteLine($"n={hits} cycles={board.Cycles} f={board.Frames + 1} line={board.Vdp.Line}"); };
        }
        StreamWriter? pcLog = null;
        if (Opt("trace-pc") is { } tpc)
        {
            // --trace-pc <a>-<b>: the address of every instruction executed in frames a..b, one per line, to <out>/<tag>.pc.txt (compare with Mesen's MESEN_EXEC)
            var ab = tpc.Split('-'); int fa = int.Parse(ab[0]), fb = int.Parse(ab[1]);
            pcLog = new StreamWriter(Path.Combine(outDir, $"{tag}.pc.txt"));
            bool withRegs = Flag("trace-pc-regs"), withCycles = Flag("trace-pc-cyc");   // --trace-pc-cyc: "pc cycles" (the Z80 cycle count before the instruction; the oracle's MESEN_EXECCYC)
            long lastFr = -1;
            int lastPc = -1;
            board.InstructionHook = pc =>
            {
                long fr = board.Frames + 1;
                // a halted CPU that an interrupt wakes reaches a boundary at the instruction after the HALT before the interrupt response starts in its place: that instruction never runs, and the oracle's log has no line for it
                bool afterHalt = lastPc >= 0 && pc == (ushort)(lastPc + 1) && board.Peek((ushort)lastPc) == 0x76 && board.Cpu.Irq;
                lastPc = pc;
                if (afterHalt) return;
                if (fr < fa || fr > fb) return;
                if (fr != lastFr) { lastFr = fr; pcLog.WriteLine("F" + fr); }   // a marker where each frame starts (the oracle's log has the same)
                if (withCycles) pcLog.WriteLine($"{pc:X4} {board.Cycles}");
                else if (withRegs) { var z = board.Cpu; pcLog.WriteLine($"{pc:X4} {z.A:X2}{z.F:X2} {z.B:X2}{z.C:X2} {z.D:X2}{z.E:X2} {z.H:X2}{z.L:X2} {z.SP:X4}"); }
                else pcLog.WriteLine(pc.ToString("X4"));
            };
        }
        if (Opt("watch-vram") is { } wv)
        {
            // --watch-vram <addr>[-<addr>] [--watch-frames a-b]: every write to those VRAM addresses (or colour RAM with code 3: "C:" prefix on the address) with frame, line and master clock
            var ab = wv.Split('-'); int lo = Convert.ToInt32(ab[0], 16), hi = ab.Length > 1 ? Convert.ToInt32(ab[1], 16) : lo;
            var wf = Opt("watch-frames")?.Split('-'); int wlo = wf == null ? 0 : int.Parse(wf[0]), whi = wf == null ? int.MaxValue : int.Parse(wf.Length > 1 ? wf[1] : wf[0]);
            int shownV = 0; bool cramOnly = Flag("cram");
            board.Vdp.DataWriteHook = (code, addr, value, line, mc) =>
            {
                long fr = board.Frames + 1;
                if (cramOnly != (code == 3) || addr < lo || addr > hi || fr < wlo || fr > whi || shownV > 3000) return;
                shownV++;
                Console.WriteLine($"{(code == 3 ? "CRAM" : "VRAM")} f{fr} l{line} mc{mc} [{addr:X4}]={value:X2} pc={board.Cpu.PC:X4} cycles={board.Cycles}");
            };
        }
        if (Opt("watch-write") is { } ww)
        {
            // --watch-write <addr>[-<addr>] [--watch-frames a-b]: print every write to those Z80 addresses with the frame, line, cycle and the PC after the write
            var ab = ww.Split('-'); int lo = Convert.ToInt32(ab[0], 16), hi = ab.Length > 1 ? Convert.ToInt32(ab[1], 16) : lo;
            var wf = Opt("watch-frames")?.Split('-'); int wlo = wf == null ? 0 : int.Parse(wf[0]), whi = wf == null ? int.MaxValue : int.Parse(wf.Length > 1 ? wf[1] : wf[0]);
            int shownW = 0;
            board.MemWriteHook = (addr, value, line, cyc) =>
            {
                long fr = board.Frames + 1;
                if (addr < lo || addr > hi || fr < wlo || fr > whi || shownW > 2000) return;
                shownW++;
                Console.WriteLine($"WR f{fr} l{line} c{cyc} [{addr:X4}]={value:X2} pc={board.Cpu.PC:X4}");
            };
        }
        // --fast-writes <cycles>: how many writes to the VDP data port during the picture (display on, a picture line) follow the previous one within that many Z80 cycles, by gap: the hardware-documented limit is about 26 cycles
        // (MacDonald's VDP document section 15, Maxim and sverx on SMS Power); a game that does this on hardware loses or misplaces bytes
        int[]? fastGaps = null; long fastTotal = 0, lastWriteMc = long.MinValue; int fastLimit = 0;
        if (Opt("fast-writes") is { } fw)
        {
            fastLimit = int.Parse(fw); fastGaps = new int[fastLimit + 1];
            var vdp0 = board.Vdp;
            board.Vdp.DataWriteHook = (code, addr, value, line, mc) =>
            {
                long now = ((long)board.Frames * vdp0.TotalLines + line) * 684 + mc;
                if (vdp0.DisplayEnabled && line < vdp0.ActiveHeight)
                {
                    fastTotal++;
                    long gap = lastWriteMc == long.MinValue ? long.MaxValue : (now - lastWriteMc) / 3;
                    if (gap >= 0 && gap <= fastLimit) fastGaps[gap]++;
                }
                lastWriteMc = now;
            };
        }
        if (Flag("trace-vdp"))
        {
            long f = 0;
            board.Vdp.RegisterWriteHook = (line, mc, reg, value) => Console.WriteLine($"VDP frame {board.Frames + 1} line {line} mc {mc}: R{reg} = ${value:X2}  pc=${board.Cpu.PC:X4} cycles={board.Cycles}");
        }

        var pcm = new List<short>();
        var abuf = new short[16384];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong hash = 0xcbf29ce484222325, hashLast = 0;
        PadButtons p1 = 0, p2 = 0;
        int stableFrames = 0, stableMatched = 0, unstableFrames = 0, unstableMatched = 0;
        int mesenShift = int.Parse(Opt("mesen-shift") ?? "0");
        long audioN = 0; double audioSqL = 0, audioSqR = 0; int audioPeakL = 0, audioPeakR = 0, audioFrameMax = 0, audioActiveFrames = 0, frameNo0 = -1;
        for (int f = 0; f < frames; f++)
        {
            if (script1.TryGetValue(f, out var h1)) p1 = h1;
            if (script2.TryGetValue(f, out var h2)) p2 = h2;
            session.SetPad(0, p1); session.SetPad(1, p2);
            session.RunFrame();
            if (Flag("frame-end-lines") && (f >= 363 && f <= 367 || board.Vdp.Line != 191 || board.Vdp.ActiveHeight != 192)) Console.WriteLine($"frame {f + 1} ended at line {board.Vdp.Line} (active height {board.Vdp.ActiveHeight}) frames={board.Frames}");
            int n;
            while ((n = session.ReadSamples(abuf)) > 0)
                for (int i = 0; i + 1 < n; i += 2)
                {
                    if (Opt("wav") != null) pcm.Add((short)((abuf[i] + abuf[i + 1]) / 2));
                    audioN++; audioSqL += (double)abuf[i] * abuf[i]; audioSqR += (double)abuf[i + 1] * abuf[i + 1];
                    audioPeakL = Math.Max(audioPeakL, Math.Abs((int)abuf[i])); audioPeakR = Math.Max(audioPeakR, Math.Abs((int)abuf[i + 1]));
                    int mono = (abuf[i] + abuf[i + 1]) / 2;
                    if (frameNo0 != f) { frameNo0 = f; if (audioFrameMax > 200) audioActiveFrames++; audioFrameMax = 0; }
                    audioFrameMax = Math.Max(audioFrameMax, Math.Abs(mono));
                }
            int frameNo = f + 1;
            if (Flag("hash") || hashAt.Contains(frameNo))
            {
                hashLast = HashFrame(session);
                if (hashAt.Contains(frameNo)) Console.WriteLine($"frame {frameNo} hash {hashLast:X16}");
            }
            if (fmAt.Contains(frameNo) && board.Sound is SmsFmSound fmUnit)
            {
                // --fm-regs-at: the YM2413's register file and the audio control in the shape of the oracle's state dump ("fmAudio.opll->regN = V", decimal), for a line by line diff
                var sb = new StringBuilder();
                sb.AppendLine($"fmAudio.audioControl = {fmUnit.AudioControl}");
                for (int i = 0; i < 64; i++) sb.AppendLine($"fmAudio.opll->reg{i} = {fmUnit.Fm.Register(i)}");
                sb.AppendLine($"fmAudio.opll->rhythm_mode = {(fmUnit.Fm.RhythmMode ? 1 : 0)}");
                sb.AppendLine($"fmAudio.opll->mix_out0 = {fmUnit.LastFmSample}");
                for (int i = 0; i < Ym2413.VoiceCount; i++) sb.AppendLine($"fmAudio.opll->ch_out{i} = {fmUnit.Fm.ChannelOutput(i)}");
                File.WriteAllText(Path.Combine(outDir, $"{tag}_f{frameNo}.fm.txt"), sb.ToString());
            }
            if (ramAt.Contains(frameNo))
            {
                File.WriteAllBytes(Path.Combine(outDir, $"{tag}_f{frameNo}.ram.bin"), board.Ram);
                File.WriteAllBytes(Path.Combine(outDir, $"{tag}_f{frameNo}.vram.bin"), board.Vdp.Vram);
                File.WriteAllBytes(Path.Combine(outDir, $"{tag}_f{frameNo}.cram.bin"), Enumerable.Range(0, 64).Select(i => board.Vdp.CramByte(i)).ToArray());
                if (mesenDir != null) Console.WriteLine($"frame {frameNo}: RAM " + SmsPng.DiffBytes(board.Ram, Path.Combine(mesenDir, $"{tag}_f{frameNo}.ram.bin")) +
                                                        " | VRAM " + SmsPng.DiffBytes(board.Vdp.Vram, Path.Combine(mesenDir, $"{tag}_f{frameNo}.vram.bin")));
            }
            if (shots.Contains(frameNo))
            {
                string path = Path.Combine(outDir, $"{tag}_f{frameNo}.png");
                if (mesenDir == null || shots.Count <= 12) SmsPng.Save(session.Frame, session.FrameWidth, session.FrameHeight, path);
                if (mesenDir != null)
                {
                    // --mesen-shift k: compare this emulator's frame N with the oracle's screenshot N+k (a lag test: the oracle may show the frame before the one just completed)
                    string theirs = Path.Combine(mesenDir, $"{tag}_f{frameNo + mesenShift}.png");
                    string? theirsB = mesenDirB != null ? Path.Combine(mesenDirB, $"{tag}_f{frameNo + mesenShift}.png") : null;
                    if (!File.Exists(theirs)) Console.WriteLine($"frame {frameNo}: no Mesen shot at {theirs}");
                    else if (new FileInfo(theirs).Length == 0) { /* Mesen takes no picture of its very first frame */ }
                    else
                    {
                        // Mesen starts the Z80 and the VDP at a random alignment (run it twice and the two runs differ in R and in the cycle count of the first frame), so a game whose
                        // picture depends on timing only matches one of its own runs by luck. With two Mesen runs, frames where they agree are the ones this emulator must match.
                        bool stable = true; uint[]? other = null;
                        uint[] a = SmsPng.Load(theirs, out int aw, out int ah, session.FrameHeight);
                        if (theirsB != null && File.Exists(theirsB) && new FileInfo(theirsB).Length > 0)
                        {
                            other = SmsPng.Load(theirsB, out int bw, out int bh, session.FrameHeight);
                            stable = bw == aw && bh == ah && a.AsSpan().SequenceEqual(other);
                        }
                        bool sizeOk = aw == session.FrameWidth && ah == session.FrameHeight;
                        bool matchA = sizeOk && SmsPng.SameAsFrame(session.Frame, a);
                        if (stable)
                        {
                            stableFrames++;
                            if (matchA) { stableMatched++; if (shots.Count <= 12) Console.WriteLine($"frame {frameNo}: IDENTICAL to Mesen"); }
                            else
                            {
                                if (firstDiffFrame < 0) { firstDiffFrame = frameNo; SmsPng.Save(session.Frame, session.FrameWidth, session.FrameHeight, path); }
                                if (diffsPrinted++ < diffDetail)
                                {
                                    SmsPng.Compare(session.Frame, session.FrameWidth, session.FrameHeight, theirs, Path.Combine(outDir, $"{tag}_f{frameNo}_diff.png"), out string summary, writeDiff: true);
                                    Console.WriteLine($"frame {frameNo}: {summary}");
                                }
                            }
                        }
                        else
                        {
                            unstableFrames++;
                            if (matchA || (sizeOk && SmsPng.SameAsFrame(session.Frame, other!))) unstableMatched++;
                        }
                    }
                }
            }
        }
        double secs = sw.Elapsed.TotalSeconds;
        pcLog?.Dispose(); watchLog?.Dispose();
        foreach (var t in trace) Console.WriteLine(t);
        if (Opt("wav") is { } wav) { MixWav(wav, pcm.ToArray(), session.SampleRate); Console.WriteLine($"wrote {wav} ({pcm.Count / (double)session.SampleRate:F1} s)"); }
        if (Opt("dump-vram") is { } dv) { File.WriteAllBytes(dv, board.Vdp.Vram); }
        if (Opt("dump-cram") is { } dc) { File.WriteAllBytes(dc, Enumerable.Range(0, 64).Select(i => board.Vdp.CramByte(i)).ToArray()); }
        if (Opt("battery-out") is { } bo && session.HasBattery) { File.WriteAllBytes(bo, session.ExportSave()); Console.WriteLine($"battery saved: {bo}"); }
        var vdp = board.Vdp;
        Console.WriteLine($"frames={frames} {secs * 1000 / frames:F2} ms/frame ({frames / secs:F0} fps, {frames / secs / session.FramesPerSecond:F1}x real time) PC=${board.Cpu.PC:X4} " +
                          $"R0..R10={string.Join(" ", vdp.Regs.Take(11).Select(b => b.ToString("X2")))} height={vdp.FrameHeight} irq={vdp.Irq} lastLine={vdp.Line}");
        if (Flag("hash")) Console.WriteLine($"hash={hashLast:X16}");
        if (fastGaps != null)
            Console.WriteLine($"fast writes: {fastTotal} data-port writes during the picture; gaps up to {fastLimit} cycles: {fastGaps.Sum()} ({string.Join(" ", Enumerable.Range(0, fastLimit + 1).Where(g => fastGaps[g] > 0).Select(g => $"{g}:{fastGaps[g]}"))})");
        if (Flag("audio-stats"))
            Console.WriteLine($"audio: {audioN} sample pairs ({audioN / (double)session.SampleRate:F1} s), peak L {audioPeakL} R {audioPeakR}, rms L {Math.Sqrt(audioSqL / Math.Max(1, audioN)):F0} R {Math.Sqrt(audioSqR / Math.Max(1, audioN)):F0}, " +
                              $"{audioActiveFrames} of {frames} frames louder than 200, battery={session.HasBattery}, sound unit {board.Sound.CoreName}");
        if (Flag("regs")) { var z = board.Cpu; Console.WriteLine($"regs AF={z.AF:X4} BC={z.BC:X4} DE={z.DE:X4} HL={z.HL:X4} IX={z.IX:X4} IY={z.IY:X4} SP={z.SP:X4} PC={z.PC:X4} R={z.R:X2} cycles={board.Cycles}"); }
        if (mesenDir != null)
        {
            Console.WriteLine($"mesen compare: stable frames {stableFrames}: {stableMatched} identical, {stableFrames - stableMatched} differ" + (firstDiffFrame > 0 ? $" (first at frame {firstDiffFrame})" : "") +
                              (mesenDirB != null ? $"; unstable frames (Mesen's two runs differ) {unstableFrames}: this emulator equals one of them in {unstableMatched}" : ""));
        }
        return mesenDir != null && stableFrames > stableMatched ? 1 : 0;
    }

    /// <summary>"60,120,300-310" into a set of frame numbers.</summary>
    internal static HashSet<int> ParseFrames(string spec)
    {
        var set = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-');
            int a = int.Parse(ab[0]), b = ab.Length > 1 ? int.Parse(ab[1]) : a;
            for (int i = a; i <= b; i++) set.Add(i);
        }
        return set;
    }

    internal static ulong HashFrame(IConsoleSession s)
    {
        ulong h = 0xcbf29ce484222325;
        int n = s.FrameWidth * s.FrameHeight;
        var f = s.Frame;
        for (int i = 0; i < n; i++) { h ^= f[i]; h *= 0x100000001b3; }
        return h;
    }

    internal static void MixWav(string path, short[] mono, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + mono.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(mono.Length * 2);
        foreach (var s in mono) w.Write(s);
    }
}

/// <summary>ROM loading and input scripts shared by the Master System tools.</summary>
internal static class SmsRomIo
{
    /// <summary>The board session under a session the app's factory returned: every Sega session is wrapped by <c>SegaPictureSession</c> (the NES picture-chip bridge), so a tool that wants the Master System / Game Gear board looks through it.</summary>
    public static SmsSession Concrete(IConsoleSession s) =>
        (s as NesEmulator.Mix.SegaPictureSession)?.Inner as SmsSession ?? s as SmsSession ?? throw new InvalidCastException($"{s.GetType().Name} is not a Master System / Game Gear session");

    /// <summary>The ROM bytes of a file: a .zip is searched for its first Master System / Game Gear / SG-1000 file; any other file is read as it is.</summary>
    public static byte[] Load(string path, out string innerName)
    {
        innerName = Path.GetFileName(path);
        // a .7z (the Master System library keeps one game per archive; some hold several versions): "archive.7z" takes the first ROM by name, "archive.7z#text" the first whose name contains the text. Needs 7-Zip.
        int hash = path.IndexOf(".7z#", StringComparison.OrdinalIgnoreCase);
        if (hash >= 0 || path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            string archive = hash >= 0 ? path[..(hash + 3)] : path, pick = hash >= 0 ? path[(hash + 4)..] : "";
            string tmp = Path.Combine(Path.GetTempPath(), "bn-7z-" + Guid.NewGuid().ToString("N"));
            try
            {
                SweepSource.SevenZip(archive, tmp);
                string[] roms = { ".sms", ".gg", ".sg", ".sc" };
                var file = Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories).Where(f => roms.Contains(Path.GetExtension(f).ToLowerInvariant()) && Path.GetFileName(f).Contains(pick, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? throw new InvalidDataException($"no ROM{(pick != "" ? $" matching '{pick}'" : "")} inside {archive}");
                innerName = Path.GetFileName(file);
                return File.ReadAllBytes(file);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var zip = ZipFile.OpenRead(path);
        string[] ext = { ".sms", ".gg", ".sg", ".sc" };
        var e = zip.Entries.FirstOrDefault(x => ext.Any(y => x.Name.EndsWith(y, StringComparison.OrdinalIgnoreCase))) ?? throw new InvalidDataException("no ROM inside " + path);
        innerName = e.Name;
        using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    /// <summary>"60:Right+A,90:" into a frame -> buttons map (an entry holds until the next).</summary>
    public static Dictionary<int, PadButtons> ParseInput(string spec)
    {
        var d = new Dictionary<int, PadButtons>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            PadButtons b = 0;
            if (kv.Length > 1) foreach (var name in kv[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) b |= Enum.Parse<PadButtons>(name, true);
            d[int.Parse(kv[0])] = b;
        }
        return d;
    }
}

/// <summary>PNG output and the comparison with a Mesen screenshot.</summary>
internal static class SmsPng
{
    public static void Save(uint[] px, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++) row[x] = (int)px[y * w + x];
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w);
        }
        bmp.UnlockBits(data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <param name="fitHeight">When the picture is taller than this (the oracle's capture script writes the whole video frame, 240 lines, when run with -Overscan 0), the rows are cut evenly top and bottom to this height: a 192-line picture sits 24 rows in, a 224-line one 8.</param>
    public static uint[] Load(string path, out int w, out int h, int fitHeight = 0)
    {
        using var bmp = new Bitmap(path);
        w = bmp.Width; h = bmp.Height;
        var px = new uint[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
            for (int x = 0; x < w; x++) px[y * w + x] = (uint)row[x] | 0xFF000000u;
        }
        bmp.UnlockBits(data);
        if (fitHeight > 0 && h > fitHeight && (h - fitHeight) % 2 == 0)
        {
            int top = (h - fitHeight) / 2;
            px = px.AsSpan(top * w, fitHeight * w).ToArray();
            h = fitHeight;
        }
        return px;
    }

    /// <summary>Compares a frame with a PNG. Returns the number of differing pixels (or -1 for a size mismatch) and describes them; writes a diff image (red where they differ, grey where they agree).</summary>
    public static bool SameAsFrame(uint[] mine, uint[] theirs)
    {
        int n = theirs.Length;
        for (int i = 0; i < n; i++) if ((mine[i] | 0xFF000000u) != theirs[i]) return false;
        return true;
    }

    /// <summary>"identical", or how many bytes differ and where (the first few runs), against a binary dump the Mesen script wrote.</summary>
    public static string DiffBytes(byte[] mine, string theirsPath)
    {
        if (!File.Exists(theirsPath)) return "no Mesen dump";
        var theirs = File.ReadAllBytes(theirsPath);
        int n = Math.Min(mine.Length, theirs.Length), count = 0;
        var runs = new List<string>();
        int i = 0;
        while (i < n)
        {
            if (mine[i] == theirs[i]) { i++; continue; }
            int start = i; while (i < n && mine[i] != theirs[i]) { count++; i++; }
            if (runs.Count < 6) runs.Add(i - start == 1 ? $"${start:X4}={mine[start]:X2}/{theirs[start]:X2}" : $"${start:X4}-${i - 1:X4}={mine[start]:X2}../{theirs[start]:X2}..");
        }
        return count == 0 ? "identical" : $"{count} bytes differ ({string.Join(" ", runs)}{(runs.Count == 6 ? " ..." : "")})";
    }

    public static int Compare(uint[] mine, int w, int h, string theirsPath, string diffPath, out string summary, bool writeDiff = true)
    {
        var theirs = Load(theirsPath, out int tw, out int th, h);
        if (tw != w || th != h) { summary = $"SIZE MISMATCH: mine {w}x{h}, Mesen {tw}x{th}"; return -1; }
        var diff = new uint[w * h];
        int count = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var firsts = new List<string>();
        for (int i = 0; i < w * h; i++)
        {
            if ((mine[i] | 0xFF000000u) == theirs[i]) { uint g = (mine[i] >> 16 & 0xFF) / 3 + 0x40; diff[i] = 0xFF000000u | g << 16 | g << 8 | g; continue; }
            diff[i] = 0xFFFF0000u;
            count++;
            int x = i % w, y = i / w;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            if (firsts.Count < 4) firsts.Add($"({x},{y}) mine {mine[i] & 0xFFFFFF:X6} mesen {theirs[i] & 0xFFFFFF:X6}");
        }
        summary = count == 0 ? "identical" : $"{count} pixels differ in x {minX}-{maxX}, y {minY}-{maxY}; e.g. {string.Join("; ", firsts)}";
        if (count > 0 && writeDiff) Save(diff, w, h, diffPath);
        return count;
    }
}

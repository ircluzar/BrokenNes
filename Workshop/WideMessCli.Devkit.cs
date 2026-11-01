using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// Milestone 5, the devkit (docs/projects/widemess-devkit.md): what a WideMess game built with BrokenDev uses, measured from outside the game.
/// <list type="bullet">
/// <item><c>--wms-run --rom game.wms [--frames N] [--input "f:Buttons,..."] [--png-at f,...] [--out-dir d]</c>: runs a game and reports its WideMess features
/// (<see cref="Measure"/>) and where the CPU spent the last 30 frames, the WideMess counterpart of <c>--snesrun</c>.</item>
/// <item><see cref="Measure"/> is also what BrokenDev's <c>verify</c> checks for a WideMess project (its <c>expect.wms</c> keys) and what the devkit's
/// <c>--wms-test</c> checks use.</item>
/// </list>
/// Measuring never changes the game: a layer is measured by drawing the frame again with one layer on (the chip's own registers, put back after),
/// sprites by reading the WideMess sprite table, sound by reading the voices' envelopes through the DSP's second page (WBANK put back after).
/// </summary>
internal static partial class WideMessCli
{
    /// <summary>What a WideMess game uses, at one moment.</summary>
    internal sealed class DevkitReport
    {
        public bool Unlocked;
        public int Width, Height;
        public bool Wide, Layers8, WideSprites, FastCpu, FastDma;
        /// <summary>Layers that draw something when drawn alone (pixels that differ from the picture with no layer on).</summary>
        public int LayersDrawing;
        public int[] LayerPixels = new int[PPU_WMS.Layers];
        /// <summary>WideMess-table sprites on the picture (their rectangle meets 400 x 224), when the WideMess table is on.</summary>
        public int SpritesOnScreen;
        /// <summary>Distinct colours in the picture.</summary>
        public int Colours;
        /// <summary>Non-zero bytes in the work RAM above the SNES's 128 KB (banks $70-$7D).</summary>
        public int BigWramBytes;
        /// <summary>Voices 8-31 sounding (envelope above 0) and voices 0-7 sounding; -1 when the sound chip is not the WideMess's.</summary>
        public int WideVoices = -1, SnesVoices = -1;

        public string Line() =>
            $"unlocked={Unlocked} picture={Width}x{Height} wide={Wide} layers8={Layers8} layersDrawing={LayersDrawing} [{string.Join(",", LayerPixels)}] " +
            $"sprites={SpritesOnScreen} (WideMess table {WideSprites}) colours={Colours} fastCpu={FastCpu} fastDma={FastDma} bigWramBytes={BigWramBytes} " +
            $"voices={(WideVoices < 0 ? "n/a" : $"{SnesVoices}+{WideVoices}")}";
    }

    /// <summary><c>--wms-run</c>: run a game, report what it uses and where the CPU is.</summary>
    public static int RunGame(string[] args)
    {
        RomTestCli.EnsureConsole();
        Consoles.WideMessEnabled = true;
        string rom = Opt(args, "rom", ""), outDir = Opt(args, "out-dir", ""), input = Opt(args, "input", "");
        if (!File.Exists(rom)) { Console.Error.WriteLine("usage: --wms-run --rom game.wms [--frames N] [--input \"f:Buttons,...\"] [--png-at f,...] [--out-dir d]"); return 2; }
        int frames = int.Parse(Opt(args, "frames", "300"));
        var pngAt = Opt(args, "png-at", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        var steps = ParsePadScript(input);
        using var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(rom));
        var hits = new Dictionary<uint, int>();
        int sampleFrom = Math.Max(0, frames - 30);
        PadButtons held = PadButtons.None;
        // --ram-trap: stop at the first instruction fetched from work RAM (a game built with the devkit never runs code there: a crash), and show
        // the instructions before it
        // --trap-pc BBXXXX: the same at the first instruction at that address (an interrupt handler a game should never reach, say)
        string trapPcText = Opt(args, "trap-pc", "");
        int trapPc = trapPcText != "" ? Convert.ToInt32(trapPcText.TrimStart('$'), 16) : -1;
        bool trap = args.Contains("--ram-trap", StringComparer.OrdinalIgnoreCase) || trapPc >= 0;
        var ring = new (uint pc, ushort a, ushort x, ushort sp, byte p)[64]; int ringAt = 0; bool trapped = false;
        int audioPeak = 0, audibleFrames = 0;
        for (int f = 1; f <= frames && !trapped; f++)
        {
            if (steps.TryGetValue(f, out var b)) held = b;
            s.SetPad(0, held);
            if (trap)
                s.Board.InstructionHook = cpu =>
                {
                    uint pc = (uint)cpu.PBR << 16 | cpu.PC;
                    if (!trapped && (trapPc >= 0 ? pc == (uint)trapPc : (cpu.PBR & 0x40) == 0 && cpu.PC < 0x8000 || cpu.PBR is >= 0x70 and <= 0x7F))
                    {
                        trapped = true;
                        Console.WriteLine($"RAM TRAP at frame {f}: PC ${cpu.PBR:X2}:{cpu.PC:X4}; the instructions before it (oldest first):");
                        for (int k = 0; k < ring.Length; k++)
                        {
                            var e = ring[(ringAt + k) % ring.Length];
                            if (e.pc != 0) Console.WriteLine($"  ${e.pc >> 16:X2}:{e.pc & 0xFFFF:X4}  A={e.a:X4} X={e.x:X4} S={e.sp:X4} P={e.p:X2}");
                        }
                    }
                    ring[ringAt] = (pc, cpu.A, cpu.X, cpu.S, cpu.P); ringAt = (ringAt + 1) % ring.Length;
                };
            else s.Board.InstructionHook = f > sampleFrom ? cpu => { uint pc = (uint)cpu.PBR << 16 | cpu.PC; hits[pc] = hits.GetValueOrDefault(pc) + 1; } : null;
            s.RunFrame();
            int got; int framePeak = 0;
            while ((got = s.ReadSamples(audio)) > 0) for (int k = 0; k < got; k++) framePeak = Math.Max(framePeak, Math.Abs((int)audio[k]));
            audioPeak = Math.Max(audioPeak, framePeak);
            if (framePeak >= 66) audibleFrames++;   // 0.002 of full scale, BrokenDev verify's threshold
            if (pngAt.Contains(f) && outDir != "")
            {
                Directory.CreateDirectory(outDir);
                string p = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(rom)}_f{f:D5}.png");
                MixLab.MixLabCli.SaveArgb(s.Frame, s.FrameWidth, s.FrameHeight, p);
                Console.WriteLine($"png: {p}");
            }
        }
        s.Board.InstructionHook = null;
        var c = s.Board.Cpu;
        Console.WriteLine($"{Path.GetFileName(rom)}: {s.Description}");
        Console.WriteLine($"frames={frames} cpu PC=${c.PBR:X2}:{c.PC:X4} A=${c.A:X4} X=${c.X:X4} Y=${c.Y:X4} S=${c.S:X4} DBR=${c.DBR:X2} P=${c.P:X2} wai={c.Waiting} stopped={c.Stopped}");
        Console.WriteLine("wms: " + Measure(s).Line());
        Console.WriteLine($"apu: {s.Board.Apu.Describe()}");
        Console.WriteLine($"audio: peak {audioPeak / 32768.0:F3}, audible in {audibleFrames} of {frames} frames");
        // --wram ADDR:LEN[,ADDR:LEN]: bytes of work RAM (20-bit work-RAM addresses: $00000-$1FFFF the SNES's 128 KB, $20000+ banks $70-$7D)
        foreach (string w in Opt(args, "wram", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = w.Split(':');
            int a = Convert.ToInt32(p[0].TrimStart('$'), 16), n = p.Length > 1 ? int.Parse(p[1]) : 16;
            byte B(int x) => x < 0x20000 ? s.Board.Wram[x] : s.Board.WramHigh is { } h && x - 0x20000 < h.Length ? h[x - 0x20000] : (byte)0;
            Console.WriteLine($"wram ${a:X5}: {string.Join(" ", Enumerable.Range(0, n).Select(k => B(a + k).ToString("X2")))}");
        }
        if (args.Contains("--voices", StringComparer.OrdinalIgnoreCase) && s.Board.Apu is APU_SFC apu && WideSoundOn(apu.Dsp))
        {
            // every voice that has a pitch: its registers (the second page through WBANK, put back after)
            var dsp = apu.Dsp;
            byte bank = (byte)WideRead(dsp, 0x8C);
            Console.WriteLine($"voices (WDIR ${WideRead(dsp, 0xED):X1}{WideRead(dsp, 0xDD):X2}00): VOLL VOLR PITCH SRCN ADSR1 ADSR2 GAIN ENVX OUTX");
            for (int v = 0; v < 32; v++)
            {
                int R(int r) { if (v < 8) return dsp.Regs[v * 16 + r]; WideWrite(dsp, 0x8C, (byte)((v - 8) >> 3)); return WideRead(dsp, 0x80 + ((v - 8) & 7) * 16 + r); }
                int pitch = R(2) | R(3) << 8;
                if (pitch != 0) Console.WriteLine($"  {v,2}: {R(0),3} {R(1),3} {pitch:X4}  {R(4),3}  {R(5):X2}  {R(6):X2}  {R(7):X2}  {R(8):X2}  {R(9):X2}");
            }
            WideWrite(dsp, 0x8C, bank);
            if (apu is APU_WMS wms)
            {
                int dir = WideRead(dsp, 0xED) << 16 | WideRead(dsp, 0xDD) << 8;
                for (int e = 0; e < 4; e++)
                {
                    int o = dir + e * 16;
                    int A(int k) => wms.SoundRam(o + k) | wms.SoundRam(o + k + 1) << 8 | (wms.SoundRam(o + k + 2) & 15) << 16;
                    int start = A(0);
                    Console.WriteLine($"  dir {e}: start ${start:X5} format {wms.SoundRam(o + 3):X2} loop ${A(4):X5} end ${A(8):X5}  first bytes {string.Join(" ", Enumerable.Range(0, 8).Select(k => wms.SoundRam(start + k).ToString("X2")))}");
                }
            }
        }
        long total = hits.Values.Sum(v => (long)v);
        Console.WriteLine($"hottest PCs over the last 30 frames ({total:N0} instructions):");
        foreach (var kv in hits.OrderByDescending(k => k.Value).Take(8)) Console.WriteLine($"  ${kv.Key >> 16:X2}:{kv.Key & 0xFFFF:X4} {100.0 * kv.Value / Math.Max(1, total),6:F1}%");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------ --wms-test, milestone 5

    /// <summary>One run of a devkit ROM: the report at the end, the most of voices 8-31 sounding in a frame, the frames with any sounding, and the
    /// game's own frame counter at the end when its address is given (the sample's game_frame, from its .sym).</summary>
    private sealed record DevkitRun(DevkitReport Report, int MaxWideVoices, int WideVoiceFrames, int GameFrames, double AudioPeak);

    private static DevkitRun RunDevkit(string rom, int frames, string input = "", int gameFrameAddr = -1)
    {
        using var s = Session(ConsoleKind.WideMess, File.ReadAllBytes(rom));
        var steps = ParsePadScript(input);
        PadButtons held = PadButtons.None;
        int maxVoices = 0, voiceFrames = 0, peak = 0;
        for (int f = 1; f <= frames; f++)
        {
            if (steps.TryGetValue(f, out var b)) held = b;
            s.SetPad(0, held);
            s.RunFrame();
            int got;
            while ((got = s.ReadSamples(audio)) > 0) for (int k = 0; k < got; k++) peak = Math.Max(peak, Math.Abs((int)audio[k]));
            int v = WideVoicesSounding(s);
            if (v > maxVoices) maxVoices = v;
            if (v > 0) voiceFrames++;
        }
        int game = gameFrameAddr >= 0 ? s.Board.Wram[gameFrameAddr & 0x1FFFF] | s.Board.Wram[(gameFrameAddr + 1) & 0x1FFFF] << 8 : -1;
        return new DevkitRun(Measure(s), maxVoices, voiceFrames, game, peak / 32768.0);
    }

    /// <summary>A symbol's 24-bit address in a WLA-DX .sym ("BBBBAAAA name"), as a work RAM offset for banks $7E-$7F; -1 when absent.</summary>
    private static int SymbolWram(string sym, string name)
    {
        foreach (string line in File.ReadLines(sym))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 2 && p[1] == name && p[0].Length == 8)
            {
                int a = Convert.ToInt32(p[0], 16), bank = (a >> 16) & 0xFF;
                if (bank is 0x7E or 0x7F) return (bank - 0x7E) << 16 | (a & 0xFFFF);
            }
        }
        return -1;
    }

    /// <summary>Milestone 5's checks (WideMess/testroms/devkit/build.ps1 builds the ROMs): the template of 'brokendev new widemess' boots on the
    /// WideMess console with what it shows, the sample game shows every feature, each with negative controls. False when the ROMs are missing.</summary>
    private static bool DevkitTests(string dir)
    {
        string tpl = Path.Combine(dir, "devkit_template.wms"), tplNo = Path.Combine(dir, "devkit_template_nounlock.wms");
        string smp = Path.Combine(dir, "devkit_sample.wms"), smpNo = Path.Combine(dir, "devkit_sample_nounlock.wms"), smpSlow = Path.Combine(dir, "devkit_sample_snesspeed.wms"), sym = Path.Combine(dir, "devkit_sample.sym");
        foreach (var f in new[] { tpl, tplNo, smp, smpNo, smpSlow, sym }) if (!File.Exists(f)) { Console.Error.WriteLine($"missing test ROM {f} (build WideMess/testroms/devkit/build.ps1)"); return false; }

        Console.WriteLine("M5.1 The template of 'brokendev new widemess' (devkit_template.wms): it builds and boots on the WideMess console");
        var t = RunDevkit(tpl, 300);
        var tr = t.Report;
        Check(tr.Unlocked && tr.Width == 400 && tr.Height == 224 && tr.Layers8, "it unlocks WideMess and draws 400 x 224 in the 8-layer mode", $"unlocked {tr.Unlocked}, {tr.Width} x {tr.Height}, LAYERS8 {tr.Layers8}");
        Check(tr.LayersDrawing == 3, "its 3 layers draw (wms.json: title BG1, hills BG7, sky BG8)", $"{tr.LayersDrawing}: {string.Join(" ", tr.LayerPixels)}");
        Check(tr.SpritesOnScreen == 1 && tr.WideSprites, "its sprite is on the picture, from the WideMess table", $"{tr.SpritesOnScreen}, table {tr.WideSprites}");
        Check(t.WideVoiceFrames >= 150 && t.MaxWideVoices >= 2, "its song sounds on the WideMess voices (8-31)", $"{t.WideVoiceFrames} of 300 frames, at most {t.MaxWideVoices} at once, peak {t.AudioPeak:F2}");
        Check(tr.FastCpu && tr.BigWramBytes > 0, "fast mode is on and the work RAM above 128 KB is in use", $"fast {tr.FastCpu}, {tr.BigWramBytes} bytes");

        Console.WriteLine("M5.2 Negative control: the template built with -D WMS_NOUNLOCK (it writes every WideMess register, locked)");
        var tn = RunDevkit(tplNo, 300).Report;
        Check(!tn.Unlocked && tn.Width == 256 && !tn.Layers8 && tn.LayersDrawing == 0 && tn.SpritesOnScreen == 0 && !tn.FastCpu, "nothing of WideMess happens: locked, 256 wide, no layer, no sprite, SNES speed",
              $"unlocked {tn.Unlocked}, {tn.Width} wide, layers {tn.LayersDrawing}, sprites {tn.SpritesOnScreen}, fast {tn.FastCpu}");
        Check(tn.WideVoices <= 0 && tn.Colours == 1, "and no WideMess voice sounds; the picture is one colour (the game's WideMess picture never reached the screen)", $"voices {tn.WideVoices}, {tn.Colours} colours");

        Console.WriteLine("M5.3 The sample game, Wide Blaster (devkit_sample.wms), 900 frames of play");
        const string play = "30:Right+B,200:Up+B,260:Down+Y,400:B,700:Left+Y";
        int gameFrame = SymbolWram(sym, "game_frame");
        Check(gameFrame >= 0, "its symbol file names game_frame", $"${gameFrame:X5}");
        var s = RunDevkit(smp, 900, play, gameFrame);
        var sr = s.Report;
        Check(sr.Unlocked && sr.Width == 400 && sr.Layers8 && sr.LayersDrawing == 8, "8 layers draw at 400 x 224", $"{sr.LayersDrawing}: {string.Join(" ", sr.LayerPixels)}");
        Check(sr.SpritesOnScreen > 128, "more sprites on the picture than a SNES's whole table (128)", $"{sr.SpritesOnScreen}");
        Check(s.MaxWideVoices > 8 && s.WideVoiceFrames >= 800, "more of the WideMess voices sound at once than the S-DSP has (8), nearly all the time", $"at most {s.MaxWideVoices}, {s.WideVoiceFrames} of 900 frames");
        Check(sr.FastCpu && sr.FastDma, "fast CPU and fast DMA are on", $"{sr.FastCpu} {sr.FastDma}");
        Check(sr.BigWramBytes >= 2000, "its pools, path and recorder fill work RAM above the SNES's 128 KB", $"{sr.BigWramBytes:N0} non-zero bytes");
        Check(s.GameFrames >= 880, "fast mode keeps its main loop at 60 frames a second (the loading frames aside)", $"game_frame {s.GameFrames} after 900 frames");

        Console.WriteLine("M5.4 Negative controls for the sample's checks");
        var sn = RunDevkit(smpNo, 300).Report;
        Check(!sn.Unlocked && sn.Width == 256 && sn.LayersDrawing == 0 && sn.SpritesOnScreen == 0, "the sample built with -D WMS_NOUNLOCK: locked, 256 wide, no layer, no sprite", $"{sn.Width} wide, layers {sn.LayersDrawing}, sprites {sn.SpritesOnScreen}");
        Check(!(tr.LayersDrawing == 8) && !(tr.SpritesOnScreen > 128) && !(t.MaxWideVoices > 8), "the template fails the sample's thresholds (8 layers, over 128 sprites, over 8 voices): the checks can say no",
              $"{tr.LayersDrawing} layers, {tr.SpritesOnScreen} sprites, {t.MaxWideVoices} voices");
        var slow = RunDevkit(smpSlow, 900, play, gameFrame);
        Check(!slow.Report.FastCpu && slow.GameFrames < 500, "the sample at SNES speed (-D SAMPLE_SNES_SPEED) cannot keep 60 frames a second", $"game_frame {slow.GameFrames} after 900 frames, fast {slow.Report.FastCpu}");
        return true;
    }

    /// <summary>"frame:Button+Button,frame:,..." (BrokenDev's input format) as frame -> the buttons held from that frame.</summary>
    internal static SortedDictionary<int, PadButtons> ParsePadScript(string spec)
    {
        var d = new SortedDictionary<int, PadButtons>();
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon < 0 || !int.TryParse(part[..colon], out int f)) throw new ArgumentException($"bad input step '{part}' (frame:Buttons)");
            PadButtons b = PadButtons.None;
            foreach (string n in part[(colon + 1)..].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) b |= Enum.Parse<PadButtons>(n, true);
            d[f] = b;
        }
        return d;
    }

    internal static DevkitReport Measure(SnesSession s)
    {
        var r = new DevkitReport { Unlocked = s.WideMess is { Unlocked: true }, Width = s.FrameWidth, Height = s.FrameHeight };
        var frame = s.Frame.AsSpan(0, s.FrameWidth * s.FrameHeight);
        var seen = new HashSet<uint>();
        foreach (var p in frame) { seen.Add(p); if (seen.Count > 4096) break; }
        r.Colours = seen.Count;
        var ppu = s.WideMessPpu;
        if (ppu != null)
        {
            r.Wide = ppu.WideOutput; r.Layers8 = ppu.LayersMode; r.WideSprites = ppu.WideSpriteTable;
            if (r.Layers8) MeasureLayers(ppu, r, s.FrameHeight);
            if (r.WideSprites) r.SpritesOnScreen = SpritesOnPicture(ppu.WideOam, ppu.WideOutput ? PPU_WMS.WideWidth : 256, s.FrameHeight);
        }
        var board = s.Board;
        r.FastCpu = board.FastCpu;
        r.FastDma = board.DmaByteClocks < 8;
        if (board.WramHigh is { } high) foreach (byte b in high) if (b != 0) r.BigWramBytes++;
        CountVoices(s, r);
        return r;
    }

    private static void MeasureLayers(PPU_WMS ppu, DevkitReport r, int height)
    {
        int lines = Math.Min(height, 224), w = ppu.WideOutput ? PPU_WMS.WideWidth : 256;
        int tm = ppu.MainLayers;
        uint[] Draw(int mask)
        {
            ppu.WriteWide(0xC1, (byte)mask);
            for (int line = 1; line <= lines; line++) ppu.RenderLine(line);
            return ppu.WideBuffer.AsSpan(0, w * lines).ToArray();
        }
        uint[] none = Draw(0);
        for (int layer = 0; layer < PPU_WMS.Layers; layer++)
        {
            if ((tm & (1 << layer)) == 0) continue;
            uint[] alone = Draw(1 << layer);
            int diff = 0;
            for (int i = 0; i < alone.Length; i++) if (alone[i] != none[i]) diff++;
            r.LayerPixels[layer] = diff;
            if (diff > 0) r.LayersDrawing++;
        }
        Draw(tm);   // the frame as the game left it (its last line's registers)
    }

    /// <summary>WideMess-table entries (spec 3.3) whose rectangle meets the picture.</summary>
    internal static int SpritesOnPicture(byte[] oam, int width, int height)
    {
        int n = 0;
        for (int i = 0; i < PPU_WMS.TableSprites; i++)
        {
            int o = i * 8;
            int x = oam[o] | (oam[o + 1] & 3) << 8; if (x >= 512) x -= 1024;
            int y = oam[o + 2] | (oam[o + 3] & 3) << 8; if (y >= 512) y -= 1024;
            int sw = 8 << (oam[o + 7] & 3), sh = 8 << ((oam[o + 7] >> 2) & 3);
            if (x + sw > 0 && x < width && y + sh > 0 && y < height) n++;
        }
        return n;
    }

    /// <summary>Voices 8-31 sounding (ENVX above 0) right now; 0 when the sound chip is not the WideMess's or is locked.</summary>
    internal static int WideVoicesSounding(SnesSession s)
    {
        var r = new DevkitReport();
        CountVoices(s, r);
        return Math.Max(0, r.WideVoices);
    }

    /// <summary>The voices sounding: the S-DSP's ENVX ($x8) for voices 0-7 and, through the second page, voices 8-31 (WBANK put back after).
    /// Leaves the counts at -1 when the sound chip is not the WideMess's, or when it is locked (the second page is then the S-DSP's mirror).</summary>
    private static void CountVoices(SnesSession s, DevkitReport r)
    {
        if (s.Board.Apu is not APU_SFC apu) return;
        var dsp = apu.Dsp;
        int snes = 0;
        for (int v = 0; v < 8; v++) if (dsp.Regs[v * 16 + 8] != 0) snes++;
        r.SnesVoices = snes;
        if (!WideSoundOn(dsp)) return;
        int wide = 0;
        byte bank = (byte)WideRead(dsp, 0x8C);
        for (int b = 0; b < 3; b++)
        {
            WideWrite(dsp, 0x8C, (byte)b);
            for (int v = 0; v < 8; v++) if (WideRead(dsp, 0x80 + v * 16 + 8) != 0) wide++;
        }
        WideWrite(dsp, 0x8C, bank);
        r.WideVoices = wide;
    }
}

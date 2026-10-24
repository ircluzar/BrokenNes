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

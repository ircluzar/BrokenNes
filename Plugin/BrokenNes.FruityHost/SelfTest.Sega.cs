// Round 3 (track C3b): Sega games in the plugin's ROM mode, and their per-channel stems. Tiny ROMs are made here (the repository carries no game): 68000 and Z80 programs that set the sound chips going.
using System.Diagnostics;
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // ------------------------------------------------------------------------------------------------
    // tiny Sega ROMs

    private static void MdMoveB(List<byte> c, int value, long addr) => c.AddRange([0x13, 0xFC, 0x00, (byte)value, (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr]);
    private static void MdMoveW(List<byte> c, int value, long addr) => c.AddRange([0x33, 0xFC, (byte)(value >> 8), (byte)value, (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr]);
    private static void MdYm(List<byte> c, int reg, int value, int part = 0) { MdMoveB(c, reg, 0xA04000 + part * 2); MdMoveB(c, value, 0xA04001 + part * 2); }

    /// <summary>The block and F-number of a YM2612 note (NTSC input clock 7,670,453 Hz): the block whose F-number lands in the top octave of its range.</summary>
    internal static (int Block, int Fnum) OpnFreq(double hz)
    {
        for (int blk = 0; blk < 8; blk++)
        {
            double fnum = hz * 1048576.0 * 144.0 / (7670453.0 * (blk == 0 ? 0.5 : 1 << (blk - 1)));
            if (fnum >= 1024 && fnum < 2048) return (blk, (int)Math.Round(fnum));
        }
        return (7, 2047);
    }

    /// <summary>The block and F-number of a YM2413 note at a 3,579,545 Hz clock (one sample per 72 clocks, F-number x 2^block / 2^19 of that rate).</summary>
    internal static (int Block, int Fnum) OpllFreq(double hz)
    {
        double unit = 3579545.0 / 72 / 524288;
        for (int blk = 0; blk < 8; blk++)
        {
            double fnum = hz / unit / (1 << blk);
            if (fnum >= 256 && fnum < 512) return (blk, (int)Math.Round(fnum));
        }
        return (7, 511);
    }

    /// <summary>A 16 KB Genesis ROM whose 68000 program sets the PSG going (tone 1 period 254 = 440.4 Hz at full volume, tone 2 period 338 = 330.9 Hz at -4 dB) and, with <paramref name="fm"/>, plays FM 1 and FM 2 as sines
    /// (261.7 Hz and 392.2 Hz) through the YM2612 the way a game does it: bus request, reset release, then the chip's registers at $A04000. With <paramref name="dac"/> it also turns the DAC on and streams a 32-sample sine at
    /// about 13 kHz (so about 415 Hz) in an endless loop. The code ends in a branch to itself.</summary>
    internal static byte[] MakeMdRom(bool fm = true, bool dac = false, bool counter = false)
    {
        var c = new List<byte>();
        foreach (byte b in new byte[] { 0x8E, 0x0F, 0x90, 0xA2, 0x15, 0xB2 }) MdMoveB(c, b, 0xC00011);
        if (fm || dac)
        {
            MdMoveW(c, 0x0100, 0xA11100);          // bus request (granted at once: the Z80 is still in reset)
            MdMoveW(c, 0x0100, 0xA11200);          // reset released (the Z80 stays held by the bus request)
        }
        if (fm)
        {
            int[] off = [0, 8, 4, 12];             // operators 1, 2, 3, 4 in the chip's register order
            double[] hz = [261.63, 392.0];
            for (int ch = 0; ch < 2; ch++)
            {
                for (int n = 0; n < 4; n++)
                {
                    int o = off[n] + ch;
                    MdYm(c, 0x30 + o, 0x01); MdYm(c, 0x40 + o, n == 3 ? 0x00 : 0x7F); MdYm(c, 0x50 + o, 0x1F); MdYm(c, 0x60 + o, 0x00); MdYm(c, 0x70 + o, 0x00); MdYm(c, 0x80 + o, 0x0A); MdYm(c, 0x90 + o, 0x00);
                }
                MdYm(c, 0xB0 + ch, 0x07); MdYm(c, 0xB4 + ch, 0xC0);
                var (blk, fnum) = OpnFreq(hz[ch]);
                MdYm(c, 0xA4 + ch, blk << 3 | fnum >> 8); MdYm(c, 0xA0 + ch, fnum & 0xFF);
                MdYm(c, 0x28, 0xF0 | ch);
            }
        }
        if (dac)
        {
            MdYm(c, 0x2B, 0x80);                   // DAC on (FM 6 gives way to it)
            MdMoveB(c, 0x2A, 0xA04000);            // the data register stays selected
            int top = c.Count;
            for (int i = 0; i < 32; i++)
            {
                MdMoveB(c, 128 + (int)Math.Round(100 * Math.Sin(i * 2 * Math.PI / 32)), 0xA04001);
                c.AddRange([0x30, 0x3C, 0x00, 0x34, 0x51, 0xC8, 0xFF, 0xFE]);      // move.w #52,d0 ; dbra d0,* : about 52 x 10 clocks of waiting, for 13 kHz
            }
            int back = top - (c.Count + 2);
            c.AddRange([0x60, 0x00, (byte)(back >> 8), (byte)back]);               // bra.w top
        }
        else if (counter)
        {
            // a counter in work RAM ($FF0000) that also picks the backdrop colour, over a palette of 64 different colours: the picture follows the machine
            c.AddRange([0x23, 0xFC, 0xC0, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x04]);        // move.l #$C0000000,$C00004 : colour RAM write from 0
            for (int i = 0; i < 64; i++) MdMoveW(c, (i * 37 + 0x111) & 0x0EEE, 0xC00000);
            int top = c.Count;
            c.AddRange([0x52, 0x39, 0x00, 0xFF, 0x00, 0x00]);                                // addq.b #1,$FF0000
            c.AddRange([0x10, 0x39, 0x00, 0xFF, 0x00, 0x00]);                                // move.b $FF0000,d0
            c.AddRange([0x02, 0x40, 0x00, 0x3F]);                                            // andi.w #$3F,d0
            c.AddRange([0x00, 0x40, 0x87, 0x00]);                                            // ori.w #$8700,d0 : VDP register 7 (backdrop colour)
            c.AddRange([0x33, 0xC0, 0x00, 0xC0, 0x00, 0x04]);                                // move.w d0,$C00004
            c.AddRange([0x60, (byte)(top - (c.Count + 2))]);                                 // bra.s top
        }
        else c.AddRange([0x60, 0xFE]);              // bra.s *
        var rom = new byte[0x4000];
        new byte[] { 0x00, 0xFF, 0xFE, 0x00, 0x00, 0x00, 0x02, 0x00 }.CopyTo(rom, 0);       // stack pointer, program counter ($200)
        "SEGA MEGA DRIVE "u8.CopyTo(rom.AsSpan(0x100));
        "(C)BN2  2026.OCT"u8.CopyTo(rom.AsSpan(0x110));
        "BROKENNES2 TEST ROM".PadRight(48).Select(ch => (byte)ch).ToArray().CopyTo(rom, 0x120);
        "BROKENNES2 TEST ROM".PadRight(48).Select(ch => (byte)ch).ToArray().CopyTo(rom, 0x150);
        new byte[] { 0x00, 0x00, 0x3F, 0xFF }.CopyTo(rom, 0x1A4);
        "U  "u8.CopyTo(rom.AsSpan(0x1F0));
        c.CopyTo(rom, 0x200);
        return rom;
    }

    private static void Z80Out(List<byte> c, int port, int value) => c.AddRange([0x3E, (byte)value, 0xD3, (byte)port]);

    /// <summary>A 32 KB Master System ROM that sets PSG tone 1 (period 254, 440.4 Hz, full volume) and tone 2 (period 338, 330.9 Hz, -4 dB) going and, with <paramref name="fm"/>, the YM2413 as the Japanese console
    /// has it: FM on ($F2), the user instrument set to a sine, FM channels 1 and 2 playing it at 261.7 Hz and 392.2 Hz.</summary>
    internal static byte[] MakeSmsRom2(bool fm, bool gameGear = false)
    {
        var rom = new byte[0x8000];
        var c = new List<byte> { 0xF3, 0x31, 0xF0, 0xDF };                        // DI ; LD SP,$DFF0
        foreach (byte b in new byte[] { 0x8E, 0x0F, 0x90, 0xA2, 0x15, 0xB2 }) Z80Out(c, 0x7F, b);
        if (fm)
        {
            Z80Out(c, 0xF2, 0x01);                                                  // FM on
            void Reg(int r, int v) { Z80Out(c, 0xF0, r); Z80Out(c, 0xF1, v); }
            byte[] sine = [0x21, 0x21, 0x3F, 0x00, 0x00, 0xF0, 0x08, 0x08];       // the user instrument: a carrier and a modulator that never opens
            for (int i = 0; i < 8; i++) Reg(i, sine[i]);
            double[] hz = [261.63, 392.0];
            for (int ch = 0; ch < 2; ch++)
            {
                var (blk, fnum) = OpllFreq(hz[ch]);
                Reg(0x30 + ch, 0x00); Reg(0x10 + ch, fnum & 0xFF); Reg(0x20 + ch, 0x10 | blk << 1 | fnum >> 8);
            }
        }
        c.AddRange([0x18, 0xFE]);                                                   // JR $
        c.CopyTo(rom, 0);
        "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
        rom[0x7FFF] = (byte)(gameGear ? 0x6C : 0x4C);
        return rom;
    }

    /// <summary>A 32 KB Master System / Game Gear ROM with a counter in work RAM ($C000) that also picks the backdrop colour, over a palette of 32 different colours: the picture follows the machine.</summary>
    internal static byte[] MakeSmsCounterRom(bool gameGear = false)
    {
        var rom = new byte[0x8000];
        byte[] code =
        [
            0xF3, 0x31, 0xF0, 0xDF,                  // DI ; LD SP,$DFF0
            0x21, 0x00, 0xC0,                        // LD HL,$C000
            0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0xC0, 0xD3, 0xBF,   // colour RAM write from 0
            0x06, 0x20, 0x3E, 0x00,                  // LD B,32 ; LD A,0
            0xD3, 0xBE, 0xC6, 0x07, 0x10, 0xFA,      // pal: OUT ($BE),A ; ADD A,7 ; DJNZ pal
            0x3E, 0x40, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF,   // VDP register 1 = $40 (display on)
            0x34, 0x7E, 0xE6, 0x0F,                  // loop: INC (HL) ; LD A,(HL) ; AND $0F
            0xD3, 0xBF, 0x3E, 0x87, 0xD3, 0xBF,      // VDP register 7 = A (backdrop colour)
            0x18, 0xF4,                              // JR loop
        ];
        code.CopyTo(rom, 0);
        "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
        rom[0x7FFF] = (byte)(gameGear ? 0x6C : 0x4C);
        return rom;
    }

    /// <summary>A NES ROM (NROM, 16 KB) with a counter in work RAM ($10) that is also written to the picture chip's mask register, so the picture follows the machine.</summary>
    internal static byte[] MakeNesCounterRom()
    {
        var rom = new byte[16 + 0x4000 + 0x2000];
        "NES\x1A"u8.CopyTo(rom);
        rom[4] = 1; rom[5] = 1;
        byte[] code = [0x78, 0xD8, 0xA2, 0xFF, 0x9A,          // SEI ; CLD ; LDX #$FF ; TXS
                       0xE6, 0x10, 0xA5, 0x10, 0x8D, 0x01, 0x20, 0x4C, 0x05, 0xC0];   // loop: INC $10 ; LDA $10 ; STA $2001 ; JMP loop
        code.CopyTo(rom, 16);
        rom[16 + 0x3FFC] = 0x00; rom[16 + 0x3FFD] = 0xC0;     // reset vector $C000
        return rom;
    }

    // ------------------------------------------------------------------------------------------------
    // helpers

    private string TempGame(string ext, byte[] data)
    {
        string path = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A plugin in ROM mode on a Sega console with the given sound chip, restored from a project that names the game, the way FL loads one. Waits for the game to be running.</summary>
    private PluginInstance SegaGame(string path, int console, string chip, int rate = 48000)
    {
        List<string> chips;
        using (var probe = lib.Create()) chips = Chips(probe);
        byte[] st;
        using (var seed = lib.Create()) { seed.SetParam(PCore, chips.IndexOf(chip)); seed.SetParam(PMode, 1); st = seed.SaveState(); }
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, rate);
        p.LoadState(StateWithRom(st, path));
        WaitForRom(p);                      // (no rendering: companions that join now start with the Mix instance, at the first frame)
        return p;
    }

    /// <summary>Another instance on the same emulator, asking for a channel by the Channel parameter (0 Auto, 1 to 5) or a Sega voice by the Voice parameter (1 = FM 1 ...).</summary>
    private PluginInstance Companion(int rate = 48000, int channelParam = 0, int voiceParam = 0)
    {
        var q = lib.Create();
        q.Dispatcher(Fpd.SetSampleRate, 0, rate);
        if (voiceParam != 0) q.SetParam(PVoice, voiceParam);
        else if (channelParam != 0) q.SetParam(PChannel, channelParam);
        return q;
    }

    /// <summary>Renders the instances block by block in lockstep (the way FL's mixer asks each in turn) and returns each one's mono output.</summary>
    private static float[][] RenderSet(IReadOnlyList<PluginInstance> set, double seconds, int block = 512)
    {
        int rate = 48000, blocks = (int)(seconds * rate / block);
        var outs = set.Select(_ => new float[blocks * block]).ToArray();
        var buf = new float[block * 2];
        fixed (float* f = buf)
            for (int b = 0; b < blocks; b++)
                for (int i = 0; i < set.Count; i++)
                {
                    set[i].NewTick();
                    set[i].GenRender(f, block);
                    for (int k = 0; k < block; k++) outs[i][b * block + k] = (buf[k * 2] + buf[k * 2 + 1]) * 0.5f;
                }
        return outs;
    }

    /// <summary>How strong a tone is in a stretch, against the same stretch measured a quarter tone either side of it.</summary>
    private static double ToneAmp(float[] x, double hz, int from, int to) => Goertzel(x, 48000, hz, from, to);

    private static (bool Present, string Detail) HasTone(float[] x, double hz, int from, int to, double ratio = 4)
    {
        double at = ToneAmp(x, hz, from, to), off = Math.Max(ToneAmp(x, hz * 1.06, from, to), ToneAmp(x, hz / 1.06, from, to));
        return (at > off * ratio && at > 0.002, $"{hz:0.0} Hz {at:0.0000} (neighbours {off:0.0000})");
    }

    // ------------------------------------------------------------------------------------------------
    // Genesis games in ROM mode

    private void RomMd(T t)
    {
        string md = TempGame(".md", MakeMdRom());
        try
        {
            using var p = SegaGame(md, 4, "MD");
            t.Expect(p.GetParam(PConsole) == 4 && p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore)) == "MD", $"the Genesis game's console is {p.GetParam(PConsole)}, chip {p.GetName(Fpn.ParamValue, PCore, p.GetParam(PCore))}");
            var audio = RenderBlocks(p, 2.0);
            t.Expect(p.Test(12) == 0, "the Genesis game crashed");
            t.Expect(audio.All(float.IsFinite), "non-finite audio");
            t.Expect(p.Test(11) >= 90, $"only {p.Test(11)} frames in 2 s");
            t.Expect(p.Test(10) != 0, "no picture");
            t.Expect(p.Test(17) == 4, $"a Genesis game plays through Mix first: the instance is on channel {p.Test(17)}");
            var a = HasTone(audio, 440.4, 48000, audio.Length - 2000); var b = HasTone(audio, 330.9, 48000, audio.Length - 2000);
            var f1 = HasTone(audio, 261.7, 48000, audio.Length - 2000); var f2 = HasTone(audio, 392.2, 48000, audio.Length - 2000);
            t.Expect(a.Present && b.Present, $"the game's PSG tones should be in Mix: {a.Detail}; {b.Detail}");
            t.Expect(f1.Present && f2.Present, $"the game's FM sines should be in Mix: {f1.Detail}; {f2.Detail}");
            nint parent = Win32Host.CreateParent();
            try
            {
                p.ShowEditor(parent);
                for (int k = 0; k < 4; k++) { RenderBlocks(p, 0.1); Win32Host.Pump(60); }
                long dims = p.Test(37);
                int w = (int)(dims & 0xFFFF), h = (int)(dims >> 16);
                t.Expect((w == 256 || w == 320) && (h == 224 || h == 240), $"the picture is {w} x {h}, expected a Genesis mode (256 or 320 wide, 224 or 240 high)");
                t.Expect(p.Test(30, 14) != 0, "the Mix cell should be in the channel strip");
                p.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
            // Instrument Runaway freezes and silences the game; releasing carries on
            p.Test(47, 0, 1);
            var frozen = RenderBlocks(p, 0.5);
            t.Expect(p.Test(45) == 1 && Audio.Rms(frozen, 4000, frozen.Length) < 0.002, "Runaway should freeze the Genesis game and silence the mix");
            p.Test(47, 0, 0);
            var back = RenderBlocks(p, 1.0);
            t.Expect(p.Test(45) == 0 && Audio.Rms(back, 12000, back.Length) > 0.003, "releasing Runaway should give the game's sound back");
        }
        finally { File.Delete(md); }

        // files: loading a Genesis file into a NES emulator switches it to the Genesis console (the game is remembered per console)
        string md2 = TempGame(".md", MakeMdRom());
        string junk = TempGame(".dat", new byte[64]);
        try
        {
            using var q = RomPlugin();
            RenderBlocks(q, 0.3);
            t.Expect(q.Test(15) == 0, "the built-in game should be a NES game");
            t.Expect(LoadFileInto(q, md2) == 0 && WaitConsole(q, 4), "loading a .md file into a NES emulator should switch it to the Genesis");
            t.Expect(q.GetParam(PConsole) == 4 && q.GetName(Fpn.ParamValue, PCore, q.GetParam(PCore)) == "MD", "the console parameter and chip did not follow the loaded file");
            var g = RenderBlocks(q, 1.0);
            t.Expect(q.Test(12) == 0 && Audio.Rms(g, 8000, g.Length) > 0.003, "the Genesis game should play");
            t.Expect(LoadFileInto(q, junk) != 0 && q.Test(15) == 4 && q.Test(12) == 0, "a file that is not a ROM is refused and leaves the Genesis game running");
            q.SetParam(PConsole, 0);
            t.Expect(WaitConsole(q, 0), "switching back to the NES should load the built-in game");
            q.SetParam(PConsole, 4);
            t.Expect(WaitConsole(q, 4) && q.Test(12) == 0, "the Genesis console's game should come back when the console is chosen again");
        }
        finally { File.Delete(md2); File.Delete(junk); }
    }

    // ------------------------------------------------------------------------------------------------
    // the stems of Sega games

    private void SegaStems(T t)
    {
        // ---- the Genesis: Mix, PSG tone 1-3, noise, FM 1-3, the six at once on one game ----
        string md = TempGame(".md", MakeMdRom());
        try
        {
            var set = new List<PluginInstance>();
            try
            {
                var mix = SegaGame(md, 4, "MD"); set.Add(mix);
                // PSG tone 1, 2, 3 and noise by the Channel parameter (1 = Pulse 1 ...: a Sega game's first four channels are the PSG's voices), FM 1 to 3 by the Voice parameter
                foreach (int ch in new[] { 1, 2, 3, 4 }) set.Add(Companion(channelParam: ch));
                foreach (int v in new[] { 1, 2, 3 }) set.Add(Companion(voiceParam: v));
                t.Expect(set.Select(p => (int)p.Test(17)).SequenceEqual([4, 0, 1, 2, 3, 6, 7, 8]), $"channels {string.Join(",", set.Select(p => p.Test(17)))}, expected Mix, PSG 1-3, noise, FM 1-3");
                t.Expect(set.All(p => p.Test(16) == 1 && p.Test(20) == 0), "all eight instances should share emulator #1 with no problem");
                var o = RenderSet(set, 4.0);
                t.Expect(set.Skip(1).All(p => p.Test(26, (nint)p.Test(17)) == 1), "the game's sound unit should have a stem for each of those channels");
                int from = 48000, to = o[0].Length - 2000;
                t.Expect(o[0].All(float.IsFinite) && set[0].Test(12) == 0, "the game crashed or the audio is not finite");
                // each stem is its voice: the tone of its voice is there and the other voices' tones are not
                var tones = new (string Name, double Hz)[] { ("PSG tone 1", 440.4), ("PSG tone 2", 330.9), ("FM 1", 261.7), ("FM 2", 392.2) };
                int[] who = { 1, 2, 5, 6 };         // the instance that carries each of the voices above (PSG 1, PSG 2, FM 1, FM 2)
                for (int s = 0; s < 4; s++)
                {
                    var own = HasTone(o[who[s]], tones[s].Hz, from, to, ratio: 20);
                    t.Expect(own.Present, $"{tones[s].Name}'s stem should carry its tone: {own.Detail}");
                    for (int other = 0; other < 4; other++)
                        if (other != s)
                        {
                            double a = ToneAmp(o[who[s]], tones[other].Hz, from, to), mine = ToneAmp(o[who[s]], tones[s].Hz, from, to);
                            t.Expect(a < mine * 0.06, $"{tones[s].Name}'s stem carries {tones[other].Name} too ({a:0.0000} against its own {mine:0.0000}: not isolated)");
                        }
                }
                t.Expect(Audio.Rms(o[3], from, to) < 0.0005 && Audio.Rms(o[4], from, to) < 0.0005, $"PSG tone 3 and the noise are silent in this game, so are their stems (rms {Audio.Rms(o[3], from, to):0.0000} and {Audio.Rms(o[4], from, to):0.0000})");
                t.Expect(Audio.Rms(o[7], from, to) < 0.0005, $"FM 3 is silent in this game, so is its stem (rms {Audio.Rms(o[7], from, to):0.0000})");
                // together they are the game's mix, sample for sample (the stems come from the same run of the chips, brought to the host rate by resamplers in step with the mix's)
                var sum = new float[o[0].Length];
                for (int i = 1; i < set.Count; i++) for (int k = 0; k < sum.Length; k++) sum[k] += o[i][k];
                double worst = 0; for (int k = from; k < to; k++) worst = Math.Max(worst, Math.Abs(sum[k] - o[0][k]));
                double corr = Correlation(o[0], sum, from);
                t.Expect(corr > 0.9999 && worst < 0.004, $"the stems summed should be the mix: correlation {corr:0.00000}, largest difference {worst:0.00000} (mix RMS {Audio.Rms(o[0], from, to):0.000})");
                t.Note($"Genesis stems: sum vs mix correlation {corr:0.00000}, largest difference {worst:0.00000}; mix RMS {Audio.Rms(o[0], from, to):0.000}");
                // every instance asked for what it got: one rms per instance
                t.Note("stem RMS: " + string.Join("/", o.Skip(1).Select(x => Audio.Rms(x, from, to).ToString("0.000"))));

                // a channel the game's console has no voice for: a Problem, silence, and the game does not run ahead looking for sound
                var fm7 = Companion(voiceParam: 7); set.Add(fm7);
                t.Expect(fm7.Test(20) == 1 && fm7.Test(17) == -1, $"FM 7 does not exist on the Genesis: the instance should be refused with a problem (problem {fm7.Test(20)}, channel {fm7.Test(17)})");
                long frames0 = set[0].Test(11);
                var quiet = RenderSet([fm7], 1.0);
                t.Expect(quiet[0].All(x => x == 0), "a refused instance should be silent");
                t.Expect(set[0].Test(11) - frames0 <= 62, $"the game ran {set[0].Test(11) - frames0} frames while a refused instance rendered 1 s (it must not run ahead)");
                fm7.SetParam(PVoice, 6);                    // FM 6: exists on the Genesis, and is silent in this game
                t.Expect(fm7.Test(20) == 0 && fm7.Test(17) == 11, $"moving to FM 6 should work (problem {fm7.Test(20)}, channel {fm7.Test(17)})");
                fm7.SetParam(PVoice, 10);                   // the DAC
                t.Expect(fm7.Test(20) == 0 && fm7.Test(17) == 15, $"the DAC is a voice of the Genesis (problem {fm7.Test(20)}, channel {fm7.Test(17)})");
            }
            finally { foreach (var p in set) p.Destroy(); }
        }
        finally { File.Delete(md); }

        // ---- the Master System: PSG only on the PSG chip, FM too on the FM chip (and the chip can be swapped under the running game) ----
        string sms = TempGame(".sms", MakeSmsRom2(fm: true));
        try
        {
            var set = new List<PluginInstance>();
            try
            {
                var mix = SegaGame(sms, 3, "SMS"); set.Add(mix);
                set.Add(Companion(channelParam: 1)); set.Add(Companion(channelParam: 2));
                var fm1 = Companion(voiceParam: 1); set.Add(fm1);
                t.Expect(fm1.Test(20) == 1, $"the PSG chip has no FM: asking for FM 1 should be refused (problem {fm1.Test(20)})");
                var o = RenderSet(set, 3.0);
                int from = 48000, to = o[0].Length - 2000;
                var tone1 = HasTone(o[1], 440.4, from, to, 20); var tone2 = HasTone(o[2], 330.9, from, to, 20);
                t.Expect(tone1.Present && tone2.Present, $"the PSG stems should carry the tones: {tone1.Detail}; {tone2.Detail}");
                var inMix = HasTone(o[0], 261.7, from, to);
                t.Expect(!inMix.Present, $"the stock Master System unit has no FM chip, so the YM2413 writes of the game make no sound ({inMix.Detail})");
                // swap to the FM chip under the running game: the instance that asked for FM 1 is accepted, the stems go on in step with the mix (the game wrote its YM2413 registers to the unit that ignored them,
                // so its FM stays silent until the game is started again: the next section starts a game on the FM chip), and swapping back refuses the FM voices again
                set[0].SetParam(PCore, Chips(set[0]).IndexOf("SMSFM"));
                var fm2 = Companion(voiceParam: 2); set.Add(fm2);
                var o2 = RenderSet(set, 3.0);
                int f2 = 48000, t2 = o2[0].Length - 2000;
                t.Expect(fm1.Test(20) == 0 && fm1.Test(17) == 6 && fm2.Test(20) == 0 && fm2.Test(17) == 7, $"after the chip became the FM chip the FM instances should be accepted (FM 1: problem {fm1.Test(20)}, channel {fm1.Test(17)}; FM 2: problem {fm2.Test(20)}, channel {fm2.Test(17)})");
                var sum = new float[o2[0].Length];
                for (int i = 1; i < set.Count; i++) for (int k = 0; k < sum.Length; k++) sum[k] += o2[i][k];
                double worst = 0; for (int k = f2; k < t2; k++) worst = Math.Max(worst, Math.Abs(sum[k] - o2[0][k]));
                double corr = Correlation(o2[0], sum, f2);
                t.Expect(corr > 0.9999 && worst < 0.004 && HasTone(o2[1], 440.4, f2, t2, 20).Present, $"across the chip swap the stems (PSG 1, 2, FM 1, 2) still add up to the mix: correlation {corr:0.00000}, largest difference {worst:0.00000}");
                set[0].SetParam(PCore, Chips(set[0]).IndexOf("SMS"));
                var o3 = RenderSet(set, 1.5);
                t.Expect(fm1.Test(20) == 1 && fm1.Test(17) == -1 && fm2.Test(17) == -1, $"after the chip went back to the PSG chip the FM instances should be refused again (FM 1: problem {fm1.Test(20)}, channel {fm1.Test(17)})");
                var sum3 = new float[o3[0].Length];
                for (int i = 1; i < 3; i++) for (int k = 0; k < sum3.Length; k++) sum3[k] += o3[i][k];
                double worst3 = 0; for (int k = 24000; k < o3[0].Length - 2000; k++) worst3 = Math.Max(worst3, Math.Abs(sum3[k] - o3[0][k]));
                t.Expect(worst3 < 0.004, $"... and the two PSG stems still add up to the mix (largest difference {worst3:0.00000})");
            }
            finally { foreach (var p in set) p.Destroy(); }
        }
        finally { File.Delete(sms); }

        // ---- the same game started on the FM chip: the stems add up to the mix (no swap, no reset in between) ----
        string sms2 = TempGame(".sms", MakeSmsRom2(fm: true));
        try
        {
            var set = new List<PluginInstance>();
            try
            {
                set.Add(SegaGame(sms2, 3, "SMSFM"));
                set.Add(Companion(channelParam: 1)); set.Add(Companion(channelParam: 2)); set.Add(Companion(voiceParam: 1)); set.Add(Companion(voiceParam: 2));
                t.Expect(set.Select(p => (int)p.Test(17)).SequenceEqual([4, 0, 1, 6, 7]), $"channels {string.Join(",", set.Select(p => p.Test(17)))}, expected Mix, PSG 1, PSG 2, FM 1, FM 2");
                var o = RenderSet(set, 3.0);
                int from = 48000, to = o[0].Length - 2000;
                var m1 = HasTone(o[0], 261.7, from, to); var m2 = HasTone(o[0], 392.2, from, to);
                t.Expect(m1.Present && m2.Present, $"the FM chip's game should have its FM in the mix: {m1.Detail}; {m2.Detail}");
                var s1 = HasTone(o[3], 261.7, from, to, 20); var s2 = HasTone(o[4], 392.2, from, to, 20);
                t.Expect(s1.Present && s2.Present, $"the FM stems should carry the FM voices: {s1.Detail}; {s2.Detail}");
                var sum = new float[o[0].Length];
                for (int i = 1; i < set.Count; i++) for (int k = 0; k < sum.Length; k++) sum[k] += o[i][k];
                double worst = 0; for (int k = from; k < to; k++) worst = Math.Max(worst, Math.Abs(sum[k] - o[0][k]));
                double corr = Correlation(o[0], sum, from);
                t.Expect(corr > 0.9999 && worst < 0.004, $"on the FM chip the stems summed should be the mix: correlation {corr:0.00000}, largest difference {worst:0.00000} (the stems here are PSG 1, 2, FM 1 and 2: other voices are silent in this game)");
                t.Note($"Master System FM stems: sum vs mix correlation {corr:0.00000}, largest difference {worst:0.00000}");
            }
            finally { foreach (var p in set) p.Destroy(); }
        }
        finally { File.Delete(sms2); }

        // ---- a Game Gear game: the PSG's stems; no FM voice ----
        string gg = TempGame(".gg", MakeSmsRom2(fm: false, gameGear: true));
        try
        {
            var set = new List<PluginInstance>();
            try
            {
                set.Add(SegaGame(gg, 3, "SMSFM"));            // even on the FM chip a Game Gear game has no YM2413
                set.Add(Companion(channelParam: 1)); set.Add(Companion(channelParam: 2));
                var fm = Companion(voiceParam: 1); set.Add(fm);
                t.Expect(fm.Test(17) == 6 && fm.Test(26, 6) == 0, $"a Game Gear game has no FM: its sound unit offers no stem for FM 1 (instance on channel {fm.Test(17)}, stem available {fm.Test(26, 6)})");
                var o = RenderSet(set, 3.0);
                int from = 48000, to = o[0].Length - 2000;
                t.Expect(HasTone(o[1], 440.4, from, to, 20).Present && HasTone(o[2], 330.9, from, to, 20).Present, "the Game Gear's PSG stems should carry the tones");
                t.Expect(o[3].All(x => x == 0), "the FM 1 instance of a Game Gear game should be silent");
                var sum = new float[o[0].Length];
                for (int i = 1; i < 3; i++) for (int k = 0; k < sum.Length; k++) sum[k] += o[i][k];
                double worst = 0; for (int k = from; k < to; k++) worst = Math.Max(worst, Math.Abs(sum[k] - o[0][k]));
                t.Expect(worst < 0.004, $"the two PSG stems summed should be the Game Gear's mix (largest difference {worst:0.00000})");
            }
            finally { foreach (var p in set) p.Destroy(); }
        }
        finally { File.Delete(gg); }
    }
}

// Instrument extraction for the SNES, the Master System / Game Gear and the Genesis (2026-11-12): the same round trip as SelfTest.Extract.cs on a synthetic driver per sound chip (S-DSP voice, PSG tone,
// YM2413 channel, YM2612 channel, Genesis DAC), each with a deliberate bug that must be caught, and on real games from the user's library when they are there (ImodelGames: never a pass without the ROM).
using BrokenNes2.Extract;
using BrokenNes2.Instruments;
using NesEmulator.Sega;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    /// <summary>Real games for the round trip: (path, console kind, sound core, channel, frame, Start at frame). Frames chosen where the channel plays a note of its music.</summary>
    internal static readonly (string Path, ConsoleKind Kind, string Apu, int Channel, int Frame, int Start, string Label)[] ExtractGames =
    [
        (ImodelGames.Smw, ConsoleKind.Snes, "", 0, 400, -1, "Super Mario World, voice 1"),
        (ImodelGames.Smw, ConsoleKind.Snes, "", 1, 400, -1, "Super Mario World, voice 2"),
        (ImodelGames.Sonic1, ConsoleKind.Genesis, "MD", 4, 700, -1, "Sonic the Hedgehog, FM 1"),
        (ImodelGames.Sonic1, ConsoleKind.Genesis, "MD", 5, 700, -1, "Sonic the Hedgehog, FM 2"),
        (ImodelGames.Sonic1, ConsoleKind.Genesis, "MD", 3, 700, -1, "Sonic the Hedgehog, PSG noise"),
        (ImodelGames.Sonic1, ConsoleKind.Genesis, "MD", 10, 700, -1, "Sonic the Hedgehog, DAC"),
        (AlexKidd, ConsoleKind.MasterSystem, "SMS", 0, 400, -1, "Alex Kidd in Miracle World, PSG tone 1"),
        (AlexKidd, ConsoleKind.MasterSystem, "SMS", 2, 400, -1, "Alex Kidd in Miracle World, PSG tone 3"),
        (AlexKidd, ConsoleKind.MasterSystem, "SMS", 3, 400, -1, "Alex Kidd in Miracle World, PSG noise"),
    ];

    private const string AlexKidd = @"X:\EMULATION\Sega Master System\Alex Kidd in Miracle World.7z";

    /// <summary>Part of the `extract` check: the new consoles.</summary>
    private void ExtractMoreTest(T t)
    {
        // the pitch measure of these consoles (the shift of the log spectra) sees a note played 50 cents sharp
        {
            var tone = Enumerable.Range(0, 8000).Select(i => (float)(Math.Sign(Math.Sin(2 * Math.PI * 440 * i / 44100.0)) * 0.3)).ToArray();
            var sharp = Enumerable.Range(0, 8000).Select(i => (float)(Math.Sign(Math.Sin(2 * Math.PI * 440 * Math.Pow(2, 50 / 1200.0) * i / 44100.0)) * 0.3)).ToArray();
            double shift = SpectralShiftCents(tone, sharp, 44100);
            t.Expect(Math.Abs(shift - 50) < 6, $"the spectral pitch measure reads a note 50 cents sharp as {shift:0.0} cents");
        }

        // ---- SNES: an S-DSP voice (a looped BRR square, ADSR, vibrato, the driver's volume change) ----
        var (sf, spcm, sat) = SyntheticSnes();
        var sn = sf.Recorder.Pick(0, 10, out string? why);
        t.Expect(sn != null && sn.KeyOnFrame == 3 && sn.End == CaptureEnd.Silenced && sn.Frames >= 26 && Math.Abs(sn.CentsAt(9)) > 5 && sn.Life[20].Duty < sn.Life[0].Duty,
            $"SNES voice 1: {sn?.Describe() ?? why}, cents at 9 {sn?.CentsAt(9):0.0}, volume {sn?.Life[0].Duty}->{(sn?.Frames > 20 ? sn.Life[20].Duty : -1)}");
        if (sn != null) RoundTripWithBug(t, sn, spcm, sat, "synthetic SNES voice 1", n => Flatten(n));

        // ---- Master System PSG tone (decay, vibrato) ----
        var (pf, ppcm, pat) = SyntheticPsg();
        var pn = pf.Recorder.Pick(0, 10, out why);
        t.Expect(pn != null && pn.KeyOnFrame == 4 && pn.End == CaptureEnd.Silenced && pn.Frames == 26, $"PSG tone 1: {pn?.Describe() ?? why}");
        if (pn != null) RoundTripWithBug(t, pn, ppcm, pat, "synthetic PSG tone 1", n => Flatten(n));

        // ---- Master System FM unit: a YM2413 channel (ROM instrument, volume steps, vibrato) ----
        var (of, opcm, oat) = SyntheticOpll();
        var on = of.Recorder.Pick(ExtractChips.Fm1, 10, out why);
        t.Expect(on != null && on.KeyOnFrame == 4 && on.End == CaptureEnd.Silenced && on.Frames == 26, $"YM2413 FM 1: {on?.Describe() ?? why}");
        if (on != null) RoundTripWithBug(t, on, opcm, oat, "synthetic YM2413 FM 1", n => Flatten(n));

        // ---- Genesis: a YM2612 channel (algorithm 4, a carrier-level step, vibrato) and the DAC ----
        var (gf, gpcm, gat) = SyntheticGenesis(dac: false);
        var gn = gf.Recorder.Pick(ExtractChips.Fm1, 10, out why);
        t.Expect(gn != null && gn.KeyOnFrame == 4 && gn.End == CaptureEnd.Silenced && gn.Frames == 26, $"YM2612 FM 1: {gn?.Describe() ?? why}");
        if (gn != null) RoundTripWithBug(t, gn, gpcm, gat, "synthetic YM2612 FM 1", n => Flatten(n));

        var (df, dpcm, dat) = SyntheticGenesis(dac: true);
        var dn = df.Recorder.Pick(ExtractChips.GenDac, 45, out why);
        t.Expect(dn != null && dn.KeyOnFrame == 40 && dn.End == CaptureEnd.Silenced && dn.Frames == 20 && ExtractChips.SegaOf(dn.Regs) is { Dac.Length: 1 } ds && Math.Abs(ds.Dac[0].Rate - 8000) < 200,
            $"Genesis DAC: {dn?.Describe() ?? why}, rate {(dn != null && ExtractChips.SegaOf(dn.Regs) is { Dac.Length: 1 } d2 ? d2.Dac[0].Rate : -1)}");
        if (dn != null) RoundTripWithBug(t, dn, dpcm, dat, "synthetic Genesis DAC", n => DacRateDoubled(n), spectral: true);

        // ---- older plugins: a wide note is version 5, which a version-4 reader keeps and does not play (the plain instrument plays) ----
        if (sn != null && CapturePreset.Make(sn, "x", "", "", 0, out _) is { } d)
        {
            t.Expect(d.NoteVersion == BrokenNes2.Presets.PresetNote.CapturedWide, $"a SNES capture is note version {d.NoteVersion}, not 5");
            var cut = (byte[])d.NoteBehaviour.Clone(); cut[0] = CapturedNote.Format;
            t.Expect(CapturedNote.FromBytes(cut) == null, "a wide note read as format 1 was accepted");
        }

        // ---- real games ----
        foreach (var g in ExtractGames)
        {
            if (!File.Exists(g.Path)) { t.Note($"extract round trip ({g.Label}): not tested, the ROM is not there"); continue; }
            var r = RecordGame(g.Path, g.Kind, g.Apu, g.Channel, g.Frame + CapturedNote.MaxFrames / 2, g.Start, out string info);
            if (r == null) { t.Warn(false, $"{g.Label}: {info}"); continue; }
            var gnote = r.Value.Feed.Recorder.Pick(g.Channel, g.Frame, out why);
            if (gnote == null) { t.Warn(false, $"{g.Label}: {why}"); continue; }
            var sc = RoundTrip(t, gnote, r.Value.Pcm, r.Value.FrameAt, g.Label);
            if (sc == null) continue;
            var bad = RoundTrip(t, Flatten(gnote), r.Value.Pcm, r.Value.FrameAt, g.Label + " (flattened)", gnote);
            t.Note($"extract round trip ({g.Label} @ {gnote.KeyOnFrame}): {sc}; flattened: envelope {bad?.EnvelopeError * 100:0.0}%");
            t.Warn(Math.Abs(sc.PitchCents) < 25 || double.IsNaN(sc.PitchCents), $"{g.Label}: pitch {sc}");
        }
    }

    private void RoundTripWithBug(T t, CapturedNote n, float[] pcm, int[] at, string what, Func<CapturedNote, CapturedNote> bug, bool spectral = false)
    {
        var score = RoundTrip(t, n, pcm, at, what);
        if (score == null) return;
        t.Note($"extract round trip ({what}): {score}");
        t.Expect((n.IsNoise || Math.Abs(score.PitchCents) < 10) && score.EnvelopeError < 0.15 && score.Spectral > 0.85, $"{what}: {score}");
        var bad = RoundTrip(t, bug(n), pcm, at, what + " (deliberate bug)", n);
        bool caught = bad != null && (spectral ? bad.Spectral < score.Spectral - 0.1 : bad.EnvelopeError > score.EnvelopeError * 1.5 + 0.03);
        t.Expect(caught, $"{what}: the deliberate bug was not caught: {bad} vs {score}");
        if (bad != null) t.Note($"  deliberate bug ({what}): {bad}");
    }

    /// <summary>The deliberate bug of the pitched channels: the captured life flattened to the key-on frame (level, period, voice volume).</summary>
    private static CapturedNote Flatten(CapturedNote n)
    {
        var f = CapturedNote.FromBytes(n.ToBytes())!;
        for (int i = 0; i < f.Life.Length; i++) { f.Life[i].Level = f.Life[0].Level; f.Life[i].Period = f.Life[0].Period; f.Life[i].Duty = f.Life[0].Duty; }
        return f;
    }

    /// <summary>The deliberate bug of the DAC: the sample's rate written twice as high.</summary>
    private static CapturedNote DacRateDoubled(CapturedNote n)
    {
        var f = CapturedNote.FromBytes(n.ToBytes())!;
        var s = ExtractChips.SegaOf(f.Regs)!;
        f.Regs = ExtractChips.SegaBlock(s, new BrokenNes2.DacSample(s.Dac[0].Rate * 2, s.Dac[0].Data, -1, -1, 1));
        return f;
    }

    // ---- the synthetic drivers ----

    /// <summary>The SNES: voice 1 plays a looped one-block BRR square (16 samples) from frame 3 (pitch 901, about 440 Hz; ADSR attack 15, decay 7, sustain 4, rate 10; volume 110), vibrato from frame 8,
    /// the volume drops to 30 at frame 15 (the driver's), key-off at 30. Returns the feed and the voice's sound at 44.1 kHz.</summary>
    private static (SnesExtractFeed Feed, float[] Pcm, int[] FrameAt) SyntheticSnes()
    {
        var apu = new APU_SFC();
        var raw = new List<float>();
        apu.Dsp.Output = (l, r) => raw.Add((l + r) / 65536f);
        var feed = new SnesExtractFeed(apu);
        void W(int reg, int v) { apu.Dsp.Write(reg, (byte)v); apu.DspWriteTap?.Invoke((byte)reg, (byte)v, 0); }
        apu.Aram[0x200] = 0x00; apu.Aram[0x201] = 0x03; apu.Aram[0x202] = 0x00; apu.Aram[0x203] = 0x03;
        apu.Aram[0x300] = 0xB3;
        for (int i = 0; i < 4; i++) { apu.Aram[0x301 + i] = 0x77; apu.Aram[0x305 + i] = 0x88; }
        W(0x6C, 0x20); W(0x0C, 127); W(0x1C, 127); W(0x2C, 0); W(0x3C, 0); W(0x5D, 0x02); W(0x4D, 0); W(0x3D, 0); W(0x2D, 0);
        var at = new int[71];
        double carry = 0;
        for (int f = 0; f < 70; f++)
        {
            at[f] = raw.Count;
            if (f == 3) { W(0x00, 110); W(0x01, 110); W(0x02, 901 & 0xFF); W(0x03, 901 >> 8); W(0x04, 0); W(0x05, 0x80 | 7 << 4 | 15); W(0x06, 4 << 5 | 10); W(0x07, 0); W(0x5C, 0); W(0x4C, 1); }
            if (f >= 8 && f < 30) { int p = 901 + (f % 4 < 2 ? 12 : -12); W(0x02, p & 0xFF); W(0x03, p >> 8); }
            if (f == 15) { W(0x00, 30); W(0x01, 30); }
            if (f == 30) W(0x5C, 1);
            double n = 32000 / 60.0988 + carry; int whole = (int)n; carry = n - whole;
            for (int i = 0; i < whole; i++) apu.Dsp.RunSample();
            feed.EndFrame();
        }
        at[70] = raw.Count;
        feed.Detach();
        return (feed, Resample(raw.ToArray(), 32000, 44100), at.Select(x => (int)((long)x * 44100 / 32000)).ToArray());
    }

    private const double SegaFrameHz = 59.9227, Z80Clock = 3579545;

    /// <summary>The Master System PSG: tone 1 from frame 4 (period 254, about 440 Hz) at full volume, one 2 dB step quieter every 3 frames, vibrato from 8, silent at 30.</summary>
    private static (SmsExtractFeed Feed, float[] Pcm, int[] FrameAt) SyntheticPsg()
    {
        var psg = new Sn76489(Z80Clock, 44100, PsgVariant.Sega);
        foreach (int c in new[] { 0x9F, 0xBF, 0xDF, 0xFF }) psg.Write((byte)c);
        var feed = new SmsExtractFeed(psg, null, Z80Clock);
        void Period(int p) { psg.Write((byte)(0x80 | p & 15)); psg.Write((byte)(p >> 4 & 0x3F)); }
        return RunSega(feed, f =>
        {
            if (f == 4) { Period(254); psg.Write(0x90); }
            if (f > 4 && f < 30) psg.Write((byte)(0x90 | Math.Min(14, (f - 4) / 3)));
            if (f >= 8 && f < 30) Period(254 + (f % 4 < 2 ? 3 : -3));
            if (f == 30) psg.Write(0x9F);
        }, cycles => psg.Run(cycles), buf => psg.ReadSamples(buf));
    }

    /// <summary>The Master System FM unit: YM2413 channel 1 plays ROM instrument 3 from frame 4 (block 4, F-number 290), volume 0, 4 at 12, 8 at 18, vibrato from 8, key-off at 30.</summary>
    private static (SmsExtractFeed Feed, float[] Pcm, int[] FrameAt) SyntheticOpll()
    {
        var unit = new SmsFmSound(Z80Clock, 44100);
        unit.WriteAudioControl(1);
        foreach (int c in new[] { 0x9F, 0xBF, 0xDF, 0xFF }) unit.WritePsg((byte)c);
        var feed = SmsExtractFeed.Of(unit, Z80Clock)!;
        void R(int reg, int v) { unit.WriteFmAddress((byte)reg); unit.WriteFmData((byte)v); }
        void Pitch(int fnum, bool key) { R(0x10, fnum & 0xFF); R(0x20, (key ? 0x10 : 0) | 4 << 1 | fnum >> 8); }
        return RunSega(feed, f =>
        {
            if (f == 4) { R(0x30, 3 << 4 | 0); Pitch(290, true); }
            if (f == 12) R(0x30, 3 << 4 | 4);
            if (f == 18) R(0x30, 3 << 4 | 8);
            if (f >= 8 && f < 30) Pitch(290 + (f % 4 < 2 ? 2 : -2), true);
            if (f == 30) Pitch(290, false);
        }, cycles => unit.Run(cycles), buf => unit.ReadSamples(buf));
    }

    /// <summary>The Genesis: YM2612 channel 1 (algorithm 4: operators 2 and 4 carry, multiple 1) keyed at frame 4 (block 4, F-number 1083, about 440 Hz), its carriers 12 steps quieter at 14, vibrato from 8,
    /// key-off at 30. Or (<paramref name="dac"/>) the DAC: a decaying 8 kHz saw from frame 40 to 60.</summary>
    private static (GenesisExtractFeed Feed, float[] Pcm, int[] FrameAt) SyntheticGenesis(bool dac)
    {
        var timeline = SegaTimeline.Genesis(SegaRegion.Ntsc);
        var machine = new SegaMachine(timeline, GenesisModel.Model1, 44100);
        var u = new MdSoundUnit(machine);
        u.Reset();
        long master = 0;
        void Ym(int part, int reg, int v) { u.WriteYm(part * 2, (byte)reg, master); u.WriteYm(part * 2 + 1, (byte)v, master); }
        foreach (int c in new[] { 0x9F, 0xBF, 0xDF, 0xFF }) u.WritePsg((byte)c, master);
        var feed = new GenesisExtractFeed(u, timeline.MasterHz);
        Ym(0, 0x27, 0); Ym(0, 0x2B, 0); Ym(0, 0x22, 0);
        Ym(0, 0xB0, 0x04); Ym(0, 0xB4, 0xC0);
        int[] tl = [0x20, 0x20, 0x10, 0x10];        // register order: operator 1, 3, 2, 4 (offsets 0, 4, 8, 12)
        int[] offs = [0, 4, 8, 12];
        for (int k = 0; k < 4; k++)
        {
            int o = offs[k];
            Ym(0, 0x30 + o, 0x01); Ym(0, 0x40 + o, tl[k]); Ym(0, 0x50 + o, 0x1F); Ym(0, 0x60 + o, 0x05); Ym(0, 0x70 + o, 0); Ym(0, 0x80 + o, 0x2F); Ym(0, 0x90 + o, 0);
        }
        void Pitch(int fnum) { Ym(0, 0xA4, 4 << 3 | fnum >> 8); Ym(0, 0xA0, fnum & 0xFF); }
        var raw = new List<float>(); var buf = new short[16384];
        var at = new int[81];
        double perFrame = timeline.MasterHz / SegaFrameHz, clock = 0;
        int saw = 0;
        for (int f = 0; f < 80; f++)
        {
            at[f] = raw.Count;
            if (!dac)
            {
                if (f == 4) { Pitch(1083); Ym(0, 0x28, 0xF0); }
                if (f == 14) { Ym(0, 0x48, 0x10 + 12); Ym(0, 0x4C, 0x10 + 12); }
                if (f >= 8 && f < 30) Pitch(1083 + (f % 4 < 2 ? 4 : -4));
                if (f == 30) Ym(0, 0x28, 0x00);
            }
            else if (f == 40) Ym(0, 0x2B, 0x80);
            else if (f == 60) Ym(0, 0x2B, 0x00);
            double end = clock + perFrame;
            if (dac && f >= 40 && f < 60)
            {
                double step = timeline.MasterHz / 8000.0;
                for (double c = clock; c < end; c += step)
                {
                    master = (long)c; u.RunTo(master);
                    int amp = Math.Max(8, 100 - (f - 40) * 4);
                    saw = (saw + 1) % 40;
                    Ym(0, 0x2A, 128 - amp + saw * 2 * amp / 40);
                }
            }
            clock = end; master = (long)clock; u.RunTo(master);
            int n; while ((n = u.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) raw.Add((buf[i] + buf[i + 1]) / 65536f);
            feed.EndFrame();
        }
        at[80] = raw.Count;
        feed.Detach();
        int rate = u.SampleRate;
        return (feed, rate == 44100 ? raw.ToArray() : Resample(raw.ToArray(), rate, 44100), at.Select(x => (int)((long)x * 44100 / rate)).ToArray());
    }

    private static (SmsExtractFeed, float[], int[]) RunSega(SmsExtractFeed feed, Action<int> driver, Action<int> run, Func<short[], int> read)
    {
        var raw = new List<float>(); var buf = new short[16384]; var at = new int[61];
        double carry = 0;
        for (int f = 0; f < 60; f++)
        {
            at[f] = raw.Count;
            driver(f);
            double c = Z80Clock / SegaFrameHz + carry; int whole = (int)c; carry = c - whole;
            run(whole);
            int n; while ((n = read(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) raw.Add((buf[i] + buf[i + 1]) / 65536f);
            feed.EndFrame();
        }
        at[60] = raw.Count;
        feed.Detach();
        return (feed, raw.ToArray(), at);
    }

    // ---- real games ----

    /// <summary>Runs a SNES / Master System / Game Gear / Genesis game headless for <paramref name="frames"/> frames with a feed on its sound unit, and records the channel alone at 44.1 kHz
    /// (SNES: the other S-DSP voices muted; Sega: the channel's stem).</summary>
    internal static (IExtractFeed Feed, float[] Pcm, int[] FrameAt)? RecordGame(string path, ConsoleKind kind, string apu, int channel, int frames, int start, out string info)
    {
        info = "";
        var rom = ImodelGames.LoadRom(path, out string name);
        if (rom == null) { info = "ROM not found"; return null; }
        if (path.EndsWith(".gg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gg", StringComparison.OrdinalIgnoreCase)) kind = ConsoleKind.GameGear;
        var s = ConsoleSessions.Create(kind, rom, CoreCatalog.Default(kind, CoreSlot.Cpu), CoreCatalog.Default(kind, CoreSlot.Ppu), apu.Length > 0 ? apu : CoreCatalog.Default(kind, CoreSlot.Apu), null, name);
        var inner = s is NesEmulator.Mix.SegaPictureSession w ? w.Inner : s;
        IExtractFeed? feed = null;
        IVoiceStems? stems = null;
        APU_SFC? spc = null;
        int stemRate = 0;
        switch (inner)
        {
            case SnesSession { Apu: APU_SFC a }: spc = a; a.Dsp.DebugMute = (byte)~(1 << channel); feed = new SnesExtractFeed(a); break;
            case SmsSession sms:
            {
                var t = sms.Board.Timeline;
                feed = SmsExtractFeed.Of(sms.Board.Sound, t.MasterHz / t.Z80.Divider);
                stems = sms.Board.Sound as IVoiceStems; stemRate = sms.Board.Sound.SampleRate;
                break;
            }
            case MdSession { Board.Sound: MdSoundUnit u } md: feed = new GenesisExtractFeed(u, md.Board.Timeline.MasterHz); stems = u; stemRate = u.SampleRate; break;
        }
        if (feed == null) { info = "no feed for this sound unit"; return null; }
        if (spc == null && stems == null) { info = "the sound unit has no stems"; return null; }
        if (stems != null) stems.StemMask = 1L << channel;
        var pcm = new List<float>(); var at = new int[frames + 1]; var buf = new short[1 << 16]; var fb = new float[1 << 16];
        int rate = spc != null ? s.SampleRate : stemRate;
        for (int f = 0; f < frames; f++)
        {
            at[f] = pcm.Count;
            s.SetPad(0, start >= 0 && f >= start && f < start + 4 ? PadButtons.Start : PadButtons.None);
            s.RunFrame();
            int n;
            if (spc != null) { while ((n = s.ReadSamples(buf)) > 0) for (int i = 0; i + 1 < n; i += 2) pcm.Add((buf[i] + buf[i + 1]) / 65536f); }
            else { while (s.ReadSamples(buf) > 0) { } while ((n = stems!.ReadStem(channel, fb)) > 0) for (int i = 0; i < n; i++) pcm.Add(fb[i]); }
            feed.EndFrame();
        }
        at[frames] = pcm.Count;
        feed.Detach();
        if (spc != null) spc.Dsp.DebugMute = 0;
        if (stems != null) stems.StemMask = 0;
        // the sound unit's output runs behind the frame it was asked for (a buffered unit): the lag, in whole frames (0-4), is where the channel's per-frame loudness best follows the captured sounding frames
        var x = pcm.ToArray();
        var sounding = feed.Recorder.FramesOf(channel).Select(c => c.Level > 0 ? 1.0 : 0.0).ToArray();
        int lag = 0; double best = double.MinValue;
        for (int l = 0; l <= 4; l++)
        {
            double sum = 0;
            for (int f = 0; f + l < frames; f++) { double e = Audio.Rms(x, at[f + l], Math.Min(at[f + l + 1], x.Length)); sum += e * (sounding[f] * 2 - 1); }
            if (sum > best) { best = sum; lag = l; }
        }
        var shifted = new int[frames + 1];
        for (int f = 0; f <= frames; f++) shifted[f] = at[Math.Min(frames, f + lag)];
        info = $"{name}: {frames} frames at {rate} Hz, the channel's sound {lag} frame{(lag == 1 ? "" : "s")} behind";
        return (feed, rate == 44100 ? x : Resample(x, rate, 44100), shifted.Select(v => (int)((long)v * 44100 / rate)).ToArray());
    }

    /// <summary>`extract-roundtrip` for a SNES / Sega game (the NES path is in SelfTest.Extract.cs): prints the channels' key-ons and the round trip of the note at --frame.</summary>
    public static int ExtractRoundTripMoreCli(string[] args, Func<string, string?> O)
    {
        string path = args[2];
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var kind = ext is ".sfc" or ".smc" ? ConsoleKind.Snes : ext is ".sms" ? ConsoleKind.MasterSystem : ext is ".gg" ? ConsoleKind.GameGear : ext is ".bin" or ".md" or ".gen" ? ConsoleKind.Genesis
            : Enum.Parse<ConsoleKind>(O("--console") ?? "Genesis", true);
        int frame = int.Parse(O("--frame") ?? "300"), channel = int.Parse(O("--channel") ?? "0"), start = int.Parse(O("--start") ?? "-1");
        int total = int.Parse(O("--frames") ?? (frame + CapturedNote.MaxFrames / 2).ToString());
        var r = RecordGame(path, kind, O("--apu") ?? "", channel, total, start, out string info);
        if (r == null) { Console.Error.WriteLine(info); return 1; }
        var rec = r.Value.Feed.Recorder;
        Console.WriteLine(info + "; per channel key-ons / sounding frames: " + string.Join(", ", Enumerable.Range(0, rec.Channels).Select(c => $"{CapturedNote.ChannelName(rec.Console, c)} {rec.FramesOf(c).Count(x => x.KeyOn)}/{rec.FramesOf(c).Count(x => x.Level > 0)}")));
        var n = rec.Pick(channel, frame, out string? why);
        if (n == null) { Console.Error.WriteLine(why); return 1; }
        Console.WriteLine("captured: " + n.Describe() + $", key {n.MidiKey:0.00}; levels {string.Join(",", n.Life.Take(24).Select(x => x.Level))}{(n.Frames > 24 ? ",..." : "")}");
        {
            int a0 = r.Value.FrameAt[n.KeyOnFrame], a1 = r.Value.FrameAt[Math.Min(r.Value.FrameAt.Length - 1, n.KeyOnFrame + n.Frames)];
            var seg = r.Value.Pcm[a0..a1];
            var tr = Audio.Track(seg, 44100, 0, seg.Length * 1000.0 / 44100 - 40).Where(x => x.Hz > 0).Select(x => x.Hz).OrderBy(x => x).ToList();
            var fa = r.Value.FrameAt;
            Console.WriteLine("game rms by frame (key-on -3 .. +10): " + string.Join(" ", Enumerable.Range(n.KeyOnFrame - 3, 14).Where(f => f >= 0 && f + 1 < fa.Length).Select(f => $"{(f == n.KeyOnFrame ? "*" : "")}{Audio.Rms(r.Value.Pcm, fa[f], Math.Min(fa[f + 1], r.Value.Pcm.Length)):0.000}")));
            Console.WriteLine($"the game's channel: {seg.Length} samples, rms {Audio.Rms(seg, 0, seg.Length):0.0000}, pitch track median {(tr.Count > 0 ? tr[tr.Count / 2] : double.NaN):0.0} Hz; the registers say {n.Hz(n.Life[0].Period) * ExtractChips.SoundingMultiple(n):0.0} Hz (x{ExtractChips.SoundingMultiple(n)})");
        }
        var suite = new SelfTest(args[1]);
        var t = new T();
        var score = suite.RoundTrip(t, n, r.Value.Pcm, r.Value.FrameAt, Path.GetFileNameWithoutExtension(path) + " ch" + channel, savePreset: O("--out"));
        foreach (var f in t.Failures) Console.WriteLine("FAIL: " + f);
        if (score == null) return 1;
        Console.WriteLine("round trip: " + score);
        var bad = suite.RoundTrip(new T(), Flatten(n), r.Value.Pcm, r.Value.FrameAt, "flattened", n);
        Console.WriteLine("deliberate bug (flattened): " + bad);
        return t.Failures.Count == 0 ? 0 : 1;
    }
}

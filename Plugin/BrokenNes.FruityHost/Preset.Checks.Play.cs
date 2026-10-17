// Round 6 (presets): the engines. A preset is played on a private engine built from the preset alone (PresetPlayable: the snapshot of its console around the one instrument, handed to the very sampler classes Instrument Runaway uses). These checks prove
// the target the brief set: a preset played in direct mode renders the same PCM as Runaway playing that instrument from the game it was ripped from, same note, same velocity. Bit-exact where the engines are the same, and the measured error where not.
using System.Runtime.InteropServices;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes.FruityHost;

internal static partial class PresetChecks
{
    /// <summary>Test-only (the mutant run): the reference side is played at another velocity, which the comparison must notice.</summary>
    internal static bool ReferenceVelocityOff;
    internal static bool EngineAllocates;

    public sealed record TestMutant(string Name, string CatchingCheck, Action<ImodelReport> Run);

    public static readonly TestMutant[] TestMutants =
    [
        new("the Runaway side is played at another velocity", "play-equals-runaway", r => { ReferenceVelocityOff = true; try { PlayEqualsRunaway(r); } finally { ReferenceVelocityOff = false; } }),
        new("an engine allocates a little on every block", "play-alloc", r => { EngineAllocates = true; try { PlayAlloc(r); } finally { EngineAllocates = false; } }),
    ];

    // =====================================================================================================================================================================
    // the notes both sides play
    // =====================================================================================================================================================================

    /// <summary>The notes Runaway and the preset engine both play on a SNES sampler: two voices, a pitch glide, a release. Level 0-127 as the plugin sets it from FL's velocity.</summary>
    internal static float[] SnesNotes(SpcSnapshot snap, int slot, bool runaway = false)
    {
        var s = new SnesSampler(snap, 48000);
        var all = new List<float>();
        var buf = new float[512];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        int hi = runaway && ReferenceVelocityOff ? 101 : 100;
        s.NoteOn(0, slot, SnesSampler.PitchFor(0), hi, hi); Run(8);
        s.NoteOn(1, slot, SnesSampler.PitchFor(700), 80, 80); Run(8);
        s.Update(0, SnesSampler.PitchFor(250), 60, 60); Run(8);
        s.NoteOff(1); Run(6);
        s.NoteOff(0); Run(20);
        return all.ToArray();
    }

    /// <summary>Notes on a Master System / Genesis sampler: the instrument's own slots, two pitches, a glide, a release.</summary>
    internal static float[] SegaNotes(SegaSnapshot snap, int index, bool runaway = false)
    {
        SegaSampler s = snap.Kind switch { SegaSnapshot.KindOpn => new OpnSampler(snap, 48000), SegaSnapshot.KindOpll => new OpllSampler(snap, 48000), _ => new PsgSampler(snap, 48000) };
        var all = new List<float>();
        var buf = new float[512];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        int first = snap.SlotBase(index), count = snap.Slots(index);
        double v1 = runaway && ReferenceVelocityOff ? 0.85 : 0.9;
        s.NoteOn(first, index, 220.0, v1); Run(12);
        if (count > 1) { s.NoteOn(first + 1, index, 330.0, 0.7); Run(12); }
        s.Update(first, 247.0, 0.8); Run(10);
        s.NoteOff(first); if (count > 1) s.NoteOff(first + 1);
        Run(14);
        return all.ToArray();
    }

    internal static float[] GbNotes(GbSnapshot snap, int channel, bool noiseShort, bool runaway = false)
    {
        var s = new GbSampler(snap, 48000);
        var all = new List<float>();
        var buf = new float[512];
        void Run(int blocks) { for (int i = 0; i < blocks; i++) { s.Render(buf, 256); all.AddRange(buf); } }
        double hz = new[] { 440.0, 330.0, 220.0, 500.0 }[channel];
        s.NoteOn(channel, hz, runaway && ReferenceVelocityOff ? 11 : 12, -1, noiseShort); Run(24);
        s.Pitch(channel, hz * 1.12, noiseShort); Run(12);
        s.NoteOff(channel); Run(8);
        return all.ToArray();
    }

    private static bool Same(float[] a, float[] b) => a.Length == b.Length && MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    private static (double Rms, double Max) Error(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double s = 0, m = 0;
        for (int i = 0; i < n; i++) { double d = a[i] - b[i]; s += d * d; m = Math.Max(m, Math.Abs(d)); }
        return (n == 0 ? 0 : Math.Sqrt(s / n), m);
    }

    /// <summary>What a game's sound memory holds that a preset starts without, and that Runaway's copy still carries: the old contents of the echo buffer (the tail of the song that was playing), and the pitch registers of the game's eight voices (an idle voice with a pitch keeps
    /// decoding whatever its address points at, which leaves the filter history of its next note's first samples in a state that depends on the game's memory). Cleared, the preset and Runaway are the same engine and render the same PCM.</summary>
    internal static SpcSnapshot WithoutLeftovers(SpcSnapshot s, bool echo = true, bool voices = true)
    {
        var aram = (byte[])s.Aram.Clone();
        int esa = s.Dsp[0x6D] * 0x100, edl = s.Dsp[0x7D] & 15, size = edl == 0 ? 4 : edl * 0x800;
        if (echo) for (int i = 0; i < size; i++) aram[(esa + i) & 0xFFFF] = 0;
        var dsp = (byte[])s.Dsp.Clone();
        if (voices) for (int v = 0; v < 8; v++) { dsp[v * 16 + 2] = 0; dsp[v * 16 + 3] = 0; }            // (their envelopes stay: they are where the sampler finds the game's own envelope for a sample)
        return new SpcSnapshot(aram, dsp, s.Overlay);
    }

    // =====================================================================================================================================================================
    // builds
    // =====================================================================================================================================================================

    public static void PlayBuilds(ImodelReport r)
    {
        int built = 0, cannot = 0;
        foreach (var src in Sources(r))
            foreach (var inst in PresetFixtures.Instruments(src))
                foreach (int steps in new[] { 0, 3 })
                {
                    if (steps > 0 && !inst.Editable) continue;
                    string what = $"{src.Name} {inst.Kind} #{inst.Index} after {steps} steps";
                    var d = PresetFixtures.Make(src, inst, steps, (ulong)inst.Index + 5);
                    var p = PresetPlayable.Build(d, out string? why);
                    r.Expect(p != null, $"{what}: could not be set up to play: {why}");
                    if (p == null) continue;
                    if (inst.Kind == InstrumentKind.NesDmc)
                    {
                        r.Expect(p.CannotPlay != null && p.CannotPlay.StartsWith("cannot play on this console yet:"), $"{what}: a NES DMC preset must say it cannot play yet, said '{p.CannotPlay}'");
                        cannot++;
                    }
                    else
                    {
                        r.Expect(p.CannotPlay == null, $"{what}: said '{p.CannotPlay}'");
                        r.Expect(inst.Console switch { ConsoleId.Snes => p.Snes != null, ConsoleId.GameBoy => p.Gb != null, ConsoleId.Nes => true, _ => p.Sega != null }, $"{what}: no snapshot for its console");
                        built++;
                    }
                }
        // a context that does not fit the console, and a sample that does not fit audio RAM, say so
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var sd = PresetFixtures.Make(snes, PresetFixtures.Instruments(snes)[1], 0, 1);
        var badCtx = sd.Copy(); badCtx.Context = new byte[10];
        r.Expect(PresetPlayable.Build(badCtx, out string? w1) == null && w1 != null, "a SNES context of 10 bytes was accepted");
        var inst0 = InstrumentCodec.Unpack(sd.Instrument, out _)!;
        var high = inst0.Clone(); high.Origin = 0xFFF8;
        var highData = sd.Copy(); highData.Instrument = InstrumentCodec.Pack(high);
        r.Expect(PresetPlayable.Build(highData, out string? w2) == null && w2 != null && w2.Contains("audio RAM"), $"a sample that runs past the end of audio RAM was accepted ('{w2}')");
        r.Note($"{built} presets set up to play, {cannot} NES DMC presets say they cannot play yet");
    }

    // =====================================================================================================================================================================
    // the same sound as Runaway
    // =====================================================================================================================================================================

    public static void PlayEqualsRunaway(ImodelReport r)
    {
        int compared = 0, exact = 0; long samples = 0;
        var echoErrors = new List<(string Name, double Rms, double Peak, double Max)>();
        var onlyEcho = new List<double>(); var onlyVoices = new List<double>();
        var perConsole = new Dictionary<string, (int N, int Exact)>();
        void Count(string console, bool ok) { var (n, e) = perConsole.GetValueOrDefault(console); perConsole[console] = (n + 1, e + (ok ? 1 : 0)); }

        foreach (var src in Sources(r))
            foreach (var inst in PresetFixtures.Instruments(src))
                foreach (int steps in new[] { 0, 3 })
                {
                    if (steps > 0 && !inst.Editable) continue;
                    if (inst.Console == ConsoleId.Nes) continue;                    // no Runaway sampler exists for the NES: nothing to be equal to
                    string what = $"{src.Name} {inst.Kind} #{inst.Index} after {steps} steps";
                    var h = PresetFixtures.Corrupt(inst, steps, (ulong)inst.Index + 5);
                    var d = PresetFixtures.Make(src, inst, steps, (ulong)inst.Index + 5);
                    var play = PresetPlayable.Build(d, out string? why);
                    if (play == null) { r.Expect(false, $"{what}: {why}"); continue; }
                    // Runaway's side: the game's own sound memory with the instrument written into it (what the Instruments tab does), played by the plugin's sampler
                    // (a ROM instrument of the YM2413 is the chip's own: nothing is written, the game's snapshot plays it as it is)
                    var written = inst.Editable ? ImodelChecks.Src(src.Snapshot).Write(h.Current) : WriteResult.Success(src.Snapshot, []);
                    if (!written.Ok) { r.Expect(false, $"{what}: the model refused its own instrument: {written.Reason}"); continue; }
                    float[] reference, mine;
                    string console = PresetFile.ConsoleName(inst.Console);
                    switch (inst.Console)
                    {
                        case ConsoleId.Snes:
                        {
                            var game = (SpcSnapshot)written.Snapshot!;
                            mine = SnesNotes(play.Snes!, play.InstrumentValue);
                            reference = SnesNotes(WithoutLeftovers(game), inst.Index, runaway: true);
                            // how far Runaway's copy as captured is from the preset, and which leftover does it: the old echo, the idle voices, or both
                            var raw = SnesNotes(game, inst.Index);
                            if (!Same(raw, reference))
                            {
                                var (rms, max) = Error(mine, raw);
                                echoErrors.Add((what, rms, ImodelData.Rms(raw), max));
                                double peak = Math.Max(1e-9, ImodelData.Rms(raw));
                                onlyEcho.Add(Error(mine, SnesNotes(WithoutLeftovers(game, echo: false, voices: true), inst.Index)).Rms / peak);
                                onlyVoices.Add(Error(mine, SnesNotes(WithoutLeftovers(game, echo: true, voices: false), inst.Index)).Rms / peak);
                            }
                            break;
                        }
                        case ConsoleId.GameBoy:
                            mine = GbNotes(play.Gb!, play.GbChannel, play.GbNoiseShort);
                            reference = GbNotes((GbSnapshot)written.Snapshot!, inst.Index, play.GbNoiseShort, runaway: true);            // (Runaway's Noise mode slider set to the preset's width)
                            break;
                        default:
                            mine = SegaNotes(play.Sega!, play.InstrumentValue - 1);
                            reference = SegaNotes((SegaSnapshot)written.Snapshot!, inst.Index, runaway: true);
                            break;
                    }
                    bool same = Same(mine, reference);
                    compared++; samples += mine.Length; if (same) exact++;
                    Count(console, same);
                    if (!same)
                    {
                        var (rms, max) = Error(mine, reference);
                        int firstDiff = 0; while (firstDiff < Math.Min(mine.Length, reference.Length) && BitConverter.SingleToInt32Bits(mine[firstDiff]) == BitConverter.SingleToInt32Bits(reference[firstDiff])) firstDiff++;
                        int diffCount = 0; for (int q = 0; q < Math.Min(mine.Length, reference.Length); q++) if (BitConverter.SingleToInt32Bits(mine[q]) != BitConverter.SingleToInt32Bits(reference[q])) diffCount++;
                        string extra = $" [first difference at frame {firstDiff / 2} ({firstDiff / 2 / 48.0:0.0} ms), {diffCount} of {mine.Length} values differ]";
                        extra += inst.Console == ConsoleId.Snes ? $" [overrun {play.Overrun} bytes, structure ok {InstrumentCorruptor.BrrWellFormed(h.Current.Blobs[0].Data)}, loop {h.Current.Get("loop.addr"):X4} origin {h.Current.Origin:X4} len {h.Current.Blobs[0].Data.Length}, noise {h.Current.Get("mix.noise")} pmod {h.Current.Get("mix.pmod")} echo {h.Current.Get("mix.echo")} tune {h.Current.Get("tune.cents")} vol {h.Current.Get("vol.l")}/{h.Current.Get("vol.r")}]" : "";
                        r.Expect(false, $"{what}: the preset renders other PCM than Runaway ({mine.Length} vs {reference.Length} values; difference rms {rms:0.000000}, largest {max:0.000000}){extra}");
                    }
                    else r.Assertions++;
                    // the preset is not silent when the instrument is not (a comparison of two silences proves nothing)
                    r.Assertions++;
                    if (ImodelData.Rms(mine) < 1e-6 && ImodelData.Rms(reference) >= 1e-6) r.Failures.Add($"{what}: the preset is silent but Runaway is not");
                }
        var loud = perConsole.Select(kv => $"{kv.Key} {kv.Value.Exact}/{kv.Value.N}");
        r.Note($"{exact} of {compared} instruments render bit-identical PCM to Runaway ({samples:N0} values): " + string.Join(", ", loud));
        if (echoErrors.Count > 0)
        {
            var worst = echoErrors.OrderByDescending(e => e.Rms / Math.Max(1e-9, e.Peak)).First();
            r.Note($"SNES against Runaway's copy as captured (it still holds the old echo of the song and the game's idle voices; a preset starts without them): {echoErrors.Count} instruments differ, error rms relative to the signal {echoErrors.Average(e => e.Rms / Math.Max(1e-9, e.Peak)):P1} on average, {worst.Rms / Math.Max(1e-9, worst.Peak):P1} at worst ({worst.Name}), largest sample difference {echoErrors.Max(e => e.Max):0.0000}");
            r.Note($"of that, with only the old echo left in Runaway's copy the error is {onlyEcho.Average():P1} on average (the song's echo tail leaking into the first notes), with only the idle voices left {onlyVoices.Average():P1} (the first few milliseconds of a note whose first BRR block uses a filter)");
        }
        r.Expect(compared > 40, $"only {compared} instruments were compared");
    }

    // =====================================================================================================================================================================
    // cost
    // =====================================================================================================================================================================

    public static void PlayAlloc(ImodelReport r)
    {
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var buf = new float[512];
        void Check(string what, Func<(Action<int> Block, Action Note)> make)
        {
            var (block, note) = make();
            note(); for (int i = 0; i < 4; i++) block(i);               // warm up: the first block may build a table
            long before = GC.GetAllocatedBytesForCurrentThread();
            object? keep = null;
            for (int i = 0; i < 100; i++) { block(i); if (EngineAllocates) keep = new byte[24]; }
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(keep);
            r.Expect(used == 0, $"{what}: the engine allocated {used} bytes in 100 blocks");
        }
        var sp = PresetPlayable.Build(PresetFixtures.Make(snes, PresetFixtures.Instruments(snes)[1], 2, 3), out _)!;
        Check("SNES preset", () => { var s = new SnesSampler(sp.Snes!, 48000); return (_ => s.Render(buf, 256), () => s.NoteOn(0, sp.InstrumentValue, SnesSampler.PitchFor(0), 100, 100)); });
        var op = PresetPlayable.Build(PresetFixtures.Make(md, PresetFixtures.Instruments(md)[0], 2, 3), out _)!;
        Check("Genesis FM preset", () => { var s = new OpnSampler(op.Sega!, 48000); return (_ => s.Render(buf, 256), () => s.NoteOn(0, op.InstrumentValue - 1, 220, 0.9)); });
        var dp = PresetPlayable.Build(PresetFixtures.Make(md, PresetFixtures.Instruments(md)[6], 0, 3), out _)!;
        Check("Genesis DAC preset", () => { var s = new OpnSampler(dp.Sega!, 48000); return (_ => s.Render(buf, 256), () => s.NoteOn(OpnSampler.DacBase, dp.InstrumentValue - 1, 220, 0.9)); });
        var gp = PresetPlayable.Build(PresetFixtures.Make(gb, PresetFixtures.Instruments(gb)[0], 2, 3), out _)!;
        Check("Game Boy preset", () => { var s = new GbSampler(gp.Gb!, 48000); return (_ => s.Render(buf, 256), () => s.NoteOn(0, 440, 12, -1, false)); });
    }
}

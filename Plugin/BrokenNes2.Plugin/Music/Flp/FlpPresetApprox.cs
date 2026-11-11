// Presets on the register-based engines (2026-11-10, docs/projects/flp-music-pipeline.md section 9): VRUN NES, CTW SMS (VRUN's engine on the PSG) and Vaults GG / GB play notes
// through their own channel registers, not samples, so a Runaway preset reaches them as an ENGINE APPROXIMATION: what such an engine can express, read from the preset's registers:
//   the channel role (pulse / triangle-or-wave / noise), the pulse duty, the noise mode (long / short), the volume envelope as level steps per 60 Hz frame (NES decay / loop, Game Boy
//   NRx2 start volume + direction + pace), the pitch as steps per frame (NES sweep unit). Everything else is LOST and listed (the back end's fence says exactly what):
//   a sample's / FM patch's timbre (SNES BRR, Genesis FM / DAC, Master System FM unit), the Game Boy sweep and wave volume, the length counters, a corrupted instrument's history.
// The back ends call Of() once per instance and keep the result with the instrument (Attach / Get), so CTW SMS's copied instances carry it into VrunNesBackend.
using System.Runtime.CompilerServices;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes2.Music.Flp;

/// <summary>A preset read as a register engine's instrument. Role: 0 pulse, 2 triangle / wave, 3 noise (-1: no role of its own: the instance's channel decides).
/// Level: per 60 Hz frame from the note's start, the level 0-1 relative to the note's own (null: held); LevelLoops: the envelope repeats (NES loop flag). Pitch: cents per frame
/// from the note's pitch (null: none). Duty -1 when the preset has none (the engine's default).</summary>
public sealed class FlpPresetApprox
{
    public const int MaxFrames = 600;          // 10 s: an envelope holds its last value after that
    public string Name = "";
    public ConsoleId Console;
    public InstrumentKind Kind;
    public int Role = -1, Duty = -1;
    public bool ShortNoise;
    public double[]? Level;
    public bool LevelLoops;
    public double[]? Pitch;
    /// <summary>What an engine with duty + level steps + pitch steps + long/short noise keeps of the preset, and what it cannot (the engine adds its own losses).</summary>
    public readonly List<string> Kept = new(), Lost = new();

    public double LevelAt(int frame)
    {
        if (Level == null || Level.Length == 0) return 1;
        if (frame < Level.Length) return Level[Math.Max(0, frame)];
        return LevelLoops ? Level[frame % Level.Length] : Level[^1];
    }

    public double PitchAt(int frame) => Pitch == null || Pitch.Length == 0 ? 0 : Pitch[Math.Clamp(frame, 0, Pitch.Length - 1)];

    private static readonly ConditionalWeakTable<FlpInstrument, FlpPresetApprox> attached = new();
    public static void Attach(FlpInstrument ins, FlpPresetApprox a) => attached.AddOrUpdate(ins, a);
    public static FlpPresetApprox? Get(FlpInstrument ins) => attached.TryGetValue(ins, out var a) ? a : null;

    /// <summary>The approximation of an instance's preset, or null with the reason (no PRS1 section, a damaged file, an instrument that cannot be built).</summary>
    public static FlpPresetApprox? Of(FlpBn2 b, out string? problem)
    {
        problem = null;
        if (b.Preset == null) { problem = "no preset in its state (no PRS1 section)"; return null; }
        if (!b.Preset.Ok) { problem = b.Preset.Problem; return null; }
        var pd = PresetFile.Read(b.Preset.File).Data;
        if (pd == null) { problem = "not a readable preset file"; return null; }
        var play = PresetPlayable.Build(pd, out problem);
        if (play == null) return null;
        var a = new FlpPresetApprox { Name = pd.Name.Length > 0 ? pd.Name : pd.InstrumentName, Console = play.Console, Kind = play.Kind };
        var i = play.Instrument;
        int G(string id) => i.Field(id) is { } f ? f.Value : 0;
        switch (play.Kind)
        {
            case InstrumentKind.NesPulse1 or InstrumentKind.NesPulse2:
                a.Role = 0; a.Duty = G("duty") & 3; a.Kept.Add($"duty {VrunNesBackend.DutyNames[a.Duty]}%");
                NesEnvelope(a, G("const") != 0, G("vol"), G("halt") != 0);
                if (G("sweep.on") != 0 && G("sweep.shift") != 0) NesSweep(a, G("sweep.period"), G("sweep.shift"), G("sweep.neg") != 0, play.Kind == InstrumentKind.NesPulse1);
                if (G("halt") == 0) a.Lost.Add("the length counter (FL's note length decides)");
                break;
            case InstrumentKind.NesTriangle:
                a.Role = 2; a.Kept.Add("the triangle");
                if (G("control") == 0) a.Lost.Add("the linear / length counters (FL's note length decides)");
                break;
            case InstrumentKind.NesNoise:
                a.Role = 3; a.ShortNoise = G("mode") != 0; a.Kept.Add(a.ShortNoise ? "short (buzz) noise" : "long noise");
                NesEnvelope(a, G("const") != 0, G("vol"), G("halt") != 0);
                a.Lost.Add("the noise period register (FL's key picks the period)");
                break;
            case InstrumentKind.NesDmc:
                a.Lost.Add("the DMC sample (the engines have no sample channel): the notes play on the instance's channel as a plain tone");
                break;
            case InstrumentKind.GbPulse1 or InstrumentKind.GbPulse2:
                a.Role = 0; a.Duty = G("duty") & 3; a.Kept.Add($"duty {VrunNesBackend.DutyNames[a.Duty]}%");
                GbEnvelope(a, G("env.vol"), G("env.dir"), G("env.pace"));
                if (play.Kind == InstrumentKind.GbPulse1 && G("sweep.period") != 0 && G("sweep.shift") != 0) a.Lost.Add("the Game Boy sweep (NR10)");
                break;
            case InstrumentKind.GbWave:
                a.Role = 2; a.Lost.Add("the wave RAM's shape (a triangle / square plays instead), the wave volume");
                break;
            case InstrumentKind.GbNoise:
                a.Role = 3; a.ShortNoise = play.GbNoiseShort; a.Kept.Add(a.ShortNoise ? "7-bit (buzz) noise" : "15-bit noise");
                GbEnvelope(a, G("env.vol"), G("env.dir"), G("env.pace"));
                a.Lost.Add("the noise clock (FL's key picks the period)");
                break;
            case InstrumentKind.PsgTones:
                a.Role = 0; a.Duty = 2; a.Kept.Add("the PSG's square (50%)");
                break;
            case InstrumentKind.PsgNoise:
                a.Role = 3; a.ShortNoise = G("noise.mode") == 0; a.Kept.Add(a.ShortNoise ? "periodic noise (as short noise)" : "white noise");
                break;
            default:
                a.Lost.Add($"the {PresetFile.ConsoleName(play.Console)} {(play.Kind is InstrumentKind.SnesSample or InstrumentKind.OpnDac ? "sample" : "FM patch")}'s timbre and envelope (a register engine has no samples or FM): the notes play on the instance's channel as its plain tone");
                break;
        }
        if (Music.Flp.FlpMutants.On(PsMutants.ApproxEnvelopeFlat)) a.Level = null;
        if (pd.Corrupted) a.Lost.Add($"the corruption history ({pd.Steps} steps): only the resulting registers are read");
        return a;
    }

    /// <summary>NES envelope: constant volume = held (the note's own level); decay = 15 down one step every (period + 1) quarter frames (240 Hz), looping with the halt flag.</summary>
    private static void NesEnvelope(FlpPresetApprox a, bool constant, int vol, bool loop)
    {
        if (constant) { a.Kept.Add($"constant volume {vol}/15 (the note's velocity sets the level)"); return; }
        double stepsPerFrame = 4.0 / (vol + 1);
        int n = Math.Min(MaxFrames, (int)Math.Ceiling(16 / stepsPerFrame) + 1);
        a.Level = new double[n];
        for (int f = 0; f < n; f++) a.Level[f] = Math.Max(0, 15 - Math.Floor(f * stepsPerFrame)) / 15.0;
        a.LevelLoops = loop;
        a.Kept.Add($"the decay envelope (period {vol}: {16 / stepsPerFrame / 60 * 1000:0} ms to silence{(loop ? ", looping" : "")}) as level steps");
    }

    /// <summary>NES sweep: every (period + 1) half frames (120 Hz) the timer period changes by period >> shift (pulse 1 negates with one's complement: ignored here).</summary>
    private static void NesSweep(FlpPresetApprox a, int period, int shift, bool down, bool pulse1)
    {
        double ratio = down ? 1 - Math.Pow(2, -shift) : 1 + Math.Pow(2, -shift);           // timer period ratio per step
        double centsPerStep = -1200 * Math.Log2(ratio);                                   // a longer period is a lower pitch
        double stepsPerFrame = 2.0 / (period + 1);
        a.Pitch = new double[MaxFrames];
        for (int f = 0; f < MaxFrames; f++) a.Pitch[f] = Math.Clamp(Math.Floor(f * stepsPerFrame) * centsPerStep, -4800, 4800);
        a.Kept.Add($"the sweep ({centsPerStep:+0;-0} cents every {(period + 1) / 120.0 * 1000:0} ms) as pitch steps");
        if (!down) a.Lost.Add("the sweep's mute when the period overflows (the pitch stops at 4 octaves)");
        if (pulse1 && down) a.Lost.Add("pulse 1's one's-complement sweep (1 period unit)");
    }

    /// <summary>Game Boy NRx2: the start volume, then one step up or down every pace/64 s (pace 0 holds).</summary>
    private static void GbEnvelope(FlpPresetApprox a, int start, int dir, int pace)
    {
        if (pace == 0) { a.Kept.Add($"held volume {start}/15 (the note's velocity sets the level)"); return; }
        double stepsPerFrame = 64.0 / pace / 60.0;
        int n = Math.Min(MaxFrames, (int)Math.Ceiling(16 / stepsPerFrame) + 1);
        a.Level = new double[n];
        double top = Math.Max(1, dir != 0 ? 15 : start);
        for (int f = 0; f < n; f++)
        {
            double v = start + (dir != 0 ? 1 : -1) * Math.Floor(f * stepsPerFrame);
            a.Level[f] = Math.Clamp(v, 0, 15) / top;
        }
        a.Kept.Add($"the volume envelope ({(dir != 0 ? "up" : "down")} from {start}, one step every {pace / 64.0 * 1000:0} ms) as level steps");
    }
}

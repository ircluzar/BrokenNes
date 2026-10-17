// Round 6 (presets): "Audition" in the Presets tab. Plays the preset once, a middle C held for a second and a half and its release, on a private engine built for the purpose (the same samplers a preset is played with), mixed into the output of the instance whose editor
// asked. It touches nothing of that instance: not its game, not its chip, not its preset. The engine is built on the editor's thread; the audio thread only renders it (no allocation).
using BrokenNes2.Instruments;
using BrokenNes2.Presets;

namespace BrokenNes2;

internal sealed class PresetAudition
{
    public const double HoldSeconds = 1.5, TailSeconds = 1.0, Velocity = 0.8;
    private const int Chunk = 64;
    private const double MiddleCHz = 261.6256;

    private readonly PresetPlayable play;
    private readonly SnesSampler? snes;
    private readonly SegaSampler? sega;
    private readonly GbSampler? gb;
    private readonly NesPresetPlayer? nes;
    private readonly int instrument, first;
    private readonly long holdFrames, totalFrames;
    private readonly float[] stereo = new float[Chunk * 2], mono = new float[Chunk];
    private long frames;
    private bool started, released;

    /// <summary>The audition's engine, or null with the reason this preset cannot be auditioned.</summary>
    public static PresetAudition? Create(PresetPlayable p, int rate, out string? why)
    {
        why = p.CannotPlay;
        return why != null ? null : new PresetAudition(p, rate);
    }

    private PresetAudition(PresetPlayable p, int rate)
    {
        play = p;
        holdFrames = (long)(HoldSeconds * rate);
        totalFrames = holdFrames + (long)(TailSeconds * rate);
        switch (p.Console)
        {
            case ConsoleId.Snes: snes = new SnesSampler(p.Snes!, rate); instrument = p.InstrumentValue; break;
            case ConsoleId.GameBoy: gb = new GbSampler(p.Gb!, rate); break;
            case ConsoleId.Nes: nes = new NesPresetPlayer(p.Instrument, rate); break;
            default:
                sega = p.Sega!.Kind switch { SegaSnapshot.KindOpn => new OpnSampler(p.Sega, rate), SegaSnapshot.KindOpll => new OpllSampler(p.Sega, rate), _ => (SegaSampler)new PsgSampler(p.Sega, rate) };
                instrument = p.Sega.Resolve(p.InstrumentValue);
                first = p.Sega.SlotBase(instrument);
                break;
        }
    }

    private void Start()
    {
        started = true;
        if (snes != null) snes.NoteOn(0, instrument, SnesSampler.PitchFor(0), (int)Math.Round(127 * Velocity), (int)Math.Round(127 * Velocity));
        else if (sega != null) sega.NoteOn(first, instrument, MiddleCHz, Velocity);
        else if (gb != null) gb.NoteOn(play.GbChannel, MiddleCHz, (int)Math.Round(15 * Velocity), -1, play.GbNoiseShort);
    }

    private void Release()
    {
        released = true;
        if (snes != null) snes.NoteOff(0);
        else if (sega != null) sega.NoteOff(first);
        else if (gb != null) gb.NoteOff(play.GbChannel);
    }

    /// <summary>Adds the next <paramref name="length"/> frames of the audition into interleaved stereo <paramref name="dest"/>; false when it has finished.</summary>
    public unsafe bool AddTo(float* dest, int length, float gain)
    {
        for (int done = 0; done < length; done += Chunk)
        {
            if (frames >= totalFrames) return false;
            int n = Math.Min(Chunk, length - done);
            if (!started) Start();
            if (!released && frames >= holdFrames) Release();
            if (nes != null)
            {
                nes.Update(!released, frames == 0, 0);
                nes.Render(mono.AsSpan(0, n));
                for (int i = 0; i < n; i++) { stereo[i * 2] = mono[i] * (float)Velocity; stereo[i * 2 + 1] = mono[i] * (float)Velocity; }
            }
            else if (snes != null) snes.Render(stereo.AsSpan(0, n * 2), n);
            else if (gb != null) gb.Render(stereo.AsSpan(0, n * 2), n);
            else sega!.Render(stereo.AsSpan(0, n * 2), n);
            for (int i = 0; i < n * 2; i++) dest[done * 2 + i] += stereo[i] * gain;
            frames += n;
        }
        return frames < totalFrames;
    }
}

// Round 8 (record-nes): the instance's side of a NES recording. A track recorded from a NES game is a NES preset with the recorded-track note behaviour (PresetNote.NesTrack): it plays FL's notes
// with Direct mode's own register code (Record/NesDirect.cs), its duty or noise mode from the preset, the volume register from FL's velocity x controller 7, and, when the channel plays its track's
// expression (TrackExpression: "Bends + volume from the recording"), each recorded note's own bends, controller 7 and velocity from the note's onset (reload: no song position needed), as the SNES tracks do. A probe of the instance's own chip
// writes (the self-test compares them with the game's); the test hooks 740-779 (the editor's NES ones: 780-789).
using BrokenNes2.Record;
using NesEmulator.Plugin;

namespace BrokenNes2;

/// <summary>Every register write a NES instance's chip makes, with the chip's own sample clock, and a mark when an FL voice starts a note: the self-test's NES replay probe (preallocated; the audio thread
/// fills it without allocating).</summary>
internal sealed class NesReplayProbe
{
    public readonly long[] Time;
    public readonly int[] Code;
    public int Count;
    public long Lost;
    public readonly Action<int, byte> Tap;
    private NesApuInstrument? chip;

    public NesReplayProbe(int capacity) { Time = new long[capacity]; Code = new int[capacity]; Tap = OnWrite; }

    public void Bind(NesApuInstrument c)
    {
        if (ReferenceEquals(chip, c)) return;
        chip = c;
        c.WriteTap = Tap;
    }

    private void OnWrite(int reg, byte value) => Add(chip?.SamplesRendered ?? 0, reg << 8 | value);

    /// <summary>An FL voice (its handle's low 16 bits) became the note this instance plays.</summary>
    public void Mark(nint handle) => Add(chip?.SamplesRendered ?? 0, 1 << 24 | (int)(handle & 0xFFFF));

    private void Add(long t, int code)
    {
        if (Count >= Time.Length) { Lost++; return; }
        Time[Count] = t; Code[Count] = code; Count++;
    }
}

public sealed unsafe partial class Bn2Plugin
{
    private volatile NesReplayProbe? nesProbe;
    private Voice? nesTrackOwner;

    /// <summary>A NES track preset: the newest held note plays (a NES channel is monophonic), its pitch and level from FL or, with the track's expression on, from the recorded note it is, from its onset.</summary>
    private void RenderNesTrack(NesPresetPlayer player, float* dest, int length, float gl, float gr)
    {
        nesProbe?.Bind(player.Chip);
        double master = Get(P.Volume) / 1000.0;
        var ex = expression;
        BeginExpressionBlock();
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            var owner = ResolveOwner();
            if (owner != null)
            {
                ref var fl = ref owner.Params->FinalLevels;
                if (fl.Pitch < owner.PitchMin) owner.PitchMin = fl.Pitch;
                if (fl.Pitch > owner.PitchMax) owner.PitchMax = fl.Pitch;
                double cents = FlCents(fl.Pitch);
                int level = NesDirect.Level(fl.Vol, master);
                double exBeat = ExpressionBeat(owner, off);
                if (!double.IsNaN(exBeat) && ex != null)
                {
                    // the recorded note's own pitch bend and controller 7 at this point of it, from its own onset: exact as the transcription made them
                    cents = ex.PitchCents(owner.TrackKey, exBeat) + Get(P.Coarse) * 100 + Get(P.Fine);
                    level = NesDirect.Level((float)(owner.TrackVel * ex.CcAt(exBeat) / 100.0), master);
                }
                if (owner.Fresh) nesProbe?.Mark(owner.Handle);
                // (a note that resumes after a newer one ended is not fresh: like Direct mode, only a new FL note or a silent channel restarts the wave)
                player.UpdateTrack(true, owner.Fresh, cents, level);
                owner.Fresh = false;
                nesTrackOwner = owner;
            }
            else { player.UpdateTrack(false, false, 0, 0); nesTrackOwner = null; }
            player.Render(mono.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                float s = mono[i];
                dest[(off + i) * 2] = s * gl;
                dest[(off + i) * 2 + 1] = s * gr;
            }
        }
    }

    /// <summary>The NES recording's hooks, for the self-test (Fpd.TestBase + n, n 740-779). See the list in the code.</summary>
    private nint NesRecordTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 740: nesProbe = value > 0 ? new NesReplayProbe((int)value) : null; return 0;                                                        // the NES replay probe: capacity (0 off)
            case 741:                                                                                                                                  // read it: index = max records, value = buffer of (int64 time, int32 code); value 0: the count held
            {
                var p = nesProbe;
                if (p == null) return -1;
                if (value == 0) return p.Count;
                int nrec = Math.Min(p.Count, (int)index);
                byte* b = (byte*)value;
                for (int i = 0; i < nrec; i++) { *(long*)(b + i * 12) = p.Time[i]; *(int*)(b + i * 12 + 8) = p.Code[i]; }
                return nrec;
            }
            case 742: { var p = nesProbe; return p == null ? -1 : (nint)p.Lost; }
            case 743: return preset?.Nes is { Track: true } ? 1 : preset?.Nes != null ? 0 : -1;                                                     // this instance's NES preset plays as a recorded track (1), a plain NES preset (0), none (-1)
            case 744: { var s = emu?.Recording?.Nes?.Session; return s == null ? -1 : CopyBytes(s.ToPayload(), index, value); }                   // the held NES recording's session payload
            case 745: return emu?.NesRecorderForTests?.Count is { } c745 ? (nint)c745 : -1;                                                          // entries captured so far (the capture in progress)
            case 746: return emu?.NesRecorderForTests?.Dropped is { } d746 ? (nint)d746 : -1;
            case 747: { var g = emu?.NesWritesForTests(); return (nint)(g ?? -1); }                                                                             // the game's own count of APU writes since Record (the independent count)
            default: return 0;
        }
    }
}

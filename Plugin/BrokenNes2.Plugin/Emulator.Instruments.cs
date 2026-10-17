// Round 6 (imodel): the instruments of the frozen game on the emulator. Instrument Runaway keeps a copy of the game's sound memory (SNES: SpcSnapshot, Game Boy: GbSnapshot, Master System / Game Gear / Genesis: SegaSnapshot);
// an edited instrument is written into a COPY of it (Instruments/*) and the copy replaces the snapshot, so every instance's sampler (which rebuilds when its snapshot is not the one it was built from) plays the edit.
using BrokenNes2.Instruments;

namespace BrokenNes2;

public sealed partial class Emulator
{
    private volatile string? instrumentNote;

    /// <summary>Set when a project's instrument edits could not be put back (a damaged or foreign section): the words for the Instruments tab, null when there is nothing to say.</summary>
    public string? InstrumentNote => instrumentNote;

    internal void SetInstrumentNote(string? note) => instrumentNote = note;

    /// <summary>The frozen game's instruments (null: Runaway is off, or the console has no sampler, like the NES).</summary>
    public IInstrumentSource? InstrumentSource
    {
        get
        {
            if (!Runaway) return null;
            object? snap = Snapshot ?? (object?)GbState ?? SegaState;
            return snap == null ? null : InstrumentSources.For(snap);
        }
    }

    /// <summary>
    /// Writes an edited instrument into the Runaway working copy: the snapshot is replaced by a copy that has the edit, and every instance's sampler rebuilds from it on its next block (the same swap Runaway's start does).
    /// Refused, with the reason, when Runaway is off, the game has no instruments the plugin can edit, or the instrument does not fit the snapshot. Call from the UI thread, never from the audio thread.
    /// </summary>
    public WriteResult ApplyInstrument(Instrument edited)
    {
        lock (EmulatorHub.Gate)
        {
            if (!Runaway) return WriteResult.Fail("Instrument Runaway is off: freeze the game first, an edit goes to the frozen game's sound memory");
            var src = InstrumentSource;
            if (src == null) return WriteResult.Fail("this game has no instruments the plugin can edit (a NES game is frozen and silenced only)");
            var res = src.Write(edited);
            if (!res.Ok) return res;
            if (res.Snapshot is not (SpcSnapshot or GbSnapshot or SegaSnapshot)) return WriteResult.Fail("the writer returned a snapshot this emulator does not know");
            // every instance plays the game's sound through a sampler of its own: build them here, on the UI thread, before the snapshot is swapped, so the audio threads only hand over (no allocation, no click)
            int rate = EmulatorHub.HostRate;
            foreach (var m in members.ToArray()) m.PrepareSamplerFor(res.Snapshot, rate);
            switch (res.Snapshot)
            {
                case SpcSnapshot s: Volatile.Write(ref snapshot, s); break;
                case GbSnapshot g: Volatile.Write(ref gbState, g); break;
                case SegaSnapshot sg: Volatile.Write(ref segaState, sg); break;
            }
            Changed();
            return res;
        }
    }
}

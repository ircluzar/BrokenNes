// Round 6 (imodel): test hooks 300-319 of the plugin, for the instrument model's end-to-end checks (the FruityHost `imodel` check drives them).
using BrokenNes2.Instruments;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private static string lastInstrumentResult = "";

    // ---- a sampler built ahead of the swap ----
    // An instrument edit replaces the working copy of the game's sound memory; each instance's sampler is rebuilt for it, which allocates (a SNES one copies 64 KB of audio RAM) and takes a while: on the audio thread that is a click and an
    // allocation at every slider tick. So the UI thread builds the sampler (Emulator.ApplyInstrument -> PrepareSamplerFor) and hands it over; the audio thread takes it when it finds the snapshot has changed, and only builds its own when none was prepared.
    private sealed record PreparedSampler(object For, int Rate, object Sampler);
    private PreparedSampler? prepared;

    /// <summary>UI thread: builds the sampler this instance will play <paramref name="snap"/> with and leaves it for the audio thread.</summary>
    internal void PrepareSamplerFor(object snap, int rate)
    {
        object? s = snap switch
        {
            SpcSnapshot a => new SnesSampler(a, rate),
            GbSnapshot g => new GbSampler(g, rate),
            SegaSnapshot x => x.Kind switch { SegaSnapshot.KindOpn => new OpnSampler(x, rate), SegaSnapshot.KindOpll => new OpllSampler(x, rate), _ => (SegaSampler)new PsgSampler(x, rate) },
            _ => null,
        };
        if (s != null) Volatile.Write(ref prepared, new PreparedSampler(snap, rate, s));
    }

    /// <summary>Audio thread: the prepared sampler when it is for exactly this snapshot and rate (taken, so it is used once), else null. Allocation-free.</summary>
    private T? TakePrepared<T>(object snap, int rate) where T : class
    {
        var p = Interlocked.Exchange(ref prepared, null);
        return p != null && ReferenceEquals(p.For, snap) && p.Rate == rate ? p.Sampler as T : null;
    }

    /// <summary>Test hooks: 300 write the instrument packed in the <c>index</c> bytes at <c>value</c> (InstrumentCodec.Pack) into the frozen game's working copy (returns 0 done, 1 refused, 2 the bytes are not an instrument), 301 copy the last result's words
    /// (the reason, or what changed) into the buffer at <c>value</c> (at most <c>index</c> chars; returns the length), 302 hash of the working copy's bytes, overlay included (0 none), 303 how many entries the SNES working copy's overlay has (-1 no SNES copy),
    /// 304 how many instruments the working copy lists (-1 none), 305 copy the project's instrument note into the buffer at <c>value</c> (at most <c>index</c> chars; returns its length, -1 none), 306 bytes this thread allocated (as hook 4; the audio side).</summary>
    private nint InstrumentTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 300:
            {
                if (emu == null || index <= 0) return 1;
                var packed = new ReadOnlySpan<byte>((void*)value, (int)index).ToArray();
                var inst = InstrumentCodec.Unpack(packed, out string? problem);
                if (inst == null) { lastInstrumentResult = "not an instrument: " + problem; return 2; }
                var res = emu.ApplyInstrument(inst);
                lastInstrumentResult = res.Ok ? "changed: " + string.Join(", ", res.Changed) : res.Reason ?? "refused";
                return res.Ok ? 0 : 1;
            }
            case 301: return CopyText(lastInstrumentResult, index, value);
            case 302:
            {
                object? snap = emu == null ? null : emu.Snapshot ?? (object?)emu.GbState ?? emu.SegaState;
                if (snap == null) return 0;
                byte[] bytes = snap switch
                {
                    SpcSnapshot s => s.ToBytes().Concat(s.Overlay?.ToBytes() ?? []).ToArray(),
                    GbSnapshot g => g.ToBytes(),
                    SegaSnapshot sg => sg.ToBytes(),
                    _ => [],
                };
                ulong h = 14695981039346656037UL;
                foreach (byte b in bytes) h = (h ^ b) * 1099511628211UL;
                return (nint)(long)(h | 1);
            }
            case 303: return emu?.Snapshot is { } sn ? sn.Overlay?.Count ?? 0 : -1;
            case 304: return emu?.InstrumentSource?.List().Count ?? -1;
            case 305: return emu?.InstrumentNote is { } note ? CopyText(note, index, value) : -1;
            case 306: return (nint)GC.GetAllocatedBytesForCurrentThread();
            case 307:     // the length of instrument <index> of the working copy, packed (-1 none)
            case 308:     // the same, copied into the buffer at <value> (the caller sized it with 307)
            {
                var inst = emu?.InstrumentSource?.Read((int)index);
                if (inst == null) return -1;
                var packed = InstrumentCodec.Pack(inst);
                if (n == 308 && value != 0) packed.CopyTo(new Span<byte>((void*)value, packed.Length));
                return packed.Length;
            }
            case 309:     // puts a SNES working copy in place, from the saved bytes of a SpcSnapshot (<index> of them at <value>): a test ROM has no samples worth the name (returns 0 done, 1 not a snapshot)
            {
                var s = SpcSnapshot.FromBytes(new ReadOnlySpan<byte>((void*)value, (int)index).ToArray());
                if (s == null || emu == null) return 1;
                emu.ReplaceSnapshotForTests(s);
                return 0;
            }
            case 312: InstrumentMutants.Active = (int)value; return 0;       // tests only: switches one of the library's deliberate bugs on (0 = none); the self-test shows its checks catch each
            default: return 0;
        }
    }

    private static nint CopyText(string text, nint max, nint buffer)
    {
        if (buffer != 0 && max > 0) text.AsSpan(0, Math.Min(text.Length, (int)max)).CopyTo(new Span<char>((void*)buffer, (int)max));
        return text.Length;
    }
}

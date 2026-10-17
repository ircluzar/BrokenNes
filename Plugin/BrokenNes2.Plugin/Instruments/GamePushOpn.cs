// Round 6 (inject): pushing a Genesis FM patch or DAC sound into the running game.
//
// A YM2612 channel holds its patch in 28 operator registers plus $B0 (algorithm, feedback) and $B4 (pan, AMS, FMS). The sound driver loads a patch when a track changes instrument and rewrites the carriers' total level when the volume
// changes (measured on Sonic the Hedgehog: the operator registers are written before about 7% of the key-ons, the total levels before about 15%); between those events the chip's registers just hold the patch. So an edited patch cannot be
// written once and forgotten, and the hook is a substitution at KEY-ON (Ym2612.Override): when a channel is keyed on while its registers are the ORIGINAL patch (all but the carriers' total level, which is the driver's volume), the edited
// patch is loaded first; the carriers' total level keeps the driver's volume and adds the edit's change to it. The patch is found by content, so every channel the game plays it on gets the edit. Pan, AMS and FMS (the driver's, from the track)
// are replaced only where the edit changed them.
// The DAC is one register that the game's driver streams a sample into. The hook recognises the start of the sound in the stream (its first bytes), and from there hands the chip the edited sound's samples instead, at the driver's pace
// (an edited rate plays the sound faster or slower inside it).
using NesEmulator.Sega;

namespace BrokenNes2.Instruments;

internal sealed class OpnPushHook : IYm2612Override
{
    public const int PatchBytes = 30;
    private const int Match = 12;                         // DAC: how many bytes of a sound's start identify it in the stream
    private const long GapClocks = 20_000;                // DAC: chip clocks without a write that end a sound (2.6 ms; a 8 kHz sample is 0.125 ms apart)

    private static readonly bool[,] Carrier =
    {
        { false, false, false, true }, { false, false, false, true }, { false, false, false, true }, { false, false, false, true },
        { false, false, true, true }, { false, true, true, true }, { false, true, true, true }, { true, true, true, true },
    };
    private static readonly int[] OpOffset = [0, 8, 4, 12];

    private sealed class Patch
    {
        public byte[] Orig = [], Edit = [];
        public bool Lfo; public byte OrigLfo, EditLfo;
        public bool[] Compare = new bool[PatchBytes];       // the registers that identify the patch (not the carriers' total level, not $B4)
        public bool[] TlChanged = new bool[4];
    }

    private sealed class Dac
    {
        public byte[] Orig = [], Edit = [];
        public int OrigRate, EditRate;
        public bool EditLoops, OrigLoops;
        public int LoopStart, LoopEnd;
    }

    private readonly Patch[] patches;
    private readonly Dac[] dacs;
    private readonly byte[] lastTl = new byte[24];       // per channel and operator: the total level this hook last wrote (255: none)

    /// <summary>Key-ons where the channel held the original patch and the edited one was loaded, and key-ons where it held the edited one already (diagnostics for the checks and the logs).</summary>
    public long Substituted, AlreadyEdited;
    /// <summary>Per DAC sound: how many times its start was recognised in the stream.</summary>
    public readonly long[] DacMatches;

    // the DAC stream
    private int active = -1, pos, fill;
    private readonly byte[] ring = new byte[Match];
    private long lastDacClock = long.MinValue / 2;

    public OpnPushHook(IReadOnlyList<PushRecord> records)
    {
        var ps = new List<Patch>();
        var ds = new List<Dac>();
        foreach (var r in records)
        {
            if (r.Kind == PushKind.OpnPatch && r.A.Length >= PatchBytes && r.B.Length >= PatchBytes)
            {
                var p = new Patch { Orig = r.A[..PatchBytes], Edit = r.B[..PatchBytes], Lfo = r.P.Length > 0 && r.P[0] != 0, OrigLfo = r.A.Length > PatchBytes ? r.A[PatchBytes] : (byte)0, EditLfo = r.B.Length > PatchBytes ? r.B[PatchBytes] : (byte)0 };
                int alg = p.Orig[28] & 7;
                for (int i = 0; i < PatchBytes; i++) p.Compare[i] = i < 28 || i == 28;
                for (int n = 0; n < 4; n++)
                {
                    if (Carrier[alg, n]) p.Compare[n * 7 + 1] = false;                  // a carrier's total level carries the driver's volume
                    p.TlChanged[n] = (p.Orig[n * 7 + 1] & 0x7F) != (p.Edit[n * 7 + 1] & 0x7F);
                }
                ps.Add(p);
            }
            else if (r.Kind == PushKind.OpnDac && r.P.Length >= 8 && r.A.Length >= Match)
                ds.Add(new Dac { Orig = r.A, Edit = r.B, OrigRate = Math.Max(1, r.P[0]), EditRate = Math.Max(1, r.P[1]), EditLoops = r.P[2] != 0, LoopStart = r.P[3], LoopEnd = r.P[4], OrigLoops = r.P[5] != 0 });
        }
        patches = ps.ToArray();
        dacs = ds.ToArray();
        DacMatches = new long[dacs.Length];
        Array.Fill(lastTl, (byte)255);
    }

    /// <summary>The volume memory (24 bytes), saved with the project.</summary>
    public byte[] SaveState() => (byte[])lastTl.Clone();

    public void LoadState(byte[] s) { if (s.Length == lastTl.Length) Array.Copy(s, lastTl, s.Length); }

    /// <summary>The 28 operator registers in algorithm order (operator 1 to 4, 7 registers each: DT/MUL, TL, RS/AR, AM/D1R, D2R, SL/RR, SSG-EG), then $B0 and $B4 of channel <paramref name="ch"/>.</summary>
    public static void ReadPatch(Ym2612 ym, int ch, Span<byte> dest)
    {
        int part = ch / 3 * 0x100, c = ch % 3;
        for (int n = 0; n < 4; n++)
            for (int k = 0; k < 7; k++) dest[n * 7 + k] = ym.Register(part + 0x30 + k * 0x10 + OpOffset[n] + c);
        dest[28] = ym.Register(part + 0xB0 + c);
        dest[29] = ym.Register(part + 0xB4 + c);
    }

    private static int RegisterOf(int ch, int i)
    {
        int part = ch / 3 * 0x100, c = ch % 3;
        if (i == 28) return part + 0xB0 + c;
        if (i == 29) return part + 0xB4 + c;
        return part + 0x30 + (i % 7) * 0x10 + OpOffset[i / 7] + c;
    }

    // ---- key-on: the patch ----

    public void OnKeyOn(Ym2612 chip, int channel)
    {
        if (patches.Length == 0) return;
        Span<byte> cur = stackalloc byte[PatchBytes];
        ReadPatch(chip, channel, cur);
        foreach (var p in patches)
        {
            if (p.Lfo && (chip.Register(0x22) & 0x0F) == (p.OrigLfo & 0x0F)) chip.OverrideRegister(0x22, (byte)(chip.Register(0x22) & ~0x0F | p.EditLfo & 0x0F));
            bool isOrig = true, isEdit = true;
            for (int i = 0; i < PatchBytes; i++)
            {
                if (!p.Compare[i]) continue;
                if (cur[i] != p.Orig[i]) isOrig = false;
                if (cur[i] != p.Edit[i]) isEdit = false;
                if (!isOrig && !isEdit) break;
            }
            if (!isOrig && !isEdit) continue;
            if (PushMutants.On(PushMutants.OpnNoSubstitution)) return;
            if (isOrig) Substituted++; else AlreadyEdited++;
            // the registers
            for (int i = 0; i < 28; i++)
            {
                bool tl = i % 7 == 1;
                if (tl) continue;
                if (isOrig && cur[i] != p.Edit[i]) chip.OverrideRegister(RegisterOf(channel, i), p.Edit[i]);
            }
            if (isOrig && cur[28] != p.Edit[28]) chip.OverrideRegister(RegisterOf(channel, 28), p.Edit[28]);
            // the total levels: the driver's volume plus the edit's change
            for (int n = 0; n < 4; n++)
            {
                if (!p.TlChanged[n]) continue;
                int i = n * 7 + 1, slot = channel * 4 + n;
                bool fresh = isOrig || cur[i] != lastTl[slot];        // after the driver loaded a patch or set a volume, the register is the driver's again
                if (!fresh) continue;
                int delta = (p.Edit[i] & 0x7F) - (p.Orig[i] & 0x7F);
                int low = PushMutants.On(PushMutants.OpnIgnoresVolume) ? p.Edit[i] & 0x7F : (cur[i] & 0x7F) + delta;      // (the deliberate bug: the edit's absolute level, the driver's volume lost)
                int v = (cur[i] & 0x80) | Math.Clamp(low, 0, 127);
                chip.OverrideRegister(RegisterOf(channel, i), (byte)v);
                lastTl[slot] = (byte)v;
            }
            // pan, AMS, FMS: only the groups the edit changed
            byte b4 = cur[29];
            foreach (int mask in (ReadOnlySpan<int>)[0xC0, 0x30, 0x07])
                if ((p.Orig[29] & mask) != (p.Edit[29] & mask)) b4 = (byte)(b4 & ~mask | p.Edit[29] & mask);
            if (b4 != cur[29]) chip.OverrideRegister(RegisterOf(channel, 29), b4);
            return;
        }
    }

    /// <summary>Puts the original patch back on every channel whose registers are the edited one (the driver may not reload it for a long time). Returns how many channels it changed.</summary>
    public int RestoreChannels(Ym2612 chip, PushRecord rec)
    {
        if (rec.Kind != PushKind.OpnPatch || rec.A.Length < PatchBytes || rec.B.Length < PatchBytes || PushMutants.On(PushMutants.OpnRemoveKeepsPatch)) return 0;
        byte[] orig = rec.A, edit = rec.B;
        int alg = orig[28] & 7, changed = 0;
        Span<byte> cur = stackalloc byte[PatchBytes];
        for (int ch = 0; ch < 6; ch++)
        {
            ReadPatch(chip, ch, cur);
            bool isEdit = true;
            for (int i = 0; i < 29 && isEdit; i++)
            {
                if (i < 28 && i % 7 == 1 && Carrier[alg, i / 7]) continue;
                if (cur[i] != edit[i]) isEdit = false;
            }
            if (!isEdit) continue;
            bool any = false;
            for (int i = 0; i < 29; i++)
            {
                if (i < 28 && i % 7 == 1) continue;
                if (cur[i] != orig[i]) { chip.OverrideRegister(RegisterOf(ch, i), orig[i]); any = true; }
            }
            for (int n = 0; n < 4; n++)
            {
                int i = n * 7 + 1, slot = ch * 4 + n;
                if ((orig[i] & 0x7F) == (edit[i] & 0x7F)) continue;
                if (cur[i] == lastTl[slot] || !Carrier[alg, n])          // the register still holds what this hook wrote (or a modulator's level, which is the patch's own)
                {
                    int v = (cur[i] & 0x80) | Math.Clamp((cur[i] & 0x7F) - ((edit[i] & 0x7F) - (orig[i] & 0x7F)), 0, 127);
                    if (v != cur[i]) { chip.OverrideRegister(RegisterOf(ch, i), (byte)v); any = true; }
                }
                lastTl[slot] = 255;
            }
            byte b4 = cur[29];
            foreach (int mask in (ReadOnlySpan<int>)[0xC0, 0x30, 0x07])
                if ((orig[29] & mask) != (edit[29] & mask)) b4 = (byte)(b4 & ~mask | orig[29] & mask);
            if (b4 != cur[29]) { chip.OverrideRegister(RegisterOf(ch, 29), b4); any = true; }
            if (any) changed++;
        }
        if (rec.P.Length > 0 && rec.P[0] != 0 && rec.A.Length > PatchBytes && rec.B.Length > PatchBytes && (chip.Register(0x22) & 0x0F) == (edit[PatchBytes] & 0x0F))
            chip.OverrideRegister(0x22, (byte)(chip.Register(0x22) & ~0x0F | orig[PatchBytes] & 0x0F));
        return changed;
    }

    // ---- the DAC stream ----

    private byte Sample(Dac d, int at)
    {
        long idx = d.EditRate == d.OrigRate || PushMutants.On(PushMutants.DacRateIgnored) ? at : (long)at * d.EditRate / d.OrigRate;
        if (d.EditLoops && d.LoopEnd > d.LoopStart && idx >= d.LoopEnd) idx = d.LoopStart + (idx - d.LoopStart) % (d.LoopEnd - d.LoopStart);
        return idx >= 0 && idx < d.Edit.Length ? d.Edit[idx] : (byte)0x80;
    }

    public byte OnDacWrite(byte value, long chipClock)
    {
        if (dacs.Length == 0) return value;
        if (chipClock - lastDacClock > GapClocks) { active = -1; fill = 0; }
        lastDacClock = chipClock;
        if (active >= 0)
        {
            var d = dacs[active];
            if (pos < d.Orig.Length)
            {
                if (value == d.Orig[pos]) return Sample(d, pos++);
                active = -1; fill = 0;                              // the stream is not this sound any more
            }
            else if (d.OrigLoops) return Sample(d, pos++);          // the game's own loop goes on past the stretch that was recorded
            else { active = -1; fill = 0; }
        }
        ring[fill % Match] = value;
        fill++;
        if (fill >= Match)
        {
            for (int di = 0; di < dacs.Length; di++)
            {
                var d = dacs[di];
                bool same = true;
                for (int j = 0; j < Match && same; j++) same = ring[(fill - Match + j) % Match] == d.Orig[j];
                if (!same) continue;
                if (PushMutants.On(PushMutants.DacNoSubstitution)) return value;
                DacMatches[di]++;
                active = di; pos = Match;
                return Sample(d, Match - 1);
            }
        }
        return value;
    }
}

internal static class OpnPusher
{
    private static string Sub(string what) => what;

    private static bool SamePatchBytes(byte[] a, byte[] b) => a.AsSpan(0, OpnPushHook.PatchBytes).SequenceEqual(b.AsSpan(0, OpnPushHook.PatchBytes));

    public static PushOutcome PushPatch(PushMachine m, GamePushes st, SegaSnapshot? work, Instrument e, Instrument? clean)
    {
        var unit = m.MdUnit;
        if (unit == null) return PushOutcome.Refused("this is not a Genesis game with the YM2612 sound unit");
        if (work == null || work.Kind != SegaSnapshot.KindOpn) return PushOutcome.Refused("there is no frozen copy of the Genesis sound chips to push from");
        int ch = e.Index;
        if (ch < 0 || ch > 5) return PushOutcome.Refused("this is not an FM channel patch");
        var inCopy = new SegaInstruments(work).Read(ch);
        if (inCopy == null || !inCopy.SameValues(e)) return PushOutcome.Refused("the instrument shown is not the one in the frozen copy of the sound chips (an edit did not reach it); make the edit again, then push");

        var old = st.Find(PushKind.OpnPatch, ch);
        var edit = (byte[])work.OpnPatch[ch].Clone();
        int editLfo = work.OpnLfo;
        // the original: what the game had on this channel before any push of it
        byte[] orig; int origLfo;
        if (old != null) { orig = old.A[..OpnPushHook.PatchBytes]; origLfo = old.A.Length > OpnPushHook.PatchBytes ? old.A[OpnPushHook.PatchBytes] : unit.Fm.Register(0x22); }
        else
        {
            orig = new byte[OpnPushHook.PatchBytes];
            if (clean != null && clean.Kind == InstrumentKind.OpnPatch)
            {
                // the clean patch as the model has it (the game's, from before the edits of this session), through the same writer the snapshot uses
                var cleanSnap = new SegaInstruments(work).Write(clean);
                if (!cleanSnap.Ok || cleanSnap.Snapshot is not SegaSnapshot cs) return PushOutcome.Refused("the clean patch could not be rebuilt: " + cleanSnap.Reason);
                orig = (byte[])cs.OpnPatch[ch].Clone();
                origLfo = cs.OpnLfo;
            }
            else { OpnPushHook.ReadPatch(unit.Fm, ch, orig); origLfo = unit.Fm.Register(0x22); }
        }
        bool lfoChanged = (editLfo & 0x0F) != (origLfo & 0x0F);
        bool patchChanged = !SamePatchBytes(orig, edit);
        if (!patchChanged && !lfoChanged)
        {
            if (old != null)
            {
                var rm = Remove(m, st, old);
                rm.Message = $"FM {ch + 1} is as the game had it again, so the earlier push was taken out: " + rm.Message;
                return rm;
            }
            return PushOutcome.Refused($"nothing to push: FM {ch + 1} is as the game has it. Edit it first");
        }
        var rec = new PushRecord { Kind = PushKind.OpnPatch, Index = ch, Name = $"FM {ch + 1} patch", A = [.. orig, (byte)origLfo], B = [.. edit, (byte)editLfo], P = [lfoChanged ? 1 : 0] };
        // another record for the same original patch (the game plays it on several channels, or it was pushed from another channel's tab): this one replaces it
        PushRecord? twin = old;
        if (twin == null)
            foreach (var r in st.Records)
                if (r.Kind == PushKind.OpnPatch && r.A.Length >= OpnPushHook.PatchBytes && SamePatchIgnoringCarrierLevels(r.A, orig)) twin = r;
        var o = new PushOutcome { Ok = true };
        if (twin != null) st.Records.Remove(twin);
        st.Records.Add(rec);
        PushHooks.Install(m, st);
        o.Poison = "an instrument was pushed into the sound unit as a key-on substitution (not a memory write a replay from power-on could repeat)";
        o.Message = $"Pushed FM {ch + 1} patch ({Differences(orig, edit, origLfo, editLfo)}): from the next key-on, every channel the game plays this patch on loads the edited one first (the driver's volume is kept). It can reload the original any time; the substitution is made again at each key-on. Remove from game undoes it."
            + (twin != null && twin != old ? $" (It replaced the earlier push of the same patch, {twin.Name}.)" : "");
        return o;
    }

    private static bool SamePatchIgnoringCarrierLevels(byte[] a, byte[] b)
    {
        int alg = b[28] & 7;
        for (int i = 0; i < 29; i++)
        {
            if (i < 28 && i % 7 == 1 && IsCarrier(alg, i / 7)) continue;
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static bool IsCarrier(int alg, int n) => alg switch { <= 3 => n == 3, 4 => n >= 2, 5 or 6 => n >= 1, _ => true };

    private static string Differences(byte[] orig, byte[] edit, int origLfo, int editLfo)
    {
        int n = 0;
        for (int i = 0; i < 30; i++) if (orig[i] != edit[i]) n++;
        var bits = new List<string>();
        if (n > 0) bits.Add($"{n} of 30 patch registers differ");
        if ((origLfo & 0x0F) != (editLfo & 0x0F)) bits.Add("LFO");
        return string.Join(", ", bits);
    }

    public static PushOutcome PushDac(PushMachine m, GamePushes st, SegaSnapshot? work, Instrument e, Instrument? clean)
    {
        var unit = m.MdUnit;
        if (unit == null) return PushOutcome.Refused("this is not a Genesis game with the YM2612 sound unit");
        if (work == null || work.Kind != SegaSnapshot.KindOpn) return PushOutcome.Refused("there is no frozen copy of the Genesis sound chips to push from");
        int idx = e.Index - 6;
        if (idx < 0 || idx >= work.Dac.Length) return PushOutcome.Refused("this is not a DAC sound of the frozen copy");
        var inCopy = new SegaInstruments(work).Read(e.Index);
        if (inCopy == null || !inCopy.SameValues(e)) return PushOutcome.Refused("the instrument shown is not the one in the frozen copy of the sound chips (an edit did not reach it); make the edit again, then push");

        var cur = work.Dac[idx];
        // the original sound: from the earlier push of it, else the clean instrument of this session
        PushRecord? old = null;
        foreach (var r in st.Records)
            if (r.Kind == PushKind.OpnDac && (r.Index == e.Index && SameStart(r.B, cur.Data) || SameStart(r.A, cur.Data) || SameStart(r.B, cur.Data))) old = r;
        byte[] origData; int origRate, origLoop, origLs, origLe;
        if (old != null) { origData = old.A; origRate = old.P[0]; origLoop = old.P[5]; origLs = old.P[6]; origLe = old.P[7]; }
        else if (clean != null && clean.Kind == InstrumentKind.OpnDac)
        {
            origData = clean.Blobs[0].Data; origRate = clean.Get("dac.rate");
            origLoop = clean.Get("dac.loop"); origLs = clean.Get("dac.loop.start"); origLe = clean.Get("dac.loop.end");
        }
        else return PushOutcome.Refused("nothing to push: this sound has not been edited");
        if (origData.Length < 64) return PushOutcome.Refused("this DAC sound is too short to be recognised in the game's stream of samples");
        var edit = cur.Data;
        int editLoop = cur.Loops ? 1 : 0;
        bool same = origData.AsSpan().SequenceEqual(edit) && origRate == cur.Rate && editLoop == origLoop && (editLoop == 0 || (cur.LoopStart == origLs && cur.LoopEnd == origLe));
        if (same)
        {
            if (old != null)
            {
                var rm = Remove(m, st, old);
                rm.Message = $"DAC sound {idx + 1} is as the game had it again, so the earlier push was taken out: " + rm.Message;
                return rm;
            }
            return PushOutcome.Refused($"nothing to push: DAC sound {idx + 1} is as the game has it. Edit it first");
        }
        var rec = new PushRecord
        {
            Kind = PushKind.OpnDac, Index = e.Index, Name = $"DAC sound {idx + 1}", A = (byte[])origData.Clone(), B = (byte[])edit.Clone(),
            P = [origRate, cur.Rate, editLoop, cur.Loops ? cur.LoopStart : 0, cur.Loops ? cur.LoopEnd : 0, origLoop, origLs, origLe],
        };
        var o = new PushOutcome { Ok = true };
        if (old != null) st.Records.Remove(old);
        st.Records.Add(rec);
        PushHooks.Install(m, st);
        o.Poison = "an instrument was pushed into the sound unit as a DAC stream substitution (not a memory write a replay from power-on could repeat)";
        int diff = 0;
        for (int i = 0; i < Math.Min(origData.Length, edit.Length); i++) if (origData[i] != edit[i]) diff++;
        diff += Math.Abs(origData.Length - edit.Length);
        o.Message = $"Pushed DAC sound {idx + 1} ({diff} samples differ{(origRate != cur.Rate ? $", rate {origRate} to {cur.Rate} Hz" : "")}): when the game's driver streams this sound to the DAC, the edited samples are handed to the chip instead, at the driver's pace (the first {12} samples are heard as the game has them; "
            + "the sound ends where the driver ends it). Remove from game undoes it.";
        return o;
    }

    private static bool SameStart(byte[] a, byte[] b) => a.Length >= 12 && b.Length >= 12 && a.AsSpan(0, 12).SequenceEqual(b.AsSpan(0, 12));

    public static PushOutcome Remove(PushMachine m, GamePushes st, PushRecord rec)
    {
        int restored = 0;
        if (rec.Kind == PushKind.OpnPatch && m.MdUnit != null && st.Opn != null) restored = st.Opn.RestoreChannels(m.MdUnit.Fm, rec);
        st.Records.Remove(rec);
        if (!PushMutants.On(PushMutants.RemoveLeavesOverrides)) PushHooks.Install(m, st);
        string what = rec.Kind == PushKind.OpnDac ? "the game's own sound plays again from the next time it is streamed"
            : restored > 0 ? $"the original patch is back on {restored} channel{(restored == 1 ? "" : "s")} and no key-on loads the edit any more"
            : "no key-on loads the edit any more (no channel held the edited registers)";
        return new PushOutcome { Ok = true, Message = $"Removed {rec.Name} from the game: {what}.", Poison = "pushed instruments were removed from the sound unit" };
    }
}

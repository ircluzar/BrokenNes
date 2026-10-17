// Round 6 (inject): pushing into the other sound chips, as far as they can take it.
//   * Game Boy wave RAM (16 bytes, the wave channel's waveform): written into the unit now, and substituted again whenever the wave channel is triggered while its RAM is the original (a driver that loads a wave per instrument change
//     writes it again). Pulse and noise settings are refused: the driver writes them at every note and no trigger hook is built for them.
//   * The YM2413's user instrument (eight registers): substituted at every key-on of a channel that plays it while the eight registers are the original ones.
//   * The PSG, the NES, a ROM instrument: refused with the reason (GamePush.WhyNot).
using NesEmulator.Gb;
using NesEmulator.Sega;

namespace BrokenNes2.Instruments;

internal sealed class GbPushHook : IGbApuOverride
{
    private readonly byte[][] orig, edit;

    public GbPushHook(IReadOnlyList<PushRecord> records)
    {
        var o = new List<byte[]>(); var e = new List<byte[]>();
        foreach (var r in records)
            if (r.Kind == PushKind.GbWave && r.A.Length == 16 && r.B.Length == 16) { o.Add(r.A); e.Add(r.B); }
        orig = o.ToArray(); edit = e.ToArray();
    }

    public void OnTrigger(APU_GB apu, int channel)
    {
        if (channel != 2 || PushMutants.On(PushMutants.GbNoSubstitution)) return;
        var w = apu.WaveRam;
        for (int k = 0; k < orig.Length; k++)
            if (w.AsSpan().SequenceEqual(orig[k])) { edit[k].CopyTo(w, 0); return; }
    }
}

internal sealed class OpllPushHook : IYm2413Override
{
    private readonly byte[][] orig, edit;

    public OpllPushHook(IReadOnlyList<PushRecord> records)
    {
        var o = new List<byte[]>(); var e = new List<byte[]>();
        foreach (var r in records)
            if (r.Kind == PushKind.OpllUser && r.A.Length == 8 && r.B.Length == 8) { o.Add(r.A); e.Add(r.B); }
        orig = o.ToArray(); edit = e.ToArray();
    }

    public void OnKeyOn(Ym2413 chip, int channel)
    {
        if (PushMutants.On(PushMutants.OpllNoSubstitution)) return;
        for (int k = 0; k < orig.Length; k++)
        {
            bool same = true;
            for (int i = 0; i < 8 && same; i++) same = chip.Register(i) == orig[k][i];
            if (!same) continue;
            for (int i = 0; i < 8; i++) if (chip.Register(i) != edit[k][i]) chip.WriteRegister(i, edit[k][i]);
            return;
        }
    }
}

internal static class OtherPushers
{
    // ---- Game Boy wave ----

    public static PushOutcome PushGbWave(PushMachine m, GamePushes st, Instrument e, Instrument? clean)
    {
        var apu = m.GbApu;
        if (apu == null) return PushOutcome.Refused(m.Gb == null ? "this is not a Game Boy game" : $"cannot push on this Game Boy game: it runs on the {m.Gb.Apu.CoreName} sound core, and a push needs the GB audio unit's wave RAM");
        var blob = e.Blob("wave");
        if (blob == null || blob.Data.Length != 16) return PushOutcome.Refused("this is not the Game Boy wave");
        var edit = (byte[])blob.Data.Clone();
        var old = st.Find(PushKind.GbWave, e.Index);
        byte[] live = (byte[])apu.WaveRam.Clone();
        byte[] orig = old != null ? old.A : clean?.Blob("wave")?.Data is { Length: 16 } cd ? (byte[])cd.Clone() : live;
        if (orig.AsSpan().SequenceEqual(edit))
        {
            if (old != null)
            {
                var rm = RemoveGbWave(m, st, old);
                rm.Message = "the wave is as the game had it again, so the earlier push was taken out: " + rm.Message;
                return rm;
            }
            return PushOutcome.Refused("nothing to push: the wave is as the game has it. Edit it first");
        }
        var rec = new PushRecord { Kind = PushKind.GbWave, Index = e.Index, Name = "Game Boy wave", A = orig, B = edit };
        rec.Regions.Add(new PushRegion { Domain = "WAVE", Address = 0, Original = orig, Pushed = edit });
        var o = new PushOutcome { Ok = true };
        for (int i = 0; i < 16; i++) if (apu.WaveRam[i] != edit[i]) o.Writes.Add(("WAVE", i, edit[i]));
        if (!PushMutants.On(PushMutants.GbNoSubstitution)) edit.CopyTo(apu.WaveRam, 0);
        if (old != null) st.Records.Remove(old);
        st.Records.Add(rec);
        PushHooks.Install(m, st);
        o.Poison = "an instrument was pushed into the sound unit as a trigger substitution (not a memory write a replay from power-on could repeat)";
        int diff = 0; for (int i = 0; i < 16; i++) if (orig[i] != edit[i]) diff++;
        o.Message = $"Pushed the Game Boy wave ({diff} of 16 bytes differ): written into the wave RAM now, and written again whenever the wave channel is triggered while the RAM holds the game's original wave (the driver reloads waves when it changes instrument). Remove from game undoes it.";
        return o;
    }

    public static PushOutcome RemoveGbWave(PushMachine m, GamePushes st, PushRecord rec)
    {
        string state = "there is no wave RAM to look at";
        var apu = m.GbApu;
        if (apu != null)
        {
            if (apu.WaveRam.AsSpan().SequenceEqual(rec.B) && !PushMutants.On(PushMutants.RemoveLeavesBytes)) { rec.A.CopyTo(apu.WaveRam, 0); state = "the original wave is back in the wave RAM"; }
            else if (apu.WaveRam.AsSpan().SequenceEqual(rec.A)) state = "the wave RAM already holds the game's own wave";
            else state = "the wave RAM holds another wave now (the game loaded one since), so it was left alone";
        }
        st.Records.Remove(rec);
        PushHooks.Install(m, st);
        return new PushOutcome { Ok = true, Message = $"Removed the Game Boy wave from the game: {state}, and no trigger writes the edit any more.", Poison = "pushed instruments were removed from the sound unit" };
    }

    // ---- YM2413 user instrument ----

    public static PushOutcome PushOpll(PushMachine m, GamePushes st, SegaSnapshot? work, Instrument e, Instrument? clean)
    {
        var fm = m.SmsFm?.Fm;
        if (fm == null) return PushOutcome.Refused("this game has no YM2413: the Master System's FM unit is not in use (choose the FM sound chip, and the game must use it)");
        if (work == null || work.Kind != SegaSnapshot.KindOpll) return PushOutcome.Refused("there is no frozen copy of the YM2413 to push from");
        var inCopy = new SegaInstruments(work).Read(0);
        if (inCopy == null || !inCopy.SameValues(e)) return PushOutcome.Refused("the instrument shown is not the one in the frozen copy of the sound chips (an edit did not reach it); make the edit again, then push");
        var edit = work.Opll[..8];
        var old = st.Find(PushKind.OpllUser, 0);
        byte[] orig;
        if (old != null) orig = old.A;
        else if (clean != null && clean.Kind == InstrumentKind.OpllUser)
        {
            var cleanSnap = new SegaInstruments(work).Write(clean);
            if (!cleanSnap.Ok || cleanSnap.Snapshot is not SegaSnapshot cs) return PushOutcome.Refused("the clean instrument could not be rebuilt: " + cleanSnap.Reason);
            orig = cs.Opll[..8];
        }
        else { orig = new byte[8]; for (int i = 0; i < 8; i++) orig[i] = fm.Register(i); }
        if (orig.AsSpan().SequenceEqual(edit))
        {
            if (old != null)
            {
                var rm = RemoveOpll(m, st, old);
                rm.Message = "the user instrument is as the game had it again, so the earlier push was taken out: " + rm.Message;
                return rm;
            }
            return PushOutcome.Refused("nothing to push: the user instrument is as the game has it. Edit it first");
        }
        var rec = new PushRecord { Kind = PushKind.OpllUser, Index = 0, Name = "YM2413 user instrument", A = (byte[])orig.Clone(), B = (byte[])edit.Clone() };
        var o = new PushOutcome { Ok = true };
        if (old != null) st.Records.Remove(old);
        st.Records.Add(rec);
        PushHooks.Install(m, st);
        o.Poison = "an instrument was pushed into the sound unit as a key-on substitution (not a memory write a replay from power-on could repeat)";
        int diff = 0; for (int i = 0; i < 8; i++) if (orig[i] != edit[i]) diff++;
        o.Message = $"Pushed the YM2413 user instrument ({diff} of 8 registers differ): at every key-on of a channel that plays the user instrument while its registers are the game's original ones, the edited ones are loaded first. Only the user instrument (the game's own instrument 0) can change; the chip's fifteen ROM instruments cannot. Remove from game undoes it.";
        return o;
    }

    public static PushOutcome RemoveOpll(PushMachine m, GamePushes st, PushRecord rec)
    {
        string state = "there is no YM2413 to look at";
        var fm = m.SmsFm?.Fm;
        if (fm != null)
        {
            bool isEdit = true, isOrig = true;
            for (int i = 0; i < 8; i++) { isEdit &= fm.Register(i) == rec.B[i]; isOrig &= fm.Register(i) == rec.A[i]; }
            if (isEdit && !isOrig) { for (int i = 0; i < 8; i++) if (fm.Register(i) != rec.A[i]) fm.WriteRegister(i, rec.A[i]); state = "the original instrument registers are back in the chip"; }
            else if (isOrig) state = "the chip's registers are the game's own";
            else state = "the chip's user instrument registers hold something else now (the game loaded another instrument since), so they were left alone";
        }
        st.Records.Remove(rec);
        PushHooks.Install(m, st);
        return new PushOutcome { Ok = true, Message = $"Removed the YM2413 user instrument from the game: {state}, and no key-on loads the edit any more.", Poison = "pushed instruments were removed from the sound unit" };
    }
}

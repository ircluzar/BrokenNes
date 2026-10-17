// Round 6 (inject): pushing a SNES sample into the running game.
//
// What a SNES instrument is, in the game: a BRR sample in audio RAM (found through the sample directory, whose second word is the loop pointer), and per voice, in the S-DSP's registers, an envelope (ADSR1, ADSR2, GAIN), two volumes, a pitch,
// a sample number (SRCN) and three flags in the global echo / noise / pitch-modulation registers. The SPC700 sound driver writes the registers; it never touches the sample memory once the sounds are loaded.
//   * The sample bytes and the loop pointer: written into the game's audio RAM. They persist until the game loads other sounds there (the driver does not rewrite them).
//   * Envelope, tuning, volume weights and the three flags: the registers are the driver's (measured on Super Mario World: pitch and volumes written at every note, the envelope when a voice changes instrument), so they are not written;
//     DSP_SFC.Overrides replaces them where the DSP reads them, for every voice whose SRCN names the sample (the same data the Runaway overlay holds for FL's notes).
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes2.Instruments;

internal static class SnesPusher
{
    // PushRecord.B: srcn, flags, adsr1, adsr2, gain, mix, mixMask, tune lo, tune hi, weight L, weight R
    public const int EntryBytes = 11;

    public static byte[] Pack(DspVoiceOverrides.Entry e) =>
        [e.Srcn, e.Flags, e.Adsr1, e.Adsr2, e.Gain, e.Mix, e.MixMask, (byte)e.TuneCents, (byte)(e.TuneCents >> 8), (byte)e.WeightL, (byte)e.WeightR];

    public static DspVoiceOverrides.Entry EntryOf(PushRecord r)
    {
        var b = r.B;
        if (b.Length != EntryBytes) return default;
        return new DspVoiceOverrides.Entry { Srcn = b[0], Flags = b[1], Adsr1 = b[2], Adsr2 = b[3], Gain = b[4], Mix = b[5], MixMask = b[6], TuneCents = (short)(b[7] | b[8] << 8), WeightL = (sbyte)b[9], WeightR = (sbyte)b[10] };
    }

    /// <summary>The overrides the Runaway overlay holds for a sample, in the form the DSP takes: only what the edit changed (an envelope, a tuning, volume weights, and the echo / noise / modulation flags that differ from the default).</summary>
    public static DspVoiceOverrides.Entry FromOverlay(SpcSnapshot work, int slot)
    {
        var ov = work.Overlay;
        int oi = ov?.IndexOf(slot) ?? -1;
        if (oi < 0) return default;
        var oe = ov!.At(oi);
        var e = new DspVoiceOverrides.Entry { Srcn = (byte)slot };
        if ((oe.Flags & SnesOverlay.HasEnv) != 0 && !PushMutants.On(PushMutants.SnesForgetsEnvelope)) { e.Flags |= DspVoiceOverrides.HasEnv; e.Adsr1 = oe.Adsr1; e.Adsr2 = oe.Adsr2; e.Gain = oe.Gain; }
        if ((oe.Flags & SnesOverlay.HasTune) != 0 && oe.Tune != 0 && !PushMutants.On(PushMutants.SnesDropsTuning)) { e.Flags |= DspVoiceOverrides.HasTune; e.TuneCents = oe.Tune; }
        if ((oe.Flags & SnesOverlay.HasVol) != 0 && !PushMutants.On(PushMutants.SnesDropsVolume)) { e.Flags |= DspVoiceOverrides.HasVol; e.WeightL = oe.WeightL; e.WeightR = oe.WeightR; }
        if ((oe.Flags & SnesOverlay.HasMix) != 0 && !PushMutants.On(PushMutants.SnesDropsMix))
        {
            // the Runaway default is echo as the game used it, noise and modulation off: only the flags that differ from it are the user's
            int def = work.Dsp[0x4D] != 0 ? SnesOverlay.MixEcho : 0;
            int mask = (oe.Mix ^ def) & 7;
            if (mask != 0) { e.Flags |= DspVoiceOverrides.HasMix; e.Mix = (byte)(oe.Mix & 7); e.MixMask = (byte)mask; }
        }
        return e;
    }

    /// <summary>A fresh copy of the game's sound memory (Instrument Runaway starting again) carries what was pushed: the Runaway overlay gets the same envelope, tuning, weights and flags the game is forced to use, so FL's notes and the game agree
    /// (the sample bytes are in the audio RAM already). Without pushes the snapshot is returned as it is.</summary>
    public static SpcSnapshot Carry(IConsoleSession session, SpcSnapshot raw)
    {
        var st = GamePushes.Peek(session);
        if (st == null || st.Count == 0) return raw;
        SnesOverlay? ov = null;
        foreach (var r in st.Records)
        {
            if (r.Kind != PushKind.SnesSample) continue;
            var ent = EntryOf(r);
            var oe = new SnesOverlay.Entry { Srcn = (byte)r.Index };
            if ((ent.Flags & DspVoiceOverrides.HasEnv) != 0) { oe.Flags |= SnesOverlay.HasEnv; oe.Adsr1 = ent.Adsr1; oe.Adsr2 = ent.Adsr2; oe.Gain = ent.Gain; }
            if ((ent.Flags & DspVoiceOverrides.HasTune) != 0) { oe.Flags |= SnesOverlay.HasTune; oe.Tune = ent.TuneCents; }
            if ((ent.Flags & DspVoiceOverrides.HasVol) != 0) { oe.Flags |= SnesOverlay.HasVol; oe.WeightL = ent.WeightL; oe.WeightR = ent.WeightR; }
            if ((ent.Flags & DspVoiceOverrides.HasMix) != 0)
            {
                int def = raw.Dsp[0x4D] != 0 ? SnesOverlay.MixEcho : 0;
                oe.Flags |= SnesOverlay.HasMix; oe.Mix = (byte)((def & ~ent.MixMask | ent.Mix & ent.MixMask) & 7);
            }
            var sample = r.Regions.Count > 0 ? r.Regions[0] : null;
            if (sample != null && sample.Pushed.Length % 9 == 0)
            {
                int blocks = sample.Pushed.Length / 9;
                if (!(raw.WellFormed(r.Index, out int found, out _) && found == blocks && raw.SampleStart(r.Index) == sample.Address)) { oe.Flags |= SnesOverlay.Pinned; oe.Blocks = (ushort)blocks; }
            }
            if (oe.Flags != 0) ov = SnesOverlay.With(ov, oe);
        }
        return ov == null ? raw : raw.WithOverlay(ov);
    }

    private static string Signed(int v) => v > 0 ? "+" + v : v.ToString();

    public static PushOutcome Push(PushMachine m, GamePushes st, SpcSnapshot? work, Instrument e)
    {
        if (m.Snes == null) return PushOutcome.Refused("this game is not a SNES game");
        var spc = m.Spc;
        if (spc == null) return PushOutcome.Refused($"cannot push on this SNES game: it runs on the {m.Snes.Apu.CoreName} sound core, and a push needs the SFC audio unit (its audio RAM and S-DSP)");
        if (work == null) return PushOutcome.Refused("there is no frozen copy of the SNES sound memory to push from");
        int slot = e.Index;
        if (e.Blobs.Length != 1) return PushOutcome.Refused("this is not a SNES sample");

        // the instrument on the tab must be the one in the frozen copy, or the overrides below (taken from the copy) would not be what the tab shows
        var inCopy = new SnesInstruments(work).Read(slot);
        if (inCopy == null) return PushOutcome.Refused($"the frozen copy has no sample {slot}");
        if (!inCopy.SameValues(e)) return PushOutcome.Refused("the instrument shown is not the one in the frozen copy of the sound memory (an edit did not reach it); make the edit again, then push");

        var aram = spc.Aram;
        var dsp = spc.Dsp;
        int dir = dsp.Regs[0x5D] * 0x100;
        if (dir != work.DirectoryBase) return PushOutcome.Refused($"the game's sample directory moved from ${work.DirectoryBase:X4} to ${dir:X4} since Instrument Runaway froze it: freeze again, then edit and push");
        int entry = (dir + slot * 4) & 0xFFFF;
        int liveStart = aram[entry] | aram[(entry + 1) & 0xFFFF] << 8;
        var data = e.Blobs[0].Data;
        if (liveStart != e.Origin) return PushOutcome.Refused($"the game's directory now puts sample {slot} at ${liveStart:X4}, not ${e.Origin:X4} where the frozen copy has it: the game loaded other sounds; freeze again, then edit and push");
        if (data.Length == 0 || data.Length % 9 != 0 || e.Origin + data.Length > 0x10000) return PushOutcome.Refused("the sample does not fit audio RAM");

        var old = st.Find(PushKind.SnesSample, slot);
        var rec = new PushRecord { Kind = PushKind.SnesSample, Index = slot, Name = $"SNES sample {slot}" };
        var o = new PushOutcome { Ok = true };

        // ---- the memory: the sample and the loop pointer ----
        int loop = e.Get("loop.addr");
        byte[] loopBytes = [(byte)loop, (byte)(loop >> 8)];
        var regions = new (int Address, byte[] Pushed)[] { (e.Origin, (byte[])data.Clone()), ((entry + 2) & 0xFFFF, loopBytes) };
        int changedBytes = 0;
        foreach (var (addr, pushed) in regions)
        {
            if (addr + pushed.Length > 0x10000) return PushOutcome.Refused("the loop pointer would run past the end of audio RAM");
            var live = aram.AsSpan(addr, pushed.Length).ToArray();
            // the original is what the game had before ANY push of this instrument: kept from an earlier push while the game's memory still holds what that push wrote
            byte[] original = live;
            var prev = old?.Regions.FirstOrDefault(r => r.Address == addr && r.Pushed.Length == pushed.Length);
            if (prev != null && live.AsSpan().SequenceEqual(prev.Pushed)) original = prev.Original;
            rec.Regions.Add(new PushRegion { Domain = "ARAM", Address = addr, Original = original, Pushed = pushed });
            for (int i = 0; i < pushed.Length; i++)
                if (live[i] != pushed[i]) { o.Writes.Add(("ARAM", addr + i, pushed[i])); changedBytes++; }
        }
        bool memoryDiffers = rec.Regions.Any(r => !r.Original.AsSpan().SequenceEqual(r.Pushed));

        // ---- the overrides ----
        var ent = FromOverlay(work, slot);
        rec.B = Pack(ent);
        rec.P = [slot];

        if (!memoryDiffers && ent.Flags == 0)
        {
            if (old != null)
            {
                var rm = Remove(m, st, old);
                rm.Message = $"sample {slot} is as the game had it again, so the earlier push was taken out: " + rm.Message;
                return rm;
            }
            return PushOutcome.Refused($"nothing to push: sample {slot} is as the game has it. Edit it first (the sample bytes, loop pointer, envelope, tuning, volumes or flags)");
        }

        // ---- apply ----
        foreach (var (addr, pushed) in regions) if (!(PushMutants.On(PushMutants.SnesDropsSample) && addr == e.Origin)) Array.Copy(pushed, 0, aram, addr, pushed.Length);
        if (old != null) st.Records.Remove(old);
        st.Records.Add(rec);
        PushHooks.Install(m, st);
        if (ent.Flags != 0) o.Poison = "an instrument was pushed into the sound unit as a voice override (not a memory write a replay from power-on could repeat)";

        // ---- the words ----
        var parts = new List<string>();
        if (memoryDiffers)
        {
            int sampleDiff = 0;
            var r0 = rec.Regions[0];
            for (int i = 0; i < r0.Pushed.Length; i++) if (r0.Original[i] != r0.Pushed[i]) sampleDiff++;
            bool loopDiff = !rec.Regions[1].Original.AsSpan().SequenceEqual(rec.Regions[1].Pushed);
            parts.Add($"{(sampleDiff > 0 ? $"{sampleDiff} sample bytes" : "")}{(sampleDiff > 0 && loopDiff ? " and " : "")}{(loopDiff ? "the loop pointer" : "")} written into the game's audio RAM (they stay until it loads other sounds)");
        }
        var forced = new List<string>();
        if ((ent.Flags & DspVoiceOverrides.HasEnv) != 0) forced.Add($"envelope ${ent.Adsr1:X2} ${ent.Adsr2:X2} ${ent.Gain:X2}");
        if ((ent.Flags & DspVoiceOverrides.HasTune) != 0) forced.Add($"tuning {Signed(ent.TuneCents)} cents");
        if ((ent.Flags & DspVoiceOverrides.HasVol) != 0) forced.Add($"volume weights {ent.WeightL}/{ent.WeightR}");
        if ((ent.Flags & DspVoiceOverrides.HasMix) != 0)
        {
            var f = new List<string>();
            if ((ent.MixMask & DspVoiceOverrides.MixEcho) != 0) f.Add("echo " + ((ent.Mix & DspVoiceOverrides.MixEcho) != 0 ? "on" : "off"));
            if ((ent.MixMask & DspVoiceOverrides.MixNoise) != 0) f.Add("noise " + ((ent.Mix & DspVoiceOverrides.MixNoise) != 0 ? "on" : "off"));
            if ((ent.MixMask & DspVoiceOverrides.MixPmod) != 0) f.Add("pitch modulation " + ((ent.Mix & DspVoiceOverrides.MixPmod) != 0 ? "on" : "off"));
            forced.Add(string.Join(", ", f));
        }
        if (forced.Count > 0) parts.Add($"forced on every voice playing it, whatever the driver writes: {string.Join(", ", forced)}");
        o.Message = $"Pushed sample {slot}: " + string.Join("; ", parts) + ".";
        return o;
    }

    public static PushOutcome Remove(PushMachine m, GamePushes st, PushRecord rec)
    {
        var o = new PushOutcome { Ok = true };
        var aram = m.Spc?.Aram;
        int restored = 0, replaced = 0;
        if (aram != null && !PushMutants.On(PushMutants.RemoveLeavesBytes))
            foreach (var r in rec.Regions)
            {
                if (r.Address < 0 || r.Address + r.Pushed.Length > 0x10000) continue;
                for (int i = 0; i < r.Pushed.Length; i++)
                {
                    byte live = aram[r.Address + i];
                    if (live == r.Pushed[i])
                    {
                        if (r.Original[i] != live) { aram[r.Address + i] = r.Original[i]; o.Writes.Add(("ARAM", r.Address + i, r.Original[i])); restored++; }
                    }
                    else if (live != r.Original[i]) replaced++;          // the game wrote something else there since: not ours to undo
                }
            }
        st.Records.Remove(rec);
        if (!PushMutants.On(PushMutants.RemoveLeavesOverrides)) PushHooks.Install(m, st);
        var bits = new List<string>();
        if (restored > 0) bits.Add($"{restored} bytes put back in audio RAM");
        if (replaced > 0) bits.Add($"{replaced} bytes left alone (the game has written over them since)");
        if (rec.B.Length == EntryBytes && rec.B[1] != 0) bits.Add("the voice overrides removed");
        o.Message = $"Removed sample {rec.Index} from the game" + (bits.Count > 0 ? ": " + string.Join(", ", bits) : "") + ".";
        return o;
    }
}

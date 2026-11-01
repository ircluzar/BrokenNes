using System;
using System.Collections.Generic;

namespace NesEmulator.Snes;

/// <summary>
/// An optional table of per-sample overrides for the S-DSP (<see cref="DSP_SFC.Overrides"/>), keyed on the sample number (SRCN) a voice plays: while a voice's SRCN register names a sample that has an entry, the DSP uses the
/// entry's envelope (ADSR1 / ADSR2 / GAIN), pitch scaled by a tuning in cents, left / right volume weights and echo / noise / pitch-modulation flags INSTEAD of what the sound driver wrote to the voice's registers. The
/// registers themselves are never changed (a driver that reads them back sees its own values), the table is looked up every sample from the live SRCN register (so it needs no state of its own: a saved machine plays the same
/// with the same table), and a DSP with no table (the default, <c>null</c>) runs exactly as it always did.
/// <para>It exists for the plugin's "push an edited instrument into the running game": a SNES sound driver rewrites a voice's envelope, volumes and pitch at every note, so an edit written into audio RAM cannot hold them; the
/// override is applied where the DSP reads them. Immutable once built; allocation-free to query.</para>
/// </summary>
public sealed class DspVoiceOverrides
{
    public const int MaxEntries = 256, MaxTuneCents = 4800;
    public const byte HasEnv = 1, HasTune = 2, HasVol = 4, HasMix = 8;
    public const byte MixEcho = 1, MixNoise = 2, MixPmod = 4;

    public struct Entry
    {
        /// <summary>Mix: the forced values of the echo / noise / pitch-modulation flags (bit 0, 1, 2); MixMask: which of them are forced at all.</summary>
        public byte Srcn, Flags, Adsr1, Adsr2, Gain, Mix, MixMask;
        /// <summary>Cents added to the voice's pitch.</summary>
        public short TuneCents;
        /// <summary>Weights of the voice's volume registers: 127 leaves a volume as it is, 0 silences it, a negative weight flips the phase.</summary>
        public sbyte WeightL, WeightR;
    }

    private readonly Entry[] entries;
    private readonly int[] pitchQ16;
    private readonly short[] map = new short[256];

    public DspVoiceOverrides(IEnumerable<Entry> items)
    {
        var list = new List<Entry>();
        var seen = new bool[256];
        foreach (var e in items)
        {
            if (seen[e.Srcn]) { list.RemoveAll(x => x.Srcn == e.Srcn); }     // the later entry for a sample wins
            seen[e.Srcn] = true;
            list.Add(e);
        }
        entries = list.ToArray();
        pitchQ16 = new int[entries.Length];
        Array.Fill(map, (short)-1);
        for (int i = 0; i < entries.Length; i++)
        {
            map[entries[i].Srcn] = (short)i;
            // 2^(cents/1200) as a 16.16 number, rounded once here: the per-sample work is integer only
            pitchQ16[i] = (entries[i].Flags & HasTune) != 0 ? (int)Math.Round(Math.Pow(2.0, Math.Clamp((int)entries[i].TuneCents, -MaxTuneCents, MaxTuneCents) / 1200.0) * 65536.0) : 65536;
        }
    }

    public int Count => entries.Length;
    public Entry At(int i) => entries[i];
    public IReadOnlyList<Entry> Entries => entries;

    /// <summary>The entry for a sample number, or -1.</summary>
    public int Find(int srcn) => map[srcn & 0xFF];

    /// <summary>Applies entry <paramref name="i"/> to a voice's values for this sample. <paramref name="volL"/> and <paramref name="volR"/> are the signed volume registers; the flags are the voice's bits in EON / NON / PMON.</summary>
    public void Apply(int i, ref int pitch, ref int volL, ref int volR, ref int adsr1, ref int adsr2, ref int gain, ref bool pmod, ref bool noise, ref bool echo)
    {
        ref readonly var e = ref entries[i];
        byte f = e.Flags;
        if ((f & HasEnv) != 0) { adsr1 = e.Adsr1; adsr2 = e.Adsr2; gain = e.Gain; }
        if ((f & HasTune) != 0 && pitch != 0) pitch = Math.Clamp((int)(((long)pitch * pitchQ16[i] + 32768) >> 16), 1, 0x3FFF);
        if ((f & HasVol) != 0)
        {
            volL = Math.Clamp(volL * e.WeightL / 127, -128, 127);
            volR = Math.Clamp(volR * e.WeightR / 127, -128, 127);
        }
        if ((f & HasMix) != 0)
        {
            // only the flags the entry names are forced (<see cref="Entry.MixMask"/>); the others stay as the driver set them
            if ((e.MixMask & MixEcho) != 0) echo = (e.Mix & MixEcho) != 0;
            if ((e.MixMask & MixNoise) != 0) noise = (e.Mix & MixNoise) != 0;
            if ((e.MixMask & MixPmod) != 0) pmod = (e.Mix & MixPmod) != 0;
        }
    }
}

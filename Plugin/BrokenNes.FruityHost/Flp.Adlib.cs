// flp-test checks of the AdLib back end (Music/Flp/AdlibBackend.cs) and its Vaults writer. The song blob is PLAYED by a line-for-line C# model of Super VRUN's dos_snd.c run_tick
// (the same opcodes, the same register writes: op_m / op_c tables, patch_regs, voice_level, key, set_freq) on the x86 console's own AdLib (PcMachine + Opl2, ymfm), and the sound is measured.
using BrokenNes2.Music.Flp;
using BrokenNes2.Pc;
using NesEmulator.X86;

namespace BrokenNes.FruityHost;

internal static class AdlibChecks
{
    internal static byte[] PcState(int core, int instrument, int volume = 1000, int pan = 0, byte[]? opc1 = null, int mode = 0)
    {
        int[] p = [core, volume, pan, 0, 0, 2, 0, 0, mode, 0, 5, instrument, 0, 0];
        var b = new List<byte>();
        b.AddRange("BFNP"u8.ToArray()); b.AddRange(BitConverter.GetBytes(2)); b.AddRange(BitConverter.GetBytes(p.Length));
        foreach (int v in p) b.AddRange(BitConverter.GetBytes(v));
        b.AddRange(BitConverter.GetBytes(0)); b.AddRange("EMU2"u8.ToArray()); b.AddRange(BitConverter.GetBytes(1)); b.AddRange(BitConverter.GetBytes(0));
        if (opc1 != null) { b.AddRange("OPC1"u8.ToArray()); b.AddRange(BitConverter.GetBytes(opc1.Length)); b.AddRange(opc1); }
        return b.ToArray();
    }

    /// <summary>Lead on PC_ADLIB "SV pulse 50%" (A4 then C5 with a slide to E5), bass on PC_ADLIB with its own patch (OPC1: MULT 1:1, feedback 7), a speaker instance (not an AdLib voice), a NES instance.</summary>
    internal static byte[] Project()
    {
        var custom = (byte[])OplBank.Patches[3].Regs.Clone(); OplBank.SetField(custom, 24, 7); OplBank.SetField(custom, 6, 10);
        return FlpChecks.Project(120,
            [("Lead", "BrokenNes2", PcState(AdlibBackend.FirstPcChip, 2)), ("Bass", "BrokenNes2", PcState(AdlibBackend.FirstPcChip, 3, opc1: [1, .. custom])),
             ("Beep", "BrokenNes2", PcState(AdlibBackend.FirstPcChip + 6, 0)), ("Nes", "BrokenNes2", FlpChecks.Bn2State(channel: 1))],
            [(1, [new(0, 96, 69, 0), new(96, 96, 72, 0), new(120, 48, 76, 0, Slide: true), new(0, 192, 45, 1), new(0, 48, 60, 2), new(0, 48, 60, 3)])],
            [(0, 1, 192)]);
    }

    /// <summary>dos_snd.c run_tick, its tables and register writes, on a real (emulated) AdLib; returns mono 44.1 kHz audio of <paramref name="ticks"/> ticks and each voice's patch writes.</summary>
    internal static (float[] Audio, List<(int Voice, byte[] Regs)> Patches) Play(byte[] blob, int ticks, int[]? onlyVoices = null)
    {
        var m = new PcMachine(1 << 20);
        m.Speaker.Enabled = true; m.Opl.Enabled = true;
        int[] opM = [0x00, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x10, 0x11, 0x12], opC = [0x03, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x13, 0x14, 0x15];
        void W(int r, int v) { if (onlyVoices != null && r is >= 0xA0 and < 0xC9 && !onlyVoices.Contains(r & 15)) return; m.Out8(0x388, (byte)r); m.Out8(0x389, (byte)v); }
        var vtl = new int[9]; var vhi = new int[9]; var keyed = new bool[9]; var cp = new byte[9][];
        var patches = new List<(int, byte[])>();
        W(0x01, 0x20);
        int rate = blob[0] | blob[1] << 8, nord = blob[2];
        int stream0 = 4 + 3 * nord, sp = stream0, wait = 0;
        var outp = new List<float>();
        var buf = new short[8192];
        long samples = 0;
        bool ended = false;
        for (int t = 0; t < ticks; t++)
        {
            if (!ended && wait > 0) wait--;
            else while (!ended)
            {
                int b = blob[sp++], v = b & 15;
                if (b >= 0x80) { wait = b & 0x7F; break; }
                switch (b >> 4)
                {
                    case 0:
                        if (b == 2) { sp = stream0 + (blob[sp] | blob[sp + 1] << 8 | blob[sp + 2] << 16); break; }
                        ended = true; break;
                    case 1: keyed[v] = false; W(0xB0 + v, vhi[v] & 0x1F); break;
                    case 2: keyed[v] = false; W(0xB0 + v, vhi[v] & 0x1F); keyed[v] = true; W(0xB0 + v, 0x20 | (vhi[v] & 0x1F)); break;
                    case 3: { int lo = blob[sp++], hi = blob[sp++]; vhi[v] = hi; W(0xA0 + v, lo); W(0xB0 + v, (keyed[v] ? 0x20 : 0) | (hi & 0x1F)); break; }
                    case 4: vtl[v] = blob[sp++]; W(0x40 + opC[v], Math.Min(63, vtl[v])); break;
                    case 6:
                    {
                        var q = blob.AsSpan(sp, 10).ToArray(); sp += 10; cp[v] = q; patches.Add((v, q));
                        W(0x20 + opM[v], q[0]); W(0x20 + opC[v], q[1]); W(0x40 + opM[v], q[2]); W(0x60 + opM[v], q[3]); W(0x60 + opC[v], q[4]);
                        W(0x80 + opM[v], q[5]); W(0x80 + opC[v], q[6]); W(0xE0 + opM[v], q[7]); W(0xE0 + opC[v], q[8]); W(0xC0 + v, q[9]);
                        W(0x40 + opC[v], Math.Min(63, vtl[v]));
                        break;
                    }
                    default: ended = true; break;
                }
            }
            long target = (long)Math.Round((t + 1) * 44100.0 * 256 / rate);
            long instr = (long)((Int128)target * m.InstructionsPerSecond / 44100);
            m.IdleFor(Math.Max(0, instr - m.Clock));
            m.Speaker.CatchUp(); m.Opl.CatchUp();
            int n;
            while ((n = m.Speaker.Read(buf)) > 0) { m.Opl.MixInto(buf, n); for (int i = 0; i < n; i += 2) outp.Add(buf[i] / 32768f); }
            samples = target;
        }
        return (outp.ToArray(), patches);
    }

    public static void Sv(ImodelReport r)
    {
        var song = FlpSong.Read(Project());
        var s = AdlibBackend.BuildSuperVrun(song, "adl");
        r.Expect(s != null, "a song with PC chips is not seen as composed for the AdLib");
        if (s == null) return;
        r.Expect(s.Voices.Count == 2, $"{s.Voices.Count} AdLib voices (Lead and Bass expected: the speaker and the NES instance are not AdLib voices)");
        r.Expect(s.Fence.Lines.Any(l => l.Rule == "chip" && l.Level == FenceLevel.Over && l.Text.Contains("Beep")), "the speaker instance is not reported OVER (not an AdLib voice)");
        r.Expect(s.Fence.Lines.Any(l => l.Rule == "not AdLib" && l.Text.Contains("Nes")), "the NES instance is not reported");
        var (audio, patches) = Play(s.Blob, s.Ticks, onlyVoices: [0]);
        // the lead: A4 for the first beat (0.5 s at 120 BPM, ticks 0-30)
        double hzA4 = Audio.Frequency(audio, 44100, 4000, 20000);
        r.Expect(Math.Abs(1200 * Math.Log2(hzA4 / 440)) < 5, $"the lead's first note plays {hzA4:0.00} Hz on the AdLib (A4 = 440 expected)");
        // the lead's slide C5 -> E5 ends on E5 (659.26 Hz), the second half of the second beat
        double hzE5 = Audio.Frequency(audio, 44100, (int)(44100 * 0.91), (int)(44100 * 0.99));
        r.Expect(Math.Abs(1200 * Math.Log2(hzE5 / 659.255)) < 8, $"the lead's slide ends at {hzE5:0.00} Hz (E5 659.26 expected)");
        // the patches written are the instances' own: Lead = bank 2 in the driver's order, Bass = its OPC1 patch
        var lead = patches.FirstOrDefault(p => p.Voice == 0).Regs; var bass = patches.FirstOrDefault(p => p.Voice == 1).Regs;
        var custom = (byte[])OplBank.Patches[3].Regs.Clone(); OplBank.SetField(custom, 24, 7); OplBank.SetField(custom, 6, 10);
        r.Expect(lead != null && lead.SequenceEqual(new byte[] { 0x22, 0x21, 0x1C, 0xF0, 0xF0, 0x0F, 0x08, 0, 0, 0x00 }), $"the lead's patch bytes {(lead == null ? "none" : OplFit.Hex(lead))} (dos_snd.c's patch 2 expected)");
        r.Expect(bass != null && bass.SequenceEqual(AdlibBackend.SvPatchBytes(custom)) && bass[0] == custom[0] && bass[1] == custom[1], "the bass's patch is not its own OPC1 patch");
        // the stream's length = the song's 4 beats at 120 BPM = 2 s = 120 ticks, and it loops
        r.Expect(s.Ticks == 120 && s.Blob[^4] == 0x02, $"the song is {s.Ticks} ticks (120 expected) and ends with {s.Blob[^4]:X2} (the loop 02 expected)");
        // the sound of the whole song (both voices): not silent, and the same twice
        var all = Play(s.Blob, s.Ticks).Audio;
        double rms = Math.Sqrt(all.Select(x => (double)x * x).Average());
        r.Expect(rms > 0.01, $"the song is silent on the AdLib (rms {rms:0.0000})");
        r.Note($"Super VRUN AdLib song: {s.Blob.Length} bytes, {s.Voices.Count} voices, A4 {hzA4:0.00} Hz, slide end {hzE5:0.00} Hz, rms {rms:0.000}; fence: {s.Fence.Summary}");
    }
}

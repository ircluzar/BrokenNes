// The x86 PC console's chips (console 5), Direct mode: this instance plays ONE voice of its own x86 sound device (Pc/PcVoice.cs), monophonic: the newest held FL note sounds,
// a release falls back to the newest still held (legato: a retrigger). Pitch = 6000 + FlCents (MIDI cents) -> Hz; changed pitch = a frequency write (the device's own steps: F-number,
// 8254 divisor, SN76496 period, GUS FC). Velocity = FlNotes.MidiVelocity, added to the patch's level (OPL: 0.75 dB a step; Tandy: 2 dB; GUS: its log volume; DAC: scaled).
// The Instrument parameter picks the patch (OPL: OplBank), the waveform (SB / Covox / GUS) or the mode (Tandy). The patch editor's patch (PcCustomPatch, section OPC1 of the saved state)
// replaces the Instrument's OPL patch while it is set. Test hooks 1100-1139.
using BrokenNes.Fruity;
using BrokenNes2.Pc;
using BrokenNes2.Record;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private volatile PcVoice? pcVoice;
    private Voice? pcHeld;
    private readonly float[] pcMono = new float[Chunk];
    /// <summary>The patch editor's OPL patch (11 bytes), or null: the Instrument parameter's bank patch plays. Saved as section OPC1.</summary>
    internal volatile byte[]? PcCustomPatch;
    internal const uint PcTag = 0x3143504F;            // "OPC1"

    /// <summary>Deliberate bugs for the self-test (hook 1139): 1 = bends ignored, 2 = the custom patch ignored, 3 = velocity ignored.</summary>
    internal static volatile int PcMutant;

    private string? PcApuOf(Emulator? e) => e != null && Emulator.IsPcChip(e.Chip) ? Cx.PcApu(Cx.Chips[e.Chip].Id) : null;

    /// <summary>UI thread: this instance's device, made ahead of its first block (and again for Reset Console or another PC chip).</summary>
    private void PreparePc(bool again = false)
    {
        var apu = PcApuOf(assignment.Emulator);
        if (apu == null) return;
        if (!again && pcVoice?.ApuId == apu) return;
        pcVoice = new PcVoice(apu);
    }

    private byte[] PcPatchRegs(out bool snare)
    {
        var bankPatch = OplBank.Patches[OplBank.Clamp(Get(P.Instrument))];
        snare = bankPatch.Snare;
        return PcCustomPatch is { } c && PcMutant != 2 ? c : bankPatch.Regs;
    }

    private void RenderPcDirect(Emulator emu, int channel, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var apu = PcApuOf(emu)!;
        var pv = pcVoice;
        if (pv == null || pv.ApuId != apu) { pv = new PcVoice(apu); pcVoice = pv; pcHeld = null; }   // (normally made on the UI thread: PreparePc)
        int bank = Cx.PcPatchBank(Cx.Chips[emu.Chip].Id);
        if (bank == 6) pv.SetPatch(PcPatchRegs(out bool sn), sn);
        else if (bank is 7 or 8) pv.SetWave(Cx.PcPatchClamp(bank, Get(P.Instrument)));
        float vol = Get(P.Volume) / 1000f, peak = 0;
        int rate = EmulatorHub.HostRate;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdatePcVoices(pv);
            pv.Render(pcMono.AsSpan(0, n), rate);
            for (int i = 0; i < n; i++)
            {
                float s = pcMono[i] * vol;
                peak = Math.Max(peak, Math.Abs(s));
                dest[(off + i) * 2] = s * gl;
                dest[(off + i) * 2 + 1] = s * gr;
            }
        }
        emu.SetActivity(channel, Math.Min(15, (int)(peak * 40)));
    }

    private double PcHz(Voice v)
    {
        ref var fl = ref v.Params->FinalLevels;
        if (fl.Pitch < v.PitchMin) v.PitchMin = fl.Pitch;
        if (fl.Pitch > v.PitchMax) v.PitchMax = fl.Pitch;
        double cents = 6000.0 + FlCents(fl.Pitch);
        return 440.0 * Math.Pow(2, (cents - 6900.0) / 1200.0);
    }

    private int PcVelocity(Voice v)
    {
        if (PcMutant == 3) return 127;
        float flVel = BitConverter.Int32BitsToSingle(Host.VoiceProcessEvent(v.HostTag, Fpv.GetVelocity, 0, 0));
        int midiVel = FlNotes.MidiVelocity(flVel);
        if (midiVel < 0) midiVel = Math.Clamp((int)Math.Round(v.Params->InitLevels.Vol * 128.0), 0, 127);
        return midiVel;
    }

    /// <summary>The control step: the newest held FL voice is the one the device plays.</summary>
    private void UpdatePcVoices(PcVoice pv)
    {
        Voice? newest = null;
        foreach (var v in voices)
        {
            if (v.Finished) continue;
            if (v.Released) { if (ReferenceEquals(v, pcHeld)) pcHeld = null; Finish(v); continue; }
            if (newest == null || v.Note > newest.Note) newest = v;
        }
        if (pcHeld != null && pcHeld.Finished) pcHeld = null;
        if (newest == null) { if (pv.Keyed) pv.NoteOff(); pcHeld = null; return; }
        if (!ReferenceEquals(newest, pcHeld))
        {
            pcHeld = newest;
            pv.NoteOn(PcHz(newest), PcVelocity(newest));
        }
        else if (PcMutant != 1) pv.Bend(PcHz(newest));
    }

    // ---- the patch editor's state (section OPC1: version 1, the 11 patch bytes) ----

    internal byte[]? PcSectionBytes() => PcCustomPatch is { } c ? [1, .. c] : null;

    internal static byte[]? ReadPcSection(byte[] b) => b.Length == 1 + OplPatch.Size && b[0] == 1 ? b[1..] : null;

    /// <summary>The editor: sets field <paramref name="field"/> (OplBank.Fields) of the custom patch, starting from the Instrument's patch when there is none yet. Returns the value now held.</summary>
    internal int PcEditField(int field, int value)
    {
        var regs = (byte[])(PcCustomPatch ?? OplBank.Patches[OplBank.Clamp(Get(P.Instrument))].Regs).Clone();
        OplBank.SetField(regs, field, value);
        PcCustomPatch = regs;
        return OplBank.GetField(regs, field);
    }

    /// <summary>Test hooks 1100-1139: 1100 PC Direct mode, 1101 the index of PC_ADLIB, 1102 OPL patches, 1103 the device keyed, 1104 port writes so far, 1105 set field index to value (returns it),
    /// 1106 field index of the patch in play, 1107 drop the custom patch, 1108 has a custom patch, 1109 the device's 44.1 kHz clock, 1110 the patch bank of the chip, 1139 a deliberate bug (PcMutant).</summary>
    private nint PcTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 1100: return emu?.PcDirectMode == true ? 1 : 0;
            case 1101: return Cx.IndexOfChip("PC_ADLIB");
            case 1102: return OplBank.Count;
            case 1103: return pcVoice?.Keyed == true ? 1 : 0;
            case 1104: return (nint)(pcVoice?.PortWrites ?? -1);
            case 1105: return PcEditField((int)index, (int)value);
            case 1106: return OplBank.GetField(PcPatchRegs(out _), (int)index);
            case 1107: PcCustomPatch = null; return 0;
            case 1108: return PcCustomPatch != null ? 1 : 0;
            case 1109: return (nint)(pcVoice?.Samples ?? -1);
            case 1110: return emu == null ? -1 : Cx.PcPatchBank(Cx.Chips[emu.Chip].Id);
            case 1139: PcMutant = (int)value; return 0;
        }
        return 0;
    }
}

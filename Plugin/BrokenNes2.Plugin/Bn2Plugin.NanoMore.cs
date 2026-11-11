// The later Nano chips (2026-11-11: NANOMD on the Genesis console, then NANOSNES and NANODOS; appended after the x86 PC chips), Direct mode: this instance plays an engine of
// its own, the line-for-line C# twin of a web library under Nano/ (NesEmulator.INanoEngine). The same design as the Nano SMS chip (Bn2Plugin.NanoSms.cs: FL's notes -> the engine
// the way the web library's MIDI player plays a .mid's notes on one MIDI channel, the tools' FlLike class), one path for all of them; the table NanoKinds says what differs.
// Test hooks 1150-1179. The Voice fields NanoId / NanoStarted / NanoNote / NanoBend are shared with the other Nano chips (an instance plays one chip at a time).
using System.Text;
using System.Text.Json;
using BrokenNes.Fruity;
using BrokenNes2.Record;
using NesEmulator;

namespace BrokenNes2;

/// <summary>One of the later Nano chips: its chip id, its name, the sidecar format of its web library, its Instrument patch bank, its presets and its engine.</summary>
public sealed record NanoKind(string Chip, int Console, string Name, string Format, int Bank, Func<int> Count, Func<int, (string Name, string Label, string Kind, string Desc)> Preset, Func<string, int> IndexOf, Func<int, INanoEngine> Create);

public static class NanoKinds
{
    /// <summary>In chip order (append-only: Chip.NanoMore is the 1-based index into this list).</summary>
    public static readonly NanoKind[] All =
    [
        new("NANOMD", Cx.Genesis, "Nano Genesis", "nano-genesis-sidecar", 9,
            () => NesEmulator.Sega.NanoGenesisPresets.Count,
            i => { var p = NesEmulator.Sega.NanoGenesisPresets.All[i]; return (p.Name, p.Label, p.Kind switch { 0 => "FM", 1 => "PSG tone", _ => "PSG noise" }, p.Desc); },
            NesEmulator.Sega.NanoGenesisPresets.IndexOf,
            rate => new NesEmulator.Sega.NanoGenesisEngine(rate)),
        new("NANOSNES", Cx.Snes, "Nano SNES", "nano-snes-sidecar", 10,
            () => NesEmulator.Snes.NanoSnesPresets.Count,
            i => { var p = NesEmulator.Snes.NanoSnesPresets.All[i]; return (p.Name, p.Label, p.Kind == 0 ? "BRR sample" : "noise", p.Desc); },
            NesEmulator.Snes.NanoSnesPresets.IndexOf,
            rate => new NesEmulator.Snes.NanoSnesEngine(rate)),
    ];

    public static NanoKind? OfChip(int chip) => (uint)chip < (uint)Cx.Chips.Count && Cx.Chips[chip].NanoMore is int k and > 0 ? All[k - 1] : null;
    public static NanoKind? OfBank(int bank) { foreach (var k in All) if (k.Bank == bank) return k; return null; }
    public static NanoKind? OfId(string chipId) { foreach (var k in All) if (k.Chip.Equals(chipId, StringComparison.OrdinalIgnoreCase)) return k; return null; }
    public static int Wrap(NanoKind k, int program) { int n = k.Count(); return ((program % n) + n) % n; }
}

public sealed unsafe partial class Bn2Plugin
{
    private volatile INanoEngine? moreEngine;          // made on the UI thread (PrepareNanoMore); the audio thread only makes one after a sample-rate change
    private NanoKind? moreKind;
    private int moreRate;
    private INanoEngine? moreVoicesOf;                 // the engine the voices' note ids belong to
    private int moreNextId;
    private readonly float[] moreL = new float[Chunk], moreR = new float[Chunk];
    private readonly object moreKillGate = new();
    private readonly int[] moreKilled = new int[64];  // notes FL killed without releasing (KillVoice): released on the engine at the next step
    private int moreKilledCount;

    /// <summary>Deliberate bugs for the self-test (hook 1179): 1 = the velocity as FL's float (MIDI velocity / 128, not / 127), 2 = live pitch changes ignored (no bends).</summary>
    internal static volatile int NanoMoreMutant;

    /// <summary>UI thread: this instance's engine, made ahead of its first block (and made again for Reset Console, another Nano chip or a new host rate).</summary>
    private void PrepareNanoMore(bool again = false)
    {
        var kind = NanoKinds.OfChip(assignment.Emulator?.Chip ?? -1);
        if (kind == null) return;
        int rate = EmulatorHub.HostRate;
        if (!again && moreEngine != null && moreRate == rate && ReferenceEquals(moreKind, kind)) return;
        var e = kind.Create(rate);
        moreRate = rate; moreKind = kind;
        moreEngine = e;
    }

    private void BindNanoMoreVoices(INanoEngine e)
    {
        if (ReferenceEquals(moreVoicesOf, e)) return;
        moreVoicesOf = e;
        lock (moreKillGate) moreKilledCount = 0;
        foreach (var v in voices) v.NanoId = 0;
    }

    /// <summary>FL killed a voice (KillVoice) that still held a note of the engine: it is released at the next step (any thread may call this).</summary>
    private void NanoMoreForget(Voice v)
    {
        if (v.NanoId == 0 || moreEngine == null) return;
        lock (moreKillGate) if (moreKilledCount < moreKilled.Length) moreKilled[moreKilledCount++] = v.NanoId;
        v.NanoId = 0;
    }

    /// <summary>Direct mode on a later Nano chip: this instance's notes on its own engine, stereo.</summary>
    private void RenderNanoMoreDirect(Emulator emu, NanoKind kind, int channel, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var e = moreEngine;
        int rate = EmulatorHub.HostRate;
        if (e == null || moreRate != rate || !ReferenceEquals(moreKind, kind)) { e = kind.Create(rate); moreEngine = e; moreRate = rate; moreKind = kind; }    // (a sample-rate change: rare; the first engine is made on the UI thread)
        BindNanoMoreVoices(e);
        float vol = Get(P.Volume) / 1000f, peak = 0;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateNanoMoreVoices(e);
            e.Render(moreL, moreR, 0, n);
            for (int i = 0; i < n; i++)
            {
                float l = moreL[i] * vol, r = moreR[i] * vol;
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                dest[(off + i) * 2] = l * gl;
                dest[(off + i) * 2 + 1] = r * gr;
            }
        }
        emu.SetActivity(channel, Math.Min(15, (int)(peak * 40)));
    }

    /// <summary>The control step: new FL voices start engine notes at the engine's clock (now), changed pitches bend them, released voices end them.</summary>
    private void UpdateNanoMoreVoices(INanoEngine e)
    {
        lock (moreKillGate)
        {
            for (int k = 0; k < moreKilledCount; k++) e.NoteOff(e.Frame, moreKilled[k]);
            moreKilledCount = 0;
        }
        int program = Get(P.Instrument);
        foreach (var v in voices)
        {
            if (v.Finished) continue;
            if (v.Released)
            {
                if (v.NanoId != 0) { e.NoteOff(e.Frame, v.NanoId); v.NanoId = 0; }
                Finish(v);
                continue;
            }
            ref var fl = ref v.Params->FinalLevels;
            if (fl.Pitch < v.PitchMin) v.PitchMin = fl.Pitch;
            if (fl.Pitch > v.PitchMax) v.PitchMax = fl.Pitch;
            double cents = 6000.0 + FlCents(fl.Pitch);          // MIDI cents (FlCents is from C5 = MIDI 60)
            if (v.NanoId == 0)
            {
                if (v.NanoStarted) continue;                    // (its note was killed: it does not start again)
                int note = Math.Clamp((int)Math.Round(cents / 100.0), 0, 127);
                double bend = cents - note * 100.0;
                float flVel = BitConverter.Int32BitsToSingle(Host.VoiceProcessEvent(v.HostTag, Fpv.GetVelocity, 0, 0));
                int midiVel = FlNotes.MidiVelocity(flVel);
                if (midiVel < 0) { midiVel = Math.Clamp((int)Math.Round(v.Params->InitLevels.Vol * 128.0), 0, 127); flVel = midiVel / 128f; }
                double vel = NanoMoreMutant == 1 ? flVel : Math.Min(1.0, midiVel / 127.0);
                if (++moreNextId <= 0) moreNextId = 1;
                e.NoteOnProgram(e.Frame, moreNextId, note, vel, program, bend);
                v.NanoId = moreNextId;
                v.NanoStarted = true;
                v.NanoNote = note;
                v.NanoBend = bend;
            }
            else if (NanoMoreMutant != 2)
            {
                double bend = cents - v.NanoNote * 100.0;
                if (bend != v.NanoBend) { e.BendVoice(e.Frame, v.NanoId, bend); v.NanoBend = bend; }
            }
        }
    }

    // ---- the sidecar: what the web library needs besides FL's MIDI export ----

    /// <summary>The instances of one Nano chip in the sidecar's order: every instance in Direct mode on that chip, on any emulator, in the order the instances were made
    /// (InstanceNo: FL makes a project's plugins channel by channel, top to bottom of the channel rack). The first is MIDI channel 1, the next 2, ... up to 16.</summary>
    internal static List<Bn2Plugin> NanoMoreInstances(NanoKind kind)
    {
        var list = new List<Bn2Plugin>();
        foreach (var e in EmulatorHub.All())
            if (e.Mode == 0 && ReferenceEquals(NanoKinds.OfChip(e.Chip), kind))
                foreach (var m in e.MembersSnapshot()) if (m.assignment.Channel >= 0) list.Add(m);
        list.Sort((a, b) => a.InstanceNo.CompareTo(b.InstanceNo));
        return list;
    }

    internal int NanoMoreMidiChannel()
    {
        var kind = NanoKinds.OfChip(assignment.Emulator?.Chip ?? -1);
        if (kind == null) return -1;
        int i = NanoMoreInstances(kind).IndexOf(this);
        return i is >= 0 and < 16 ? i + 1 : -1;
    }

    /// <summary>The sidecar JSON (the web library's song.js normalizeSidecar): {"format":kind.Format,"version":1,"channels":{"1":{"preset":..,"volume":..,"pan":"L"|"R","name":..}},"presets":{}}.
    /// volume = Volume / 1000, clamped 0..1; pan is written only off centre (Pan at or past 50% L / R: the web engines pan a channel left, both or right, nothing between).</summary>
    internal static string NanoMoreSidecar(NanoKind kind, out int written, out int left)
    {
        var all = NanoMoreInstances(kind);
        written = Math.Min(16, all.Count);
        left = all.Count - written;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", kind.Format);
            w.WriteNumber("version", 1);
            w.WriteStartObject("channels");
            for (int i = 0; i < written; i++)
            {
                var p = all[i];
                w.WriteStartObject((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("preset", kind.Preset(NanoKinds.Wrap(kind, p.Get(P.Instrument))).Name);
                w.WriteNumber("volume", Math.Round(Math.Clamp(p.Get(P.Volume) / 1000.0, 0, 1), 3));
                int pan = p.Get(P.Pan);
                if (pan <= -50) w.WriteString("pan", "L");
                else if (pan >= 50) w.WriteString("pan", "R");
                if (p.ChannelName is { Length: > 0 } name) w.WriteString("name", name);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("presets");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string? SaveNanoMoreSidecar(NanoKind kind, string path, out string summary)
    {
        summary = "";
        try
        {
            string json = NanoMoreSidecar(kind, out int written, out int left);
            if (written == 0) return $"no instance plays the {kind.Name} chip in Direct mode";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            summary = $"{written} channel{(written == 1 ? "" : "s")} (MIDI 1-{written}, in the order the instances were made)" + (left > 0 ? $"; {left} more instance{(left == 1 ? "" : "s")} left out (MIDI has 16 channels)" : "");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Test hooks 1150-1179: 1150 Direct mode on a later Nano chip (its 1-based kind, 0 none), 1151 index = the chip index of kind (index 1-based), 1152 presets of kind, 1153 sounding channels,
    /// 1154 voices holding a note, 1155 write the sidecar, 1156 its text, 1157 MIDI channel, 1158 preset index, 1159 engine clock, 1179 mutant.</summary>
    private nint NanoMoreTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        var kind = emu != null && emu.Mode == 0 ? NanoKinds.OfChip(emu.Chip) : null;
        switch (n)
        {
            case 1150: return kind == null ? 0 : Array.IndexOf(NanoKinds.All, kind) + 1;
            case 1151: return index is > 0 && index <= NanoKinds.All.Length ? Cx.IndexOfChip(NanoKinds.All[index - 1].Chip) : -1;
            case 1152: return index is > 0 && index <= NanoKinds.All.Length ? NanoKinds.All[index - 1].Count() : -1;
            case 1153: return moreEngine?.SoundingChannels ?? -1;
            case 1154: { int k = 0; foreach (var v in voices) if (v.NanoId != 0 && !v.Finished) k++; return k; }     // (no LINQ: the allocation check reads it inside its window)
            case 1155: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return kind != null && path != null && SaveNanoMoreSidecar(kind, path, out _) == null ? 1 : 0; }
            case 1156: return kind == null ? 0 : CopyText(NanoMoreSidecar(kind, out _, out _), index, value);
            case 1157: return NanoMoreMidiChannel();
            case 1158: return kind == null ? -1 : NanoKinds.Wrap(kind, Get(P.Instrument));
            case 1159: return (nint)(moreEngine?.Frame ?? -1);
            case 1179: NanoMoreMutant = (int)value; return 0;
        }
        return 0;
    }
}

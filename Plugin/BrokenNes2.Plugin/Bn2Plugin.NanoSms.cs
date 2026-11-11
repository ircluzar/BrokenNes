// The Nano SMS chip (NANOSMS, on the Master System console; appended after the harvested chips), Direct mode: this instance plays a NanoSmsEngine of its own, the line-for-line C#
// twin of the web library Nano/sms (BrokenNes's own SN76489, 24 presets). The same design as the Nano GB chip (Bn2Plugin.NanoGb.cs: FL's notes -> the engine the way
// the web library's MIDI player plays a .mid's notes on one MIDI channel, Nano/sms/tools/NanoSmsParity class FlLike), and its sidecar for the web library. Test hooks 960-979.
// The Voice fields NanoId / NanoStarted / NanoNote / NanoBend are shared with the Nano SMS chip (an instance plays one chip at a time).
using System.Text;
using System.Text.Json;
using BrokenNes.Fruity;
using BrokenNes2.Record;
using NesEmulator.Sega;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private volatile NanoSmsEngine? smsEngine;        // made on the UI thread (PrepareNanoSms); the audio thread only makes one after a sample-rate change
    private int smsRate;
    private NanoSmsEngine? smsVoicesOf;               // the engine the voices' note ids belong to
    private int smsNextId;
    private readonly float[] smsL = new float[Chunk], smsR = new float[Chunk];
    private readonly object smsKillGate = new();
    private readonly int[] smsKilled = new int[64];  // notes FL killed without releasing (KillVoice): released on the engine at the next step
    private int smsKilledCount;

    /// <summary>Deliberate bugs for the self-test (hook 979): 1 = the velocity as FL's float (MIDI velocity / 128, not / 127), 2 = live pitch changes ignored (no bends).</summary>
    internal static volatile int NanoSmsMutant;

    /// <summary>UI thread: this instance's engine, made ahead of its first block (and made again for Reset Console or a new host rate).</summary>
    private void PrepareNanoSms(bool again = false)
    {
        int rate = EmulatorHub.HostRate;
        if (!again && smsEngine != null && smsRate == rate) return;
        var e = new NanoSmsEngine(rate);
        smsRate = rate;
        smsEngine = e;
    }

    private void BindNanoSmsVoices(NanoSmsEngine e)
    {
        if (ReferenceEquals(smsVoicesOf, e)) return;
        smsVoicesOf = e;
        lock (smsKillGate) smsKilledCount = 0;
        foreach (var v in voices) v.NanoId = 0;
    }

    /// <summary>FL killed a voice (KillVoice) that still held a note of the engine: it is released at the next step (any thread may call this).</summary>
    private void NanoSmsForget(Voice v)
    {
        if (v.NanoId == 0) return;
        lock (smsKillGate) if (smsKilledCount < smsKilled.Length) smsKilled[smsKilledCount++] = v.NanoId;
        v.NanoId = 0;
    }

    /// <summary>Direct mode on the Nano SMS chip: this instance's notes on its own engine, stereo.</summary>
    private void RenderNanoSmsDirect(Emulator emu, int channel, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var e = smsEngine;
        int rate = EmulatorHub.HostRate;
        if (e == null || smsRate != rate) { e = new NanoSmsEngine(rate); smsEngine = e; smsRate = rate; }    // (a sample-rate change: rare; the first engine is made on the UI thread)
        BindNanoSmsVoices(e);
        float vol = Get(P.Volume) / 1000f, peak = 0;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateNanoSmsVoices(e);
            e.Render(smsL, smsR, 0, n);
            for (int i = 0; i < n; i++)
            {
                float l = smsL[i] * vol, r = smsR[i] * vol;
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                dest[(off + i) * 2] = l * gl;
                dest[(off + i) * 2 + 1] = r * gr;
            }
        }
        emu.SetActivity(channel, Math.Min(15, (int)(peak * 40)));
    }

    /// <summary>The control step: new FL voices start engine notes at the engine's clock (now), changed pitches bend them, released voices end them.</summary>
    private void UpdateNanoSmsVoices(NanoSmsEngine e)
    {
        lock (smsKillGate)
        {
            for (int k = 0; k < smsKilledCount; k++) e.NoteOff(e.Frame, smsKilled[k]);
            smsKilledCount = 0;
        }
        var preset = NanoSmsPresets.Program(Get(P.Instrument));
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
                double vel = NanoSmsMutant == 1 ? flVel : Math.Min(1.0, midiVel / 127.0);
                if (++smsNextId <= 0) smsNextId = 1;
                e.NoteOn(e.Frame, smsNextId, note, vel, preset, bend: bend);
                v.NanoId = smsNextId;
                v.NanoStarted = true;
                v.NanoNote = note;
                v.NanoBend = bend;
            }
            else if (NanoSmsMutant != 2)
            {
                double bend = cents - v.NanoNote * 100.0;
                if (bend != v.NanoBend) { e.BendVoice(e.Frame, v.NanoId, bend); v.NanoBend = bend; }
            }
        }
    }

    // ---- the sidecar: what the web library needs besides FL's MIDI export ----

    /// <summary>The Nano SMS instances of the process in the sidecar's order: every instance in Direct mode on the Nano SMS chip, on any emulator, in the order the instances were made
    /// (InstanceNo: FL makes a project's plugins channel by channel, top to bottom of the channel rack, when it opens it). The first is MIDI channel 1, the next 2, ... up to 16.</summary>
    internal static List<Bn2Plugin> NanoSmsInstances()
    {
        var list = new List<Bn2Plugin>();
        foreach (var e in EmulatorHub.All())
            if (e.NanoSmsDirectMode)
                foreach (var m in e.MembersSnapshot()) if (m.assignment.Channel >= 0) list.Add(m);
        list.Sort((a, b) => a.InstanceNo.CompareTo(b.InstanceNo));
        return list;
    }

    /// <summary>This instance's MIDI channel in the sidecar (1-16), or -1 (not a Nano SMS instance, or past the 16th).</summary>
    internal int NanoSmsMidiChannel()
    {
        int i = NanoSmsInstances().IndexOf(this);
        return i is >= 0 and < 16 ? i + 1 : -1;
    }

    /// <summary>The sidecar JSON (Nano/sms/src/song.js normalizeSidecar): {"format":"nano-sms-sidecar","version":1,"channels":{"1":{"preset":..,"volume":..,"pan":"L"|"R","name":..}},"presets":{}}.
    /// volume = Volume / 1000 (the default 1000 = 1.0), clamped 0..1; pan is written only off centre (Pan at or past 50% L / R: the Game Gear web engine pans a channel hard left or right, nothing between; a Master System is mono).</summary>
    internal static string NanoSmsSidecar(out int written, out int left)
    {
        var all = NanoSmsInstances();
        written = Math.Min(16, all.Count);
        left = all.Count - written;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", "nano-sms-sidecar");
            w.WriteNumber("version", 1);
            w.WriteStartObject("channels");
            for (int i = 0; i < written; i++)
            {
                var p = all[i];
                w.WriteStartObject((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("preset", NanoSmsPresets.Program(p.Get(P.Instrument)).Name);
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

    /// <summary>Writes the sidecar to <paramref name="path"/>; null when done, else why not.</summary>
    internal static string? SaveNanoSmsSidecar(string path, out string summary)
    {
        summary = "";
        try
        {
            string json = NanoSmsSidecar(out int written, out int left);
            if (written == 0) return "no instance plays the Nano SMS chip in Direct mode";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            summary = $"{written} channel{(written == 1 ? "" : "s")} (MIDI 1-{written}, in the order the instances were made)" + (left > 0 ? $"; {left} more instance{(left == 1 ? "" : "s")} left out (MIDI has 16 channels)" : "");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Test hooks 960-979, the Nano GB chip's 940-959 (Bn2Plugin.NanoGb.cs) for this chip: 960 Direct mode, 961 the chip index, 962 presets, 963 sounding channels, 964 voices holding a note, 965 write the sidecar, 966 its text, 967 MIDI channel, 968 preset index, 969 engine clock, 979 mutant.</summary>
    private nint NanoSmsTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 960: return emu?.NanoSmsDirectMode == true ? 1 : 0;
            case 961: return Cx.IndexOfChip(Cx.NanoSmsChip);
            case 962: return NanoSmsPresets.Count;
            case 963: return smsEngine?.SoundingChannels ?? -1;
            case 964: { int k = 0; foreach (var v in voices) if (v.NanoId != 0 && !v.Finished) k++; return k; }     // (no LINQ: the allocation check reads it inside its window)
            case 965: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return path != null && SaveNanoSmsSidecar(path, out _) == null ? 1 : 0; }
            case 966: return CopyText(NanoSmsSidecar(out _, out _), index, value);
            case 967: return NanoSmsMidiChannel();
            case 968: return Cx.PatchClamp(5, Get(P.Instrument));
            case 969: return (nint)(smsEngine?.Frame ?? -1);
            case 979: NanoSmsMutant = (int)value; return 0;
        }
        return 0;
    }
}

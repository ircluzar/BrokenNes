// The Nano GB chip (chip 30, NANOGB, on the Game Boy console), Direct mode: this instance plays a NanoGbEngine of its own, the line-for-line C# twin of the web library Nano/gb (gnuboy's Game Boy sound,
// 27 presets). It does with FL's notes what the web library's MIDI player does with a .mid's notes on one MIDI channel (Nano/gb/tools/NanoGbParity, class FlLike), so a song made in FL plays the same on
// the web from FL's MIDI export plus the sidecar JSON this file writes (the instances' presets, volumes and pans keyed by MIDI channel). Test hooks 940-959.
//
// FL's note -> the engine (all on the audio thread, nothing allocated; the engine is made on the UI thread ahead of time, PrepareNanoGb):
//   pitch     MIDI cents = 6000 + FlCents(pitch) (FlCents = cents from C5 = MIDI 60, the channel's base and Coarse / Fine included: Record/FlNotes.cs); note = the nearest MIDI key, the rest is the
//             voice's bend; every 64-sample step a changed pitch becomes BendVoice(id, cents - note * 100).
//   velocity  FL's FPV_GetVelocity is MIDI velocity / 128: the MIDI velocity is rounded back (FlNotes.MidiVelocity) and the engine gets velocity / 127, as the web player does.
//   preset    NanoGbPresets.Program(Instrument): program n = preset n (wrapping like the web player's program % 27).
//   output    the engine's stereo x Volume / 1000 (default 1000 = x1, the web player's default) x the Pan parameter's gains (gl / gr, as every chip of the plugin).
using System.Text;
using System.Text.Json;
using BrokenNes.Fruity;
using BrokenNes2.Record;
using NesEmulator.Gb;

namespace BrokenNes2;

public sealed unsafe partial class Bn2Plugin
{
    private volatile NanoGbEngine? nanoEngine;        // made on the UI thread (PrepareNanoGb); the audio thread only makes one after a sample-rate change
    private int nanoRate;
    private NanoGbEngine? nanoVoicesOf;               // the engine the voices' note ids belong to
    private int nanoNextId;
    private readonly float[] nanoL = new float[Chunk], nanoR = new float[Chunk];
    private readonly object nanoKillGate = new();
    private readonly int[] nanoKilled = new int[64];  // notes FL killed without releasing (KillVoice): released on the engine at the next step
    private int nanoKilledCount;

    /// <summary>Deliberate bugs for the self-test (hook 959): 1 = the velocity as FL's float (MIDI velocity / 128, not / 127), 2 = live pitch changes ignored (no bends).</summary>
    internal static volatile int NanoGbMutant;

    /// <summary>UI thread: this instance's engine, made ahead of its first block (and made again for Reset Console or a new host rate).</summary>
    private void PrepareNanoGb(bool again = false)
    {
        int rate = EmulatorHub.HostRate;
        if (!again && nanoEngine != null && nanoRate == rate) return;
        var e = new NanoGbEngine(rate);
        nanoRate = rate;
        nanoEngine = e;
    }

    private void BindNanoVoices(NanoGbEngine e)
    {
        if (ReferenceEquals(nanoVoicesOf, e)) return;
        nanoVoicesOf = e;
        lock (nanoKillGate) nanoKilledCount = 0;
        foreach (var v in voices) v.NanoId = 0;
    }

    /// <summary>FL killed a voice (KillVoice) that still held a note of the engine: it is released at the next step (any thread may call this).</summary>
    private void NanoGbForget(Voice v)
    {
        if (v.NanoId == 0) return;
        lock (nanoKillGate) if (nanoKilledCount < nanoKilled.Length) nanoKilled[nanoKilledCount++] = v.NanoId;
        v.NanoId = 0;
    }

    /// <summary>Direct mode on the Nano GB chip: this instance's notes on its own engine, stereo.</summary>
    private void RenderNanoGbDirect(Emulator emu, int channel, float* dest, int length, float gl, float gr)
    {
        DropChip();
        var e = nanoEngine;
        int rate = EmulatorHub.HostRate;
        if (e == null || nanoRate != rate) { e = new NanoGbEngine(rate); nanoEngine = e; nanoRate = rate; }    // (a sample-rate change: rare; the first engine is made on the UI thread)
        BindNanoVoices(e);
        float vol = Get(P.Volume) / 1000f, peak = 0;
        for (int off = 0; off < length; off += Chunk)
        {
            int n = Math.Min(Chunk, length - off);
            UpdateNanoVoices(e);
            e.Render(nanoL, nanoR, 0, n);
            for (int i = 0; i < n; i++)
            {
                float l = nanoL[i] * vol, r = nanoR[i] * vol;
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                dest[(off + i) * 2] = l * gl;
                dest[(off + i) * 2 + 1] = r * gr;
            }
        }
        emu.SetActivity(channel, Math.Min(15, (int)(peak * 40)));
    }

    /// <summary>The control step: new FL voices start engine notes at the engine's clock (now), changed pitches bend them, released voices end them.</summary>
    private void UpdateNanoVoices(NanoGbEngine e)
    {
        lock (nanoKillGate)
        {
            for (int k = 0; k < nanoKilledCount; k++) e.NoteOff(e.Frame, nanoKilled[k]);
            nanoKilledCount = 0;
        }
        var preset = NanoGbPresets.Program(Get(P.Instrument));
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
                double vel = NanoGbMutant == 1 ? flVel : Math.Min(1.0, midiVel / 127.0);
                if (++nanoNextId <= 0) nanoNextId = 1;
                e.NoteOn(e.Frame, nanoNextId, note, vel, preset, bend: bend);
                v.NanoId = nanoNextId;
                v.NanoStarted = true;
                v.NanoNote = note;
                v.NanoBend = bend;
            }
            else if (NanoGbMutant != 2)
            {
                double bend = cents - v.NanoNote * 100.0;
                if (bend != v.NanoBend) { e.BendVoice(e.Frame, v.NanoId, bend); v.NanoBend = bend; }
            }
        }
    }

    // ---- the sidecar: what the web library needs besides FL's MIDI export ----

    /// <summary>The Nano GB instances of the process in the sidecar's order: every instance in Direct mode on the Nano GB chip, on any emulator, in the order the instances were made
    /// (InstanceNo: FL makes a project's plugins channel by channel, top to bottom of the channel rack, when it opens it). The first is MIDI channel 1, the next 2, ... up to 16.</summary>
    internal static List<Bn2Plugin> NanoGbInstances()
    {
        var list = new List<Bn2Plugin>();
        foreach (var e in EmulatorHub.All())
            if (e.NanoGbDirectMode)
                foreach (var m in e.MembersSnapshot()) if (m.assignment.Channel >= 0) list.Add(m);
        list.Sort((a, b) => a.InstanceNo.CompareTo(b.InstanceNo));
        return list;
    }

    /// <summary>This instance's MIDI channel in the sidecar (1-16), or -1 (not a Nano GB instance, or past the 16th).</summary>
    internal int NanoGbMidiChannel()
    {
        int i = NanoGbInstances().IndexOf(this);
        return i is >= 0 and < 16 ? i + 1 : -1;
    }

    /// <summary>The sidecar JSON (Nano/gb/src/song.js normalizeSidecar): {"format":"nano-gb-sidecar","version":1,"channels":{"1":{"preset":..,"volume":..,"pan":"L"|"R","name":..}},"presets":{}}.
    /// volume = Volume / 1000 (the default 1000 = 1.0), clamped 0..1; pan is written only off centre (Pan at or past 50% L / R: the web engine pans a Game Boy channel hard left or right, nothing between).</summary>
    internal static string NanoGbSidecar(out int written, out int left)
    {
        var all = NanoGbInstances();
        written = Math.Min(16, all.Count);
        left = all.Count - written;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", "nano-gb-sidecar");
            w.WriteNumber("version", 1);
            w.WriteStartObject("channels");
            for (int i = 0; i < written; i++)
            {
                var p = all[i];
                w.WriteStartObject((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("preset", NanoGbPresets.Program(p.Get(P.Instrument)).Name);
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
    internal static string? SaveNanoGbSidecar(string path, out string summary)
    {
        summary = "";
        try
        {
            string json = NanoGbSidecar(out int written, out int left);
            if (written == 0) return "no instance plays the Nano GB chip in Direct mode";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            summary = $"{written} channel{(written == 1 ? "" : "s")} (MIDI 1-{written}, in the order the instances were made)" + (left > 0 ? $"; {left} more instance{(left == 1 ? "" : "s")} left out (MIDI has 16 channels)" : "");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Test hooks 940-959: 940 whether the emulator is in Nano GB Direct mode, 941 the chip index of NANOGB, 942 the number of presets, 943 the engine's sounding Game Boy channels (-1 no engine),
    /// 944 how many of this instance's FL voices hold an engine note, 945 write the sidecar to the path at value (UTF-16; 1 done, 0 refused), 946 the sidecar's text (into the buffer at value, at most index chars),
    /// 947 this instance's sidecar MIDI channel, 948 the preset index the Instrument parameter plays, 949 the engine's clock (frames), 959 set a deliberate bug (NanoGbMutant).</summary>
    private nint NanoGbTestHook(int n, nint index, nint value)
    {
        var emu = assignment.Emulator;
        switch (n)
        {
            case 940: return emu?.NanoGbDirectMode == true ? 1 : 0;
            case 941: return Cx.IndexOfChip(Cx.NanoGbChip);
            case 942: return NanoGbPresets.Count;
            case 943: return nanoEngine?.SoundingChannels ?? -1;
            case 944: { int k = 0; foreach (var v in voices) if (v.NanoId != 0 && !v.Finished) k++; return k; }     // (no LINQ: the allocation check reads it inside its window)
            case 945: { var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(value); return path != null && SaveNanoGbSidecar(path, out _) == null ? 1 : 0; }
            case 946: return CopyText(NanoGbSidecar(out _, out _), index, value);
            case 947: return NanoGbMidiChannel();
            case 948: return Cx.PatchClamp(4, Get(P.Instrument));
            case 949: return (nint)(nanoEngine?.Frame ?? -1);
            case 959: NanoGbMutant = (int)value; return 0;
        }
        return 0;
    }
}

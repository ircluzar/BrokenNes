// Round 6 (presets): the NES player, the project's own copy of a preset, and the cost (allocation) of playing and loading presets, through the real plugin DLL.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;
using BrokenNes2.Presets;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private static byte[] PresetFileOf(PresetSource src, Instrument inst, int steps, ulong seed = 5, string? name = null)
    {
        var file = PresetFile.TryWrite(PresetFixtures.Make(src, inst, steps, seed, name), out string? why);
        return file ?? throw new InvalidOperationException("could not write a preset: " + why);
    }

    /// <summary>A preset of <paramref name="inst"/> with some fields set by hand (history: those sets).</summary>
    private static byte[] PresetFileEdited(PresetSource src, Instrument inst, string name, params (string Id, int Value)[] sets)
    {
        var h = new InstrumentHistory(inst);
        foreach (var (id, v) in sets) h.Set(id, v);
        var d = PresetMaker.Make(name, inst.Name, src.Game, src.Sha1, "", PresetFixtures.Created, InstrumentCodec.Pack(h.Current), InstrumentCodec.Pack(h.Clean), h.Serialize(), PresetContext.Of(src.Snapshot, h.Current), out string? why)
            ?? throw new InvalidOperationException(why);
        return PresetFile.TryWrite(d, out why) ?? throw new InvalidOperationException(why);
    }

    // =====================================================================================================================================================================
    // the NES
    // =====================================================================================================================================================================

    private void PresetsNes(T t)
    {
        var src = PresetFixtures.Synthetic().First(s => s.Name == "nes");
        var snap = (NesSnapshot)src.Snapshot;
        var list = PresetFixtures.Instruments(src);
        const double velocity = 0.78;
        var pitches = new List<string>();
        foreach (var (idx, key, name) in new[] { (0, 69.0, "pulse 1"), (1, 69.0, "pulse 2"), (2, 57.0, "triangle"), (3, 60.0, "noise") })
        {
            var inst = list[idx];
            using var p = NewOn(2, 0);
            int r = PLoad(p, PresetFileOf(src, inst, 0), out string text);
            t.Expect(r == 0 && p.Test(HkPMode) == 1 && p.Test(HkPState) == 1, $"NES {name} preset: load said {r} '{text}'");
            var m = new Mixer { SampleRate = 44100 };
            var stereo = m.Run(p, One(key, 0, 0.5, velocity));
            var mono = Mono(stereo);
            var log = m.Log[0];
            double rms = Audio.Rms(mono, log.StartSample + 4000, log.EndSample - 2000);
            t.Expect(rms > 0.004, $"NES {name} preset: no sound (rms {rms:0.0000})");
            double cents = (key - 60) * 100;
            if (idx < 3)
            {
                double hz = SteadyHz(mono, 44100, log);
                double want = Nes.PeriodHz(Nes.Period(cents, idx == 2), idx == 2);
                t.NearCents($"NES {name} preset at key {key}", hz, want, 4);
                pitches.Add($"{name} {hz:0.0} Hz");
            }
            // the same registers written by hand to a bare chip (from the game's own register bytes, not from the preset): what the preset must sound like, scaled by the note's velocity
            var chip = new NesApuInstrument("FIX", 44100);
            var regs = snap.Regs;
            if (idx < 2)
            {
                int b = idx * 4, per = Nes.Period(cents, false);
                chip.Write(b, regs[b]); chip.Write(b + 1, regs[b + 1]); chip.Write(b + 2, (byte)(per & 0xFF)); chip.Write(b + 3, (byte)((per >> 8 & 7) | (regs[b + 3] & 0xF8)));
            }
            else if (idx == 2)
            {
                int per = Nes.Period(cents, true);
                chip.Write(8, regs[8]); chip.Write(0x0A, (byte)(per & 0xFF)); chip.Write(0x0B, (byte)((per >> 8 & 7) | (regs[0x0B] & 0xF8)));
            }
            else { chip.Write(0x0C, regs[0x0C]); chip.Write(0x0E, regs[0x0E]); chip.Write(0x0F, (byte)(regs[0x0F] & 0xF8)); }
            var reference = new float[8000];
            chip.Render(reference);
            double worst = 0;
            for (int i = 0; i < reference.Length; i++) worst = Math.Max(worst, Math.Abs(reference[i] * (float)velocity - mono[log.StartSample + i]));
            t.Expect(worst < 1e-5, $"NES {name} preset: the first 8000 frames differ from a bare chip given the game's registers by up to {worst:0.000000} (the player does not write what the game wrote)");
        }
        t.Note("NES presets through the register player: " + string.Join(", ", pitches) + "; noise and all four equal a bare chip driven by hand with the game's registers (to within 1e-5)");

        // the preset's own settings are what is heard: another duty sounds different at the same pitch
        var pulse = list[0];
        using (var a = NewOn(2, 0)) using (var b = NewOn(2, 0))
        {
            PLoad(a, PresetFileEdited(src, pulse, "duty 2"), out _);
            PLoad(b, PresetFileEdited(src, pulse, "duty 0", ("duty", 0)), out _);
            var ma = new Mixer { SampleRate = 44100 }; var mb = new Mixer { SampleRate = 44100 };
            var sa = ma.Run(a, One(69, 0, 0.5)); var sb = mb.Run(b, One(69, 0, 0.5));
            var fa = Audio.Frequency(Mono(sa), 44100, ma.Log[0].StartSample + 4000, ma.Log[0].EndSample - 2000);
            var fb = Audio.Frequency(Mono(sb), 44100, mb.Log[0].StartSample + 4000, mb.Log[0].EndSample - 2000);
            t.Expect(ImodelData.DiffRms(sa, sb) > 0.01 && Math.Abs(Audio.Cents(fa, fb)) < 3, $"duty 12.5% against 50%: difference {ImodelData.DiffRms(sa, sb):0.0000}, pitches {fa:0.0} / {fb:0.0} Hz");
        }

        // the DMC cannot be played: said so, on screen, and nothing is loaded
        using (var p = NewOn(2, 0))
        {
            int r = PLoad(p, PresetFileOf(src, list[4], 0), out string text);
            t.Expect(r == 1 && text.StartsWith("cannot play on this console yet:") && text.Contains("DMC") && p.Test(HkPState) == 0, $"a NES DMC preset must be refused with the reason: {r} '{text}', state {p.Test(HkPState)}");
            t.Expect(p.Test(HkPMode) == 0, "a refused preset must not change the emulator");
        }
    }

    // =====================================================================================================================================================================
    // the project's own copy
    // =====================================================================================================================================================================

    private static byte[] ReplaceSection(byte[] project, uint tag, Func<byte[], byte[]> edit)
    {
        int at = IndexOfTag(project, tag);
        if (at < 0) throw new InvalidOperationException("no section");
        int len = BitConverter.ToInt32(project, at + 4);
        var body = edit(project.AsSpan(at + 8, len).ToArray());
        return project.Take(at).Concat(BitConverter.GetBytes(tag)).Concat(BitConverter.GetBytes(body.Length)).Concat(body).Concat(project.Skip(at + 8 + len)).ToArray();
    }

    private static byte[] FixSectionCrc(byte[] section)
    {
        var s = (byte[])section.Clone();
        BitConverter.GetBytes(Crc(s.AsSpan(0, s.Length - 4).ToArray())).CopyTo(s, s.Length - 4);
        return s;
    }

    private void PresetsProject(T t)
    {
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var file = PresetFileOf(snes, PresetFixtures.Instruments(snes)[1], 2);
        byte[] saved; float[] played; long crc;
        using (var p = NewOn(2, 0))
        {
            t.Expect(PLoad(p, file, out string text) == 0, "load: " + text);
            played = PlayScript(p);
            crc = p.Test(HkPCrc);
            saved = p.SaveState();
        }
        int at = IndexOfTag(saved, PresetSection.Tag);
        t.Expect(at > 0 && BitConverter.ToInt32(saved, at + 4) == PresetSection.Build(file).Length, $"the saved project holds no PRS1 section of the right size (at {at})");

        // the library has nothing: the project plays by itself, the same sound, and saves the same bytes again
        string folder = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR") ?? "";
        t.Expect(folder.Length > 0 && !Directory.EnumerateFiles(folder, "*.bn2preset", SearchOption.AllDirectories).Any(), "the scratch library is not empty: the project check would prove nothing about a library file being gone");
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            q.LoadState(saved);
            t.Expect(q.Test(HkPState) == 1 && q.Test(HkPMode) == 1 && q.Test(HkPCrc) == crc && PNote(q) == "", $"the reopened project: preset {q.Test(HkPState)}, preset mode {q.Test(HkPMode)}, note '{PNote(q)}'");
            t.Expect(SamePcm(PlayScript(q), played), "the reopened project sounds different from the one that was saved (the library has no file for it)");
            var again = q.SaveState();
            int firstDiff = 0; while (firstDiff < Math.Min(again.Length, saved.Length) && again[firstDiff] == saved[firstDiff]) firstDiff++;
            t.Expect(again.AsSpan().SequenceEqual(saved), $"save -> load -> save of a project with a preset is not stable (lengths {saved.Length} and {again.Length}, first difference at byte {firstDiff}: {(firstDiff < saved.Length ? saved[firstDiff] : -1)} against {(firstDiff < again.Length ? again[firstDiff] : -1)})");
            t.Expect(q.Test(HkPLibId) == -1, "a preset restored from a project claims a library file");
        }

        // an older project (no section) and a project from before presets existed open exactly as before
        var without = saved.Take(at).Concat(saved.Skip(at + 8 + BitConverter.ToInt32(saved, at + 4))).ToArray();
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            q.LoadState(without);
            t.Expect(q.Test(HkPState) == 0 && PNote(q) == "" && q.Test(HkPMode) == 1, $"a project without the section: preset {q.Test(HkPState)}, note '{PNote(q)}', preset mode {q.Test(HkPMode)} (the chip is the project's, the instance has nothing to play, nothing is said)");
            t.Expect(ImodelData.Rms(PlayScript(q)) < 1e-9, "an instance with no preset on a Presets chip is not silent");
        }
        byte[] plain;
        using (var seed = lib.Create()) plain = seed.SaveState();
        t.Expect(IndexOfTag(plain, PresetSection.Tag) < 0, "an instance with no preset wrote a PRS1 section");
        using (var q = lib.Create()) { q.LoadState(plain); t.Expect(q.Test(HkPState) == 0 && q.Test(HkPMode) == 0 && PNote(q) == "", "a project from before presets did not open as before"); }

        // damage: each is refused with a reason on screen, the project still opens, and nothing plays
        int len = BitConverter.ToInt32(saved, at + 4);
        var damages = new List<(string What, byte[] Project, string Mention)>
        {
            ("a flipped byte in the preset", ReplaceSection(saved, PresetSection.Tag, s => { s[40] ^= 0x10; return s; }), "damaged"),
            ("a flipped byte of the section's own header", ReplaceSection(saved, PresetSection.Tag, s => { s[2] ^= 0x01; return s; }), "damaged"),
            ("a newer section (the checksum right)", ReplaceSection(saved, PresetSection.Tag, s => { s[0] = 9; return FixSectionCrc(s); }), "newer"),
            ("an unknown feature flag (the checksum right)", ReplaceSection(saved, PresetSection.Tag, s => { s[1] = 1; return FixSectionCrc(s); }), "feature"),
            ("a length that lies (the checksum right)", ReplaceSection(saved, PresetSection.Tag, s => { s[4] = (byte)(s[4] + 7); return FixSectionCrc(s); }), "length"),
            ("a section of a few bytes", ReplaceSection(saved, PresetSection.Tag, s => s.AsSpan(0, 12).ToArray()), "length"),
            ("a newer preset format inside (the checksums right)", ReplaceSection(saved, PresetSection.Tag, s =>
            {
                var inner = s.AsSpan(8, s.Length - 12).ToArray();
                inner[4] = 2;                                                                                     // the preset file's format version
                BitConverter.GetBytes(Crc(inner.AsSpan(0, inner.Length - 4).ToArray())).CopyTo(inner, inner.Length - 4);
                return PresetSection.Build(inner);
            }), "newer"),
            ("a preset section cut off by the end of the project", saved.AsSpan(0, at + 8 + len / 2).ToArray(), ""),
        };
        foreach (var (what, project, mention) in damages)
        {
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            q.LoadState(project);
            string note = PNote(q);
            t.Expect(q.Test(HkPState) == 0, $"{what}: the damaged preset was used");
            if (mention.Length > 0) t.Expect(note.Contains("project's preset was not used") && note.Contains(mention, StringComparison.OrdinalIgnoreCase), $"{what}: the note says '{note}'");
            else t.Expect(note.Length > 0, $"{what}: nothing was said");
            t.Expect(ImodelData.Rms(PlayScript(q)) < 1e-9 && PlayScript(q).All(float.IsFinite), $"{what}: not silent or not finite");
        }
        // sections nobody here knows, before and after ours, are skipped by their length
        byte[] Unknown(uint tag, int n) => BitConverter.GetBytes(tag).Concat(BitConverter.GetBytes(n)).Concat(new byte[n]).ToArray();
        var wrapped = saved.Take(at).Concat(Unknown(0x39585A5A, 5)).Concat(saved.Skip(at)).Concat(Unknown(0x38585A5A, 9)).ToArray();
        using (var q = lib.Create())
        {
            q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            q.LoadState(wrapped);
            t.Expect(q.Test(HkPState) == 1 && q.Test(HkPCrc) == crc && SamePcm(PlayScript(q), played), "unknown sections around the preset's changed what was restored");
        }

        // two instances of one project, each its own preset
        var fm = PresetFileOf(md, PresetFixtures.Instruments(md)[0], 1);
        byte[] s1, s2; float[] a1, a2;
        using (var p1 = NewOn(2, 0))
        using (var p2 = lib.Create())
        {
            p2.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            p2.SetParam(PEmulator, 2);
            t.Expect(PLoad(p1, file, out _) == 0 && PLoad(p2, fm, out _) == 0 && p1.Test(HkPCrc) == crc && p2.Test(HkPCrc) != crc, "two instances on one emulator did not each keep their own preset");
            a1 = PlayScript(p1); a2 = PlayScript(p2);
            t.Expect(!SamePcm(a1, a2) && ImodelData.Rms(a1) > 1e-4 && ImodelData.Rms(a2) > 1e-4, "two instances on a Presets emulator with two presets sound the same or are silent");
            s1 = p1.SaveState(); s2 = p2.SaveState();
        }
        using (var q1 = lib.Create())
        using (var q2 = lib.Create())
        {
            q1.Dispatcher(Fpd.SetSampleRate, 0, 44100); q2.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            q1.LoadState(s1); q2.LoadState(s2);
            t.Expect(SamePcm(PlayScript(q1), a1) && SamePcm(PlayScript(q2), a2), "the two instances did not each get their own preset back from the project");
        }
        t.Note($"a project with a preset reopens with no library file and sounds the same ({played.Length:N0} values compared), saves the same bytes, and survives {damages.Count} kinds of damage with a note on screen; two instances keep two presets");
    }

    // =====================================================================================================================================================================
    // cost
    // =====================================================================================================================================================================

    /// <summary>A collection before a measurement. The runtime's per-thread allocation counter is not exact across a collection (a GC that happens during the window, typically one the big allocations of a load triggered, makes it jump by a few KB with nothing allocated:
    /// measured, with the plugin's own audio code instrumented section by section); a window that starts right after a collection has none.</summary>
    private static void Quiet() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static long QuietBlock(PluginInstance p) { Quiet(); return AllocOfOneBlock(p); }

    private void PresetsAlloc(T t)
    {
        var snes = PresetFixtures.Synthetic().First(s => s.Name == "snes-samples");
        var md = PresetFixtures.Synthetic().First(s => s.Name == "opn");
        var gb = PresetFixtures.Synthetic().First(s => s.Name == "gb-dmg");
        var nes = PresetFixtures.Synthetic().First(s => s.Name == "nes");
        var presets = new (string Name, byte[] File)[]
        {
            ("SNES sample", PresetFileOf(snes, PresetFixtures.Instruments(snes)[1], 2)),
            ("Genesis FM patch", PresetFileOf(md, PresetFixtures.Instruments(md)[0], 2)),
            ("Genesis DAC sound", PresetFileOf(md, PresetFixtures.Instruments(md)[6], 0)),
            ("Game Boy pulse", PresetFileOf(gb, PresetFixtures.Instruments(gb)[0], 2)),
            ("NES pulse", PresetFileOf(nes, PresetFixtures.Instruments(nes)[0], 0)),
        };
        using var p = NewOn(2, 0);
        var held = new Scenario { Name = "alloc", Tempo = 60, LengthBeats = 7 };
        foreach (var k in new[] { 60.0, 64.0, 67.0 }) held.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 6.5, Key = k, Channel = 0, Velocity = 0.7 });
        long a0 = 0, a1 = 0;
        var m = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm =>
        {
            if (sm >= 48000 && a0 == 0) { Quiet(); a0 = AllocStart(p); }
            if (sm >= 48000 * 6 && a1 == 0) a1 = p.Test(HkPAlloc);
        } };
        p.Dispatcher(Fpd.SetSampleRate, 0, 48000);
        t.Expect(PLoad(p, presets[0].File, out _) == 0, "load");
        m.Run(p, held, 0);
        t.Expect(a1 > 0 && a1 - a0 == 0, $"the plugin allocated {a1 - a0:N0} bytes while a 3-note chord played a SNES preset for 5 s (must be 0)");

        // loading another preset while notes are held: the first block after the load allocates nothing (the engine was built on the UI thread), whatever the console
        var rows = new List<string>();
        foreach (var (name, file) in presets)
        {
            var hold = PresetScript();
            using var q = NewOn(2, 0, 48000);
            // a note is held across the load
            var voices = q.Host;
            PLoad(q, presets[0].File, out _);
            var v = voices.NewVoice(0, 0.8f);
            ref var fl = ref v.Params->FinalLevels; fl.Pitch = 0; fl.Vol = 0.8f; v.Params->InitLevels = fl;
            v.Handle = q.TriggerVoice(v.Params, v.Tag);
            QuietBlock(q); QuietBlock(q);
            t.Expect(PLoad(q, file, out string text) == 0, $"{name}: load said '{text}'");
            long first = QuietBlock(q), second = QuietBlock(q);
            t.Expect(first == 0 && second == 0, $"{name}: the blocks after loading a preset under a held note allocated {first:N0} and {second:N0} bytes on the audio side");
            rows.Add($"{name} {first}/{second} B");
            q.Test(43);                                                                // Reset Console: the engines are built again, ahead of the audio thread
            long afterReset = QuietBlock(q), afterReset2 = QuietBlock(q);
            t.Expect(afterReset == 0 && afterReset2 == 0, $"{name}: the blocks after Reset Console allocated {afterReset:N0} and {afterReset2:N0} bytes");
            q.VoiceRelease(v.Handle);
        }
        // the control: a change of the host's sample rate makes the audio side rebuild its sampler, which this measurement does see
        using (var q = NewOn(2, 0, 48000))
        {
            PLoad(q, presets[0].File, out _);
            var v = q.Host.NewVoice(0, 0.8f);
            ref var fl = ref v.Params->FinalLevels; fl.Pitch = 0; fl.Vol = 0.8f; v.Params->InitLevels = fl;
            v.Handle = q.TriggerVoice(v.Params, v.Tag);
            QuietBlock(q); QuietBlock(q);
            q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
            long unprepared = QuietBlock(q);
            t.Expect(unprepared > 60_000, $"the control failed: a sampler rebuilt on the audio side allocated only {unprepared:N0} bytes");
            rows.Add($"control (rate change) {unprepared:N0} B");
        }
        t.Note("allocation on the audio side: a chord for 5 s " + (a1 - a0) + " B; the blocks after a load / a Reset Console: " + string.Join(", ", rows));
    }
}

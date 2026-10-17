// Round 6 (imodel): the instrument model through the real plugin DLL (Native AOT): an instrument is read from the frozen game's working copy, edited with the model's tools, written back with the plugin's own code, and played by the
// plugin's own samplers; the project keeps the edit (SNES: the overlay section), a damaged or foreign overlay section is dropped with a note, and nothing allocates on the audio side while the edit plays. Hooks 300-308 (Bn2Plugin.Instruments.cs).
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkInstApply = 300, HkInstText = 301, HkInstHash = 302, HkInstOverlay = 303, HkInstCount = 304, HkInstNote = 305, HkInstAlloc = 306, HkInstLen = 307, HkInstRead = 308;

    private static string DllText(PluginInstance p, int hook)
    {
        var c = new char[600];
        fixed (char* cp = c) { long n = p.Test(hook, c.Length, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private static Instrument? DllRead(PluginInstance p, int index)
    {
        long n = p.Test(HkInstLen, index);
        if (n <= 0) return null;
        var buf = new byte[n];
        fixed (byte* b = buf) p.Test(HkInstRead, index, (nint)b);
        return InstrumentCodec.Unpack(buf, out _);
    }

    private static int DllApply(PluginInstance p, Instrument inst, out string text)
    {
        var packed = InstrumentCodec.Pack(inst);
        long r;
        fixed (byte* b = packed) r = p.Test(HkInstApply, packed.Length, (nint)b);
        text = DllText(p, HkInstText);
        return (int)r;
    }

    /// <summary>The instrument edited with the model's own tools: the copy of <paramref name="index"/> from the DLL with each (field, value) set.</summary>
    private static Instrument Edited(PluginInstance p, int index, params (string Id, int Value)[] sets)
    {
        var h = new InstrumentHistory(DllRead(p, index)!);
        foreach (var (id, v) in sets) h.Set(id, v);
        return h.Current;
    }

    /// <summary>Bytes the plugin allocated while rendering one block (the host's own buffer is made before the first reading).</summary>
    private static long AllocOfOneBlock(PluginInstance p)
    {
        var buf = new float[1024];
        fixed (float* f = buf)
        {
            p.NewTick();
            long a0 = p.Test(HkInstAlloc);
            p.GenRender(f, 512);
            return p.Test(HkInstAlloc) - a0;
        }
    }

    private static (float[] Stereo, float[] Mono, NoteLog Log) Note(PluginInstance p, double key = 60, double seconds = 0.5, int rate = 44100)
    {
        var m = new Mixer { SampleRate = rate };
        var stereo = m.Run(p, One(key, 0, seconds));
        return (stereo, Mono(stereo), m.Log[0]);
    }

    private static double SideRms(float[] stereo, int side, int fromFrame, int toFrame)
    {
        double s = 0; int n = 0;
        for (int i = fromFrame; i < toFrame && i * 2 + 1 < stereo.Length; i++) { double v = stereo[i * 2 + side]; s += v * v; n++; }
        return n == 0 ? 0 : Math.Sqrt(s / n);
    }

    private static int IndexOfTag(byte[] blob, uint tag)
    {
        var t = BitConverter.GetBytes(tag);
        for (int i = blob.Length - 8; i >= 0; i--) if (blob[i] == t[0] && blob[i + 1] == t[1] && blob[i + 2] == t[2] && blob[i + 3] == t[3]) return i;
        return -1;
    }

    private void ImodelDll(T t)
    {
        // ---- SNES: the overlay (envelope, tuning, volume weights, flags) is played, kept in a project, and survives damage ----
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            byte[] st;
            using (var seed = lib.Create()) { seed.SetParam(PConsole, 2); seed.SetParam(PMode, 1); st = seed.SaveState(); }
            byte[] saved, savedDamaged = [], savedNewer = [], savedWithout = [];
            using (var p = lib.Create())
            {
                p.Dispatcher(Fpd.SetSampleRate, 0, 44100);
                p.LoadState(StateWithRom(st, sfc));
                t.Expect(WaitConsole(p, 2), "the SNES test game did not load");
                RenderBlocks(p, 0.5, 44100);
                p.Test(47, 0, 1);
                p.Test(46);                                                     // a made-up sound memory with one looping square-wave sample (2 kHz at C5)
                t.Expect(p.Test(HkInstCount) == 1, $"the working copy lists {p.Test(HkInstCount)} instruments, expected 1");
                var inst0 = DllRead(p, 0);
                t.Expect(inst0 != null && inst0.Kind == InstrumentKind.SnesSample && inst0.Blobs[0].Data.Length == 9, "the DLL did not give back the made-up sample as a SNES instrument");
                if (inst0 == null) return;
                long hashClean = p.Test(HkInstHash);

                // an unchanged write is a no-op: the working copy has identical bytes, and the note sounds exactly as before it
                var before = Note(p);
                int r = DllApply(p, inst0, out string text);
                t.Expect(r == 0 && text == "changed: ", $"writing the instrument back unchanged: {r} '{text}'");
                t.Expect(p.Test(HkInstHash) == hashClean && p.Test(HkInstOverlay) == 0, "an unchanged write changed the working copy");
                var again = Note(p);
                t.Expect(ImodelData.Hash(again.Stereo) == ImodelData.Hash(before.Stereo), "an unchanged write changed the sound");
                double f0 = Audio.Frequency(before.Mono, 44100, before.Log.StartSample + 6000, before.Log.EndSample - 3000);
                t.NearCents("the made-up sample at C5", f0, 2000, 8);

                // tuning +1200 cents: an octave up, through the plugin's own sampler
                r = DllApply(p, Edited(p, 0, ("tune.cents", 1200)), out text);
                t.Expect(r == 0 && text.Contains("tuning") && p.Test(HkInstOverlay) == 1, $"tuning edit: {r} '{text}', overlay entries {p.Test(HkInstOverlay)}");
                var up = Note(p);
                t.NearCents("the sample tuned +1200 cents", Audio.Frequency(up.Mono, 44100, up.Log.StartSample + 6000, up.Log.EndSample - 3000), 4000, 8);
                // back to 0: the overlay is gone and the sound is exactly the original again
                r = DllApply(p, Edited(p, 0, ("tune.cents", 0)), out text);
                var back = Note(p);
                t.Expect(r == 0 && p.Test(HkInstOverlay) == 0 && p.Test(HkInstHash) == hashClean, $"tuning back to 0 left a trace: overlay {p.Test(HkInstOverlay)}");
                t.Expect(ImodelData.Hash(back.Stereo) == ImodelData.Hash(before.Stereo), "tuning back to 0 does not sound exactly like the original");

                // a slow attack: the first 60 ms are much quieter than the organ-like default's
                double loud = Audio.Rms(before.Mono, before.Log.StartSample + 200, before.Log.StartSample + 2646);
                r = DllApply(p, Edited(p, 0, ("env.adsr", 1), ("env.ar", 2)), out text);
                var slow = Note(p);
                double soft = Audio.Rms(slow.Mono, slow.Log.StartSample + 200, slow.Log.StartSample + 2646);
                t.Expect(r == 0 && text.Contains("envelope") && soft < loud * 0.4, $"a slow attack should be much quieter in the first 60 ms: {soft:0.0000} vs {loud:0.0000} ('{text}')");

                // volume weights: the right side is silent, the left is not
                r = DllApply(p, Edited(p, 0, ("vol.r", 0)), out text);
                var left = Note(p);
                int a = left.Log.StartSample + 4000, b = left.Log.EndSample - 3000;
                t.Expect(r == 0 && SideRms(left.Stereo, 1, a, b) < 1e-4 && SideRms(left.Stereo, 0, a, b) > 0.005, $"right weight 0: right {SideRms(left.Stereo, 1, a, b):0.00000}, left {SideRms(left.Stereo, 0, a, b):0.0000} ('{text}')");

                // noise instead of the sample
                r = DllApply(p, Edited(p, 0, ("vol.r", 127), ("mix.noise", 1)), out text);
                var noise = Note(p);
                t.Expect(r == 0 && ImodelData.Hash(noise.Stereo) != ImodelData.Hash(before.Stereo) && Audio.Rms(noise.Mono, noise.Log.StartSample + 6000, noise.Log.EndSample - 3000) > 0.01, "the noise flag did not change the sound");

                // the kept edit: tuning, weights, a slow attack
                r = DllApply(p, Edited(p, 0, ("mix.noise", 0), ("tune.cents", 700), ("vol.l", 100), ("vol.r", 60), ("env.adsr", 1), ("env.ar", 6)), out text);
                t.Expect(r == 0 && p.Test(HkInstOverlay) == 1, $"the kept edit: {r} '{text}'");
                var kept = Note(p);
                long hashEdited = p.Test(HkInstHash);
                saved = p.SaveState();

                // the swap itself: with the sampler prepared on the UI thread (Emulator.ApplyInstrument) the first block after an edit allocates nothing; a snapshot put in place with no preparation
                // makes the audio side rebuild its sampler (the control: this measurement does see that)
                DllApply(p, Edited(p, 0, ("tune.cents", 650)), out _);
                long preparedBytes = AllocOfOneBlock(p);
                p.Test(46);
                long unpreparedBytes = AllocOfOneBlock(p);
                t.Expect(preparedBytes == 0, $"the first block after an edit allocated {preparedBytes:N0} bytes on the audio side although the sampler was prepared on the UI thread");
                t.Expect(unpreparedBytes > 60_000, $"the control failed: a swap with no prepared sampler allocated only {unpreparedBytes:N0} bytes (the rebuild copies 64 KB of audio RAM)");
                t.Note($"SNES swap: first block after an edit {preparedBytes} bytes allocated (prepared); {unpreparedBytes:N0} bytes when the audio side has to rebuild the sampler");

                // the sampler's audio-side allocation while the edit plays (the swap itself, which rebuilds each instance's sampler on its next block, is the one block that allocates)
                p.Test(46);                                                      // (a fresh working copy, then the same edit: nothing is cached anywhere)
                DllApply(p, Edited(p, 0, ("tune.cents", 700), ("vol.l", 100), ("vol.r", 60), ("env.adsr", 1), ("env.ar", 6)), out _);
                long a0 = 0, a1 = 0;
                var m = new Mixer { SampleRate = 48000, BlockSize = 512, AfterBlock = sm =>
                {
                    if (sm >= 48000 && a0 == 0) a0 = p.Test(HkInstAlloc);
                    if (sm >= 48000 * 4 && a1 == 0) a1 = p.Test(HkInstAlloc);
                } };
                var s = new Scenario { Name = "alloc", Tempo = 60, LengthBeats = 5 };
                foreach (var k in new[] { 60.0, 64.0, 67.0 }) s.Notes.Add(new SimNote { StartBeat = 0, LengthBeats = 4.5, Key = k, Channel = 0, Velocity = 0.7 });
                m.Run(p, s, 0);
                t.Expect(a1 - a0 == 0, $"the plugin allocated {a1 - a0:N0} bytes while playing an edited instrument for 3 s (must be 0)");
                t.Note($"SNES edit through the DLL: a 3-note chord for 3 s allocated {a1 - a0} bytes on the audio side");
            }

            // the project remembers it
            using (var q = lib.Create())
            {
                q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
                q.LoadState(saved);
                t.Expect(q.Test(45) == 1 && q.Test(48) == 1, "the saved project did not come back in Instrument Runaway");
                t.Expect(q.Test(HkInstOverlay) == 1, $"the project lost the instrument edits (overlay entries {q.Test(HkInstOverlay)})");
                t.Expect(DllText(q, HkInstNote) == "", "a project with undamaged edits carries a note: " + DllText(q, HkInstNote));
                var back = Note(q);
                double fq = Audio.Frequency(back.Mono, 44100, back.Log.StartSample + 6000, back.Log.EndSample - 3000);
                t.NearCents("the restored edit (+700 cents)", fq, 2000 * Math.Pow(2, 700 / 1200.0), 10);
                int a = back.Log.StartSample + 6000, b = back.Log.EndSample - 3000;
                t.Expect(SideRms(back.Stereo, 0, a, b) > SideRms(back.Stereo, 1, a, b) * 1.3, "the restored weights (left 100, right 60) are not heard");
                // a project saved again is the same bytes (the section is deterministic)
                t.Expect(q.SaveState().AsSpan().SequenceEqual(saved), "save -> load -> save is not stable with instrument edits");
                // damage: a flipped byte in the overlay section, a newer version, a cut section
                int at = IndexOfTag(saved, SnesOverlay.Tag);
                t.Expect(at > 0, "the saved project has no overlay section");
                if (at > 0)
                {
                    int len = BitConverter.ToInt32(saved, at + 4);
                    savedDamaged = (byte[])saved.Clone(); savedDamaged[at + 8 + 5] ^= 0x10;
                    savedNewer = (byte[])saved.Clone(); savedNewer[at + 8] = 9;
                    // a newer version whose checksum is right: the reader must refuse it by its version, not its checksum
                    uint crc = Crc(savedNewer.AsSpan(at + 8, len - 4).ToArray());
                    savedNewer[at + 8 + len - 4] = (byte)crc; savedNewer[at + 8 + len - 3] = (byte)(crc >> 8); savedNewer[at + 8 + len - 2] = (byte)(crc >> 16); savedNewer[at + 8 + len - 1] = (byte)(crc >> 24);
                    savedWithout = saved.Take(at).Concat(saved.Skip(at + 8 + len)).ToArray();
                }
            }
            foreach (var (what, blob, wantNote) in new[] { ("a damaged overlay section", savedDamaged, "damaged"), ("a newer overlay section", savedNewer, "newer"), ("a project without an overlay section (an older build)", savedWithout, "") })
            {
                if (blob.Length == 0) continue;
                using var q = lib.Create();
                q.Dispatcher(Fpd.SetSampleRate, 0, 44100);
                q.LoadState(blob);
                t.Expect(q.Test(45) == 1 && q.Test(48) == 1 && q.Test(HkInstOverlay) == 0, $"{what}: the project must still open in Runaway, without the edits (overlay entries {q.Test(HkInstOverlay)})");
                string note = DllText(q, HkInstNote);
                if (wantNote == "") t.Expect(note == "", $"{what}: no note expected, got '{note}'");
                else t.Expect(note.Contains("instrument edits") && note.Contains(wantNote), $"{what}: the user must be told ('{note}')");
                var n = Note(q);
                t.NearCents($"{what}: the sound is the game's own again", Audio.Frequency(n.Mono, 44100, n.Log.StartSample + 6000, n.Log.EndSample - 3000), 2000, 8);
            }
        }
        finally { DeleteQuietly(sfc); }

        // ---- Game Boy: the wave RAM and the duty are the sampler's instruments ----
        string gb = TempGame(".gb", MakeGbRom(tone: true));
        try
        {
            using var p = SegaGame(gb, 1, "DMG");
            RenderBlocks(p, 0.8);
            p.Test(47, 0, 1);
            t.Expect(p.Test(45) == 1 && p.Test(50) == 1, "Runaway did not start on the Game Boy test game");
            t.Expect(p.Test(HkInstCount) == 4, $"the Game Boy working copy lists {p.Test(HkInstCount)} instruments, expected 4");
            var pulse = DllRead(p, 0);
            if (pulse == null) { t.Expect(false, "no Game Boy pulse instrument from the DLL"); return; }
            t.Expect(pulse.Get("duty") == 2, $"the test game's pulse duty reads {pulse.Get("duty")}, expected 2 (50%)");
            long h0 = p.Test(HkInstHash);
            var clean = Note(p, 60, 0.5, 48000);
            DllApply(p, pulse, out string text);
            t.Expect(p.Test(HkInstHash) == h0, "an unchanged Game Boy write changed the working copy");
            t.Expect(ImodelData.Hash(Note(p, 60, 0.5, 48000).Stereo) == ImodelData.Hash(clean.Stereo), "an unchanged Game Boy write changed the sound");
            int r = DllApply(p, Edited(p, 0, ("duty", 0)), out text);
            var thin = Note(p, 60, 0.5, 48000);
            t.Expect(r == 0 && ImodelData.DiffRms(clean.Stereo, thin.Stereo) > 0.01, $"duty 12.5% should sound different from 50% (difference {ImodelData.DiffRms(clean.Stereo, thin.Stereo):0.0000}, '{text}')");
            DllApply(p, Edited(p, 0, ("duty", 1)), out text);
            long gbSwap = AllocOfOneBlock(p);
            t.Expect(gbSwap == 0, $"the first block after a Game Boy edit allocated {gbSwap:N0} bytes on the audio side");
            // the project keeps it
            var savedGb = p.SaveState();
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(savedGb);
            t.Expect(q.Test(45) == 1 && DllRead(q, 0)?.Get("duty") == 0, $"the project lost the Game Boy edit (duty {DllRead(q, 0)?.Get("duty")})");
        }
        finally { DeleteQuietly(gb); }

        // ---- Genesis: a patch field and a DAC sound's rate ----
        string md = TempGame(".md", MakeMdRom(fm: true, dac: true));
        try
        {
            using var p = SegaGame(md, 4, "MD");
            RenderBlocks(p, 1.2);
            p.Test(47, 0, 1);
            t.Expect(p.Test(45) == 1, "Runaway did not start on the Genesis test game");
            int fm = (int)p.Test(78);                                           // the instrument the game's choice resolves to
            var patch = DllRead(p, fm);
            t.Expect(patch != null && patch.Kind == InstrumentKind.OpnPatch, $"instrument {fm} of the Genesis working copy is not an FM patch");
            if (patch == null || patch.Kind != InstrumentKind.OpnPatch) return;
            long h0 = p.Test(HkInstHash);
            var clean = Note(p, 60, 0.5, 48000);
            t.Expect(DllApply(p, patch, out string text) == 0 && p.Test(HkInstHash) == h0, "an unchanged Genesis write changed the working copy");
            t.Expect(ImodelData.Hash(Note(p, 60, 0.5, 48000).Stereo) == ImodelData.Hash(clean.Stereo), "an unchanged Genesis write changed the sound");
            // quieten every operator that reaches the output: whichever algorithm the patch has, the level must fall
            var carriers = Enumerable.Range(1, 4).Where(n => (NesEmulator.Mix.FmPatchBank.CarriersOf(patch.Get("alg")) >> (n - 1) & 1) != 0).ToArray();
            var sets = carriers.Select(n => ($"op{n}.tl", Math.Min(127, patch.Get($"op{n}.tl") + 40))).ToArray();
            int r = DllApply(p, Edited(p, fm, sets), out text);
            var quiet = Note(p, 60, 0.5, 48000);
            double rc = Audio.Rms(clean.Mono, clean.Log.StartSample + 6000, clean.Log.EndSample - 3000), rq = Audio.Rms(quiet.Mono, quiet.Log.StartSample + 6000, quiet.Log.EndSample - 3000);
            t.Expect(r == 0 && rc > 0.005 && rq < rc * 0.2, $"carriers 40 steps (30 dB) quieter should be much quieter: {rq:0.00000} vs {rc:0.00000} ('{text}')");
            // a pan edit is accepted and changes nothing the sampler plays
            DllApply(p, Edited(p, fm, sets), out text);                          // (every comparison is between two fresh samplers: a sampler that has played a note is in another state)
            var panned = Note(p, 60, 0.5, 48000);
            DllApply(p, Edited(p, fm, sets.Concat(new[] { ("pan.l", 0) }).ToArray()), out text);
            t.Expect(ImodelData.Hash(Note(p, 60, 0.5, 48000).Stereo) == ImodelData.Hash(panned.Stereo), "the pan edit changed the sound (the sampler plays both sides)");
            DllApply(p, Edited(p, fm, sets), out text);
            long mdSwap = AllocOfOneBlock(p);
            t.Expect(mdSwap == 0, $"the first block after a Genesis edit allocated {mdSwap:N0} bytes on the audio side");
            var saved = p.SaveState();
            using var q = lib.Create();
            q.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            q.LoadState(saved);
            var back = DllRead(q, fm);
            t.Expect(back != null && carriers.All(n => back.Get($"op{n}.tl") == Math.Min(127, patch.Get($"op{n}.tl") + 40)), "the project lost the Genesis patch edit");
            t.Expect(q.Test(HkInstHash) == p.Test(HkInstHash), "the restored Genesis working copy differs from the saved one");
            // refusals say why, and change nothing
            long hash = p.Test(HkInstHash);
            var bad = patch.Clone(); bad.Field("alg")!.Value = 99;
            t.Expect(DllApply(p, bad, out text) == 2 && text.Contains("alg") && p.Test(HkInstHash) == hash, $"an out-of-range patch must be refused with a reason and change nothing: '{text}'");
            // a patch that is in range but meant for another snapshot (a DAC sound of an index this game does not have) is refused by the writer
            var ghostDac = SegaInstruments.Template(ConsoleId.Genesis, InstrumentKind.OpnDac, 40, 10); ghostDac.Field("dac.rate")!.Value = 11025;
            t.Expect(DllApply(p, ghostDac, out text) == 1 && text.Contains("no instrument") && p.Test(HkInstHash) == hash, $"an instrument the working copy does not have must be refused: '{text}'");
            var junk = new byte[40]; fixed (byte* jb = junk) t.Expect(p.Test(HkInstApply, junk.Length, (nint)jb) == 2, "bytes that are not an instrument must be refused");
        }
        finally { DeleteQuietly(md); }
    }

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1; }
        return ~crc;
    }
}

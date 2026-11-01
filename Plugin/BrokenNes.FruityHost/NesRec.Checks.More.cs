// Round 8 (record-nes): the rest of `FruityHost nesrec-test`:
//
//   direct      NesDirect (the code Direct mode and the recorded-track presets share) gives what Bn2Plugin.Apply gave before round 8 (its old expressions, copied here) on 200,000 pitches and levels;
//               every period a channel can hold is reachable by a key plus a 14-bit bend at the bend ranges a track can have (pulse 8-$7FF, triangle 2-$7FF, noise 0-15)
//   capture     the capture keeps every write the bus reported (VRUN, SMB1, Contra with DMC samples: its entries = the bus's writes + its frame marks + its DMC marks), a copy per distinct DMC sample;
//               nothing allocated per write (300,000 writes); a starved capture COUNTS what it lost; the session's saved form reads back to the same bytes and refuses damage
//   transcribe  real games recorded through the capture, transcribed, and the MIDI file replayed (NesReplay.Library: VRUN's parser, FL's arithmetic, the presets' own register code): every value held
//               4 ms or more is the game's period, volume and duty, or the transcription says it is not; duty changes replayed by the duty tracks taking turns; a corrupted VRUN and SMB1 the same
//   smf         the file read back by an independent Python parser and by VRUN's (tracks, channels, notes, RPN ranges, tempo); a damaged file refused by it
//   section     REC1 version 2 (a NES session) round trip; damaged, cut, newer, version 1 with a NES session and version 2 with a SNES one refused; the SNES reader calls a NES section newer
//   live        live MIDI out beside a recording of the same frames: every note of the transcription (on pulse, triangle and noise) a note-on on its instrument's channel at the same moment
//               with the same key and velocity, every note ended; nothing allocated per write
using System.Diagnostics;
using BrokenNes2.Record;
using NesEmulator;
using NesEmulator.Plugin;

namespace BrokenNes.FruityHost;

internal static partial class NesRecChecks
{
    // ------------------------------------------------------------------------------------------------ direct

    public static void Direct(ImodelReport r)
    {
        var rnd = new Random(31);
        int diff = 0;
        string? first = null;
        for (int i = 0; i < 200_000; i++)
        {
            double cents = (float)((rnd.NextDouble() * 2 - 1) * 6000 + (i % 7 == 0 ? rnd.Next(-3, 4) * 0.001 : 0));
            float vol = (float)rnd.NextDouble();
            double master = rnd.Next(4) == 0 ? rnd.NextDouble() : 1.0;
            // Bn2Plugin.Apply as it was (round 7)
            double freq = 440.0 * Math.Pow(2.0, ((60.0 + cents / 100.0) - 69.0) / 12.0);
            int level = Math.Clamp((int)Math.Round(15 * vol * master), 0, 15);
            int pulse = (int)Math.Round(1789773.0 / (16.0 * freq) - 1.0); if (pulse > 0x7FF) pulse = 0x7FF;
            int tri = Math.Clamp((int)Math.Round(1789773.0 / (32.0 * freq) - 1.0), 2, 0x7FF);
            double semis = 60.0 + cents / 100.0;
            int idx = Math.Clamp(15 - (int)Math.Floor((semis - 24.0) / 3.0), 0, 15);
            if (NesDirect.PulsePeriod(cents) != pulse || NesDirect.TrianglePeriod(cents) != tri || NesDirect.NoiseIndex(cents) != idx || NesDirect.Level(vol, master) != level)
            {
                diff++;
                first ??= $"cents {cents}, vol {vol}: old {pulse}/{tri}/{idx}/{level}, NesDirect {NesDirect.PulsePeriod(cents)}/{NesDirect.TrianglePeriod(cents)}/{NesDirect.NoiseIndex(cents)}/{NesDirect.Level(vol, master)}";
            }
        }
        r.Expect(diff == 0, $"NesDirect differs from Direct mode's old arithmetic on {diff} of 200,000 inputs; first: {first}");
        // every period reachable: a key (the period's note) and a bend, at the ranges a track can have
        int[] ranges = [2, 3, 8, 12, 24, 48];
        int reach = 0, total = 0;
        string? unreachable = null;
        foreach (int range in ranges)
            for (int c = 0; c < 4; c++)
            {
                if (c == 1) continue;                              // (pulse 2 is pulse 1's arithmetic)
                int lo = c == 0 ? 8 : c == 2 ? 2 : 0, hi = c == 3 ? 15 : 0x7FF;
                for (int p = lo; p <= hi; p++)
                {
                    int key = c == 3 ? NesDirect.NoiseKey(p) : (int)Math.Round(NesDirect.MidiOf(p, c == 2));
                    if (key is < 0 or > 127) continue;
                    total++;
                    var (_, _, ideal, exact, _) = NesTranscribe.BendInterval(c, p, key, range);
                    if (exact && NesTranscribe.Register(c, key, ideal, range) == p) reach++;
                    else unreachable ??= $"channel {c} period {p:X3} at range {range}";
                }
            }
        r.Expect(reach == total, $"{total - reach} of {total} periods cannot be reached by a key and a bend; first: {unreachable}");
        r.Note($"NesDirect = Direct mode's old arithmetic on 200,000 pitches and levels; {reach:N0} of {total:N0} (channel, period, range) reachable exactly by a key and a bend");
    }

    // ------------------------------------------------------------------------------------------------ capture

    public static void Capture(ImodelReport r)
    {
        foreach (var (pick, park, script) in new[] { ("VRUN", 60, ""), ("Super Mario Bros. (JU) (PRG0)", 200, "180:start,4"), ("Contra (U)", 300, "300:start,4") })
        {
            var g = pick == "VRUN" ? NesRecGame.Vrun() : NesRecGame.Find(pick, script);
            if (g == null) { r.Warn($"NOT RUN: {pick} is not in {NesRecGame.Roster}"); continue; }
            g.Run(park);
            var s = g.Record((int)(Math.Min(GameSeconds, 20) * NesAudioMachine.FramesPerSecond), "capture");
            int frames = s.RegVals.Count(v => v >> 8 == NesSession.MarkerFrame), dmcMarks = s.RegVals.Count(v => v >> 8 == NesSession.MarkerDmc);
            long writes = s.Count - frames - dmcMarks;
            r.Expect(writes == g.TapWrites && s.Dropped == 0, $"{g.Name}: the capture holds {writes:N0} writes, the bus reported {g.TapWrites:N0}; {s.Dropped} lost");
            r.Expect(s.BaseExact && s.Chip == "APU_FIX", $"{g.Name}: the starting state is not the chip's own (exact {s.BaseExact}, chip {s.Chip})");
            var back = NesSession.FromPayload(s.ToPayload(), out var why);
            r.Expect(back != null && back.ToPayload().AsSpan().SequenceEqual(s.ToPayload()), $"{g.Name}: the session's saved form does not read back to itself ({why})");
            if (pick.StartsWith("Contra"))
            {
                r.Expect(s.Dmc.Length > 0 && dmcMarks > 0 && s.DmcMissing == 0, $"{g.Name}: {s.Dmc.Length} DMC sample copies, {dmcMarks} marks, {s.DmcMissing} missing (Contra plays DMC samples)");
                r.Expect(s.Dmc.Select(d => Convert.ToHexString(d.Data)).Distinct().Count() == s.Dmc.Length, $"{g.Name}: two DMC copies hold the same bytes");
                // the copies are the bytes the game's own DMC reads: the cartridge at the address, as the CPU sees it
                var d0 = s.Dmc[0];
                int same = 0;
                for (int i = 0; i < d0.Length; i++) { g.M.Nes.TryCpuToPrgIndex((ushort)(d0.Address + i), out int pi); if (g.M.Nes.PeekPrg(pi) == d0.Bytes[i]) same++; }
                r.Expect(same == d0.Length, $"{g.Name}: the first DMC copy differs from the cartridge in {d0.Length - same} of {d0.Length} bytes");
            }
            r.Note($"{g.Name}: {s.Seconds:0.0} s, {writes:N0} writes = the bus's count, {frames} frames, {s.Dmc.Length} DMC samples copied ({dmcMarks} starts marked)");
        }
        // nothing allocated per write: 300,000 writes into a capture with its chunks ready (the frame marks too)
        {
            var g = NesRecGame.Vrun();
            g.Run(30);
            var rec = new NesRecorder();
            rec.Arm();
            for (int i = 0; i < 12; i++) rec.Feed();
            var latch = new byte[0x18];
            uint w = g.M.CopyApuLatch(latch);
            rec.Begin(g.M.Nes, latch, w, g.M.ApuCoreId);
            long clock = g.M.ApuClock;
            // the same loop warms every path first (a write, a frame mark, a change of chunk) and the feeder tops the chunks up; then a collection, so that neither the JIT's first passes nor a
            // collection inside the window shows in this thread's count (both did, once each in a combined selftest: 4,008 bytes; a real allocation per write would be 300,000 x 24 bytes or more)
            static void Pump(NesRecorder rec, long from, int n) { for (int i = 0; i < n; i++) { rec.OnWrite((ushort)(0x4000 + i % 20), (byte)(i * 7), from + i * 3); if (i % 500 == 0) rec.FrameEnd(from + i * 3); } }
            Pump(rec, clock, 70_000);
            rec.Feed();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            Pump(rec, clock + 70_000 * 3, 300_000);
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            r.Expect(a1 - a0 == 0, $"the capture allocated {a1 - a0:N0} bytes for 300,000 writes");
            // a starved capture counts what it lost
            rec.StallFeeder(true);
            long before = rec.Count;
            int sent = 0;
            for (int i = 0; i < NesRecorder.ChunkWrites * 12; i++) { rec.OnWrite(0x4002, (byte)i, clock + 1_000_000 + i); sent++; if (rec.Halted) break; }
            r.Expect(rec.Dropped > 0 && rec.Count - before + rec.Dropped == sent, $"a starved capture: {sent:N0} writes sent, {rec.Count - before:N0} kept, {rec.Dropped:N0} counted as lost");
            r.Note($"0 bytes allocated for 300,000 writes; a starved capture kept {rec.Count - before:N0} and counted {rec.Dropped:N0} lost of {sent:N0}");
        }
        // damage: every cut and a flipped byte in each part
        {
            var g = NesRecGame.Vrun();
            g.Run(60);
            var p = g.Record(300, "damage").ToPayload();
            int refused = 0, tried = 0;
            for (int cut = 0; cut < p.Length; cut += Math.Max(1, p.Length / 300)) { tried++; if (NesSession.FromPayload(p[..cut], out _) == null) refused++; }
            var newer = (byte[])p.Clone(); newer[1] = 9;
            bool newerRefused = NesSession.FromPayload(newer, out var wn) == null;
            r.Expect(refused == tried && newerRefused && wn?.Contains("newer") == true, $"the session's saved form: {refused} of {tried} cuts refused; a newer one: {wn}");
        }
    }

    // ------------------------------------------------------------------------------------------------ transcribe (real games, replayed)

    public static readonly (string Pick, int Park, string Script)[] TranscribeGames =
    [
        ("VRUN", 60, ""),
        ("Super Mario Bros. (JU) (PRG0)", 200, "180:start,4"),
        ("Adventures of Bayou Billy", 900, "300:start,4"),             // (its music starts about 12 s after power-on)
        ("Adventures of Rad Gravity", 300, "300:start,4"),
        ("Base Wars", 300, "300:start,4"),
        ("Contra (U)", 300, "300:start,4"),
        ("Alien 3", 300, "300:start,4"),
        ("Anticipation", 300, "300:start,4"),
    ];

    public static void Transcribe(ImodelReport r)
    {
        int games = 0;
        foreach (var (pick, park, script) in TranscribeGames)
        {
            var g = pick == "VRUN" ? NesRecGame.Vrun() : NesRecGame.Find(pick, script);
            if (g == null) { r.Warn($"NOT RUN: {pick} is not in {NesRecGame.Roster}"); continue; }
            g.Run(park);
            var s = g.Record((int)(GameSeconds * NesAudioMachine.FramesPerSecond), pick);
            TranscribeAndReplay(r, g.Name, s);
            games++;
        }
        // corrupted: random bytes of work RAM changed (seeded) before the take: VRUN (24 bytes: 300 silence its music) and SMB1 (300)
        foreach (var (pick, park, script, bytes) in new[] { ("VRUN", 120, "", 24), ("Super Mario Bros. (JU) (PRG0)", 400, "180:start,4", 300) })
        {
            var g = pick == "VRUN" ? NesRecGame.Vrun() : NesRecGame.Find(pick, script);
            if (g == null) continue;
            g.Run(park);
            var rnd = new Random(9);
            for (int i = 0; i < bytes; i++) g.M.PokeRam((ushort)rnd.Next(0x800), (byte)rnd.Next(256));
            var s = g.Record((int)(GameSeconds * NesAudioMachine.FramesPerSecond), "corrupted " + pick);
            TranscribeAndReplay(r, g.Name + " (corrupted, seed 9)", s);
        }
        if (games == 0) r.Warn("NOT RUN: no game (VRUN is built in: this cannot happen)");
    }

    public static NesTranscription TranscribeAndReplay(ImodelReport r, string label, NesSession s)
    {
        var tr = NesTranscribe.Run(s, new NesOptions { Name = "take" });
        var m = MidiReplay.Read(tr.Midi);
        foreach (var p in m.Problems.Take(3)) r.Expect(false, $"{label}: the file: {p}");
        r.Expect(m.Notes.Count == tr.Tracks.Sum(t => t.Notes.Count), $"{label}: the file holds {m.Notes.Count} notes, the tracks {tr.Tracks.Sum(t => t.Notes.Count)}");
        var played = NesReplay.Library(tr.Midi, tr);
        NesReplay.Compare(r, label, tr, played, out string line);
        // the tracks: a channel and its timbre each
        var keys = tr.Tracks.Select(t => (t.Channel, t.Duty, t.Mode, t.DmcCopy)).ToList();
        r.Expect(keys.Distinct().Count() == keys.Count, $"{label}: two tracks for one channel and timbre");
        r.Note($"{line}; {tr.Tracks.Count} tracks ({string.Join(", ", tr.Tracks.Select(t => t.Name[(t.Name.IndexOf(" - ", StringComparison.Ordinal) + 6)..]))}); {tr.Facts[1]}");
        foreach (var f in tr.Facts.Skip(2).Where(f => f.Contains("automation") || f.Contains("changed duty") || f.Contains("DMC") || f.Contains("left out"))) r.Note("   " + f);
        return tr;
    }

    // ------------------------------------------------------------------------------------------------ smf

    public static void Smf(ImodelReport r)
    {
        var g = NesRecGame.Find("Base Wars", "300:start,4") ?? NesRecGame.Vrun();
        g.Run(300);
        var tr = NesTranscribe.Run(g.Record(1200, "smf"), new NesOptions { Name = "Smf" });
        var m = MidiReplay.Read(tr.Midi);
        r.Expect(m.Format == 1 && m.TrackNames.Count == tr.Tracks.Count + 1 && m.Problems.Count == 0, $"VRUN's parser: format {m.Format}, {m.TrackNames.Count} tracks (a conductor and {tr.Tracks.Count}), problems: {string.Join("; ", m.Problems.Take(2))}");
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var c = m.TrackChannels[k + 1];
            r.Expect(c.Count == 1 && c.First() == tr.Tracks[k].MidiChannel, $"track {k + 1} uses MIDI channels {string.Join(",", c)} (its own is {tr.Tracks[k].MidiChannel})");
            r.Expect(m.Of(k + 1, tr.Tracks[k].MidiChannel).Range == tr.Tracks[k].BendRange, $"track {k + 1}'s RPN range {m.Of(k + 1, tr.Tracks[k].MidiChannel).Range}, its own {tr.Tracks[k].BendRange}");
            var one = MidiReplay.Read(tr.TrackMidi(k));
            r.Expect(one.Format == 0 && one.Notes.Count == tr.Tracks[k].Notes.Count, $"track {k + 1} alone: format {one.Format}, {one.Notes.Count} notes of {tr.Tracks[k].Notes.Count}");
        }
        string? py = PythonExe();
        if (py == null) { r.Warn("NOT RUN: no Python 3 on this machine: the file is not read by the independent parser"); return; }
        string dir = Path.Combine(Path.GetTempPath(), $"bn2_nesrec_{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "nes.mid");
        File.WriteAllBytes(file, tr.Midi);
        string script = Path.Combine(MidiFixtures.RepoRoot(), @"Plugin\BrokenNes.FruityHost\Record.SmfCheck.py");
        var p2 = MidiFixtures.Run(py, ["-I", script, file], timeoutSec: 60);
        r.Expect(p2.Exit == 0, $"Record.SmfCheck.py refused the file (exit {p2.Exit}): {p2.Output.Trim()}");
        if (p2.Exit != 0) return;
        var lines = p2.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        r.Expect(lines.FirstOrDefault(l => l.StartsWith("format")) == $"format 1 tracks {tr.Tracks.Count + 1} ppq {RecMidi.Ppq}", $"the Python parser's header: {lines.FirstOrDefault()}");
        for (int k = 0; k < tr.Tracks.Count; k++)
        {
            var t = tr.Tracks[k];
            string want = $"track {k + 1} channels {t.MidiChannel + 1} notes {t.Notes.Count} range {t.BendRange}";
            r.Expect(lines.Any(l => l.StartsWith(want)), $"the Python parser has no line \"{want}...\"; it said: {lines.FirstOrDefault(l => l.StartsWith($"track {k + 1} ")) ?? "nothing"}");
        }
        string tempoLine = lines.FirstOrDefault(l => l.StartsWith("tempo")) ?? "";
        r.Expect(tempoLine.StartsWith($"tempo {tr.Map.Count} first {tr.Map[0].MicrosPerQuarter}"), $"the Python parser's tempo: \"{tempoLine}\"");
        var bad = (byte[])tr.Midi.Clone();
        bad[18] ^= 0x40;
        File.WriteAllBytes(file, bad);
        var p3 = MidiFixtures.Run(py, ["-I", script, file], timeoutSec: 60);
        r.Expect(p3.Exit != 0, "the Python parser accepted a file whose first track lies about its length");
        r.Note($"{g.Name}: {tr.Tracks.Count} tracks read back by VRUN's parser and Python's: " + string.Join(" / ", lines.Take(3)));
    }

    private static string? PythonExe()
    {
        foreach (var cand in new[] { "python", "py" })
        {
            try { var t = MidiFixtures.Run(cand, ["-I", "-c", "import sys; print(sys.version_info[0])"], timeoutSec: 60); if (t.Exit == 0 && t.Output.Trim().StartsWith('3')) return cand; }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------ section

    public static void Section(ImodelReport r)
    {
        var g = NesRecGame.Vrun();
        g.Run(60);
        var s = g.Record(600, "section");
        var b = RecSection.BuildNes(s);
        var back = RecSection.ReadAny(b, out var why, out var nes);
        r.Expect(back == null && nes != null && nes.ToPayload().AsSpan().SequenceEqual(s.ToPayload()), $"REC1 version 2 does not read back to the session ({why})");
        r.Expect(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b) == RecSection.VersionNes, "a NES recording's section is not version 2");
        RecSection.Read(b, out var old);
        r.Expect(old?.Contains("newer") == true, $"the SNES reader (an older BrokenNes2's) said of a NES section: {old}");
        int refused = 0, tried = 0;
        var rnd = new Random(4);
        for (int i = 0; i < 200; i++)
        {
            var d = (byte[])b.Clone();
            d[rnd.Next(d.Length)] ^= (byte)(1 << rnd.Next(8));
            tried++;
            RecSection.ReadAny(d, out var w2, out var n2);
            if (n2 == null && w2 != null) refused++;
        }
        for (int cut = 1; cut < b.Length; cut += Math.Max(1, b.Length / 100)) { tried++; RecSection.ReadAny(b[..cut], out var w3, out var n3); if (n3 == null && w3 != null) refused++; }
        r.Expect(refused == tried, $"{tried - refused} of {tried} damaged or cut NES sections were not refused");
        // version 1 holding a NES session, version 2 holding a SNES one: refused
        var v1 = (byte[])b.Clone(); v1[0] = 1; Resign(v1);
        RecSection.ReadAny(v1, out var w4, out var n4);
        r.Expect(n4 == null && w4?.Contains("damaged") == true, $"a version 1 section holding a NES session: {w4}");
        var drv = new SynthDriver(120, 5);
        var srec = new SpcRecorder(); srec.Arm(); for (int i = 0; i < 20; i++) srec.Feed();
        drv.Play(srec, 4);
        var snes = RecSection.Build(srec.Finish(drv.Aram, drv.Regs, "S", "synthetic", "", 1_760_000_000, 5));
        var v2 = (byte[])snes.Clone(); v2[0] = 2; Resign(v2);
        RecSection.ReadAny(v2, out var w5, out var n5);
        r.Expect(n5 == null && w5?.Contains("damaged") == true, $"a version 2 section holding a SNES session: {w5}");
        var v3 = (byte[])b.Clone(); v3[0] = 3; Resign(v3);
        RecSection.ReadAny(v3, out var w6, out _);
        r.Expect(w6?.Contains("newer") == true, $"a version 3 section: {w6}");
        r.Note($"REC1 version 2: {b.Length:N0} bytes round trip; {refused} of {tried} damaged or cut sections refused; the SNES reader says \"{old}\"");

        static void Resign(byte[] x) => BitConverter.TryWriteBytes(x.AsSpan(x.Length - 4), BrokenNes2.Music.Crc32.Of(x.AsSpan(0, x.Length - 4)));
    }

}

// flp-test's checks of the noise volume and the song room (2026-11-10):
//   noise-volume  a NES noise part with hits under full level: CC 15 in the MIDI, the converter's 0xD4 at the noise stream's start, and the sound-test ROM's $400C writes carry each
//                 hit's level; all hits full: no CC, no 0xD4, every hit $3F (the old sound, byte for byte the old stream); CTW SMS: the same levels reach the PSG's noise
//                 attenuation (nes_apu.c's vol2att); the user's songs with soft noise hits start their noise with 0xD4
//   room          every song converts without truncation up to the template's measured song room (VRUN NES and CTW SMS): a channel is truncated only past the room; the
//                 Reverse Coaster songs the fence used to cut (Title Theme square 1 at 1,289 bytes, battle's triangle at 1,700) fit both
using BrokenNes2.Music;
using BrokenNes2.Music.Flp;
using Vrun.Audio;

namespace BrokenNes.FruityHost;

internal static class FlpNoiseRoomChecks
{
    public static readonly FlpChecks.Check[] All =
    [
        new("noise-volume", "the noise's levels reach the MIDI (CC 15), the stream (0xD4) and the ROMs' noise volume (VRUN NES $400C, CTW SMS the PSG); all-full parts stay as they were", NoiseVolume),
        new("room", "the converter truncates at each template's measured song room (VRUN NES, CTW SMS), not the game's 1280: the songs that passed 1280 fit", Room),
    ];

    private static byte[] NoiseProject(int console, int[] vels) => FlpChecks.Project(150,
        [("Lead", "BrokenNes2", FlpChecks.Bn2State(console: console, place: 0)), ("Hat", "BrokenNes2", FlpChecks.Bn2State(console: console, place: 3))],
        [(1, [new(0, 96, 72, 0), .. vels.Select((v, i) => new FlpChecks.SNote(i * 24, 12, 60, 1, Vel: v))])],
        [(0, 1, 96)]);

    /// <summary>The volumes of the sounding $400C writes, in order (repeats of the same value merged).</summary>
    private static List<int> NoiseWrites(byte[] rom, int frames)
    {
        var m = new NesEmulator.Plugin.NesAudioMachine(rom, null, "FIX", "FIX", "FIX") { HostSampleRate = 44100 };
        var o = new List<int>();
        m.RecordTap = (a, val, _) => { if (a == 0x400C && (val & 0x0F) != 0 && (o.Count == 0 || o[^1] != (val & 0x0F))) o.Add(val & 0x0F); };
        var buf = new float[4096];
        for (int f = 0; f < frames; f++) { m.RunFrame(); while (m.ReadAvailable(buf) > 0) { } if (m.Crashed) break; }
        return o;
    }

    public static void NoiseVolume(ImodelReport r)
    {
        var t = MidiChecks.BundledTemplate();
        int[] soft = [128, 64, 32, 128], loud = [128, 128, 128, 128];
        var want = soft.Select(v => (int)Math.Round(15 * (v / 128.0), MidpointRounding.AwayFromZero)).ToList();      // the back end's level (Volume 1000 = 1.0)
        foreach (var (vels, tag) in new[] { (soft, "soft"), (loud, "loud") })
        {
            var v = VrunNesBackend.Build(FlpSong.Read(NoiseProject(0, vels)), "noise" + tag);
            var midi = MidiFile.Parse(v.Midi);
            bool cc = Midi2Nes.FindFirstCc(midi, Midi2Nes.NoiseVolumeCc).TryGetValue(9, out int ccv) && ccv >= 64;
            r.Expect(cc == (tag == "soft"), $"{tag}: CC 15 on the noise's MIDI channel is {(cc ? "on" : "off")}");
            var b = SongConvert.Convert(v.Midi, out var why, t.Region);
            r.Expect(b != null, $"{tag}: the converter refused it: {why}");
            if (b == null) continue;
            int nes = Array.IndexOf(b.Facts.Source, 10);
            r.Expect(nes == 3, $"{tag}: the noise part is on NES channel {nes}");
            if (nes != 3) continue;
            var st = b.Channels[3];
            r.Expect((st[0] == Midi2Nes.OpNoiseVolume) == (tag == "soft"), $"{tag}: the noise stream starts with ${st[0]:X2}");
            var got = NoiseWrites(t.Patch(b.Channels, b.Facts.LoopFrames, "noise"), Math.Min(b.Facts.LoopFrames, 400) + 20);
            var expect = tag == "soft" ? want : [15];
            r.Expect(got.Take(expect.Count).SequenceEqual(expect), $"{tag}: the ROM's noise hits at {string.Join(", ", got.Take(8))}, FL's levels are {string.Join(", ", expect)}");
            if (tag == "loud")
            {
                // the old way, byte for byte: converted without the noise volume at all
                var plain = Midi2Nes.ConvertSong(midi, "noiseloud", _ => { }, new ConvertOptions { Bends = true, Duty = true, MaxChannelBytes = t.Region });
                r.Expect(plain.Channels[3].SequenceEqual(st), "loud: the noise stream differs from the conversion without noise volume");
            }
            r.Note($"VRUN NES {tag}: CC 15 {(cc ? "127" : "none")}, stream {(st[0] == Midi2Nes.OpNoiseVolume ? "$D4 + " : "")}{st.Length} bytes, $400C hits {string.Join(", ", got.Take(8))}");
        }

        // CTW SMS: the same levels through nes_apu.c to the PSG's noise attenuation
        var (tmpl, man) = FlpSmsChecks.Template();
        var sv = CtwSmsBackend.Build(FlpSong.Read(NoiseProject(3, soft)), "noisesms");
        var rom = CtwSmsBackend.Patch(tmpl, man, sv, out var swhy);
        r.Expect(rom != null, $"CTW SMS: the test ROM refused the song: {swhy}");
        r.Expect(!sv.Fence.Any(f => f.Rule == "opcodes" && f.Level == FenceLevel.Over), "CTW SMS: the fence says $D4 is an opcode the game cannot play");
        r.Expect(sv.Fence.Any(f => f.Rule == "volume" && f.Text.Contains("noise levels")), "CTW SMS: no fence line for the noise levels");
        if (rom != null)
        {
            var run = FlpSmsChecks.RunSms(rom, 1500);
            var atts = new List<int>();
            foreach (var f in run) if (f.Att[3] != 15 && (atts.Count == 0 || atts[^1] != f.Att[3])) atts.Add(f.Att[3]);
            var wantAtt = want.Select(l => CtwSmsBackend.Vol2Att[l]).ToList();
            bool found = Enumerable.Range(0, Math.Max(0, atts.Count - wantAtt.Count + 1)).Any(i => atts.Skip(i).Take(wantAtt.Count).SequenceEqual(wantAtt));
            r.Expect(found, $"CTW SMS: the PSG noise attenuations {string.Join(", ", atts.Take(12))} do not hold the levels' {string.Join(", ", wantAtt)}");
            r.Note($"CTW SMS: noise attenuation {string.Join(", ", atts.Take(8))} (levels {string.Join(", ", want)} -> {string.Join(", ", wantAtt)})");
        }

        // the user's songs: a song with soft noise hits starts its noise with $D4, one without does not
        foreach (var (name, v, b, _) in FlpChecks.Roms(r))
        {
            var model = FlpSong.Read(File.ReadAllBytes(Path.Combine(FlpChecks.SongsDir, name + ".flp")));
            bool quiet = model.Instruments.Where(i => i.Bn2 is { Mode: "direct", Console: 0 } x && x.Channel == 3).SelectMany(i => FlpMono.Resolve([i]).Segs).Any(s => s.V.Level is > 0 and < 15);
            int nes = Array.IndexOf(b.Facts.Source, 10);
            if (nes < 0) { r.Note($"{name,-14} no noise part"); continue; }
            bool op = b.Channels[nes].Length > 0 && b.Channels[nes][0] == Midi2Nes.OpNoiseVolume;
            r.Expect(op == quiet, $"{name}: soft noise hits {quiet}, the noise stream {(op ? "starts" : "does not start")} with $D4");
            r.Note($"{name,-14} soft noise hits: {(quiet ? "yes, $D4" : "no, full volume")}");
        }
    }

    public static void Room(ImodelReport r)
    {
        var t = MidiChecks.BundledTemplate();
        r.Expect(t.Region > Midi2Nes.MaxChannelBytes, $"the VRUN NES template's room is {t.Region} bytes, not more than the game's {Midi2Nes.MaxChannelBytes}");
        var smsRoom = CtwSmsBackend.Room;
        r.Expect(smsRoom != null && smsRoom.All(x => x >= Midi2Nes.MaxChannelBytes), $"the CTW SMS room is {(smsRoom == null ? CtwSmsBackend.RoomProblem : string.Join("/", smsRoom))}");
        r.Note($"song room: VRUN NES {t.Region} bytes a channel; CTW SMS {(smsRoom == null ? "?" : string.Join(" / ", smsRoom))}");
        foreach (var (name, v, b, _) in FlpChecks.Roms(r))
        {
            for (int c = 0; c < 4; c++)
                r.Expect(!(b.Facts.Truncated[c] && b.Facts.Bytes[c] <= t.Region - 2) && !(!b.Facts.Truncated[c] && b.Facts.Bytes[c] > t.Region), $"{name} {MusicTemplate.ChannelNames[c]}: {b.Facts.Bytes[c]} bytes, truncated {b.Facts.Truncated[c]} in a room of {t.Region}");
            var past = Enumerable.Range(0, 4).Where(c => b.Facts.Bytes[c] > Midi2Nes.MaxChannelBytes).Select(c => $"{MusicTemplate.ChannelNames[c]} {b.Facts.Bytes[c]}").ToList();
            var sv = smsRoom == null ? null : CtwSmsBackend.Build(FlpSms(name), name);
            bool smsOver = sv != null && sv.Fence.Any(f => f.Rule == "bytes" && f.Level == FenceLevel.Over);
            if (sv?.Build != null) r.Expect(!smsOver && !sv.Build.Facts.AnyTruncated, $"{name}: CTW SMS truncates or fences the bytes ({string.Join(" + ", sv.Build.Channels.Select(x => x.Length))} in {string.Join("/", smsRoom!)})");
            r.Note($"{name,-14} {string.Join(" + ", b.Facts.FinalBytes)} bytes{(past.Count > 0 ? $" (past 1280: {string.Join(", ", past)}: fits)" : "")}{(sv?.Build != null ? $"; CTW SMS {string.Join(" + ", sv.Build.Channels.Select(x => x.Length))}" : "")}");
        }
        foreach (var (name, ch) in new[] { ("Title Theme", 0), ("battle", 2) })
        {
            var f = FlpChecks.Roms(r).FirstOrDefault(x => x.Name == name);
            if (f.Build == null) { r.Warn($"NOT RUN: no {name}.flp"); continue; }
            int nes = Array.IndexOf(f.Build.Facts.Source, new[] { 1, 2, 3, 10 }[ch]);
            r.Expect(nes >= 0 && f.Build.Facts.Bytes[nes] > Midi2Nes.MaxChannelBytes && !f.Build.Facts.Truncated[nes],
                $"{name} {VrunNesBackend.NesNames[ch]}: {(nes < 0 ? "not played" : $"{f.Build.Facts.Bytes[nes]} bytes, truncated {f.Build.Facts.Truncated[nes]}")} (it passed 1280 and must fit the room)");
        }
    }

    /// <summary>The user's song with its NES instances moved to the Master System console (CTW SMS reads the Master System instances).</summary>
    private static FlpSongModel FlpSms(string name)
    {
        var m = FlpSong.Read(File.ReadAllBytes(Path.Combine(FlpChecks.SongsDir, name + ".flp")));
        foreach (var i in m.Instruments) if (i.Bn2 is { Console: 0 } b) { b.Console = 3; b.ConsoleName = "Master System"; }
        return m;
    }
}

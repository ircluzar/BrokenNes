// Relink (2026-10-18): the user's report from FL (build r12): "I drag and drop the MIDI file, it creates MIDI Out channels, then I replace them one by one and the counter never increases ... adding
// new BrokenNes channel instances, it doesn't detect them either". Through the real plugin DLL (Native AOT), for a SNES recording and a NES one:
//   joining    every instance FL could make while tracks are on offer joins them AT ONCE, and the Record window's counter says so after each one, before any note: an empty state, NON-default
//              parameter pushes after construction (the r12 rule took those for the user's own configuration), a state of ours with other values, a channel FL names after its track (it takes
//              that track at once: FHD_GetContextInfo answered by the test host), a host that answers with a number (not used); a project state with a preset makes one leave, a change in its
//              editor (a real click on a slider) makes one leave
//   anchors    the editor's "Link to recording" button and the Record window's "Link all BrokenNes2 channels", by real mouse messages; instances made before the tracks were offered (FL had set
//              their values) are linked by Link all; the counter and the list as the window DRAWS them (editor hooks 677 / 679)
//   play       every linked channel then finds its track from its notes (key + song position) and settles; the named one plays its track from its first note; 0 bytes allocated on the audio thread
//   log        the host log ran for every instance while the tracks were on offer, with the box unticked, into the test's scratch file; the folder standing for Documents got nothing
// Each check is run again with a deliberate bug switched on in the DLL (plugin hook 657) and must fail. The `hostlog` test (last in the suite) checks that the whole run left the user's real
// Documents\BrokenNes2 host log.txt untouched.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2.Record;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkLinkState = 720, HkLinkHand = 721, HkUnlink = 722, HkLinkAllHook = 723, HkRole = 724, HkCounter = 725, HkChannelList = 726, HkBanner = 727, HkAskName = 728, HkNameGot = 729,
        HkNameAsks = 730, HkPristine = 731, HkLogPath = 732, HkLogFlush = 733, HkLinkNote = 734, HkMembers = 735, HkLogUser = 736, HkLogAuto = 737, HkAlive = 738;
    private const int EdRCounter = 677, EdRBanner = 678, EdRList = 679;
    private const int IdRecLinkAllBtn = 2036, IdLinkBtn = 2060, IdUnlinkBtn = 2061;

    private static string LongText(PluginInstance p, int hook)
    {
        var c = new char[16000];
        fixed (char* cp = c) { long n = p.Test(hook, c.Length, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private static string EdLong(PluginInstance p, int hook)
    {
        var c = new char[16000];
        fixed (char* cp = c) { long n = p.Test(hook, 16000 & 0xFFFF, (nint)cp); return n < 0 ? "" : new string(c, 0, (int)Math.Min(n, c.Length)); }
    }

    private delegate void RelinkBody(T t, string dir, bool nes);

    private void RelinkTest(T t)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bn2_relink_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string presets = Path.Combine(dir, "presets");
        Directory.CreateDirectory(presets);
        string? oldPresets = Environment.GetEnvironmentVariable("BROKENNES_PRESETS_DIR");
        Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", presets);            // (the recordings' presets go into a scratch library)
        using (var h = lib.Create()) { h.Test(120, 0, 1); h.Test(HkRTempo, 0, 120_000); h.Test(HkRMutant, 0, 0); h.Test(HkOfferSet, 0, 0); h.Test(HkLogUser, 0, 0); }
        try
        {
            foreach (bool nes in new[] { false, true })
            {
                var tt = new T();
                RelinkScenario(tt, dir, nes);
                string tag = nes ? "NES" : "SNES";
                t.Failures.AddRange(tt.Failures.Select(f => $"{tag}: {f}"));
                t.Warnings.AddRange(tt.Warnings.Select(f => $"{tag}: {f}"));
                t.Info.AddRange(tt.Info.Select(f => $"{tag}: {f}"));
            }
            int caught = 0, armed = 0;
            relinkMutantPass = true;
            foreach (var (mutant, nes, why) in RelinkMutants())
            {
                armed++;
                var tt = new T();
                using (var probe = lib.Create()) probe.Test(HkRMutant, 0, mutant);
                try { RelinkScenario(tt, dir, nes); }
                catch (Exception e) { tt.Expect(false, "exception " + e.Message); }
                finally { using var probe = lib.Create(); probe.Test(HkRMutant, 0, 0); probe.Test(HkOfferSet, 0, 0); }
                if (tt.Failures.Count > 0) { caught++; t.Note($"deliberate bug '{why}' ({(nes ? "NES" : "SNES")}) -> failed: {(tt.Failures[0].Length > 200 ? tt.Failures[0][..197] + "..." : tt.Failures[0])}"); }
                else t.Expect(false, $"deliberate bug '{why}' ({(nes ? "NES" : "SNES")}) was not caught: that check cannot fail");
            }
            t.Note($"{caught} of {armed} deliberate bugs caught");
        }
        finally
        {
            relinkMutantPass = false;
            using (var h = lib.Create()) { h.Test(120, 0, 0); h.Test(HkOfferSet, 0, 0); h.Test(HkRMutant, 0, 0); h.Test(HkRTempo, 0, 120_000); h.Test(HkLogUser, 0, 0); }
            Environment.SetEnvironmentVariable("BROKENNES_PRESETS_DIR", oldPresets);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private bool relinkMutantPass;

    private static (int Mutant, bool Nes, string Why)[] RelinkMutants() =>
    [
        (RecMutants.ParamPushUnjoins, false, "FL's non-default parameter pushes make a joined channel leave (the r12 rule)"),
        (RecMutants.NoAutoJoin, false, "instances made while tracks are on offer do not join them"),
        (RecMutants.CounterCountsFoundOnly, false, "the counter counts only the channels that found their track (what the user saw in r12)"),
        (RecMutants.LinkButtonIgnored, false, "Link to recording says linked and does nothing"),
        (RecMutants.LinkAllIgnored, false, "Link all counts the channels and links none"),
        (RecMutants.NameIgnored, false, "the channel's name never takes its track"),
        (RecMutants.UsersOwnStateJoins, false, "a project state with a preset stays joined"),
        (RecMutants.LogNotAutomatic, false, "the host log does not run by itself while tracks are on offer"),
        (RecMutants.LogIgnoresTestPath, false, "the host log ignores the test's file (it would write the user's Documents)"),
        (RecMutants.EnginesNotPrebuilt, false, "the track's engine is built on the audio thread"),
        (RecMutants.NoAutoJoin, true, "instances made while NES tracks are on offer do not join them"),
        (RecMutants.NameIgnored, true, "the channel's name never takes its NES track"),
    ];

    /// <summary>The recording on offer: the instance holding it (on emulator 6, Direct mode, as a project brings it), the MIDI file, the track names, the tracks the channels play (most notes first;
    /// NES DMC tracks left out: no engine plays them yet).</summary>
    private (PluginInstance? P, MidiReplay? M, string[] Names, List<int> Order) RelinkSource(T t, bool nes)
    {
        byte[] section;
        List<int> order;
        if (nes)
        {
            NesSession? s;
            using (var g = NesRomGame(null, 3))
            {
                DriveFrames(g, _ => 0, 0, 60);
                s = NesTake(t, g, 900, "VRUN");
                section = RBytes(g, HkRSection);
                g.Test(HkRForget);
            }
            if (s == null) return (null, null, [], []);
            var tr = NesTranscribe.Run(s, new NesOptions { Name = s.Name, FlTempo = 120 });
            order = Enumerable.Range(0, tr.Tracks.Count).Where(k => tr.Tracks[k].Channel != 4 && tr.Tracks[k].Notes.Count >= 6).OrderByDescending(k => tr.Tracks[k].Notes.Count).ToList();
        }
        else
        {
            section = SynthSection(20, out var session);
            var tr = RecTranscribe.Run(session, new RecOptions { Name = session.Name, FlTempo = 120 });
            order = Enumerable.Range(0, tr.Tracks.Count).OrderByDescending(k => tr.Tracks[k].Notes.Count).ToList();
        }
        var p = Adopted(t, section);
        if (p == null) return (null, null, [], []);
        long until = Environment.TickCount64 + 20000;
        while (p.Test(HkOffer) <= 0 && Environment.TickCount64 < until) Thread.Sleep(10);
        var m = MidiReplay.Read(RBytes(p, HkRMidi));
        var names = RText(p, 5).Split(" | ");
        return (p, m, names, order);
    }

    private void RelinkScenario(T t, string dir, bool nes)
    {
        var made = new List<PluginInstance>();
        var hosts = new List<HostSim>();
        PluginInstance? p = null;
        nint parent = 0;
        PluginInstance Make() { var q = lib.Create(); made.Add(q); q.Dispatcher(Fpd.SetSampleRate, 0, 48000); return q; }
        try
        {
            long alive;
            using (var probe = lib.Create()) alive = probe.Test(HkAlive) - 1;
            t.Expect(alive == 0, $"{alive} BrokenNes2 instance(s) from earlier checks are still alive: the counts below would include them");
            string scratchLog = HostLogGuard.ScratchFile, standIn = Path.Combine(HostLogGuard.ScratchDocuments, "BrokenNes2 host log.txt");
            try { if (File.Exists(standIn)) File.Delete(standIn); } catch (IOException) { }
            long logStart = File.Exists(scratchLog) ? new FileInfo(scratchLog).Length : 0;

            // made BEFORE the tracks are offered: two FL had set values on (they are not pristine: the offer leaves them alone), one pristine (it joins when the tracks are offered)
            var pre1 = Make(); pre1.SetParam(PVolume, 700);
            var pre2 = Make(); pre2.SetParam(PPan, 30);
            var pre3 = Make();
            var (src, m, names, order) = RelinkSource(t, nes);
            p = src;
            if (p == null || m == null) return;
            if (order.Count < 2 || names.Length < order.Max() + 1) { t.Expect(false, $"the recording has {order.Count} usable tracks ({string.Join(", ", names)})"); return; }
            t.Expect(m.TrackNames.Count > order.Max() + 1 && order.All(k => m.TrackNames[k + 1] == names[k]), $"the MIDI file's track names (what FL names the imported channels) are not the offered tracks' names: {string.Join(" / ", m.TrackNames)}");
            t.Expect(names.Distinct().Count() == names.Length, $"two tracks have the same name: {string.Join(" / ", names)}");
            double fileBpm = 60e6 / m.Tempo[0].Us;
            p.Test(HkRTempo, 0, (nint)Math.Round(fileBpm * 1000));

            var steps = new List<string>();
            void Step(string what, string want)
            {
                string got = LongText(pre3, HkCounter);
                steps.Add($"{what}: \"{got}\"");
                t.Expect(got == want, $"{what}: the counter says \"{got}\", it should say \"{want}\"");
            }
            void Role(PluginInstance q, int want, string what) { long r = q.Test(HkRole); t.Expect(r == want, $"{what}: role {r} ({DllText(q, HkLinkNote)}), expected {want} (2 not linked, 3 linked and waiting, 4 found its track)"); }

            Step("the tracks offered (made before: one pristine, two FL set values on)", "1 channel joined, 0 found their track; 2 not linked");
            Role(pre3, 3, "the pristine instance made before the offer"); Role(pre1, 2, "an instance made before the offer, FL set its volume"); Role(p, 1, "the instance that holds the recording");

            // FL's Replace, every way it could make the instance
            var r1 = Make(); r1.LoadState([]);
            Step("a replacing channel with an empty state", "2 channels joined, 0 found their track; 2 not linked");
            var r2 = Make();
            foreach (var (i, v) in new[] { (PVolume, 640), (PPan, -25), (PDuty, 2), (PNoiseMode, 1), (PInstrument, 3), (PEmulator, 7), (PChannel, 2) }) r2.SetParam(i, v);
            t.Expect(r2.Test(HkPristine) == 0, "the parameter pushes did not reach the instance");
            Step("a replacing channel FL pushed NON-default values on (volume, pan, duty, noise mode, instrument, emulator 7, channel)", "3 channels joined, 0 found their track; 2 not linked");
            byte[] other;
            using (var o = lib.Create()) { o.SetParam(PVolume, 500); o.SetParam(PPan, -40); o.SetParam(PDuty, 3); other = o.SaveState(); }
            var r3 = Make(); r3.LoadState(other);
            Step("a replacing channel given a state of ours with other values (no game, preset or track)", "4 channels joined, 0 found their track; 2 not linked");
            // FL names the channel after its track (an imported channel keeps the MIDI track's name): the instance takes it at once, before any note
            int nameTrack = order[1];
            var hs = new HostSim { TrackName = names[nameTrack] }; hosts.Add(hs);
            var r4 = lib.Create(hs); made.Add(r4); r4.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            r4.Dispatcher(Fpd.PluginLoaded);
            long until = Environment.TickCount64 + 5000;
            while (r4.Test(HkTrackOf) != nameTrack && Environment.TickCount64 < until) Thread.Sleep(10);
            t.Expect(r4.Test(HkTrackOf) == nameTrack && r4.Test(HkBindHow) == 3 && r4.Test(342) == 1 && r4.Test(352) == 1 && hs.ContextInfoCalls > 0,
                $"the channel FL named \"{names[nameTrack]}\": plays track {r4.Test(HkTrackOf) + 1} (should be {nameTrack + 1}) found by {r4.Test(HkBindHow)} (3 = its name), holds a preset {r4.Test(342)}, preset mode {r4.Test(352)}, FL asked {hs.ContextInfoCalls} times: {DllText(r4, HkTrackStatus)}");
            Step("a replacing channel FL named after its track (taken before any note)", "5 channels joined, 1 found its track; 2 not linked");
            var hs5 = new HostSim { RawContextAnswer = 7 }; hosts.Add(hs5);
            var r5 = lib.Create(hs5); made.Add(r5); r5.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            r5.Dispatcher(Fpd.PluginLoaded);
            t.Expect(r5.Test(HkNameGot) == -1 && r5.Test(HkRole) == 3 && hs5.ContextInfoCalls > 0, $"a host that answers the name call with a number: name {r5.Test(HkNameGot)} (-1: none taken), role {r5.Test(HkRole)}");
            Step("a replacing channel whose host answers the name call with a number", "6 channels joined, 1 found its track; 2 not linked");
            // a project state that is clearly the user's own (it holds a preset): it leaves
            byte[] withTrack = r4.SaveState();
            int trk = FindSection(withTrack, 0x314B5254u);
            t.Expect(trk >= 0 && FindSection(withTrack, 0x31535250u) >= 0, "the named channel's state lacks its preset or its track");
            byte[] withPreset = trk < 0 ? withTrack : withTrack[..trk].Concat(withTrack[(trk + 8 + BitConverter.ToInt32(withTrack, trk + 4))..]).ToArray();
            var u1 = Make();
            Step("a new channel (before its state)", "7 channels joined, 1 found its track; 2 not linked");
            u1.LoadState(withPreset);
            t.Expect(u1.Test(HkRole) == 2 && DllText(u1, HkLinkNote).Contains("preset"), $"a channel given a project state with a preset: role {u1.Test(HkRole)}, '{DllText(u1, HkLinkNote)}'");
            Step("that channel's project state holds a preset (it leaves)", "6 channels joined, 1 found its track; 3 not linked");

            // the editor: a change made in it makes a channel that joined by itself leave; the banner's "Link to recording" (real mouse) links it again
            var e1 = Make();
            Step("one more new channel", "7 channels joined, 1 found its track; 3 not linked");
            parent = Win32Host.CreateParent();
            e1.ShowEditor(parent);
            Win32Host.Pump(80);
            t.Expect(e1.Test(HkRole) == 3, $"opening the editor made the channel leave ({DllText(e1, HkLinkNote)})");
            t.Expect(EdLong(e1, EdRBanner).StartsWith("LINKED TO THE RECORDING | Press Play in FL and I will find my track from my first note"), $"the banner of a linked channel says '{EdLong(e1, EdRBanner)}'");
            ClickControl(t, e1, 20 + PVolume, "the Volume slider");
            Win32Host.Pump(80);
            Role(e1, 2, "after a change in its editor");
            Step("that channel changed in its editor (it leaves)", "6 channels joined, 1 found its track; 4 not linked");
            t.Expect(EdLong(e1, EdRBanner).StartsWith("TRACKS OF"), $"the banner of an unlinked channel says '{EdLong(e1, EdRBanner)}'");
            ClickControl(t, e1, IdLinkBtn, "Link to recording");
            Win32Host.Pump(80);
            t.Expect(e1.Test(HkLinkState) == 2 && e1.Test(HkRole) == 3, $"Link to recording: link {e1.Test(HkLinkState)}, role {e1.Test(HkRole)}");
            Step("Link to recording pressed in its editor", "7 channels joined, 1 found its track; 3 not linked");
            // the Record window: the counter as it draws it, then Link all (real mouse)
            ClickControl(t, e1, 2020, "Record button");
            Win32Host.Pump(80);
            string drawn = EdLong(e1, EdRCounter);
            t.Expect(drawn == "7 channels joined, 1 found its track; 3 not linked", $"the Record window draws '{drawn}'");
            ClickControl(t, e1, IdRecLinkAllBtn, "Link all BrokenNes2 channels");
            Win32Host.Pump(80);
            t.Expect(new[] { pre1, pre2, u1 }.All(q => q.Test(HkLinkState) == 2), $"Link all: pre1 {pre1.Test(HkLinkState)}, pre2 {pre2.Test(HkLinkState)}, the preset channel {u1.Test(HkLinkState)} (2 = linked by hand)");
            Step("Link all pressed in the Record window", "10 channels joined, 1 found its track");
            drawn = EdLong(e1, EdRCounter);
            string list = EdLong(e1, EdRList);
            t.Expect(drawn == "10 channels joined, 1 found its track", $"after Link all the Record window draws '{drawn}'");
            long n4 = r4.Test(HkInstanceNo), n5 = r5.Test(HkInstanceNo);
            t.Expect(list.Contains($"#{n4} \"{names[nameTrack]}\": plays track {nameTrack + 1} (found by its channel's name)") && list.Contains($"#{n5}: waiting for its first note"), $"the window's list of channels: '{list}'");

            // Play: each linked channel finds its track from its notes (the first 12 of its track, at the file's song positions) and settles; the named one plays its track from its first note
            var linked = new (string Name, PluginInstance Q)[] { ("pre3 (pristine, made before)", pre3), ("r1 (empty state)", r1), ("r2 (non-default pushes)", r2), ("r3 (other values)", r3), ("r5 (a number for a name)", r5),
                ("u1 (preset state, Link all)", u1), ("e1 (Link to recording)", e1), ("pre1 (Link all)", pre1), ("pre2 (Link all)", pre2) };
            var others = order.Where(k => k != nameTrack).ToList();
            var parts = new List<ReplacePart>();
            for (int i = 0; i < linked.Length; i++)
            {
                int k = others[i % others.Count];
                long cut = m.Notes.Where(n => n.Track == k + 1).Select(n => n.On).OrderBy(x => x).Skip(11).FirstOrDefault(long.MaxValue);
                parts.Add(Part(linked[i].Name, linked[i].Q, m, k, k, n => n.On <= cut));
            }
            long cutName = m.Notes.Where(n => n.Track == nameTrack + 1).Select(n => n.On).OrderBy(x => x).Skip(11).FirstOrDefault(long.MaxValue);
            var named = Part("r4 (named after its track)", r4, m, nameTrack, nameTrack, n => n.On <= cutName);
            parts.Add(named);
            e1.HideEditor();
            long alloc = PlayReplace(parts, m);
            until = Environment.TickCount64 + 8000;
            while (parts.Any(x => x != named && x.P.Test(HkBindState) != 2) && Environment.TickCount64 < until) Thread.Sleep(20);
            foreach (var pt in parts)
            {
                long track = pt.P.Test(HkTrackOf), bstate = pt.P.Test(HkBindState), how = pt.P.Test(HkBindHow);
                bool settled = pt == named ? how == 3 : bstate == 2 && how == 1;
                t.Expect(track == pt.Expect && settled && pt.P.Test(352) == 1, $"{pt.Name}: plays track {track + 1} (should be {pt.Expect + 1}), state {bstate}, found by {how}, preset mode {pt.P.Test(352)}: {DllText(pt.P, HkTrackStatus)}");
            }
            t.Expect(named.BoundAtFirst == nameTrack, $"the named channel did not play its track from its first note (it played track {named.BoundAtFirst + 1} then)");
            if (!nes)
            {
                var marks = ProbeMarks(r4);
                t.Expect(marks.Count > 0 && named.Handles.Count > 0 && marks[0] == (int)(named.Handles[0] & 0xFFFF), "the named channel's first note was not played by its track's preset");
            }
            t.Expect(alloc == 0, $"{alloc:N0} bytes allocated on the audio thread while {parts.Count} linked channels found their tracks and played");
            Step("after Play", "10 channels joined, 10 found their track");

            // the host log: it ran by itself while the tracks were on offer (the box unticked), for every instance, into the test's scratch file; nothing in the folder standing for Documents
            t.Expect(pre3.Test(HkLogUser) == 0 && pre3.Test(HkLogAuto) == 1, $"the log: the user's box {pre3.Test(HkLogUser)} (should be 0), automatic {pre3.Test(HkLogAuto)} (should be 1)");
            pre3.Test(HkLogFlush);
            Thread.Sleep(300);
            string log = "";
            try { using var fs = new FileStream(scratchLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); fs.Seek(Math.Min(logStart, fs.Length), SeekOrigin.Begin); log = new StreamReader(fs).ReadToEnd(); }
            catch (IOException) { }
            var missing = new List<string>();
            foreach (var q in made)
            {
                long no = q.Test(HkInstanceNo);
                bool before = q == pre1 || q == pre2 || q == pre3;          // (made before the log ran: named when the tracks were offered)
                string want = before ? $"#{no} present when the tracks were offered" : $"#{no} created";
                if (!log.Contains(want)) missing.Add(want);
            }
            foreach (var q in new[] { pre3, r1, r2, r3, r4, r5, u1, e1 })
            {
                long no = q.Test(HkInstanceNo);
                if (!log.Contains($"#{no} joined the recording")) missing.Add($"#{no} joined");
            }
            if (!log.Contains($"#{n4} FL's name for this channel (FL opened the instance (FPD_PluginLoaded), FHD_GetContextInfo CI_TrackName): \"{names[nameTrack]}\"")) missing.Add("the name FL gave r4");
            if (!log.Contains($"#{n5} FL's name for this channel") || !log.Contains("(a number, not a pointer to a name: not used)")) missing.Add("the number r5's host gave");
            if (!log.Contains($"#{u1.Test(HkInstanceNo)} left the recording: its project state holds a preset")) missing.Add("u1 left");
            if (!log.Contains($"#{e1.Test(HkInstanceNo)} left the recording: you set Volume")) missing.Add("e1 left");
            if (!log.Contains($"#{e1.Test(HkInstanceNo)} linked to the recording by hand: the Link to recording button")) missing.Add("e1 linked");
            if (!log.Contains($"#{pre1.Test(HkInstanceNo)} linked to the recording by hand: Link all")) missing.Add("pre1 linked by Link all");
            if (!log.Contains($"#{r2.Test(HkInstanceNo)} ProcessParam index=1 (Volume) value=640")) missing.Add("r2's parameter pushes");
            t.Expect(missing.Count == 0, $"the host log ({log.Length:N0} chars of {scratchLog}) lacks: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? $" (and {missing.Count - 8} more)" : "")}");
            t.Expect(!File.Exists(standIn), $"the host log wrote into the folder standing for the user's Documents ({standIn}): the test's file was ignored");
            t.Expect(HostLogGuard.RealFileTouched() == null, "the user's real host log: " + HostLogGuard.RealFileTouched());
            if (!relinkMutantPass)
            {
                t.Note(string.Join("; ", steps));
                t.Note($"{log.Split('\n').Length} host log lines for this scenario; 0 bytes on the audio thread: {alloc}");
                if (Environment.GetEnvironmentVariable("BROKENNES_KEEP_HOSTLOG") is { Length: > 0 } keep) File.WriteAllText(keep + (nes ? ".nes.txt" : ".snes.txt"), log);
            }
        }
        finally
        {
            if (parent != 0) Win32Host.DestroyParent(parent);
            foreach (var q in made) q.Dispose();
            foreach (var h in hosts) h.Dispose();
            if (p != null) { p.Test(HkRDeal, 0, 0); p.Test(HkRForget); p.Dispose(); }
            using (var h = lib.Create()) h.Test(HkOfferSet, 0, 0);
        }
    }

    /// <summary>The last check of the run: the user's real Documents\BrokenNes2 host log.txt is as it was when the run began (every test logs into a scratch file); the guard's comparison is shown
    /// able to fail on a scratch file; the log follows the user's box when no tracks are on offer (on: every new instance logs; off: none does).</summary>
    private void HostLogTest(T t)
    {
        using (var h = lib.Create())
        {
            h.Test(HkOfferSet, 0, 0);
            string path = DllText(h, HkLogPath);
            t.Expect(path == HostLogGuard.ScratchFile && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(HostLogGuard.RealPath), StringComparison.OrdinalIgnoreCase), $"the plugin logs into '{path}' (the test's file is '{HostLogGuard.ScratchFile}')");
            long before = File.Exists(path) ? new FileInfo(path).Length : 0;
            h.Test(HkLogUser, 0, 1);
            long on;
            using (var x = lib.Create()) on = x.Test(HkInstanceNo);
            h.Test(HkLogUser, 0, 0);
            long off;
            using (var y = lib.Create()) off = y.Test(HkInstanceNo);
            h.Test(HkLogFlush);
            Thread.Sleep(300);
            string log = "";
            try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); fs.Seek(Math.Min(before, fs.Length), SeekOrigin.Begin); log = new StreamReader(fs).ReadToEnd(); }
            catch (IOException) { }
            t.Expect(log.Contains($"#{on} created") && !log.Contains($"#{off} created"), $"with the box ticked instance #{on} logged {(log.Contains($"#{on} created") ? "its creation" : "nothing")}; unticked (no tracks on offer) #{off} logged {(log.Contains($"#{off} created") ? "its creation" : "nothing")}");

            // the cap: past 1 MB the file becomes "<name>.old.txt" and a new one begins (and with the deliberate bug "never capped" this check fails)
            string? Rotation()
            {
                h.Test(HkLogFlush);
                string old = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".old" + Path.GetExtension(path));
                if (File.Exists(old)) File.Delete(old);
                File.AppendAllText(path, new string('.', 1_100_000) + Environment.NewLine);
                h.Test(HkLogUser, 0, 1);
                long z;
                using (var q = lib.Create()) z = q.Test(HkInstanceNo);
                h.Test(HkLogUser, 0, 0);
                h.Test(HkLogFlush);
                long size = File.Exists(path) ? new FileInfo(path).Length : -1;
                string head = File.Exists(path) ? File.ReadLines(path).FirstOrDefault() ?? "" : "";
                bool oldOk = File.Exists(old) && new FileInfo(old).Length >= 1_100_000;
                return size is > 0 and < 200_000 && oldOk && head.StartsWith("(the log went over 1024 KB") && File.ReadAllText(path).Contains($"#{z} created")
                    ? null : $"past 1 MB: the log is {size:N0} bytes (should start again), the older part {(oldOk ? "kept" : "missing")} at {old}, first line '{(head.Length > 80 ? head[..80] : head)}'";
            }
            string? rot = Rotation();
            t.Expect(rot == null, rot ?? "");
            h.Test(HkRMutant, 0, RecMutants.LogNotCapped);
            string? rotBug;
            try { rotBug = Rotation(); }
            finally { h.Test(HkRMutant, 0, 0); }
            t.Expect(rotBug != null, "the deliberate bug 'the log is never capped' was not caught: the cap check cannot fail");
            if (rot == null && rotBug != null) t.Note("the cap: past 1 MB the log starts again and keeps the older part (the deliberate bug 'never capped' is caught: " + rotBug + ")");
        }
        // the guard's comparison can fail: a scratch file changed under it is reported
        string probe = Path.Combine(Path.GetTempPath(), $"bn2_guard_probe_{Guid.NewGuid():N}.txt");
        File.WriteAllText(probe, "a");
        var snap = HostLogGuard.SnapFile(probe);
        File.AppendAllText(probe, "b");
        t.Expect(HostLogGuard.SnapFile(probe) != snap, "the guard did not see a file change (its check of the real log could not fail)");
        File.Delete(probe);
        string? touched = HostLogGuard.RealFileTouched();
        t.Expect(touched == null, "the user's real host log: " + touched);
        t.Note($"the user's real log ({HostLogGuard.RealPath}) untouched by the run; this run logged into {HostLogGuard.ScratchFile}");
    }
}

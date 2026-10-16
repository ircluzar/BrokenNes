// Track C4b: a project keeps the WHOLE machine of a game in ROM mode, including every change made to it (a corrupted byte of work RAM stands for any), and Instrument Runaway OFF after the project is reopened
// goes on from that state. Tiny ROMs with a counter in work RAM that also paints the picture stand in for games (the repository carries none): after the same frames and the same corruption, a machine that came back
// from a project must be byte for byte the machine that never left, and show the same picture.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // test hooks of the plugin (see Bn2Plugin.TestDispatcher)
    private const int HkPeek = 81, HkPoke = 82, HkRun = 83, HkHash = 84, HkNote = 85, HkRestore = 86, HkCap = 87, HkSection = 88, HkSupported = 89, HkNoteShown = 90;
    private const int FramesBefore = 90, FramesAfter = 120;
    private const int SpareOffset = 0x1F0;       // an offset of work RAM that none of the test ROMs touches (the NES has 2 KB)
    private const byte Corrupt = 0xA5, CorruptSpare = 0x5A;

    private sealed record StateCase(string Name, Func<PluginInstance> Open, int Counter);

    /// <summary>A plugin opened from a saved project the way FL does it, once the game is built.</summary>
    private PluginInstance Reopen(byte[] project, int rate = 48000)
    {
        var p = lib.Create();
        p.Dispatcher(Fpd.SetSampleRate, 0, rate);
        p.LoadState(project);
        WaitForRom(p);
        return p;
    }

    /// <summary>The emulator's state note (null: none).</summary>
    private static string? NoteOf(PluginInstance p)
    {
        int n = (int)p.Test(HkNote);
        if (n < 0) return null;
        var buf = new char[n + 1];
        fixed (char* c = buf) p.Test(HkNote, n, (nint)c);
        return new string(buf, 0, n);
    }

    /// <summary>Where a section with this tag starts in a saved project (after the EMU2 place record), -1 when there is none.</summary>
    private static int FindSection(byte[] project, string tag)
    {
        var place = "EMU2"u8.ToArray();
        int at = -1;
        for (int i = 0; i + 4 <= project.Length && at < 0; i++)
            if (project.AsSpan(i, 4).SequenceEqual(place)) at = i;
        if (at < 0) return -1;
        var want = System.Text.Encoding.ASCII.GetBytes(tag);
        for (int i = at + 12; i + 8 <= project.Length; i++)
            if (project.AsSpan(i, 4).SequenceEqual(want)) return i;
        return -1;
    }

    /// <summary>The project as an older build saved it: everything before the game-state section.</summary>
    private static byte[] WithoutGameSection(byte[] project)
    {
        int at = FindSection(project, "GAM1");
        return at < 0 ? project : project[..at];
    }

    private static byte[] Splice(byte[] project, int at, byte[] insert, int remove = 0) => [.. project[..at], .. insert, .. project[(at + remove)..]];

    private static byte[] UnknownSection() => [.. "ZZZ1"u8.ToArray(), 5, 0, 0, 0, 1, 2, 3, 4, 5];

    // ------------------------------------------------------------------------------------------------

    private void RunawayState(T t)
    {
        string dir = Path.GetTempPath(), tag = Guid.NewGuid().ToString("N");
        string Make(string ext, byte[] rom) { string p = Path.Combine(dir, $"bn2_{tag}{ext}"); File.WriteAllBytes(p, rom); return p; }
        string nes = Make(".nes", MakeNesCounterRom()), gb = Make(".gb", MakeGbRom(counter: true)), gbc = Make(".gbc", MakeGbRom(counter: true, color: true));
        string sms = Make(".sms", MakeSmsCounterRom()), gg = Make(".gg", MakeSmsCounterRom(gameGear: true)), md = Make(".md", MakeMdRom(fm: true, dac: false, counter: true)), sfc = Make(".sfc", MakeSnesRom());
        var files = new[] { nes, gb, gbc, sms, gg, md, sfc };
        try
        {
            var families = new List<StateCase>
            {
                new("NES (the built-in game)", () => RomPlugin(), -1),
                new("NES (counter ROM)", () => SegaGame(nes, 0, "FIX"), 0x10),
                new("Game Boy", () => SegaGame(gb, 1, "DMG"), 0),
                new("Game Boy Color", () => SegaGame(gbc, 1, "DMG"), 0),
                new("Master System (PSG)", () => SegaGame(sms, 3, "SMS"), 0),
                new("Master System (FM chip)", () => SegaGame(sms, 3, "SMSFM"), 0),
                new("Game Gear", () => SegaGame(gg, 3, "SMS"), 0),
                new("Genesis", () => SegaGame(md, 4, "MD"), 0),
            };
            foreach (var fam in families) StateFamily(t, fam);

            // the SNES: with a state to keep it goes through the same scenarios; without one it says so, visibly, and never restarts silently
            bool snesState;
            using (var s = SegaGame(sfc, 2, "SNES")) { s.Test(HkRun, 0, 10); snesState = s.Test(HkSupported) == 1; }
            if (snesState) StateFamily(t, new StateCase("SNES", () => SegaGame(sfc, 2, "SNES"), -1));
            else StateUnsupported(t, () => SegaGame(sfc, 2, "SNES"), "SNES");
            t.Note(snesState ? "SNES games keep their state in a project" : "SNES: no state support yet, the project says so");

            StateEdges(t, sms, nes);
        }
        finally { foreach (var f in files) DeleteQuietly(f); }
    }

    /// <summary>One family: the machine that came back from a project, frozen or running, is the machine that never left.</summary>
    private void StateFamily(T t, StateCase fam)
    {
        (long Hash, long Pic, long Frames, int Counter, int Spare) Control()
        {
            using var c = fam.Open();
            c.Test(HkRun, 0, FramesBefore);
            Corruption(c, fam);
            c.Test(HkRun, 0, FramesAfter);
            return (c.Test(HkHash), c.Test(10), c.Test(11), fam.Counter >= 0 ? (int)c.Test(HkPeek, fam.Counter) : -1, (int)c.Test(HkPeek, SpareOffset));
        }
        var control = Control();
        var again = Control();
        t.Expect(control == again, $"{fam.Name}: two runs of the same frames and the same corruption differ ({control} / {again}): the machine is not deterministic, the comparison would mean nothing");
        t.Expect(control.Frames == FramesBefore + FramesAfter, $"{fam.Name}: the control ran {control.Frames} frames, expected {FramesBefore + FramesAfter}");
        if (fam.Counter >= 0) t.Expect(control.Counter != Corrupt, $"{fam.Name}: the counter should have moved on from the corrupted value (it is {control.Counter})");

        foreach (bool frozen in new[] { true, false })
        {
            string what = $"{fam.Name}, {(frozen ? "saved frozen (Runaway on)" : "saved running")}";
            byte[] project; long hashAtSave, picAtSave;
            using (var a = fam.Open())
            {
                a.Test(HkRun, 0, FramesBefore);
                Corruption(a, fam);
                if (frozen) a.Test(47, 0, 1);
                t.Expect(a.Test(45) == (frozen ? 1 : 0), $"{what}: Runaway is {a.Test(45)}");
                hashAtSave = a.Test(HkHash);
                picAtSave = a.Test(10);
                project = a.SaveState();
            }
            int section = FindSection(project, "GAM1");
            t.Expect(section >= 0, $"{what}: the project has no game-state section");
            t.Expect(hashAtSave != 0 && picAtSave != 0, $"{what}: nothing to compare (hash {hashAtSave}, picture {picAtSave})");

            using var b = Reopen(project);
            long hashAtLoad = b.Test(HkHash);
            t.Expect(b.Test(HkRestore) == 1, $"{what}: the restore result is {b.Test(HkRestore)} ({NoteOf(b)})");
            t.Expect(b.Test(45) == (frozen ? 1 : 0), $"{what}: after reopening Runaway is {b.Test(45)}, saved {(frozen ? 1 : 0)}");
            t.Expect(b.Test(11) == FramesBefore, $"{what}: the game is at frame {b.Test(11)}, it was saved at frame {FramesBefore} (a restart from power-on is the bug)");
            t.Expect(hashAtLoad == hashAtSave, $"{what}: the machine that came back is not the machine that was saved");
            if (fam.Counter >= 0) t.Expect(b.Test(HkPeek, fam.Counter) == Corrupt, $"{what}: the corrupted byte came back as {b.Test(HkPeek, fam.Counter)}, saved {Corrupt}");
            t.Expect(b.Test(HkPeek, SpareOffset) == CorruptSpare, $"{what}: the second corrupted byte came back as {b.Test(HkPeek, SpareOffset)}, saved {CorruptSpare}");
            if (frozen)
            {
                t.Expect(b.Test(10) == picAtSave, $"{what}: the frozen picture is not the one that was saved ({b.Test(10)} / {picAtSave})");
                b.Test(HkRun, 0, 30);
                RenderBlocks(b, 0.2);
                t.Expect(b.Test(11) == FramesBefore && b.Test(HkHash) == hashAtSave, $"{what}: the frozen game ran ({b.Test(11)} frames)");
                t.Expect(b.SaveState().SequenceEqual(project), $"{what}: saving the reopened project again does not give the same bytes");
                b.Test(47, 0, 0);
            }
            b.Test(HkRun, 0, FramesAfter);
            t.Expect(b.Test(45) == 0, $"{what}: Runaway is still on after it was switched off");
            t.Expect(b.Test(11) == control.Frames, $"{what}: {b.Test(11)} frames after the same run, the control has {control.Frames}");
            t.Expect(b.Test(HkHash) == control.Hash, $"{what}: after the same frames the machine differs from the one that never left");
            t.Expect(b.Test(10) == control.Pic, $"{what}: after the same frames the picture differs from the one that never left");
            if (fam.Counter >= 0) t.Expect(b.Test(HkPeek, fam.Counter) == control.Counter, $"{what}: the corrupted counter is {b.Test(HkPeek, fam.Counter)}, the control's {control.Counter}");
            t.Expect(b.Test(HkPeek, SpareOffset) == control.Spare, $"{what}: the second corrupted byte is {b.Test(HkPeek, SpareOffset)}, the control's {control.Spare}");
            t.Expect(NoteOf(b) == null, $"{what}: a note is shown for a state that came back: {NoteOf(b)}");
            if (frozen) t.Note($"{fam.Name}: the section adds {project.Length - section:N0} bytes to the project");
        }
    }

    /// <summary>The corruption the scenarios share: the game's counter (when it has one) and a byte nothing touches are overwritten, between frames.</summary>
    private static void Corruption(PluginInstance p, int counter)
    {
        if (p.Test(HkPeek, SpareOffset) < 0) return;      // a game with no work RAM to reach
        if (counter >= 0) p.Test(HkPoke, counter, Corrupt);
        p.Test(HkPoke, SpareOffset, CorruptSpare);
    }

    private static void Corruption(PluginInstance p, StateCase fam) => Corruption(p, fam.Counter);

    // ------------------------------------------------------------------------------------------------
    // a game with no state to keep: the project says so, where the user sees it

    private void StateUnsupported(T t, Func<PluginInstance> open, string name)
    {
        byte[] project;
        using (var a = open())
        {
            a.Test(HkRun, 0, 30);
            a.Test(47, 0, 1);
            t.Expect(NoteOf(a) == null, $"{name}: a note before anything was saved: {NoteOf(a)}");
            project = a.SaveState();
            string? saveNote = NoteOf(a);
            t.Expect(saveNote != null && saveNote.StartsWith($"{name} games cannot keep their state in a project yet"), $"{name}: saving a game that has no state said '{saveNote}'");
        }
        using (var b = Reopen(project))
        {
            string? note = NoteOf(b);
            t.Expect(b.Test(HkRestore) == 2, $"{name}: the restore result is {b.Test(HkRestore)}, expected 2 (not restored)");
            t.Expect(b.Test(45) == 1, $"{name}: the project was saved in Runaway and came back with it {b.Test(45)}");
            t.Expect(note == $"{name} games cannot keep their state in a project yet: the game restarts when Instrument Runaway is switched off", $"{name}: the note while frozen is '{note}'");
            nint parent = Win32Host.CreateParent();
            try
            {
                b.ShowEditor(parent);
                for (int k = 0; k < 4; k++) { RenderBlocks(b, 0.1); Win32Host.Pump(60); }
                t.Expect(b.Test(HkNoteShown) == 1, $"{name}: the editor does not show the note");
                b.HideEditor();
            }
            finally { Win32Host.DestroyParent(parent); }
            b.Test(47, 0, 0);
            string? after = NoteOf(b);
            t.Expect(b.Test(45) == 0 && after != null && after.StartsWith($"{name} games cannot keep their state in a project yet") && after.EndsWith("the game started from power-on"), $"{name}: the note after Runaway was switched off is '{after}'");
            b.Test(HkRun, 0, 5);
            t.Expect(b.Test(11) == 5, $"{name}: the restarted game is at frame {b.Test(11)}, expected 5");
        }
        // an older project (no section at all) says the same
        using (var c = Reopen(WithoutGameSection(project)))
        {
            string? note = NoteOf(c);
            t.Expect(note != null && note.StartsWith($"{name} games cannot keep their state in a project yet"), $"{name}: an older project said '{note}'");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // the edges: another game, older projects, unknown / damaged / hostile / oversize sections, several instances

    private void StateEdges(T t, string sms, string nes)
    {
        var fam = new StateCase("SMS", () => SegaGame(sms, 3, "SMS"), 0);
        byte[] project; long hashAtSave;
        using (var a = fam.Open())
        {
            a.Test(HkRun, 0, FramesBefore);
            Corruption(a, fam);
            hashAtSave = a.Test(HkHash);
            project = a.SaveState();
        }
        int sec = FindSection(project, "GAM1");
        t.Expect(sec >= 0, "edges: the base project has no game-state section");
        if (sec < 0) return;

        // an older project (saved before the section existed) loads exactly as before: the game starts from power-on, nothing is said
        using (var old = Reopen(WithoutGameSection(project)))
        {
            old.Test(HkHash);
            t.Expect(old.Test(HkRestore) == 0 && NoteOf(old) == null && old.Test(11) == 0 && old.Test(45) == 0, $"older project: restore {old.Test(HkRestore)}, note '{NoteOf(old)}', frame {old.Test(11)}, Runaway {old.Test(45)}");
            old.Test(HkRun, 0, 5);
            t.Expect(old.Test(11) == 5, $"older project: the game ran {old.Test(11)} frames after 5");
        }
        // sections nobody here knows are skipped, before and after ours
        foreach (var (name, data) in new (string, byte[])[]
        {
            ("an unknown section before", Splice(project, sec, UnknownSection())),
            ("an unknown section after", [.. project, .. UnknownSection()]),
        })
        {
            using var u = Reopen(data);
            t.Expect(u.Test(HkHash) == hashAtSave && u.Test(HkRestore) == 1, $"{name}: the state did not come back (restore {u.Test(HkRestore)})");
        }
        // damaged, truncated and hostile sections: a visible refusal, a game at power-on, no crash
        int payload = sec + 8, length = BitConverter.ToInt32(project, sec + 4);
        var damaged = (byte[])project.Clone();
        damaged[payload + length - 40] ^= 0x5A;
        foreach (var (name, data, word) in new (string, byte[], string)[]
        {
            ("a damaged section", damaged, "damaged"),
            ("a truncated section", project[..(payload + length / 2)], "cut short"),
            ("a section that claims 1 GB", PatchU32(project, payload + 16, 0x40000000), "damaged"),
            ("a section of absurd length", PatchU32(project, sec + 4, 0x7FFFFFF0), "damaged"),
        })
        {
            using var h = Reopen(data);
            h.Test(HkHash);
            string? note = NoteOf(h);
            t.Expect(h.Test(HkRestore) == 2 && note != null && note.Contains(word) && h.Test(11) == 0, $"{name}: restore {h.Test(HkRestore)}, note '{note}', frame {h.Test(11)} (wanted a note with '{word}' and a game at power-on)");
            h.Test(HkRun, 0, 5);
            t.Expect(h.Test(12) == 0 && h.Test(11) == 5, $"{name}: the game at power-on did not run ({h.Test(11)} frames, crashed {h.Test(12)})");
        }

        // the game file is another game now: the state is not used, the game starts from power-on, and the note says so
        string other = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sms");
        File.WriteAllBytes(other, MakeSmsCounterRom());
        byte[] mismatch;
        using (var a = SegaGame(other, 3, "SMS"))
        {
            a.Test(HkRun, 0, FramesBefore);
            Corruption(a, fam);
            mismatch = a.SaveState();
        }
        try
        {
            var changed = MakeSmsCounterRom(); changed[0x100] ^= 0x01;       // one byte of another game
            File.WriteAllBytes(other, changed);
            using var m = Reopen(mismatch);
            m.Test(HkHash);
            string? note = NoteOf(m);
            t.Expect(m.Test(HkRestore) == 2 && note != null && note.Contains("different game") && m.Test(11) == 0 && m.Test(HkPeek, 0) != Corrupt, $"another game: restore {m.Test(HkRestore)}, note '{note}', frame {m.Test(11)}");
            m.Test(HkRun, 0, 5);
            t.Expect(m.Test(11) == 5 && m.Test(12) == 0, $"another game: the new game ran {m.Test(11)} frames");
        }
        finally { DeleteQuietly(other); }

        // a state too big for the project is refused when it is saved, visibly, and again when the project is opened
        byte[] big;
        try
        {
            using var a = fam.Open();
            a.Test(HkRun, 0, FramesBefore);
            a.Test(HkCap, 0, 2000);
            big = a.SaveState();
            string? saveNote = NoteOf(a);
            t.Expect(saveNote != null && saveNote.Contains("too big"), $"oversize: saving said '{saveNote}'");
            t.Expect(big.Length < project.Length / 2 + 2000, $"oversize: the project is {big.Length:N0} bytes, it still carries the state ({project.Length:N0} with it)");
        }
        finally { using var reset = lib.Create(); reset.Test(HkCap, 0, 0); }
        using (var o = Reopen(big))
        {
            o.Test(HkHash);
            string? note = NoteOf(o);
            t.Expect(o.Test(HkRestore) == 2 && note != null && note.Contains("too big") && o.Test(11) == 0, $"oversize: reopened with restore {o.Test(HkRestore)}, note '{note}', frame {o.Test(11)}");
        }

        // several instances of one project share one game: only the first to open puts the state back
        byte[] first, second; long hashFirst;
        using (var i1 = fam.Open())
        using (var i2 = Companion(channelParam: 1))
        {
            i1.Test(HkRun, 0, FramesBefore);
            hashFirst = i1.Test(HkHash);
            first = i1.SaveState();
            i1.Test(HkRun, 0, 20);
            t.Expect(i2.Test(19) == 2, $"several instances: {i2.Test(19)} instances on the emulator");
            second = i2.SaveState();
        }
        using (var b1 = Reopen(first))
        using (var b2 = Reopen(second))
        {
            t.Expect(b2.Test(19) == 2, $"several instances: {b2.Test(19)} instances on the reopened emulator");
            t.Expect(b1.Test(HkHash) == hashFirst && b2.Test(11) == FramesBefore, $"several instances: the second to open put its state back over the first's (frame {b2.Test(11)}, expected {FramesBefore})");
        }

        // saving a project again while its game is still being built keeps what the project had (nothing is lost by saving at the wrong moment)
        byte[] nesProject; long nesHash;
        using (var a = SegaGame(nes, 0, "FIX"))
        {
            a.Test(HkRun, 0, FramesBefore);
            Corruption(a, 0x10);
            nesHash = a.Test(HkHash);
            nesProject = a.SaveState();
        }
        using (var loading = lib.Create())
        {
            loading.Dispatcher(Fpd.SetSampleRate, 0, 48000);
            loading.LoadState(nesProject);                      // (the game is being built now)
            byte[] again = loading.Test(9) == 0 ? loading.SaveState() : nesProject;
            WaitForRom(loading);
            t.Expect(again.SequenceEqual(nesProject), "a project saved again while its game was still loading lost the game's state");
            t.Expect(loading.Test(HkHash) == nesHash && loading.Test(HkRestore) == 1, "the game that was loading when the project was saved again did not come back");
        }
    }
}

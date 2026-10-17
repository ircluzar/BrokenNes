// Round 6 (inject): "push into the game" through the real plugin DLL (Native AOT), on real games from the user's library. The library-level checks (Inject.Checks*.cs) measure the sound; these prove the plumbing around it: the emulator's lock and
// Runaway on / off, the corruption farm being told, the project section INJ1 (saved, reopened, damaged, newer, absent), the machine that comes back playing on exactly as the one that stayed open, Remove from game, a replaced game, and that nothing
// allocates on the audio thread. Test hooks 320-339 (Bn2Plugin.Inject.cs). A game that is not there is "not tested", never a pass.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;
using BrokenNes2;
using BrokenNes2.Instruments;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    private const int HkPush = 320, HkPushClean = 321, HkPushRemove = 322, HkPushCount = 323, HkPushText = 324, HkPushNote = 325, HkLiveHash = 326, HkPushSection = 327, HkPushVerify = 328, HkPushAll = 329, HkPushMutant = 330, HkFarmWrites = 333;
    private const int HkRunFrames = 83, HkMachine = 84, HkPad = 44, HkRunaway = 47, HkFarmPoisoned = 114;

    private static string? PushNote(PluginInstance p)
    {
        int n = (int)p.Test(HkPushNote);
        if (n < 0) return null;
        var buf = new char[n + 1];
        fixed (char* c = buf) p.Test(HkPushNote, n, (nint)c);
        return new string(buf, 0, n);
    }

    /// <summary>Runs frames [from, to) of a game with a scripted pad, one frame at a time through the plugin's own hooks.</summary>
    private static void DriveFrames(PluginInstance p, Func<int, int> pad, int from, int to)
    {
        for (int f = from; f < to; f++) { p.Test(HkPad, 0, pad(f)); p.Test(HkRunFrames, 0, 1); }
    }

    private sealed record InjectGame(string Name, string Path, int Console, string Chip, string Script, int Frame, int Index, Func<Instrument, InstrumentHistory, bool> Edit, int After);

    private PluginInstance? InjectOpen(InjectGame g) => File.Exists(g.Path) ? SegaGame(g.Path, g.Console, g.Chip) : null;

    private (string Path, string Chip)? InjectRomPath(string folder, string fragment, string chip, params string[] ext)
    {
        string? found = ImodelGames.Find(folder, fragment, ext);
        return found == null ? null : (found, chip);
    }

    private void InjectDll(T t)
    {
        var tmp = new List<string>();
        try
        {
            // a Master System game in a 7z is unpacked by the checks' own loader; the plugin wants a file
            string? Unpack(string? path)
            {
                if (path == null) return null;
                if (!path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return path;
                var rom = ImodelGames.LoadRom(path, out string name);
                if (rom == null) return null;
                string f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bn2_inj_{Guid.NewGuid():N}_{name}");
                File.WriteAllBytes(f, rom); tmp.Add(f);
                return f;
            }

            // ---- SNES: Super Mario World; the music's most played sample gets a new pitch, envelope, volume weights and sample bytes ----
            if (File.Exists(InjectRun.Smw))
            {
                var g = new InjectGame("Super Mario World", InjectRun.Smw, 2, "SNES", InjectRun.SmwScript, 2200, 7,
                    (inst, h) => { h.Randomise(0.15, 0xFEEDUL, true); h.Set("tune.cents", 500); h.Set("env.adsr", 1); h.Set("env.ar", 4); h.Set("vol.r", 70); return true; }, 240);
                InjectOne(t, g, snes: true);
            }
            else t.Note($"SNES push through the DLL: not tested, {InjectRun.Smw} is not there");

            // ---- Genesis: Sonic the Hedgehog; FM 2's patch ----
            if (File.Exists(InjectRun.Sonic1))
            {
                var g = new InjectGame("Sonic the Hedgehog", InjectRun.Sonic1, 4, "MD", InjectRun.SonicScript, 1400, 1,
                    (inst, h) => { for (int n = 1; n <= 4; n++) { int m = h.Current.Get($"op{n}.mul"); h.Set($"op{n}.mul", m == 0 ? 1 : Math.Min(15, m * 2)); } h.Set("op4.tl", Math.Min(127, h.Current.Get("op4.tl") + 12)); return true; }, 240);
                InjectOne(t, g, snes: false);
            }
            else t.Note($"Genesis push through the DLL: not tested, {InjectRun.Sonic1} is not there");

            // ---- Game Boy: Link's Awakening; the wave ----
            string? zelda = Unpack(ImodelGames.Find(@"X:\EMULATION\GameBoy", "Link's Awakening", ".zip"));
            if (zelda != null)
            {
                var g = new InjectGame("Link's Awakening", zelda, 1, "DMG", "300:Start,310:,600:Start,610:,900:A,910:", 1500, 2,
                    (inst, h) => { var d = (byte[])inst.Blobs[0].Data.Clone(); for (int i = 0; i < d.Length; i++) d[i] = (byte)(~d[i]); h.SetBytes("wave", 0, d); return true; }, 240);
                InjectOne(t, g, snes: false);
            }
            else t.Note("Game Boy push through the DLL: not tested, Link's Awakening is not there");

            // ---- Master System FM: Galactic Protector; the user instrument ----
            string? gp = Unpack(ImodelGames.Find(@"X:\EMULATION\Sega Master System", "Galactic Protector", ".7z"));
            if (gp != null)
            {
                var g = new InjectGame("Galactic Protector", gp, 3, "SMSFM", "200:Start,210:,500:Start,510:,800:A,810:", 1200, 0,
                    (inst, h) => { h.Set("mod.mul", (inst.Get("mod.mul") + 3) & 15); h.Set("car.mul", (inst.Get("car.mul") + 1) & 15); return true; }, 600);
                InjectOne(t, g, snes: false);
            }
            else t.Note("Master System push through the DLL: not tested, Galactic Protector is not there");

            InjectRefusals(t);
            InjectUi(t);
        }
        finally { foreach (var f in tmp) DeleteQuietly(f); }
    }

    /// <summary>Everything the plumbing has to do for one game.</summary>
    private void InjectOne(T t, InjectGame g, bool snes)
    {
        var pad = InjectRun.Pad(g.Script);
        string what = g.Name;

        // the base project: the game run to a place where its music plays, saved running
        byte[] baseProject;
        using (var p = InjectOpen(g)!)
        {
            DriveFrames(p, pad, 0, g.Frame);
            t.Expect(p.Test(11) == g.Frame, $"{what}: the game is at frame {p.Test(11)}, expected {g.Frame}");
            baseProject = p.SaveState();
        }
        PluginInstance Open() { var q = Reopen(baseProject); return q; }
        (long Machine, long Live) Control()
        {
            using var c = Open();
            long live0 = c.Test(HkLiveHash);
            DriveFrames(c, pad, g.Frame, g.Frame + g.After);
            return (c.Test(HkMachine), live0);
        }
        var control = Control();
        t.Expect(control == Control(), $"{what}: two runs of the same project differ: the machine is not deterministic, the comparisons would mean nothing");

        // ---- push (the game frozen), then switch Instrument Runaway off: the game plays on with it ----
        long pushedMachine, pushedLive; byte[] frozenProject;
        string pushText;
        using (var p = Open())
        {
            p.Test(HkRunaway, 0, 1);
            t.Expect(p.Test(45) == 1, $"{what}: Instrument Runaway did not start");
            var inst = DllRead(p, g.Index);
            t.Expect(inst != null, $"{what}: instrument {g.Index} of the frozen copy is not there");
            if (inst == null) return;
            var clean = inst.Clone();
            var h = new InstrumentHistory(inst);
            g.Edit(inst, h);
            t.Expect(DllApply(p, h.Current, out string applyText) == 0, $"{what}: the model's edit was refused: {applyText}");
            { var packed = InstrumentCodec.Pack(h.Clean); fixed (byte* b = packed) p.Test(HkPushClean, packed.Length, (nint)b); }
            long live0 = p.Test(HkLiveHash);
            t.Expect(live0 == control.Live, $"{what}: the live sound memory differs between two reopenings of the same project");
            int rc = (int)p.Test(HkPush, g.Index);
            pushText = DllText(p, HkPushText);
            t.Expect(rc == 0, $"{what}: the push was refused: {pushText}");
            t.Expect(p.Test(HkPushCount) == 1, $"{what}: {p.Test(HkPushCount)} pushes after one push");
            t.Expect(pushText.Length > 30 && pushText.Contains("Remove from game"), $"{what}: the push says nothing useful: '{pushText}'");
            pushedLive = p.Test(HkLiveHash);
            if (snes || g.Console == 1) t.Expect(pushedLive != live0, $"{what}: the game's own sound memory is unchanged by the push");
            t.Expect(p.Test(HkFarmPoisoned) == 1, $"{what}: the corruption farm was not told that something a replay cannot repeat was installed");
            if (snes || g.Console == 1) t.Expect(p.Test(HkFarmWrites) > 0, $"{what}: the corruption farm was told of no memory write");
            t.Expect(p.Test(HkPushVerify) == 1, $"{what}: the hooks are not in the sound core after the push");
            frozenProject = p.SaveState();
            // Instrument Runaway off: the game resumes
            p.Test(HkRunaway, 0, 0);
            DriveFrames(p, pad, g.Frame, g.Frame + g.After);
            pushedMachine = p.Test(HkMachine);
            t.Expect(pushedMachine != control.Machine, $"{what}: after {g.After} frames the pushed game's machine is the untouched game's: the push changed nothing in the game");
            t.Expect(p.Test(HkPushCount) == 1, $"{what}: the push was lost when Instrument Runaway went off");
            t.Note($"{what}: push \"{pushText}\"");
        }

        // ---- a project saved with the game frozen and the push in place: reopened, it plays on as the game that stayed open ----
        int at = FindSection(frozenProject, "INJ1");
        t.Expect(at > 0, $"{what}: the project has no INJ1 section");
        using (var q = Reopen(frozenProject))
        {
            t.Expect(q.Test(HkPushCount) == 1, $"{what}: the reopened project has {q.Test(HkPushCount)} pushes");
            t.Expect(PushNote(q) == null, $"{what}: a note on a good project: {PushNote(q)}");
            t.Expect(q.Test(45) == 1, $"{what}: the reopened project is not in Instrument Runaway");
            t.Expect(q.Test(HkLiveHash) == pushedLive, $"{what}: the reopened game's own sound memory is not the one that was saved");
            t.Expect(q.Test(HkPushVerify) == 1, $"{what}: the reopened game's sound core has no hooks");
            q.Test(HkRunaway, 0, 0);
            DriveFrames(q, pad, g.Frame, g.Frame + g.After);
            t.Expect(q.Test(HkMachine) == pushedMachine, $"{what}: the reopened game does not play on as the one that stayed open (machine {q.Test(HkMachine):X} against {pushedMachine:X})");
            // and it can be taken out of the reopened game
            q.Test(HkRunaway, 0, 1);
            t.Expect(q.Test(HkPushRemove, g.Index) == 0 && q.Test(HkPushCount) == 0, $"{what}: Remove from game failed on the reopened project: {DllText(q, HkPushText)}");
        }
        // the same project without the section: the machine alone does not carry the overrides
        if (at > 0)
        {
            using (var q = Reopen(frozenProject[..at]))
            {
                t.Expect(q.Test(HkPushCount) == 0 && PushNote(q) == null, $"{what}: a project without the section shows {q.Test(HkPushCount)} pushes and note '{PushNote(q)}'");
                q.Test(HkRunaway, 0, 0);
                DriveFrames(q, pad, g.Frame, g.Frame + g.After);
                if (snes || g.Console == 4) t.Expect(q.Test(HkMachine) != pushedMachine, $"{what}: without the section the reopened game still plays as the pushed one: the section carries nothing");
            }
            // damaged, newer
            int len = BitConverter.ToInt32(frozenProject, at + 4);
            var damaged = (byte[])frozenProject.Clone(); damaged[at + 8 + Math.Min(len / 2, len - 6)] ^= 0x21;
            var newer = (byte[])frozenProject.Clone(); newer[at + 8] = 9;
            uint crc = Crc(newer.AsSpan(at + 8, len - 4).ToArray());
            newer[at + 8 + len - 4] = (byte)crc; newer[at + 8 + len - 3] = (byte)(crc >> 8); newer[at + 8 + len - 2] = (byte)(crc >> 16); newer[at + 8 + len - 1] = (byte)(crc >> 24);
            foreach (var (label, blob, want) in new[] { ("damaged", damaged, "damaged"), ("newer", newer, "newer") })
            {
                using var d = Reopen(blob);
                string? note = PushNote(d);
                t.Expect(d.Test(HkPushCount) == 0 && note != null && note.Contains("pushed instruments were not restored") && note.Contains(want), $"{what}: a {label} section: {d.Test(HkPushCount)} pushes, note '{note}'");
                t.Expect(d.Test(11) == g.Frame && d.Test(45) == 1, $"{what}: a {label} section stopped the game from coming back (frame {d.Test(11)}, Runaway {d.Test(45)})");
            }
        }

        // ---- Remove from game: the original memory back, the game as the untouched one ----
        using (var p = Open())
        {
            p.Test(HkRunaway, 0, 1);
            var inst = DllRead(p, g.Index)!;
            var h = new InstrumentHistory(inst);
            g.Edit(inst, h);
            DllApply(p, h.Current, out _);
            { var packed = InstrumentCodec.Pack(h.Clean); fixed (byte* b = packed) p.Test(HkPushClean, packed.Length, (nint)b); }
            long before = p.Test(HkLiveHash);
            t.Expect(p.Test(HkPush, g.Index) == 0, $"{what}: push (for removal) refused: {DllText(p, HkPushText)}");
            t.Expect(p.Test(HkPushRemove, g.Index) == 0, $"{what}: Remove from game refused: {DllText(p, HkPushText)}");
            string removeText = DllText(p, HkPushText);
            t.Expect(p.Test(HkPushCount) == 0 && p.Test(HkLiveHash) == before, $"{what}: after Remove the game's sound memory is not the original ({p.Test(HkPushCount)} pushes left)");
            p.Test(HkRunaway, 0, 0);
            DriveFrames(p, pad, g.Frame, g.Frame + g.After);
            t.Expect(p.Test(HkMachine) == control.Machine, $"{what}: pushed and removed at once, the game does not play on as the untouched one");
            t.Note($"{what}: remove \"{removeText}\"");
        }

        // ---- a replaced game takes its pushes with it, and says so ----
        using (var p = Open())
        {
            p.Test(HkRunaway, 0, 1);
            var inst = DllRead(p, g.Index)!;
            var h = new InstrumentHistory(inst);
            g.Edit(inst, h);
            DllApply(p, h.Current, out _);
            p.Test(HkPush, g.Index);
            t.Expect(p.Test(HkPushCount) == 1, $"{what}: nothing to lose");
            p.Test(43);                                                                  // Reset Console: the game restarts from power-on
            RenderBlocks(p, 1.0);
            WaitForRom(p);
            RenderBlocks(p, 0.5);
            string? note = PushNote(p);
            t.Expect(p.Test(HkPushCount) == 0 && note != null && note.Contains("left the game"), $"{what}: after a reset {p.Test(HkPushCount)} pushes and note '{note}'");
        }

        // ---- deliberate bugs in the plumbing are caught ----
        foreach (var (mutant, label) in new[] { (PushMutants.PushForgetsFarm, "the push does not tell the corruption farm"), (PushMutants.RestoreSkipsHooks, "a reopened project gets no hooks") })
        {
            if (mutant == PushMutants.PushForgetsFarm && !(snes || g.Console == 1)) continue;
            bool caught;
            using (var p = Open())
            {
                p.Test(HkPushMutant, 0, mutant);
                try
                {
                    if (mutant == PushMutants.PushForgetsFarm)
                    {
                        p.Test(HkRunaway, 0, 1);
                        var inst = DllRead(p, g.Index)!; var h = new InstrumentHistory(inst); g.Edit(inst, h); DllApply(p, h.Current, out _);
                        p.Test(HkPush, g.Index);
                        caught = p.Test(HkFarmWrites) == 0;                                         // the check above (farm told of no write) would fail
                    }
                    else
                    {
                        using var q = Reopen(frozenProject);
                        q.Test(HkRunaway, 0, 0);
                        DriveFrames(q, pad, g.Frame, g.Frame + g.After);
                        caught = q.Test(HkMachine) != pushedMachine || q.Test(HkPushVerify) != 1;   // the reopened game would not play on as the one that stayed open
                    }
                }
                finally { p.Test(HkPushMutant, 0, 0); }
            }
            t.Expect(caught, $"{what}: the deliberate bug \"{label}\" was not caught by the checks above");
        }

        // ---- nothing allocates on the audio thread while a pushed game plays ----
        using (var p = Open())
        {
            p.Test(HkRunaway, 0, 1);
            var inst = DllRead(p, g.Index)!; var h = new InstrumentHistory(inst); g.Edit(inst, h); DllApply(p, h.Current, out _);
            p.Test(HkPush, g.Index);
            p.Test(HkRunaway, 0, 0);
            RenderBlocks(p, 0.5);
            long bytes = 0;
            for (int i = 0; i < 40; i++) bytes += AllocOfOneBlock(p);
            // the same game without the push
            using var c = Open();
            RenderBlocks(c, 0.5);
            long baseBytes = 0;
            for (int i = 0; i < 40; i++) baseBytes += AllocOfOneBlock(c);
            t.Expect(bytes <= baseBytes, $"{what}: the audio thread allocated {bytes:N0} bytes in 40 blocks with the push against {baseBytes:N0} without it");
            t.Note($"{what}: 40 blocks of the pushed game allocate {bytes} bytes on the audio thread (the same game without the push: {baseBytes})");
        }
    }

    /// <summary>Kinds that cannot be pushed are refused with their reason, through the same hook; nothing changes.</summary>
    private void InjectRefusals(T t)
    {
        foreach (var (console, kind, fragment) in new[] { (ConsoleId.MasterSystem, InstrumentKind.PsgTones, "PSG"), (ConsoleId.GameBoy, InstrumentKind.GbPulse1, "pulse"), (ConsoleId.Nes, InstrumentKind.NesPulse1, "NES"), (ConsoleId.MasterSystem, InstrumentKind.OpllRom, "ROM instrument") })
        {
            string? why = GamePush.WhyNot(console, kind);
            t.Expect(why != null && why.Contains(fragment), $"{kind}: the refusal is '{why}'");
            t.Expect(why == null || why.StartsWith("cannot push") || why.StartsWith("a ROM instrument"), $"{kind}: the refusal does not start the way the tab's others do: '{why}'");
        }
        t.Note("kinds that cannot be pushed are refused with: " + string.Join(" | ", new[] { InstrumentKind.PsgTones, InstrumentKind.GbPulse1, InstrumentKind.NesPulse1 }.Select(k => GamePush.WhyNot(ConsoleId.Snes, k))));
    }

    // ------------------------------------------------------------------------------------------------
    // the Instruments tab: Push into the game / Remove from game with real mouse messages, the "in the game" mark, the standing note

    private const int UiRemove = 1191, UiMutRemoveCallsPush = 1 << 17, UiMutNoticeHidden = 1 << 18;        // (the plugin's own constants, Ui/UiIds.cs)

    private UiCtx OpenProjectTab(byte[] project)
    {
        var p = Reopen(project, 44100);
        nint parent = Win32Host.CreateParent();
        p.ShowEditor(parent);
        p.Host.EditorResized = () => Win32Host.FitFrame(parent, p.EditorHandle);
        Win32Host.Pump(150);
        return new UiCtx(p, parent);
    }

    /// <summary>The live game gets the sound memory the frozen copy has (the test game has no sound driver, so nothing else puts it there).</summary>
    private static void GiveGameTheMemory(PluginInstance p, SpcSnapshot s)
    {
        for (int a = 0; a < s.Aram.Length; a++) if (s.Aram[a] != 0) p.Test(332, a, s.Aram[a]);
        for (int r = 0; r < s.Dsp.Length; r++) if (s.Dsp[r] != 0) p.Test(334, r, s.Dsp[r]);
    }

    private void InjectUi(T t)
    {
        string sfc = Path.Combine(Path.GetTempPath(), $"bn2_{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(sfc, MakeSnesRom());
        try
        {
            var snap = ImodelData.SnesWithSamples();
            byte[] projectPushed;
            using (var c = OpenModelTab(2, sfc, ""))
            {
                var p = c.P;
                UiFreeze(c);
                GiveGameTheMemory(p, snap);
                InstallSnes(p, snap);
                UiTabTo(t, c, UiTab1, "Instruments");
                Win32Host.Pump(150);
                t.Expect(p.Test(420, 1) == 3, $"the tab lists {p.Test(420, 1)} samples, expected 3");
                t.Expect(!UiText(p, 14).Contains("in the game"), $"before any push the tab marks the sample: '{UiText(p, 14)}'");

                // Remove with nothing pushed: refused, says why
                UiClick(t, c, UiRemove, "Remove from game (nothing pushed)");
                t.Expect(p.Test(420, 11) == 0 && UiText(p, 3).Contains("not pushed"), $"Remove with nothing pushed said '{UiText(p, 3)}'");

                // Push with nothing edited: refused
                UiClick(t, c, UiPush, "Push into the game (unedited)");
                t.Expect(p.Test(420, 11) == 0 && UiText(p, 3).Contains("nothing to push"), $"Push of an unedited sample said '{UiText(p, 3)}'");

                // edit Tuning by a hundred cents with shift-clicks, then Push
                UiScrollFieldsToEnd(c);
                for (int i = 0; i < 10; i++) UiClick(t, c, UiFieldBase + 2 * 8 + 2, "Tuning plus", shift: true);
                long liveBefore = p.Test(HkLiveHash);
                UiClick(t, c, UiPush, "Push into the game");
                string said = UiText(p, 3);
                t.Expect(p.Test(420, 11) == 1 && said.Contains("Pushed sample 0") && said.Contains("tuning +100 cents") && said.Contains("Remove from game"), $"Push said '{said}'");
                t.Expect(p.Test(HkPushCount) == 1 && p.Test(HkPushVerify) == 1, $"after the push: {p.Test(HkPushCount)} pushes, hooks {p.Test(HkPushVerify)}");
                t.Expect(p.Test(HkLiveHash) == liveBefore, "a tuning-only push changed the game's audio RAM");
                Win32Host.Pump(150);
                t.Expect(UiText(p, 14).Contains("in the game"), $"the pushed sample is not marked on the list: '{UiText(p, 14)}'");

                // Remove: ok, the mark goes
                UiClick(t, c, UiRemove, "Remove from game");
                t.Expect(p.Test(420, 11) == 1 && UiText(p, 3).Contains("Removed sample 0") && p.Test(HkPushCount) == 0, $"Remove said '{UiText(p, 3)}', pushes left {p.Test(HkPushCount)}");
                Win32Host.Pump(150);
                t.Expect(!UiText(p, 14).Contains("in the game"), $"the removed sample is still marked: '{UiText(p, 14)}'");

                // a deliberate bug: Remove that pushes again leaves the instrument pushed, which the check above notices
                UiClick(t, c, UiPush, "Push into the game");
                p.Test(499, 0, UiMutRemoveCallsPush);
                UiClick(t, c, UiRemove, "Remove from game (bug)");
                bool caught = p.Test(HkPushCount) == 1;
                p.Test(499, 0, 0);
                t.Expect(caught, "the bug \"Remove from game pushes\" was not caught: the push count is " + p.Test(HkPushCount));
                projectPushed = p.SaveState();
            }

            // the project with the push: reopened, the tab marks it and shows no note; a damaged or newer section shows the note on the tab itself
            int at = FindSection(projectPushed, "INJ1");
            t.Expect(at > 0, "the saved project has no INJ1 section");
            if (at <= 0) return;
            using (var c = OpenProjectTab(projectPushed))
            {
                UiTabTo(t, c, UiTab1, "Instruments");
                Win32Host.Pump(100);
                t.Expect(c.P.Test(HkPushCount) == 1 && UiText(c.P, 14).Contains("in the game") && UiText(c.P, 13) == "", $"the reopened project: {c.P.Test(HkPushCount)} pushes, list '{UiText(c.P, 14)}', note '{UiText(c.P, 13)}'");
                UiClick(t, c, UiRemove, "Remove from game after reopening");
                t.Expect(c.P.Test(420, 11) == 1 && c.P.Test(HkPushCount) == 0, $"Remove after reopening said '{UiText(c.P, 3)}'");
            }
            int len = BitConverter.ToInt32(projectPushed, at + 4);
            var damaged = (byte[])projectPushed.Clone(); damaged[at + 8 + len / 2] ^= 0x44;
            using (var c = OpenProjectTab(damaged))
            {
                UiTabTo(t, c, UiTab1, "Instruments");
                Win32Host.Pump(100);
                string note = UiText(c.P, 13);
                t.Expect(c.P.Test(HkPushCount) == 0 && note.Contains("pushed instruments were not restored") && note.Contains("damaged"), $"a damaged section: {c.P.Test(HkPushCount)} pushes, the tab's note is '{note}'");
                // a deliberate bug: the tab does not draw the note
                c.P.Test(499, 0, UiMutNoticeHidden); UiTabTo(t, c, UiTab0, "Emulator"); UiTabTo(t, c, UiTab1, "Instruments"); Win32Host.Pump(150);
                bool hidden = UiText(c.P, 13) == "";
                c.P.Test(499, 0, 0);
                t.Expect(hidden, "the bug \"the tab hides the note\" was not caught");
            }
        }
        finally { DeleteQuietly(sfc); }
    }
}

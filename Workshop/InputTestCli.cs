using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using BrokenNes.Windows;
using NesEmulator;
using NesEmulator.Sega;
using NesEmulator.Systems;

namespace BrokenNes.Workshop;

/// <summary>
/// <c>--input-test</c>: the d-pad protection ("Allow opposite d-pad directions" off, the default), by measurement.
///
///   1. The pure function <see cref="DpadGuard"/>: all 16 combinations of Up, Down, Left, Right, protected and unprotected, against a table written out by hand (not derived from the
///      code under test); then every one of the 2^15 pad values for the properties that must hold (only the direction bits change, nothing else, idempotent, allow = identity); then the
///      NES-order list form.
///   2. The real desktop <see cref="InputManager"/> with simulated keys (its KeyProbe seam) and the real <see cref="PlayerControllerConfig"/>: the 16 combinations read through
///      <c>GetButton</c> and <c>Pad()</c>, the setting on and off, the other buttons unaffected, a direction handed back when its opposite is let go, the legacy key map, a config saved
///      before the setting existed (no field in its JSON) reading as protected, and the field round-tripping.
///   3. Recorded and programmatic input is untouched: the NES controller port (a movie frame with Left+Right and Up+Down) and the Genesis pad both reach the console exactly as given.
///   4. The callers: the guard is called from the human input sources only (the desktop's InputManager, the plugin's Bn2Input, Lite's nesInterop.js - once each, and they do call it),
///      and from nowhere else in the repository: not from the API injector, a movie player, a Workshop run or a replay tool. A new caller outside the list fails the test.
///
/// Run from the repository root (or pass <c>--repo &lt;dir&gt;</c>). Exit code 0 = every check passed.
/// </summary>
internal static class InputTestCli
{
    private static int failures, passes;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    // The four directions as a 4-bit number: Up 1, Down 2, Left 4, Right 8.
    private static PadButtons Dirs(int n) =>
        ((n & 1) != 0 ? PadButtons.Up : 0) | ((n & 2) != 0 ? PadButtons.Down : 0) | ((n & 4) != 0 ? PadButtons.Left : 0) | ((n & 8) != 0 ? PadButtons.Right : 0);

    private static string Name(int n) => n == 0 ? "neutral" : string.Join("+", new[] { (1, "Up"), (2, "Down"), (4, "Left"), (8, "Right") }.Where(d => (n & d.Item1) != 0).Select(d => d.Item2));

    // What a protected pad must show for each of the 16 combinations, written out by hand: Up+Down cancel each other and so do Left+Right; a direction with no opposite stays.
    //                                      0  U  D  UD L  UL DL UDL R  UR DR UDR LR ULR DLR UDLR
    private static readonly int[] Protected = { 0, 1, 2, 0, 4, 5, 6, 4, 8, 9, 10, 8, 0, 1, 2, 0 };

    public static int Run(string[] args)
    {
        string? repo = null, shot = null;
        for (int i = 1; i < args.Length - 1; i++) { if (args[i] == "--repo") repo = args[i + 1]; if (args[i] == "--shot") shot = args[i + 1]; }
        Console.WriteLine("== the pure function"); PureFunction();
        Console.WriteLine("\n== the desktop InputManager, simulated keys"); DesktopInput();
        Console.WriteLine("\n== the player's config"); ConfigField();
        Console.WriteLine("\n== the controller configuration window"); ControllerWindow(shot);
        Console.WriteLine("\n== recorded and programmatic input is untouched"); ProgrammaticInput();
        Console.WriteLine("\n== who calls the guard"); Callers(repo);
        Console.WriteLine($"\n{passes} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ 1. the pure function

    private static void PureFunction()
    {
        var bad = new List<string>();
        for (int n = 0; n < 16; n++)
        {
            var got = DpadGuard.Resolve(Dirs(n), allowOpposite: false);
            if (got != Dirs(Protected[n])) bad.Add($"protected {Name(n)} -> {got} (expected {Name(Protected[n])})");
            var raw = DpadGuard.Resolve(Dirs(n), allowOpposite: true);
            if (raw != Dirs(n)) bad.Add($"unprotected {Name(n)} -> {raw} (expected it unchanged)");
        }
        Check("all 16 combinations of the four directions, protected: Up+Down and Left+Right cancel, a lone direction stays", bad.Count == 0, string.Join("; ", bad.Take(4)));
        Check("the two cases the user named are neutral: Left+Right -> nothing, Up+Down -> nothing", DpadGuard.Resolve(PadButtons.Left | PadButtons.Right, false) == PadButtons.None
                                                                                              && DpadGuard.Resolve(PadButtons.Up | PadButtons.Down, false) == PadButtons.None);
        Check("with all four held (a mashed keyboard) the pad is neutral, not a diagonal", DpadGuard.Resolve(Dirs(15), false) == PadButtons.None);

        // every one of the 2^15 pad values: the non-direction bits never change; the direction bits follow the table; idempotent; allow is the identity
        const PadButtons DirMask = PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right;
        int checkedPads = 0; var props = new List<string>();
        for (int v = 0; v < 1 << 15; v++)
        {
            var pad = (PadButtons)v;
            var once = DpadGuard.Resolve(pad, false);
            int n = (pad.HasFlag(PadButtons.Up) ? 1 : 0) | (pad.HasFlag(PadButtons.Down) ? 2 : 0) | (pad.HasFlag(PadButtons.Left) ? 4 : 0) | (pad.HasFlag(PadButtons.Right) ? 8 : 0);
            if ((once & ~DirMask) != (pad & ~DirMask)) props.Add($"{v:X4}: a non-direction bit changed");
            else if ((once & DirMask) != Dirs(Protected[n])) props.Add($"{v:X4}: the directions are not as the table says");
            else if (DpadGuard.Resolve(once, false) != once) props.Add($"{v:X4}: not idempotent");
            else if (DpadGuard.Resolve(pad, true) != pad) props.Add($"{v:X4}: allow is not the identity");
            checkedPads++;
        }
        Check($"all {checkedPads} pad values (every button combination): only the direction bits can change, they follow the table, the rule is idempotent, allow = identity", props.Count == 0, string.Join("; ", props.Take(3)));

        // the NES-order list (A, B, Select, Start, Up, Down, Left, Right, then X Y L R): the same rule in place
        var listBad = new List<string>();
        for (int n = 0; n < 16; n++)
            foreach (bool others in new[] { false, true })
            {
                var l = new bool[12];
                if (others) for (int k = 0; k < 4; k++) l[k] = true;            // A B Select Start
                if (others) for (int k = 8; k < 12; k++) l[k] = true;           // X Y L R
                l[4] = (n & 1) != 0; l[5] = (n & 2) != 0; l[6] = (n & 4) != 0; l[7] = (n & 8) != 0;
                var expected = (bool[])l.Clone();
                int e = Protected[n]; expected[4] = (e & 1) != 0; expected[5] = (e & 2) != 0; expected[6] = (e & 4) != 0; expected[7] = (e & 8) != 0;
                var p = (bool[])l.Clone(); DpadGuard.ResolveNesOrder(p, false);
                if (!p.SequenceEqual(expected)) listBad.Add($"protected {Name(n)}{(others ? " + other buttons" : "")}");
                var u = (bool[])l.Clone(); DpadGuard.ResolveNesOrder(u, true);
                if (!u.SequenceEqual(l)) listBad.Add($"unprotected {Name(n)}");
            }
        var shortList = new bool[] { true, true, false, false };               // fewer than eight entries: left alone, no exception
        DpadGuard.ResolveNesOrder(shortList, false);
        Check("the NES-order list form agrees with the table for all 16 combinations, with and without the other buttons held, protected and not", listBad.Count == 0, string.Join("; ", listBad.Take(3)));
        Check("a list shorter than eight entries is left alone", shortList.SequenceEqual(new[] { true, true, false, false }));
    }

    // ------------------------------------------------------------------------------------------------ 2. the desktop InputManager

    /// <summary>An InputManager for a keyboard player whose keys are whatever the test says is held (by the key names the config binds).</summary>
    private static (InputManager Manager, HashSet<Keys> Held, PlayerControllerConfig Config) KeyboardPlayer(bool allowOpposite)
    {
        var held = new HashSet<Keys>();
        var config = PlayerControllerConfig.CreateDefaultPlayer1();
        config.AllowOppositeDpad = allowOpposite;
        var m = new InputManager { KeyProbe = vk => held.Contains((Keys)vk) };
        m.SetPlayerConfig(config);
        return (m, held, config);
    }

    private static void Hold(HashSet<Keys> held, int dirs, params Keys[] extra)
    {
        held.Clear();
        if ((dirs & 1) != 0) held.Add(Keys.Up);
        if ((dirs & 2) != 0) held.Add(Keys.Down);
        if ((dirs & 4) != 0) held.Add(Keys.Left);
        if ((dirs & 8) != 0) held.Add(Keys.Right);
        foreach (var k in extra) held.Add(k);
    }

    private static int ReadDirs(InputManager m) => (m.GetButton(4) ? 1 : 0) | (m.GetButton(5) ? 2 : 0) | (m.GetButton(6) ? 4 : 0) | (m.GetButton(7) ? 8 : 0);

    private static void DesktopInput()
    {
        // 16 combinations through the real polling code, protection on (the default config) and off
        foreach (bool allow in new[] { false, true })
        {
            var (m, held, _) = KeyboardPlayer(allow);
            var bad = new List<string>();
            for (int n = 0; n < 16; n++)
            {
                Hold(held, n); m.Poll();
                int got = ReadDirs(m), want = allow ? n : Protected[n];
                if (got != want) bad.Add($"{Name(n)} -> {Name(got)} (expected {Name(want)})");
                // the same through the whole pad (what the SNES, Game Boy and the Sega consoles read)
                var pad = m.Pad() & (PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right);
                if (pad != Dirs(want)) bad.Add($"Pad(): {Name(n)} -> {pad}");
            }
            Check($"InputManager, keyboard, 'Allow opposite d-pad directions' {(allow ? "ON" : "OFF (the default)")}: the 16 combinations read through GetButton and Pad()", bad.Count == 0, string.Join("; ", bad.Take(3)));
        }

        // the other buttons are not touched by the protection, with Left+Right and Up+Down held
        {
            var (m, held, _) = KeyboardPlayer(false);
            Hold(held, 15, Keys.X, Keys.Z, Keys.Space, Keys.Return, Keys.A, Keys.S, Keys.Q, Keys.W); m.Poll();
            var pad = m.Pad();
            const PadButtons Others = PadButtons.A | PadButtons.B | PadButtons.Select | PadButtons.Start | PadButtons.X | PadButtons.Y | PadButtons.L | PadButtons.R;
            Check("with all four directions mashed and the eight other buttons held, the eight others still read and no direction does", pad == Others, pad.ToString());
        }

        // letting go of one of an opposite pair hands the other back (the raw state is kept, only the answer is protected)
        {
            var (m, held, _) = KeyboardPlayer(false);
            var seq = new List<string>();
            Hold(held, 4); m.Poll(); seq.Add(Name(ReadDirs(m)));                 // Left
            Hold(held, 4 | 8); m.Poll(); seq.Add(Name(ReadDirs(m)));             // Left+Right
            Hold(held, 8); m.Poll(); seq.Add(Name(ReadDirs(m)));                 // Right alone: Left was let go
            Hold(held, 8 | 1); m.Poll(); seq.Add(Name(ReadDirs(m)));             // Right+Up: a diagonal is fine
            Hold(held, 8 | 1 | 2); m.Poll(); seq.Add(Name(ReadDirs(m)));         // Up+Down+Right: Up and Down cancel, Right stays
            Hold(held, 0); m.Poll(); seq.Add(Name(ReadDirs(m)));
            Check("a sequence of key presses: Left, +Right, Left let go, +Up, +Down, all let go", string.Join(" | ", seq) == "Left | neutral | Right | Up+Right | Right | neutral", string.Join(" | ", seq));
        }

        // the setting is read live from the config (the controller window changes the very object the manager holds)
        {
            var (m, held, config) = KeyboardPlayer(false);
            Hold(held, 12); m.Poll();
            int before = ReadDirs(m);
            config.AllowOppositeDpad = true;
            int after = ReadDirs(m);
            config.AllowOppositeDpad = false;
            int back = ReadDirs(m);
            Check("switching the setting in the config changes the answer at once, and back", before == 0 && after == 12 && back == 0, $"{before}, {after}, {back}");
        }

        // the legacy key map (no player config) is protected too
        {
            var held = new HashSet<Keys>();
            var m = new InputManager { KeyProbe = vk => held.Contains((Keys)vk) };
            m.SetKeyMap(new Dictionary<Keys, int> { [Keys.Left] = 6, [Keys.Right] = 7, [Keys.Up] = 4, [Keys.Down] = 5 });
            held.Add(Keys.Left); held.Add(Keys.Right); m.Poll();
            Check("the legacy key map (no player config) is protected: Left+Right -> neutral", ReadDirs(m) == 0);
            held.Remove(Keys.Right); m.Poll();
            Check("... and Left alone passes", ReadDirs(m) == 4);
        }

        // keys of the other player's manager are separate: protection is per player
        {
            var (m1, h1, _) = KeyboardPlayer(false);
            var (m2, h2, _) = KeyboardPlayer(true);
            Hold(h1, 12); Hold(h2, 12); m1.Poll(); m2.Poll();
            Check("two players: one protected, one not, each reads its own setting", ReadDirs(m1) == 0 && ReadDirs(m2) == 12);
        }
    }

    // ------------------------------------------------------------------------------------------------ the config field

    private static void ConfigField()
    {
        var fresh = new PlayerControllerConfig();
        Check("a new config is protected (AllowOppositeDpad is false)", !fresh.AllowOppositeDpad && !PlayerControllerConfig.CreateDefaultPlayer1().AllowOppositeDpad && !PlayerControllerConfig.CreateDefaultGamepad(2).AllowOppositeDpad);

        // a player config as saved before the setting existed: not a word about it
        const string old = "{\"playerNumber\":1,\"enabled\":true,\"deviceType\":0,\"gamepadIndex\":0,\"a\":{\"key\":\"X\"},\"b\":{\"key\":\"Z\"},\"select\":{\"key\":\"Space\"},\"start\":{\"key\":\"Return\"},"
                         + "\"up\":{\"key\":\"Up\"},\"down\":{\"key\":\"Down\"},\"left\":{\"key\":\"Left\"},\"right\":{\"key\":\"Right\"}}";
        var loaded = JsonSerializer.Deserialize<PlayerControllerConfig>(old)!;
        Check("a config saved before the setting existed (no field) loads as protected", loaded != null && !loaded.AllowOppositeDpad && loaded.Left.Key == "Left");

        var json = JsonSerializer.Serialize(new PlayerControllerConfig { AllowOppositeDpad = true });
        var back = JsonSerializer.Deserialize<PlayerControllerConfig>(json)!;
        Check("the setting is saved in the JSON as allowOppositeDpad and round-trips", json.Contains("\"allowOppositeDpad\":true") && back.AllowOppositeDpad);
        var off = JsonSerializer.Serialize(new PlayerControllerConfig());
        Check("... and the default is written as false", off.Contains("\"allowOppositeDpad\":false") && !JsonSerializer.Deserialize<PlayerControllerConfig>(off)!.AllowOppositeDpad);

        // the manager given the config that was loaded from the old JSON is protected
        var held = new HashSet<Keys> { Keys.Left, Keys.Right };
        var m = new InputManager { KeyProbe = vk => held.Contains((Keys)vk) };
        m.SetPlayerConfig(loaded); m.Poll();
        Check("an InputManager built on that old config is protected", ReadDirs(m) == 0);
    }

    // ------------------------------------------------------------------------------------------------ the controller window

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls) { yield return c; foreach (var d in AllControls(c)) yield return d; }
    }

    /// <summary>The real ControllerConfigWindow on a real config: the box is there, unticked by default, ticking it changes the config the InputManager reads, Cancel puts it back, a saved
    /// ticked config opens ticked. (Reset to defaults asks with a message box and is not driven here.) With --shot &lt;file.png&gt; the window is also saved as a picture.</summary>
    private static void ControllerWindow(string? shotPath)
    {
        var config = PlayerControllerConfig.CreateDefaultPlayer1();
        using var form = new ControllerConfigWindow(config);
        form.StartPosition = FormStartPosition.Manual; form.Location = new System.Drawing.Point(-10000, -10000);   // off screen: nobody is disturbed
        form.Show(); Application.DoEvents();
        var box = AllControls(form).OfType<CheckBox>().FirstOrDefault(c => c.Name == "allowOppositeDpad");
        Check("the window has the 'Allow opposite d-pad directions' box", box != null && box.Text == "Allow opposite d-pad directions" && box.Visible);
        if (box == null) { form.Close(); return; }
        var held = new HashSet<Keys> { Keys.Left, Keys.Right };
        var m = new InputManager { KeyProbe = vk => held.Contains((Keys)vk) };
        m.SetPlayerConfig(config); m.Poll();
        Check("... it is unticked on a default config, and the manager built on that config reads Left+Right as neutral", !box.Checked && ReadDirs(m) == 0);
        box.Checked = true;      // raises CheckedChanged, as a click does
        Application.DoEvents();
        Check("... ticking it sets the player's config and the manager reads Left+Right at once", config.AllowOppositeDpad && ReadDirs(m) == 12, $"config {config.AllowOppositeDpad}, read {ReadDirs(m)}");
        if (shotPath != null)
        {
            using var bmp = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  (window picture: {shotPath})");
        }
        var cancel = AllControls(form).OfType<Button>().First(b => b.Text == "Cancel");
        cancel.PerformClick(); Application.DoEvents();
        Check("... Cancel puts the setting back (the window restores its snapshot)", !config.AllowOppositeDpad && ReadDirs(m) == 0, $"config {config.AllowOppositeDpad}");

        var saved = PlayerControllerConfig.CreateDefaultPlayer1(); saved.AllowOppositeDpad = true;
        using var second = new ControllerConfigWindow(saved);
        second.StartPosition = FormStartPosition.Manual; second.Location = new System.Drawing.Point(-10000, -10000);
        second.Show(); Application.DoEvents();
        var box2 = AllControls(second).OfType<CheckBox>().First(c => c.Name == "allowOppositeDpad");
        Check("a config saved with the setting on opens ticked", box2.Checked);
        second.Close();
    }

    // ------------------------------------------------------------------------------------------------ 3. recorded / programmatic input

    private static void ProgrammaticInput()
    {
        // The NES controller port, as a movie (or the Web API) feeds it: A B Select Start Up Down Left Right, Left+Right and Up+Down at once.
        var input = new Input();
        var frame = new bool[8]; frame[4] = frame[5] = frame[6] = frame[7] = true;
        input.SetInput(frame);
        Check("NES: a recorded frame with Up+Down+Left+Right reaches the controller state exactly (0xF0)", input.DebugGetRawState() == 0xF0, $"0x{input.DebugGetRawState():X2}");
        input.Write4016(1); input.Write4016(0);
        int shifted = 0; for (int i = 0; i < 8; i++) shifted |= (input.Read4016() & 1) << i;
        Check("... and a game reading $4016 sees all four directions", shifted == 0xF0, $"0x{shifted:X2}");

        // the Genesis pad (active-low lines): Left+Right and Up+Down held at once read as all four lines low
        var pads = new MdPads(0xA0);
        pads.SetButtons(0, PadButtons.Left | PadButtons.Right | PadButtons.Up | PadButtons.Down);
        byte lines = pads.ReadData(0, 0);
        Check("Genesis: a pad with all four directions set shows all four lines low (programmatic input is not protected)", (lines & 0x0F) == 0, $"0x{lines:X2}");
        pads.SetButtons(0, PadButtons.Left);
        Check("... and Left alone shows just the Left line low", (pads.ReadData(0, 0) & 0x0F) == 0x0B, $"0x{pads.ReadData(0, 0):X2}");
    }

    // ------------------------------------------------------------------------------------------------ 4. the callers

    private static readonly Regex CallPattern = new(@"\bDpadGuard\s*\.\s*(Resolve|ResolveNesOrder)\s*\(|\bdpadGuard\s*\.\s*resolve\s*\(", RegexOptions.Compiled);

    // The human input sources, each of which must call the guard exactly once. (Files are given from the repository root.)
    private static readonly string[] HumanSources =
    {
        "Windows/InputManager.cs",                         // the desktop: keyboard and XInput pad, every console
        "Plugin/BrokenNes2.Plugin/Bn2Input.cs",            // the FL plugin: keyboard, pad and on-screen pad together (Inputs.Human)
        "WebLite/wwwroot/lib/nesInterop.js",               // Lite: keyboard, pad and touch controller (_sendPad)
    };

    // Files that may mention a call without being an input source: the tests that exercise the guard.
    private static readonly string[] Tests =
    {
        "Workshop/InputTestCli.cs", "UAT/lite/test-dpad-guard.mjs", "Plugin/BrokenNes.FruityHost/SelfTest.Dpad.cs",
    };

    private static void Callers(string? repoArg)
    {
        string? repo = repoArg ?? FindRepo(Directory.GetCurrentDirectory());
        if (repo == null || !File.Exists(Path.Combine(repo, "BrokenNes.sln")))
        {
            Check("the repository was found (run from its root, or pass --repo <dir>)", false, repo ?? "no BrokenNes.sln above the current directory");
            return;
        }
        var skip = new[] { "/bin/", "/obj/", "/.git/", "/.claude/", "/node_modules/", "/docs/" };
        var files = new List<string>();
        foreach (var ext in new[] { "*.cs", "*.js", "*.mjs", "*.razor", "*.html", "*.ps1" })
            foreach (var f in Directory.EnumerateFiles(repo, ext, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(repo, f).Replace('\\', '/');
                if (skip.Any(s => ("/" + rel).Contains(s))) continue;
                files.Add(rel);
            }
        var callers = new Dictionary<string, int>();
        foreach (var rel in files)
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(repo, rel)); } catch { continue; }
            int n = CallPattern.Matches(text).Count;
            if (n > 0) callers[rel] = n;
        }
        Console.WriteLine($"  (scanned {files.Count} source files; call sites: {(callers.Count == 0 ? "none" : string.Join(", ", callers.Select(c => $"{c.Key} x{c.Value}")))})");
        foreach (var src in HumanSources)
            Check($"{src} calls the guard exactly once (the human source is wired)", callers.TryGetValue(src, out int n) && n == 1, $"{(callers.TryGetValue(src, out int c) ? c : 0)} call(s)");
        var stray = callers.Keys.Where(k => !HumanSources.Contains(k) && !Tests.Contains(k)).ToList();
        Check("no other file calls the guard: not the Web API injector, a movie or TAS player, Workshop's headless runs, a replay tool or a console core", stray.Count == 0, string.Join(", ", stray));
    }

    private static string? FindRepo(string from)
    {
        for (var d = new DirectoryInfo(from); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "BrokenNes.sln"))) return d.FullName;
        return null;
    }
}

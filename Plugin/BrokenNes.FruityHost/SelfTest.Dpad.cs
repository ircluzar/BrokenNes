// Round 5 (inputs): the plugin's d-pad protection ("Allow opposite d-pad directions", off by default), through the real editor.
using BrokenNes.Fruity;
using BrokenNes.SynthHost;

namespace BrokenNes.FruityHost;

public sealed unsafe partial class SelfTest
{
    // ------------------------------------------------------------------------------------------------
    // D-pad protection. The editor's poll merges the keyboard, the gamepad and the on-screen pad, then Inputs.Human (DpadGuard) takes opposite directions out, then the pad goes to the game.
    // The keyboard is simulated through the editor hook 130 (the bound keys of the buttons in a mask count as down); the on-screen pad is held with a real mouse message; the setting is
    // clicked with the real mouse. Not tested here: a real gamepad (none is attached to the test machine; it shares the same merge point as the keyboard).

    private void DpadGuardTest(T t)
    {
        const int Up = 1, Down = 2, Left = 4, Right = 8, DirMask = 15;
        // what a protected pad shows for each of the 16 combinations (Up 1, Down 2, Left 4, Right 8), written out by hand
        int[] expected = { 0, 1, 2, 0, 4, 5, 6, 4, 8, 9, 10, 8, 0, 1, 2, 0 };

        // recorded / programmatic input is not touched: the test hook SetPad is what a project, a script or a replay would use (no editor open, so nothing polls the devices)
        using (var p = RomPlugin())
        {
            p.Test(44, 0, Up | Down | Left | Right);
            t.Expect((p.Test(49) & DirMask) == DirMask, $"a pad set from outside with all four directions did not reach the emulator as set ({p.Test(49):X})");
            p.Test(44, 0, 0);
        }

        using var q = RomPlugin();
        nint parent = Win32Host.CreateParent();
        try
        {
            q.ShowEditor(parent);
            for (int k = 0; k < 3; k++) { RenderBlocks(q, 0.1); Win32Host.Pump(40); }
            var hwnd = q.EditorHandle;
            ClickControl(t, q, 80, "Inputs button");
            t.Expect(q.Test(38) == 1, "the Inputs window did not open");
            t.Expect(q.Test(30, 86) != 0, "no 'Allow opposite d-pad directions' control in the Inputs window");
            ClickControl(t, q, 85, "Reset to defaults");
            t.Expect(q.Test(131) == 0, "the protection is not on after Reset to defaults");

            // all 16 combinations through the real poll, protection on (the default)
            var bad = new List<string>();
            for (int n = 0; n < 16; n++)
            {
                q.Test(130, 1, n);                                   // the keys of these directions are down
                Win32Host.Pump(110);
                int got = (int)q.Test(49) & DirMask;
                if (got != expected[n]) bad.Add($"{n:X} -> {got:X} (expected {expected[n]:X})");
                if (((int)q.Test(39) & DirMask) != got) bad.Add($"{n:X}: the editor shows {(int)q.Test(39) & DirMask:X} but the emulator has {got:X}");
            }
            t.Expect(bad.Count == 0, "protected, keyboard: " + string.Join("; ", bad.Take(4)));

            // the other buttons are untouched when opposite directions are held
            q.Test(130, 1, Left | Right | (1 << 4) | (1 << 10));     // Left, Right, A, Start
            Win32Host.Pump(110);
            int pad = (int)q.Test(49);
            t.Expect((pad & DirMask) == 0 && (pad & (1 << 4)) != 0 && (pad & (1 << 10)) != 0, $"with Left+Right held, A and Start were not both passed on, or a direction was ({pad:X})");

            // the three sources are merged before the guard: the keyboard's Left and the mouse on the on-screen Right button make neutral, and letting go gives Left back
            q.Test(130, 1, Left);
            Win32Host.Pump(110);
            t.Expect(((int)q.Test(49) & DirMask) == Left, "Left alone did not reach the emulator");
            var r = RectOf(q.Test(30, 240 + 3));
            Win32Host.MouseMove(hwnd, r.X + r.W / 2, r.Y + r.H / 2);
            Win32Host.MouseDown(hwnd, r.X + r.W / 2, r.Y + r.H / 2);
            Win32Host.Pump(120);
            t.Expect(((int)q.Test(49) & DirMask) == 0 && ((int)q.Test(39) & DirMask) == 0, $"keyboard Left + on-screen Right was not neutral ({q.Test(49):X})");
            Win32Host.MouseUp(hwnd, r.X + r.W / 2, r.Y + r.H / 2);
            Win32Host.Pump(120);
            t.Expect(((int)q.Test(49) & DirMask) == Left, $"letting go of the on-screen Right did not give Left back ({q.Test(49):X})");

            // the setting: a click on the box allows opposite directions, is remembered, and shows in the screenshot; all 16 combinations pass unchanged
            var before = Win32Host.CapturePixels(hwnd, out int w, out int h);
            ClickControl(t, q, 86, "Allow opposite d-pad directions");
            Win32Host.Pump(80);
            t.Expect(q.Test(131) == 1 && PrefsText().Contains("InputAllowOpposite=1"), $"ticking the box was not applied and remembered ({q.Test(131)}, {PrefsText().Replace("\n", " ")})");
            var after = Win32Host.CapturePixels(hwnd, out _, out _);
            var rc = RectOf(q.Test(30, 86));
            t.Expect(DifferentPixels(before, after, w, rc.X, rc.Y, 22, 22) > 40, "the box does not look ticked");
            bad.Clear();
            for (int n = 0; n < 16; n++)
            {
                q.Test(130, 1, n);
                Win32Host.Pump(110);
                int got = (int)q.Test(49) & DirMask;
                if (got != n) bad.Add($"{n:X} -> {got:X}");
            }
            t.Expect(bad.Count == 0, "allowed, keyboard: " + string.Join("; ", bad.Take(4)));

            // untick: protected again and remembered; Reset to defaults also puts the protection back
            ClickControl(t, q, 86, "Allow opposite d-pad directions");
            Win32Host.Pump(80);
            t.Expect(q.Test(131) == 0 && PrefsText().Contains("InputAllowOpposite=0"), "unticking the box was not applied and remembered");
            q.Test(130, 1, Left | Right);
            Win32Host.Pump(110);
            t.Expect(((int)q.Test(49) & DirMask) == 0, "Left+Right was not neutral after the box was unticked");
            ClickControl(t, q, 86, "Allow opposite d-pad directions");
            Win32Host.Pump(80);
            t.Expect(q.Test(131) == 1 && ((int)q.Test(49) & DirMask) == (Left | Right), "ticked again: Left+Right should pass");
            ClickControl(t, q, 85, "Reset to defaults");
            Win32Host.Pump(110);
            t.Expect(q.Test(131) == 0 && PrefsText().Contains("InputAllowOpposite=0") && ((int)q.Test(49) & DirMask) == 0, "Reset to defaults did not put the protection back");

            // a stored setting is read at start: ticked in the file, a fresh load of the preferences allows opposites; no such line (an older install) protects
            var lines = File.ReadAllLines(AboutFile).Where(l => !l.StartsWith("InputAllowOpposite=")).ToList();
            File.WriteAllLines(AboutFile, lines);                                    // an older install: the line is not there
            t.Expect(!PrefsText().Contains("InputAllowOpposite"), "could not remove the stored setting for the old-install check");
            t.Expect(DpadPrefsReadAs(0, q), "a preferences file with no InputAllowOpposite line did not read as protected");
            File.WriteAllLines(AboutFile, lines.Append("InputAllowOpposite=1"));
            t.Expect(DpadPrefsReadAs(1, q), "a preferences file with InputAllowOpposite=1 did not read as allowed");
            File.WriteAllLines(AboutFile, lines.Append("InputAllowOpposite=0"));
            t.Expect(DpadPrefsReadAs(0, q), "a preferences file with InputAllowOpposite=0 did not read as protected");

            q.Test(130, 0);
            ClickControl(t, q, 82, "Close button");
            q.HideEditor();
        }
        finally { q.Test(130, 0); Win32Host.DestroyParent(parent); }
    }

    /// <summary>Has the plugin read its remembered preferences afresh (the editor hook 132 runs Inputs.Load, which the plugin runs when the library is first loaded) and says whether the
    /// d-pad protection setting (hook 131: 1 = opposite directions allowed) came out as <paramref name="expectedValue"/>.</summary>
    private static bool DpadPrefsReadAs(int expectedValue, PluginInstance q)
    {
        q.Test(132, 0);
        return q.Test(131) == expectedValue;
    }

    private static int DifferentPixels(byte[] a, byte[] b, int width, int x, int y, int w, int h)
    {
        int n = 0;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int i = (yy * width + xx) * 4;
                if (i + 2 < a.Length && i + 2 < b.Length && (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])) n++;
            }
        return n;
    }
}

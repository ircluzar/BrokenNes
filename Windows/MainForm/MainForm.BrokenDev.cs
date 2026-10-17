using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using BrokenNes.Dev;
using NesEmulator;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenDev, the developer-only devkit module: the app shows nothing of it unless it is switched on (<see cref="BrokenDevGate"/>: the
    /// environment variable BROKENNES_DEV=1 or "brokenDevEnabled": true in config.json). When on, one extra top-level menu lists the projects
    /// the BrokenDev scripts have built (BROKENDEV_HOME\out\&lt;id&gt;\build.json) and opens their ROM; the rest of BrokenDev (restore, build,
    /// verify) is the command line, <c>BrokenDev\brokendev.ps1</c> and <c>BrokenNes.Workshop.exe --brokendev</c>.
    /// </summary>
    public partial class MainForm
    {
        private const string DefaultBrokenDevHome = @"P:\BrokenDev\env";

        // Power-on RAM (BrokenDev only). BrokenNes fills work RAM with FCEUX's pattern, which is right for movie portability and honest to hardware (a
        // real console's RAM powers on undefined), but a homebrew written against an emulator set to all-zeros can read a variable it never wrote and
        // work there by accident: the SMB1 remake reads two (chr_test_active, fire_anim_timer). A developer picks the fill here to find out.
        private enum BrokenDevRam { Fceux, Zeros, Ones }
        private BrokenDevRam brokenDevPowerOnRam =
            Environment.GetEnvironmentVariable("BROKENDEV_POWERON_RAM")?.ToLowerInvariant() switch { "zeros" => BrokenDevRam.Zeros, "ones" => BrokenDevRam.Ones, _ => BrokenDevRam.Fceux };

        private void ApplyBrokenDevPowerOnRam(NES target)
        {
            if (brokenDevPowerOnRam == BrokenDevRam.Fceux || !BrokenDevGate.IsEnabled()) return;
            byte fill = brokenDevPowerOnRam == BrokenDevRam.Ones ? (byte)0xFF : (byte)0x00;
            for (int i = 0; i < 2048; i++) target.PokeSystemRam(i, fill);
            Console.WriteLine($"BrokenDev: work RAM filled with {(fill == 0 ? "zeros" : "ones")} before the first frame.");
        }

        private static string BrokenDevHome() => Environment.GetEnvironmentVariable("BROKENDEV_HOME") is { Length: > 0 } h ? h : DefaultBrokenDevHome;

        /// <summary>The BrokenDev menu, or null when BrokenDev is not enabled (then the menu bar is exactly what it always was).</summary>
        private ToolStripMenuItem? BuildBrokenDevMenu()
        {
            if (!BrokenDevGate.IsEnabled(out string why)) return null;

            var menu = new ToolStripMenuItem("&BrokenDev");
            menu.DropDownOpening += (_, _) => FillBrokenDevMenu(menu);
            FillBrokenDevMenu(menu);
            Console.WriteLine($"BrokenDev menu enabled ({why}).");
            return menu;
        }

        private void FillBrokenDevMenu(ToolStripMenuItem menu)
        {
            menu.DropDownItems.Clear();
            string home = BrokenDevHome();
            var heading = new ToolStripMenuItem($"Developer module - projects built under {home}") { Enabled = false };
            menu.DropDownItems.Add(heading);
            menu.DropDownItems.Add(new ToolStripSeparator());

            int shown = 0;
            string outRoot = Path.Combine(home, "out");
            if (Directory.Exists(outRoot))
            {
                foreach (string dir in Directory.GetDirectories(outRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string record = Path.Combine(dir, "build.json");
                    if (!File.Exists(record)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(record));
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) continue;
                        string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
                        string? rom = null;
                        if (root.TryGetProperty("outputs", out var outs))
                            foreach (var o in outs.EnumerateArray())
                                if (o.TryGetProperty("id", out var id) && id.GetString() == "rom" && o.TryGetProperty("published", out var p)) rom = p.GetString();
                        if (rom == null || !File.Exists(rom)) continue;
                        string path = rom;
                        menu.DropDownItems.Add(new ToolStripMenuItem($"Open {title}", null, (_, _) => LoadRomFile(path)) { ToolTipText = path });
                        shown++;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                    {
                        Console.WriteLine($"BrokenDev: unreadable build record {record}: {ex.Message}");
                    }
                }
            }
            if (shown == 0)
                menu.DropDownItems.Add(new ToolStripMenuItem("(no built projects yet: run BrokenDev\\brokendev.ps1 build all)") { Enabled = false });

            menu.DropDownItems.Add(new ToolStripSeparator());
            var ramMenu = new ToolStripMenuItem("Power-on RAM (next ROM load)");
            foreach (var (mode, label) in new[] { (BrokenDevRam.Fceux, "FCEUX pattern (default)"), (BrokenDevRam.Zeros, "All zeros"), (BrokenDevRam.Ones, "All ones") })
            {
                var m = mode;
                ramMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => { brokenDevPowerOnRam = m; }) { Checked = brokenDevPowerOnRam == m, CheckOnClick = false });
            }
            ramMenu.DropDownItems.Add(new ToolStripSeparator());
            ramMenu.DropDownItems.Add(new ToolStripMenuItem("Reload the current ROM now", null, (_, _) => ResetGame()));
            menu.DropDownItems.Add(ramMenu);
            menu.DropDownItems.Add(new ToolStripMenuItem("Open BrokenDev folder", null, (_, _) =>
            {
                try { if (Directory.Exists(home)) Process.Start(new ProcessStartInfo(home) { UseShellExecute = true }); else MessageBox.Show($"{home} does not exist yet. Run BrokenDev\\brokendev.ps1 restore all.", "BrokenDev"); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "BrokenDev"); }
            }));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using BrokenNes.Windows.DebugWindows;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Emulator > Debug Windows (BrokenDev D8, docs/projects/brokendev-debugging-plan.md "D8 settled"): the debugger's desktop windows. Each is
    /// opened from the menu, one of each, owned by the main form, never saved: closed, no timer runs and no hook is armed.
    /// </summary>
    public partial class MainForm
    {
        private readonly Dictionary<string, Form> debugWindows = new();

        private object? DebugWindowGame => (object?)session ?? nes;

        private void AddDebugWindowsMenu(ToolStripMenuItem emulatorMenu)
        {
            var menu = new ToolStripMenuItem("Debug &Windows") { Name = "debugWindowsMenu" };
            menu.DropDownItems.Add(new ToolStripMenuItem("&Debugger (registers, disassembly, breakpoints, step)", null, (_, _) => OpenDebugWindow("debugger")) { Name = "debugWindowDebugger" });
            menu.DropDownItems.Add(new ToolStripMenuItem("&Memory (live hex, watches)", null, (_, _) => OpenDebugWindow("memory")) { Name = "debugWindowMemory" });
            menu.DropDownItems.Add(new ToolStripMenuItem("&Palettes", null, (_, _) => OpenDebugWindow("palettes")) { Name = "debugWindowPalettes" });
            menu.DropDownItems.Add(new ToolStripMenuItem("&Tiles", null, (_, _) => OpenDebugWindow("tiles")) { Name = "debugWindowTiles" });
            menu.DropDownItems.Add(new ToolStripMenuItem("M&aps", null, (_, _) => OpenDebugWindow("maps")) { Name = "debugWindowMaps" });
            menu.DropDownItems.Add(new ToolStripMenuItem("&Sprites", null, (_, _) => OpenDebugWindow("sprites")) { Name = "debugWindowSprites" });
            menu.DropDownItems.Add(new ToolStripMenuItem("&Event View", null, (_, _) => OpenDebugWindow("events")) { Name = "debugWindowEvents" });
            menu.DropDownItems.Add(new ToolStripMenuItem("S&ound Monitor", null, (_, _) => OpenDebugWindow("sound")) { Name = "debugWindowSound" });
            AddTimeflowMenu(menu);   // Timeflow: Record (REC) and the Timeflow window (MainForm.Timeflow.cs)
            AddLanShareMenu(menu);   // Share Read-Only Pages on the LAN (MainForm.LanShare.cs)
            emulatorMenu.DropDownItems.Add(menu);
        }

        /// <summary>Opens (or brings forward) one of the debug windows.</summary>
        private void OpenDebugWindow(string which)
        {
            if (debugWindows.TryGetValue(which, out var open) && !open.IsDisposed) { open.Activate(); return; }
            Form f = which switch
            {
                "debugger" => new DebuggerWindow(() => webApiServer?.Debuggers, () => DebugWindowGame, () => currentRomPath),
                "memory" => new MemoryWindow(() => webApiServer?.Debuggers, () => DebugWindowGame, () => currentRomPath),
                "palettes" => new PalettesWindow(() => webApiServer?.Debuggers, () => DebugWindowGame),
                "tiles" => new TilesWindow(() => webApiServer?.Debuggers, () => DebugWindowGame),
                "sprites" => new SpritesWindow(() => webApiServer?.Debuggers, () => DebugWindowGame),
                "maps" => new MapsWindow(() => webApiServer?.Debuggers, () => DebugWindowGame),
                "events" => new EventsWindow(() => webApiServer?.Debuggers, () => DebugWindowGame),
                "sound" => new SoundWindow(() => webApiServer?.Debuggers, () => DebugWindowGame),
                _ => throw new ArgumentException("no debug window " + which),
            };
            f.Location = new Point(Right + 8 < Screen.FromControl(this).WorkingArea.Right - 200 ? Right + 8 : Left + 40, Top + 40);
            f.FormClosed += (_, _) => debugWindows.Remove(which);
            debugWindows[which] = f;
            f.Show(this);
            Console.WriteLine($"[debug windows] {which} opened");
        }
    }
}

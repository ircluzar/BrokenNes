using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using NesEmulator;
using NesEmulator.Debugging;
using NesEmulator.Debugging.Port;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// The BrokenDev debug port in the desktop app (milestone D7, docs/projects/brokendev-debugging-plan.md "D7 settled"): with Emulator > Debug
    /// Port on (config <c>debugPortEnabled</c>, off by default), a homebrew dev build's prints, asserts, markers and timers (NES $401C-$401F,
    /// SNES / WideMess $4230-$423F, Game Boy the bgb / Emulicious messages) go to the app's log ("[port] ..."), to the HTTP API
    /// (GET /api/debug/port) and into the bug capture's ring (F9: the markers in the bundle's movie, every event in bug.json). A failed assert
    /// pauses the game and shows the message and the place in the title bar and the log (config <c>debugPortPauseOnAssert</c>, on). Off, the port
    /// is never attached: the machine is exactly as without it.
    /// </summary>
    public partial class MainForm
    {
        private DebugPort? port;
        private object? portGame;          // the NES or session the port listens to
        private Bus? portBus;              // the NES's bus (a ROM load makes a new one)
        private string portWhy = "";

        private bool PortOn => config.DebugPortEnabled;

        /// <summary>Before a frame (emulation thread): the port follows the running game; off, it is let go.</summary>
        private void PortBeforeFrame(object game)
        {
            if (!PortOn) { if (port != null) PortRelease(); return; }
            var bus = (game as NES)?.DebugBus;
            if (port != null && ReferenceEquals(game, portGame) && ReferenceEquals(bus, portBus)) return;
            PortRelease();
            portGame = game; portBus = bus;
            try
            {
                if (game is NES n && string.Equals(n.RomName, "test.nes", StringComparison.OrdinalIgnoreCase)) { portWhy = "the idle screen"; return; }
                var machine = game switch { NES nn when nn.DebugBus != null => DebugMachines.For(nn), IConsoleSession s => DebugMachines.For(s), _ => null };
                if (machine == null) { portWhy = "no game is running"; return; }
                var p = new DebugPort(machine, new Debugger(machine)) { Keep = 2000 };
                p.Event += PortEvent;
                p.Attach();
                port = p;
                portWhy = "listening: " + p.Where;
                Console.WriteLine($"[port] the debug port listens ({p.Where})");
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { portWhy = "no debug port on this machine: " + e.Message; }
        }

        private void PortRelease()
        {
            port?.Detach();
            port = null; portGame = null; portBus = null;
        }

        /// <summary>An event of the port (emulation thread).</summary>
        private void PortEvent(PortEvent e)
        {
            if (e.Kind is not (PortEventKind.TimerStart or PortEventKind.TimerStop)) Console.WriteLine(e.ToString());
            if (bugRing.Attached) bugRing.Note(e.KindName, e.Text);
            if (e.Kind == PortEventKind.Assert && config.DebugPortPauseOnAssert)
            {
                isPaused = true;   // the emulation loop stops after this frame
                string msg = $"assert failed: {e.Text} (at {e.Place}, frame {e.Frame})";
                try { BeginInvoke(new Action(() => { audioManager?.Stop(); Text = "BrokenNes - PAUSED: " + msg; Console.WriteLine("[port] paused: " + msg); })); } catch (InvalidOperationException) { }
            }
        }

        private void AddDebugPortMenu(ToolStripMenuItem emulatorMenu)
        {
            var on = new ToolStripMenuItem("Debug Port (homebrew prints, asserts, timers)") { CheckOnClick = true, Checked = config.DebugPortEnabled };
            on.ToolTipText = "A dev build's messages to BrokenDev's debug port go to the log and the HTTP API; a failed assert pauses the game. Off: nothing listens.";
            on.CheckedChanged += (_, _) => SetDebugPortEnabled(on.Checked);
            emulatorMenu.DropDownOpening += (_, _) => on.Checked = config.DebugPortEnabled;
            emulatorMenu.DropDownItems.Add(on);
        }

        private void SetDebugPortEnabled(bool on)
        {
            if (config.DebugPortEnabled == on) return;
            Helpers.ConfigHelper.Update(config, c => c.DebugPortEnabled = on);
            Console.WriteLine($"[port] the debug port: {(on ? "on" : "off")}");
            if (!on) _ = RunOnEmulationThreadAsync(PortRelease);   // let go now, even while the game is paused (no frame would do it)
        }

        private object PortStatus(long since, int limit)
        {
            var p = port;
            if (p == null) return new { success = true, enabled = config.DebugPortEnabled, listening = false, why = config.DebugPortEnabled ? portWhy : "the debug port is off (Emulator > Debug Port)" };
            var ev = p.Since(since);
            return new
            {
                success = true, enabled = config.DebugPortEnabled, listening = p.Attached && config.DebugPortEnabled, where = p.Where, cycleUnit = p.CycleUnit, total = p.EventCount,
                counts = new { print = p.Count(PortEventKind.Print), assert = p.Count(PortEventKind.Assert), marker = p.Count(PortEventKind.Marker), error = p.Count(PortEventKind.Error), breaks = p.Count(PortEventKind.Break) },
                events = ev.TakeLast(Math.Clamp(limit, 1, 2000)).Select(e => new { seq = e.Seq, kind = e.KindName, text = e.Text, frame = e.Frame, pc = e.PcText, pcSymbol = e.PcSymbol }).ToArray(),
                timers = p.Timers.Select(t => new { name = t.Name, calls = t.Calls, average = Math.Round(t.Average, 1), best = t.Calls > 0 ? t.Best : 0, worst = t.Worst, worstFrame = t.WorstFrame, frameWorst = t.FrameWorst, unmatched = t.Unmatched }).ToArray(),
            };
        }

        private void WirePortApi(WebApi.WebApiServer server)
        {
            server.GetDebugPort = PortStatus;
            server.SetDebugPortEnabled = on => SetDebugPortEnabled(on);
        }
    }
}

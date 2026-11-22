using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator.Systems;
using NesEmulator.X86;

namespace BrokenNes.Windows
{
    // nano98 install flow (2026-11-21): Emulator > Install a Windows game... The install seat (NesEmulator.X86.N98Install) runs in the game area like
    // any session (real keyboard + mouse); the wizard window (DebugWindows/N98InstallWizard.cs) and the HTTP API (/api/n98/install/ACTION) drive it
    // with the same actions.
    public partial class MainForm
    {
        private N98Install? n98Install;
        private DebugWindows.N98InstallWizard? n98Wizard;
        private static string N98InstallDiscardSave => Path.Combine(Path.GetTempPath(), "BrokenNes_n98install.n98s");

        internal void OpenN98InstallWizard()
        {
            if (n98Wizard == null || n98Wizard.IsDisposed) { n98Wizard = new DebugWindows.N98InstallWizard(this); n98Wizard.Show(this); }
            else n98Wizard.Activate();
        }

        internal N98Install? N98InstallSession => n98Install != null && ReferenceEquals(session, n98Install.Session) ? n98Install : null;

        /// <summary>Boots the install seat in the app's window (off the UI thread, behind the nano98 loading screen).</summary>
        internal async Task<N98Install> StartN98Install(N98Install.InstallOptions o)
        {
            StopEmulation();
            CloseN98Install();
            ShowN98Loading("Install a Windows game");
            N98Seat.Report("starting the install seat", -1);
            try
            {
                var ins = await Task.Run(() => N98Install.Start(o));
                try { File.Delete(N98InstallDiscardSave); } catch (IOException) { }
                FinishStartSession(ConsoleKind.Pc, ins.Session, Array.Empty<byte>(), "Install a Windows game.n98", null, start: false);
                n98Install = ins;
                await Task.Run(() => { lock (emulationLock) ins.Session.Prestart(); });
                HideN98Loading();
                StartEmulation();
                return ins;
            }
            catch
            {
                HideN98Loading();
                throw;
            }
        }

        /// <summary>The install seat is off (the user shut Windows down, or the wizard's Setup is done): the wizard captures.</summary>
        private void N98InstallSeatEnded(string kind, string detail)
        {
            n98Wizard?.SeatEnded(kind, detail);
        }

        internal void OpenN98Package(string path) => LoadN98File(path);

        internal void CloseN98Install()
        {
            if (n98Install == null) return;
            if (ReferenceEquals(session, n98Install.Session)) CloseRom_Click(this, EventArgs.Empty);
            else n98Install.Dispose();
            n98Install = null;
            try { File.Delete(N98InstallDiscardSave); } catch (IOException) { }
        }

        /// <summary>Runs an install action on the emulation lock (the session's frames run under it), off the UI thread.</summary>
        internal Task<JsonObject> N98InstallDo(string action, JsonObject a) => Task.Run(() =>
        {
            var ins = N98InstallSession ?? throw new InvalidOperationException("no install session: start one first (action 'start')");
            if (action is "programs" or "capture" or "capture-layer")
            {
                // the drives are read from the stopped machine (capture writes a few GB: never under the lock)
                if (!ins.PoweredOff) throw new InvalidOperationException("power the machine off first (action 'poweroff', then wait for status powered_off)");
                return ins.Do(action, a);
            }
            lock (emulationLock) return ins.Do(action, a);
        });

        private void WireN98InstallApi(WebApi.WebApiServer server)
        {
            server.N98InstallAction = async (action, body) =>
            {
                switch (action)
                {
                    case "start":
                        {
                            var o = new N98Install.InstallOptions
                            {
                                Disc = body["disc"]?.GetValue<string>(), Machine = body["machine"]?.GetValue<string>() ?? "win98-p166-nano",
                                Sound = body["sound"]?.GetValue<string>() ?? "SB16_D072", Settle = body["settle"]?.GetValue<bool>() ?? false,
                                Layers = body["layers"] is JsonArray la ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(la, x => x!.GetValue<string>())) : Array.Empty<string>(),
                            };
                            if (body["drive"]?.GetValue<string>() is { Length: > 0 } drv)
                                o.Disc = (await Task.Run(() => N98Install.DoStatic("rip", new JsonObject { ["drive"] = drv })))!["iso"]!.GetValue<string>();
                            var ins = await (Task<N98Install>)Invoke(new Func<Task<N98Install>>(() => StartN98Install(o)));
                            var st = ins.Status(); st["ok"] = true; return st;
                        }
                    case "open":
                        {
                            string p = body["path"]?.GetValue<string>() ?? throw new ArgumentException("path");
                            Invoke(() => { CloseN98Install(); LoadN98File(p); });
                            return new JsonObject { ["ok"] = true, ["opened"] = p };
                        }
                    case "close": Invoke(CloseN98Install); return new JsonObject { ["ok"] = true };
                    case "status" when N98InstallSession == null: return new JsonObject { ["ok"] = true, ["phase"] = "none" };
                }
                if (!(action == "setups" && body["disc"] == null) && N98Install.DoStatic(action, body) is { } r) return r;
                var res = await N98InstallDo(action, body);
                if (action == "capture" && body["open"]?.GetValue<bool>() != false && res["package"]?.GetValue<string>() is { } pkg)
                    Invoke(() => { CloseN98Install(); LoadN98File(pkg); });
                return res;
            };
        }
    }
}

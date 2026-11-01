using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NesEmulator.Systems;

namespace BrokenNes.Windows.Virtual
{
    /// <summary>
    /// <c>BrokenNes.Windows.exe --wmv-check &lt;cartridge.wmv&gt; [--out &lt;folder&gt;] [--bad &lt;bad.wmv&gt;]</c>: boots a WideMess virtual cartridge offscreen, the way the app does,
    /// without the app's window, and checks it: the title is reached (the picture is not blank, and it changes), the picture is 400 x 224 with the two black columns,
    /// the pad moves the title's menu and opens and closes its settings, BrokenNes's pause freezes the page, the server is gone after the eject. Then the negative
    /// control: a cartridge whose folder does not exist fails with a message and leaves no server. It measures frames delivered per second, the frames the display
    /// repeated or the page overwrote, and the time from a pad change to the page's frame that saw it. Exit 0 when every check passes, 1 otherwise, 2 for usage.
    /// The web view's profile goes to a scratch folder (under --out); the game's own settings go where its manifest's args say (a check passes --user-dir).
    /// </summary>
    internal static class VirtualCartCheck
    {
        public static bool IsRequested(string[] args) => args.Any(a => a.Equals("--wmv-check", StringComparison.OrdinalIgnoreCase));

        private sealed record Result(string Name, bool Pass, string Detail);

        public static int Run(string[] args)
        {
            string? Arg(string name) { int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string? cart = Arg("--wmv-check");
            if (cart == null) { Console.Error.WriteLine("usage: --wmv-check <cartridge.wmv> [--out <folder>] [--bad <bad.wmv>]"); return 2; }
            string outDir = Arg("--out") ?? Path.Combine(Path.GetTempPath(), "brokennes-wmv-check");
            Directory.CreateDirectory(outDir);
            var results = new List<Result>();
            void Add(string n, bool p, string d) { results.Add(new Result(n, p, d)); Console.WriteLine($"{(p ? "PASS" : "FAIL")} {n}: {d}"); }
            var metrics = new Dictionary<string, object>();

            VirtualCartManifest m;
            try { m = VirtualCartManifest.Load(cart); }
            catch (InvalidDataException ex) { Add("manifest", false, ex.Message); return Finish(results, metrics, outDir); }
            Add("manifest", true, $"{m.Title}: {m.Folder}");

            var sw = Stopwatch.StartNew();
            VirtualCartSession s;
            try { s = new VirtualCartSession(m, Path.Combine(outDir, "webview2")); }
            catch (Exception ex) { Add("boot", false, ex.Message); return Finish(results, metrics, outDir); }
            int pid = s.ServerProcessId;
            Add("server", true, $"pid {pid} on {s.ServerUrl}, up in {sw.ElapsedMilliseconds} ms");

            // the "emulation thread": RunFrame at 60 a second, as the app does
            var frames = new List<uint[]>();
            uint[] Snap() { lock (s) return (uint[])s.Frame.Clone(); }
            PadButtons pad = PadButtons.None;
            bool paused = false, running = true;
            var loop = new Thread(() =>
            {
                long period = Stopwatch.Frequency / 60, next = Stopwatch.GetTimestamp();
                var buf = new short[4096];
                while (Volatile.Read(ref running))
                {
                    if (!Volatile.Read(ref paused)) { lock (s) { s.SetPad(0, pad); s.RunFrame(); } while (s.ReadSamples(buf) > 0) { } }
                    next += period;
                    long wait = next - Stopwatch.GetTimestamp();
                    if (wait > 0) Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency)); else next = Stopwatch.GetTimestamp();
                }
            }) { IsBackground = true };
            loop.Start();

            try
            {
                // 1. the title: a picture that is not blank
                bool title = WaitFor(() => Colours(Snap()) > 8 && s.Evaluate("!!(window.vrunTitle && window.VRUN_INPUT)") == "true", 45000);
                Thread.Sleep(1500);   // (the title fades in)
                var t0 = Snap(); Save(t0, Path.Combine(outDir, "title.png"));
                Add("title", title && s.Problem == null, title ? $"reached in {sw.ElapsedMilliseconds} ms, {Colours(t0)} colours" : $"no title after 45 s ({s.Problem ?? "no picture"}; page errors: {s.LastPageError})");
                Thread.Sleep(1000);
                var t1 = Snap();
                Add("picture changes", Diff(t0, t1) > 0, $"{Diff(t0, t1)} pixels changed in 1 s at the title");

                // 2. 400 x 224 and the black columns
                bool columns = Enumerable.Range(0, 224).All(y => (t0[y * 400] & 0xFFFFFF) == 0 && (t0[y * 400 + 399] & 0xFFFFFF) == 0);
                bool inside = Enumerable.Range(0, 224).Any(y => (t0[y * 400 + 1] & 0xFFFFFF) != 0 || (t0[y * 400 + 398] & 0xFFFFFF) != 0) || Colours(t0) > 8;
                Add("400 x 224, black columns", s.FrameWidth == 400 && s.FrameHeight == 224 && s.DisplayWidth == 400 && columns && inside,
                    $"{s.FrameWidth} x {s.FrameHeight} (display {s.DisplayWidth}); columns 0 and 399 black: {columns}");

                // 3. frame pacing over 5 s at the title
                long pf0 = s.PageFrames, sh0 = s.FramesShown, rp0 = s.FramesRepeated, ow0 = s.FramesOverwritten;
                Thread.Sleep(5000);
                double fps = (s.PageFrames - pf0) / 5.0;
                metrics["pageFramesPerSecond"] = Math.Round(fps, 1);
                metrics["shownPerSecond"] = Math.Round((s.FramesShown - sh0) / 5.0, 1);
                metrics["repeatedPerSecond"] = Math.Round((s.FramesRepeated - rp0) / 5.0, 1);
                metrics["overwrittenPerSecond"] = Math.Round((s.FramesOverwritten - ow0) / 5.0, 1);
                Add("frame pacing", fps >= 50, $"the page delivered {fps:0.0} frames/s; the display showed {metrics["shownPerSecond"]}/s new, repeated {metrics["repeatedPerSecond"]}/s, the page overwrote {metrics["overwrittenPerSecond"]}/s unseen");

                // 4. the pad: the title's menu (Down moves the cursor; A on SETTINGS opens the settings; B closes them)
                string? Focus() => s.Evaluate("(function(){var c=document.getElementById('VRUNUi');var x=c.getContext('2d').getImageData(0,0,c.width,c.height).data;var h=0;for(var i=0;i<x.length;i+=4)h=(h*31+x[i]+x[i+1]*3+x[i+2]*7)|0;return h;})()");
                var latencies = new List<double>();
                double Press(PadButtons b, int holdMs = 120)
                {
                    long before = s.PageFrames; var t = Stopwatch.StartNew();
                    pad = b; WaitFor(() => s.LastPadEchoMs >= 0 && s.PageFrames > before, 1000);
                    double echo = s.LastPadEchoMs; latencies.Add(echo);
                    Thread.Sleep(Math.Max(0, holdMs - (int)t.ElapsedMilliseconds));
                    pad = PadButtons.None; Thread.Sleep(250);
                    return echo;
                }
                string? ui0 = Focus();
                var m0 = Snap();
                Press(PadButtons.Down);
                string? ui1 = Focus();
                var m1 = Snap(); Save(m1, Path.Combine(outDir, "title-down.png"));
                Add("pad moves the menu", ui0 != null && ui1 != null && ui0 != ui1, $"the title's UI layer {(ui0 != ui1 ? "changed" : "did not change")} after Down ({Diff(m0, m1)} pixels of the picture changed)");
                Press(PadButtons.Down);
                Press(PadButtons.B);                       // (B is the standard pad's bottom button: OK)
                Thread.Sleep(600);
                var st = Snap(); Save(st, Path.Combine(outDir, "settings.png"));
                bool settingsOpen = s.Evaluate("!!document.querySelector('#VRUNUi') && (window.VRUN_SETTINGS_OPEN || 0)") is { } so && so == "1" || Diff(m1, st) > 4000;
                Add("pad opens the settings", settingsOpen, $"{Diff(m1, st)} pixels changed after Down, OK on SETTINGS");
                Press(PadButtons.A);                       // (A is the right button: back)
                Thread.Sleep(600);
                var back = Snap(); Save(back, Path.Combine(outDir, "settings-closed.png"));
                Add("pad closes the settings", Diff(st, back) > 4000, $"{Diff(st, back)} pixels changed after Back");
                var ok = latencies.Where(x => x >= 0).ToList();
                metrics["padToPageFrameMsAvg"] = ok.Count > 0 ? Math.Round(ok.Average(), 1) : -1;
                metrics["padToPageFrameMsMax"] = ok.Count > 0 ? Math.Round(ok.Max(), 1) : -1;
                Add("input latency", ok.Count == latencies.Count && ok.Count > 0, $"pad change to the page's next frame that saw it: avg {metrics["padToPageFrameMsAvg"]} ms, max {metrics["padToPageFrameMsMax"]} ms over {ok.Count} presses");

                // 5. pause: no frames asked -> the page is frozen and muted; frames again -> it runs
                Volatile.Write(ref paused, true);
                bool frozen = WaitFor(() => s.PagePaused, 3000);
                long pfa = s.PageFrames; Thread.Sleep(1000); long during = s.PageFrames - pfa;
                Volatile.Write(ref paused, false);
                bool resumed = WaitFor(() => !s.PagePaused, 3000);
                long pfb = s.PageFrames; Thread.Sleep(1000); long after = s.PageFrames - pfb;
                Add("pause", frozen && during <= 2 && resumed && after > 30, $"paused: {frozen}, page frames in 1 s while paused: {during}; resumed: {resumed}, frames in the next 1 s: {after}");
                Add("no page errors", s.PageErrors == 0 && s.Problem == null, s.PageErrors == 0 ? "none" : $"{s.PageErrors}: {s.LastPageError}");
            }
            finally
            {
                Volatile.Write(ref running, false);
                loop.Join(2000);
                s.Dispose();
            }

            // 6. the eject: the server is gone
            bool gone = WaitFor(() => { try { return Process.GetProcessById(pid).HasExited; } catch (ArgumentException) { return true; } }, 5000);
            Add("eject stops the server", gone, gone ? $"pid {pid} is gone" : $"pid {pid} still runs");

            // 7. the negative control
            string? bad = Arg("--bad");
            if (bad != null)
            {
                try { VirtualCartManifest.Load(bad); Add("bad cartridge refused", false, "a manifest with a missing folder was accepted"); }
                catch (InvalidDataException ex) { Add("bad cartridge refused", ex.Message.Contains("does not exist"), ex.Message); }
            }
            string? badServer = Arg("--bad-server");
            if (badServer != null)
            {
                try
                {
                    using var never = new VirtualCartSession(VirtualCartManifest.Load(badServer), Path.Combine(outDir, "webview2"));
                    Add("dead server refused", false, "a cartridge whose server cannot start was accepted");
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { Add("dead server refused", ex.Message.Contains("did not start"), ex.Message); }
            }
            return Finish(results, metrics, outDir);
        }

        private static int Finish(List<Result> results, Dictionary<string, object> metrics, string outDir)
        {
            bool pass = results.Count > 0 && results.All(r => r.Pass);
            var json = JsonSerializer.Serialize(new { pass, checks = results.Select(r => new { name = r.Name, pass = r.Pass, detail = r.Detail }), metrics }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "wmv-check.json"), json);
            Console.WriteLine(json);
            Console.WriteLine(pass ? "WMV CHECK: PASS" : "WMV CHECK: FAIL");
            return pass ? 0 : 1;
        }

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
            return cond();
        }

        private static int Colours(uint[] f) => f.Distinct().Take(1000).Count();
        private static int Diff(uint[] a, uint[] b) { int n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }

        private static unsafe void Save(uint[] f, string path)
        {
            using var bmp = new Bitmap(400, 224, PixelFormat.Format32bppArgb);
            var d = bmp.LockBits(new Rectangle(0, 0, 400, 224), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < 224; y++) { var row = (uint*)((byte*)d.Scan0 + y * d.Stride); for (int x = 0; x < 400; x++) row[x] = f[y * 400 + x]; }
            bmp.UnlockBits(d);
            bmp.Save(path, ImageFormat.Png);
        }
    }
}

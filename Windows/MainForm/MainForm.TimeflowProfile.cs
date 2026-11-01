using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenDev Timeflow T3 in the desktop app: the Timeflow window's "Profile selection". The span is re-run at full depth on a HEADLESS machine of
    /// the timeline's own ROM and cores (never on the game the user is playing, which runs on untouched), kept in the game's profiles folder for the
    /// windows' scrubbing, and opened in the Debugger window (the trace, registers and call stack at any instruction) and the Event View (the
    /// chip events of a frame), both in profile mode. A profile not marked keep is deleted when the app closes (or, after a crash, by the next
    /// start's sweep); every profile is deleted when its time to live passes.
    /// </summary>
    public partial class MainForm
    {
        private string tfProfileResult = "";
        private string? tfProfileDir;
        private bool tfProfileBusy;
        private ProfileView? tfProfileView;
        private readonly List<Form> tfProfileWindows = new();
        private bool tfProfileHooked;

        /// <summary>Once per app: the start-up sweep (profiles a crashed or killed app left, expired ones) and the session's end at closing.</summary>
        private void TimeflowProfileInit()
        {
            if (tfProfileHooked) return;
            tfProfileHooked = true;
            Task.Run(() => { try { if (Directory.Exists(TimeflowStore.Root())) foreach (var s in ProfileStore.Sweep()) Console.WriteLine($"[Timeflow] swept profile {s.Id}: {s.Why}"); } catch (Exception e) { Console.WriteLine("[Timeflow] profile sweep: " + e.Message); } });
            FormClosed += (_, _) => { try { CloseProfileWindows(); int n = ProfileStore.EndSession(); if (n > 0) Console.WriteLine($"[Timeflow] {n} unkept profile(s) deleted as the app closes"); } catch (Exception) { } };
        }

        /// <summary>What the last Profile selection did (the window and GET /api/timeflow/profile show it).</summary>
        internal object TimeflowProfileStatus() => new
        {
            success = true, busy = tfProfileBusy, result = tfProfileResult, profileDir = tfProfileDir,
            profile = tfProfileDir != null && Directory.Exists(tfProfileDir) ? SafeMeta(tfProfileDir) : null,
            windows = tfProfileWindows.Where(f => !f.IsDisposed).Select(f => f.Name).ToArray(),
            kept = SafeList().Select(p => new { id = p.Meta.Id, dir = p.Dir, timeline = p.Meta.Timeline, from = p.Meta.From, to = p.Meta.To, status = p.Meta.Status, keep = p.Meta.Keep, bytes = p.Bytes, expires = p.Meta.ExpiresUtc.ToString("o"), owner = p.Meta.OwnerPid }).ToArray(),
        };

        private static ProfileMeta? SafeMeta(string dir) { try { return ProfileMeta.Load(dir); } catch (Exception) { return null; } }
        private static List<ProfileInfo> SafeList() { try { return ProfileStore.List(); } catch (Exception) { return new(); } }

        /// <summary>Profiles frames from..to of a timeline (the window's selection) on a headless machine, keeps it, and opens it in the debug windows.</summary>
        internal async Task<string> TimeflowProfileAsync(string timelineDir, long from, long to, bool keep, Action<string> progress)
        {
            if (tfProfileBusy) return "a profile is already being made";
            tfProfileBusy = true;
            try
            {
                string? sym = currentRomPath != null ? DebugWindows.DebuggerWindow.SymbolFileBeside(currentRomPath, ConsoleOfTimeline(timelineDir)) : null;
                var syms = sym != null ? new List<string> { sym } : new List<string>();
                var (msg, dir) = await Task.Run(() =>
                {
                    using var pb = new TimeflowPlayback(timelineDir);
                    using var pm = ProfileMachine.Open(timelineDir, syms);
                    pb.CheckMachine(pm.Source, pb.Meta.RomSha256);
                    var w = ProfileStore.Create(pb.Meta, timelineDir, from, to, new ProfileStoreOptions { Keep = keep, Symbols = syms, Note = "the desktop app's Profile selection" });
                    var summary = ProfileQuery.Parse("summary");
                    ProfileReport r;
                    try
                    {
                        r = TimeflowProfiler.Run(pb, pm.Target, new ProfileSpan
                        {
                            From = from, To = to,
                            Progress = (done, of) => { if (done % 10 == 0 || done == of) { try { BeginInvoke(new Action(() => progress($"profiling: frame {done} of {of}..."))); } catch (InvalidOperationException) { } } },
                        }, new ProfileSink[] { summary, w });
                    }
                    catch { w.Dispose(); ProfileStore.Delete(w.Dir); throw; }
                    w.Close(r);
                    string s = $"profiled frames {from}-{to} ({r.FramesProfiled} frames, {r.Instructions:N0} instructions, {r.Accesses:N0} accesses, {r.Fps:0} fps = {r.TimesSlower:0.00}x real time): " +
                               (r.InSync ? "in sync with the recording" : $"OUT OF SYNC at frame {r.OffFrame} ({r.OffWhat})") +
                               $"; kept as {w.Meta.Id} ({w.Meta.Status}, {w.Meta.Bytes / 1024:N0} KB{(keep ? ", kept after the app closes" : ", deleted when the app closes")})";
                    return (s, w.Dir);
                });
                tfProfileResult = msg; tfProfileDir = dir;
                Console.WriteLine("[Timeflow] " + msg);
                OpenProfileWindows(dir);
                return msg;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException or NotSupportedException or FileNotFoundException)
            {
                tfProfileResult = "profile refused: " + e.Message;
                return tfProfileResult;
            }
            finally { tfProfileBusy = false; }
        }

        private static string ConsoleOfTimeline(string dir) { try { var c = TimelineMeta.Load(dir).Console; return c == "wms" ? "widemess" : c; } catch (Exception) { return ""; } }

        /// <summary>Opens a kept profile in the Debugger window and the Event View (profile mode), replacing the windows of the profile before.</summary>
        internal void OpenProfileWindows(string profileDir)
        {
            CloseProfileWindows();
            var view = ProfileView.Open(profileDir);
            tfProfileView = view;
            var dbg = new DebugWindows.DebuggerWindow(view);
            var ev = new DebugWindows.EventsWindow(view);
            int x = Right + 8 < Screen.FromControl(this).WorkingArea.Right - 200 ? Right + 8 : Left + 40;
            dbg.Location = new Point(x, Top + 60);
            ev.Location = new Point(x + 40, Top + 100);
            foreach (var f in new Form[] { dbg, ev })
            {
                tfProfileWindows.Add(f);
                f.FormClosed += (_, _) =>
                {
                    tfProfileWindows.Remove(f);
                    if (tfProfileWindows.Count == 0 && ReferenceEquals(tfProfileView, view)) { view.Dispose(); tfProfileView = null; }
                };
                f.Show(this);
            }
            Console.WriteLine($"[debug windows] profile {view.Meta.Id} opened in the debugger and the event view");
        }

        private void CloseProfileWindows()
        {
            foreach (var f in tfProfileWindows.ToList()) { try { f.Close(); } catch (Exception) { } }
            tfProfileWindows.Clear();
            tfProfileView?.Dispose(); tfProfileView = null;
        }
    }
}

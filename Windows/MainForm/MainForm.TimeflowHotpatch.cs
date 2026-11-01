using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenDev Timeflow T5 in the desktop app: the Timeflow window's hotpatch panel. Byte patches (AT=OLD>NEW) are applied from the chosen frame of
    /// the chosen timeline on HEADLESS machines (the old build and the patched one; never the game the user is playing, which runs on untouched), the
    /// recorded inputs replay on the patched ROM (recorded as a branch of the timeline, with its patches), and the window shows the divergence from the
    /// old future and the verdict of the test the user wrote (also on the old future, for comparison). Nothing else is written: no verdict is kept as
    /// a test unless the user promotes it (brokendev.ps1 timeflow verdict-promote). GET /api/timeflow/hotpatch reads the last one (no route starts one).
    /// </summary>
    public partial class MainForm
    {
        private bool tfPatchBusy;
        private string tfPatchResult = "";
        private HotpatchReport? tfPatchReport;

        internal bool TimeflowPatchBusy => tfPatchBusy;
        internal HotpatchReport? TimeflowPatchLast => tfPatchReport;

        /// <summary>What the last hotpatch did (the window and GET /api/timeflow/hotpatch show it).</summary>
        internal object TimeflowPatchStatus() => new { success = true, busy = tfPatchBusy, result = tfPatchResult, report = tfPatchReport?.ToJson() };

        /// <summary>Byte patches from <paramref name="frame"/> of a timeline, its recorded inputs replayed on the patched ROM (a branch), the divergence and the verdicts.</summary>
        internal async Task<string> TimeflowPatchAsync(string timelineDir, long frame, IReadOnlyList<string> patches, IReadOnlyList<string> verdicts, Action<string> progress)
        {
            if (tfPatchBusy) return "a hotpatch is already running";
            List<RomPatch> ps; List<VerdictTest> vs;
            try
            {
                ps = patches.Where(p => p.Trim().Length > 0).Select(RomPatch.Parse).ToList();
                vs = verdicts.Where(v => v.Trim().Length > 0).Select(VerdictTest.Parse).ToList();
            }
            catch (FormatException e) { tfPatchResult = e.Message; return tfPatchResult; }
            if (ps.Count == 0) { tfPatchResult = "give a byte patch: " + RomPatch.Help; return tfPatchResult; }
            tfPatchBusy = true; tfPatchReport = null;
            try
            {
                string? sym = currentRomPath != null ? DebugWindows.DebuggerWindow.SymbolFileBeside(currentRomPath, ConsoleOfTimeline(timelineDir)) : null;
                var syms = sym != null ? new List<string> { sym } : new List<string>();
                var rep = await Task.Run(() => TimeflowHotpatch.Run(new HotpatchRequest
                {
                    TimelineDir = timelineDir, Frame = frame, Patches = ps, Symbols = syms, Verdicts = vs, Open = ProfileMachine.OpenForHotpatch,
                    Progress = s => { try { BeginInvoke(new Action(() => progress(s))); } catch (InvalidOperationException) { } },
                }));
                tfPatchReport = rep;
                if (rep.Refused != null) tfPatchResult = "REFUSED: " + rep.Refused;
                else
                {
                    string div = rep.Divergence?["summary"]?.ToString() ?? "";
                    string ver = rep.Verdicts.Count == 0 ? "no verdict asked" : string.Join("; ", rep.Verdicts.Select(v => $"{(v.Pass ? "PASS" : v.Decided ? "FAIL" : "UNDECIDED")} {v.Test} ({v.Evidence}){(v.Old != null ? $", old future {(v.Old.Pass ? "PASS" : "FAIL")}" : "")}"));
                    tfPatchResult = $"patched at frame {rep.ResumedAt}, {rep.FramesReplayed} frames replayed as branch {rep.Branch}: {div}. Verdict: {ver}";
                }
                Console.WriteLine($"[Timeflow] hotpatch: {tfPatchResult}");
                return tfPatchResult;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException or NotSupportedException or FormatException)
            {
                tfPatchResult = "hotpatch refused: " + e.Message;
                return tfPatchResult;
            }
            finally { tfPatchBusy = false; }
        }
    }
}

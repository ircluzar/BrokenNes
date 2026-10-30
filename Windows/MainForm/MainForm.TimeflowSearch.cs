using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NesEmulator.Debugging.Timeflow;

namespace BrokenNes.Windows
{
    /// <summary>
    /// BrokenDev Timeflow T4 in the desktop app: the Timeflow window's search box. A condition is searched over the running game's timelines (its
    /// Timeflow game folder): the index rules frames out, the unsure ones are re-run on HEADLESS machines of each timeline's own ROM and cores (never
    /// on the game the user is playing, which runs on untouched). The hits go on the window's scrubber and in its hit list; a hit is a jump target
    /// (the window's Jump) and a selection to profile. Nothing is written. GET /api/timeflow/search reads the last search (no route starts one).
    /// </summary>
    public partial class MainForm
    {
        private bool tfSearchBusy;
        private string tfSearchResult = "";
        private string tfSearchCondition = "";
        private SearchReport? tfSearchReport;
        private CancellationTokenSource? tfSearchCancel;

        internal bool TimeflowSearchBusy => tfSearchBusy;
        internal SearchReport? TimeflowSearchLast => tfSearchReport;

        /// <summary>What the last search did (the window and GET /api/timeflow/search show it).</summary>
        internal object TimeflowSearchStatus() => new
        {
            success = true, busy = tfSearchBusy, condition = tfSearchCondition, result = tfSearchResult,
            report = tfSearchReport?.ToJson(),
        };

        /// <summary>Searches the running game's timelines for a condition on headless machines; each hit is handed to <paramref name="onHit"/> as it is found (on the UI thread).</summary>
        internal async Task<string> TimeflowSearchAsync(string condition, Action<SearchHit> onHit, Action<string> progress)
        {
            if (tfSearchBusy) return "a search is already running";
            string? game = TimeflowGameDir();
            if (game == null || !Directory.Exists(game)) return "no timelines of this game yet (record some with REC, or from BrokenDev / an agent)";
            SearchCondition cond;
            try { cond = SearchCondition.Parse(condition); }
            catch (FormatException e) { tfSearchResult = "the condition does not read: " + e.Message; return tfSearchResult; }
            tfSearchBusy = true; tfSearchCondition = condition; tfSearchReport = null;
            tfSearchCancel = new CancellationTokenSource();
            var token = tfSearchCancel.Token;
            try
            {
                string? sym = currentRomPath != null ? DebugWindows.DebuggerWindow.SymbolFileBeside(currentRomPath, ConsoleOfTimeline(TimeflowStore.List(game).Select(t => t.Dir).FirstOrDefault() ?? "")) : null;
                var syms = sym != null ? new List<string> { sym } : new List<string>();
                var dirs = TimeflowStore.List(game).OrderBy(t => t.Meta.CreatedUtc).Select(t => t.Dir).ToList();
                var rep = await Task.Run(() => TimeflowSearch.Run(new SearchRequestCore
                {
                    Condition = cond, TimelineDirs = dirs, Open = ProfileMachine.OpenForSearch, Symbols = syms, MaxHits = 500, Cancel = () => token.IsCancellationRequested,
                    OnHit = h => { try { BeginInvoke(new Action(() => onHit(h))); } catch (InvalidOperationException) { } },
                    Progress = (d, n) => { try { BeginInvoke(new Action(() => progress($"searching: timeline {d} of {n}..."))); } catch (InvalidOperationException) { } },
                }));
                tfSearchReport = rep;
                tfSearchResult = $"{rep.TotalHits} hit(s){(rep.HitLimitReached ? " (the first 500)" : "")} in {rep.Timelines.Count} timeline(s): {rep.CoarseFrames} frames scanned, {rep.Candidates} candidate(s), {rep.ReRunFrames} re-run, {rep.TotalMs / 1000:0.0} s" +
                                 (rep.Cancelled ? " (cancelled)" : "") + (rep.Timelines.FirstOrDefault(t => t.Skipped != null) is { } sk ? $"; skipped: {sk.Skipped}" : "");
                Console.WriteLine($"[Timeflow] search '{condition}': {tfSearchResult}");
                return tfSearchResult;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException or NotSupportedException or FormatException)
            {
                tfSearchResult = "search refused: " + e.Message;
                return tfSearchResult;
            }
            finally { tfSearchBusy = false; tfSearchCancel?.Dispose(); tfSearchCancel = null; }
        }

        internal void TimeflowSearchCancel() { try { tfSearchCancel?.Cancel(); } catch (ObjectDisposedException) { } }
    }
}

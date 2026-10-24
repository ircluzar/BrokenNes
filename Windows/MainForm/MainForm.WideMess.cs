using System;
using System.Linq;
using System.Windows.Forms;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes.Windows
{
    /// <summary>
    /// WideMess milestone 6 (docs/projects/widemess-spec.md section 6.3): Console > WideMess enhancements, for a SNES game on the WideMess console. The game's profile (or "no profile for this
    /// game", or why its profile is broken), "Use the profile", one item per enhancement with the profile's verdict, and the profile's known issues. The choices are kept per game (by the image's
    /// SHA-1) and applied when the game starts again. The HTTP API (/api/widemess/...) drives the same session without touching the settings.
    /// </summary>
    public partial class MainForm
    {
        private ToolStripMenuItem? wmsMenu, wmsStatusItem, wmsUseProfileItem, wmsIssuesItem;
        private readonly ToolStripMenuItem?[] wmsItems = new ToolStripMenuItem?[4];

        private static readonly string[] WmsLabels = { "Widescreen (400 x 224)", "No sprite flicker", "Better sound (cubic interpolation)", "Fast CPU (changes the game's timing; switch it on after the game has started, never kept)" };

        private SnesSession? WideMessSnesGame => session is SnesSession { WideMessPpu: not null } s ? s : null;

        /// <summary>Console > WideMess enhancements (shown only where the WideMess console is offered).</summary>
        private ToolStripItem BuildWideMessMenu()
        {
            wmsMenu = new ToolStripMenuItem("WideMess enhancements") { Visible = Consoles.WideMessEnabled, ToolTipText = "A SNES game on the WideMess console: widescreen, no sprite flicker, better sound, fast CPU (the console's, never the game's)" };
            wmsStatusItem = new ToolStripMenuItem("") { Enabled = false };
            wmsUseProfileItem = new ToolStripMenuItem("Use the profile", null, (s, e) => ToggleWideMessProfile());
            wmsMenu.DropDownItems.Add(wmsStatusItem);
            wmsMenu.DropDownItems.Add(wmsUseProfileItem);
            wmsMenu.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < 4; i++)
            {
                var which = (WmsEnhancement)i;
                wmsItems[i] = new ToolStripMenuItem(WmsLabels[i], null, (s, e) => ToggleWideMessEnhancement(which));
                wmsMenu.DropDownItems.Add(wmsItems[i]!);
            }
            wmsMenu.DropDownItems.Add(new ToolStripSeparator());
            wmsIssuesItem = new ToolStripMenuItem("Known issues...", null, (s, e) => ShowWideMessIssues());
            wmsMenu.DropDownItems.Add(wmsIssuesItem);
            wmsMenu.DropDownOpening += (s, e) => RefreshWideMessMenu();
            RefreshWideMessMenu();
            return wmsMenu;
        }

        /// <summary>The menu's items as the running game has them: the profile line, each enhancement's verdict and state, and what is not offered and why.</summary>
        private void RefreshWideMessMenu()
        {
            if (wmsMenu == null) return;
            wmsMenu.Visible = Consoles.WideMessEnabled;
            var s = WideMessSnesGame;
            string? refusal = s == null ? "Start a SNES game on the WideMess console (Console > WideMess)" : s.EnhanceRefusal;
            var look = s?.EnhanceProfile;
            wmsStatusItem!.Text = refusal ?? look!.Describe();
            var p = look?.Profile;
            wmsUseProfileItem!.Enabled = refusal == null && p != null;
            wmsUseProfileItem.Checked = s != null && p != null && p.Recommended.Length > 0 && RecommendedOn(s, p);
            wmsUseProfileItem.Text = p == null ? "Use the profile (no profile)" : $"Use the profile ({(p.Recommended.Length == 0 ? "it recommends nothing" : string.Join(", ", p.Recommended.Select(WmsProfile.Key)))})";
            for (int i = 0; i < 4; i++)
            {
                var e = (WmsEnhancement)i;
                var item = wmsItems[i]!;
                string? why = s == null ? refusal : s.CannotEnhance(e);
                string verdict = p != null ? $"profile: {WmsProfile.VerdictText(p.Verdict(e))}" : e == WmsEnhancement.Widescreen ? "needs a profile" : "not verified for this game";
                item.Text = $"{WmsLabels[i]} - {verdict}";
                item.Checked = s != null && s.Enhancements[e];
                item.Enabled = s != null && (why == null || item.Checked);
                item.ToolTipText = why ?? (p?.Verdict(e) == WmsVerdict.Broken ? "The profile says this does not work for this game; it is still offered" : null);
            }
            wmsIssuesItem!.Enabled = p != null && p.Issues.Length > 0;
            if (s != null && s.EnhanceNote.Length > 0) wmsStatusItem.Text += $" ({s.EnhanceNote})";
        }

        private static bool RecommendedOn(SnesSession s, WmsProfile p)
        {
            for (int i = 0; i < 4; i++) if (s.Enhancements[(WmsEnhancement)i] != p.Recommended.Contains((WmsEnhancement)i)) return false;
            return true;
        }

        private void ToggleWideMessProfile()
        {
            var s = WideMessSnesGame;
            if (s == null) return;
            var p = s.EnhanceProfile.Profile;
            bool on = !(p != null && p.Recommended.Length > 0 && RecommendedOn(s, p));
            string? why;
            lock (emulationLock) why = s.UseProfile(on);
            AfterWideMessChange(s, why);
        }

        private void ToggleWideMessEnhancement(WmsEnhancement e)
        {
            var s = WideMessSnesGame;
            if (s == null) return;
            bool on = !s.Enhancements[e];
            string? why;
            lock (emulationLock)
                why = e switch
                {
                    WmsEnhancement.Widescreen => s.SetEnhancements(widescreen: on),
                    WmsEnhancement.NoFlicker => s.SetEnhancements(noFlicker: on),
                    WmsEnhancement.BetterSound => s.SetEnhancements(betterSound: on),
                    _ => s.SetEnhancements(fastCpu: on),
                };
            AfterWideMessChange(s, why);
        }

        /// <summary>After a switch from the menu: keep the choice for this game, or say why it was refused.</summary>
        private void AfterWideMessChange(SnesSession s, string? why)
        {
            if (why != null) { MessageBox.Show(this, why, "WideMess enhancements", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var e = s.Enhancements;
            Helpers.ConfigHelper.Update(config, c =>
            {
                if (e.Widescreen || e.NoFlicker || e.BetterSound) c.WideMessEnhancements[s.ImageSha1] = new WideMessEnhanceChoice { Widescreen = e.Widescreen, NoFlicker = e.NoFlicker, BetterSound = e.BetterSound };   // fast CPU is never kept (spec 6.3)
                else c.WideMessEnhancements.Remove(s.ImageSha1);
            });
            Console.WriteLine($"[WideMess] {s.Title}: {e}");
            RefreshWideMessMenu();
            UpdateConsoleTitle();
        }

        /// <summary>A game starting: the enhancements kept for it, switched on again (each one that its profile no longer offers stays off, and the title says so).</summary>
        private void ApplySavedWideMessEnhancements()
        {
            var s = WideMessSnesGame;
            if (s == null || !s.CanEnhance || !config.WideMessEnhancements.TryGetValue(s.ImageSha1, out var c)) return;
            string? why = null;
            foreach (var (on, e) in new[] { (c.Widescreen, WmsEnhancement.Widescreen), (c.NoFlicker, WmsEnhancement.NoFlicker), (c.BetterSound, WmsEnhancement.BetterSound) })
            {
                if (!on) continue;
                string? r = e switch
                {
                    WmsEnhancement.Widescreen => s.SetEnhancements(widescreen: true),
                    WmsEnhancement.NoFlicker => s.SetEnhancements(noFlicker: true),
                    WmsEnhancement.BetterSound => s.SetEnhancements(betterSound: true),
                    _ => s.SetEnhancements(fastCpu: true),
                };
                why ??= r;
            }
            if (why != null) consoleNote = $"a kept WideMess enhancement stays off: {why}";
            Console.WriteLine($"[WideMess] {s.Title}: kept enhancements {s.Enhancements}{(why != null ? $" ({why})" : "")}");
        }

        private void ShowWideMessIssues()
        {
            var p = WideMessSnesGame?.EnhanceProfile.Profile;
            if (p == null) return;
            string text = $"{p.Title} ({p.Source}, verified {p.VerifiedDate}: {p.VerifiedHow})\n\n" + string.Join("\n\n", p.Issues.Select(i => "- " + i));
            MessageBox.Show(this, text, "WideMess enhancements: known issues", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>The title's note while enhancements are on.</summary>
        private string WideMessTitleNote() => WideMessSnesGame is { Enhancements.Any: true } s ? $" [WideMess: {s.Enhancements}]" : "";

        // ------------------------------------------------------------------ HTTP API (scratch only: the settings file is not touched)
        private void WireWideMessApi(WebApi.WebApiServer server)
        {
            server.GetWideMessEnhance = () => WideMessState();
            server.SetWideMessEnhance = req =>
            {
                string? error = null;
                Invoke(() =>
                {
                    var s = WideMessSnesGame;
                    if (s == null) { error = "no SNES game is running on the WideMess console"; return; }
                    lock (emulationLock)
                    {
                        if (req.UseProfile is bool use) error = s.UseProfile(use);
                        error ??= s.SetEnhancements(req.Widescreen, req.NoFlicker, req.BetterSound, req.FastCpu);
                    }
                    RefreshWideMessMenu();
                    UpdateConsoleTitle();
                });
                return (error, WideMessState());
            };
            server.SetWideMessScratchProfile = json =>
            {
                string? error = null;
                Invoke(() =>
                {
                    var s = WideMessSnesGame;
                    if (s == null) { error = "no SNES game is running on the WideMess console"; return; }
                    try
                    {
                        var p = WmsProfile.Parse(json, "scratch profile");
                        lock (emulationLock) s.SetScratchProfile(p);
                    }
                    catch (System.IO.InvalidDataException ex) { error = ex.Message; }
                    RefreshWideMessMenu();
                });
                return error;
            };
        }

        private object WideMessState()
        {
            var s = WideMessSnesGame;
            if (s == null) return new { running = false, reason = "no SNES game is running on the WideMess console" };
            var look = s.EnhanceProfile; var p = look.Profile; var e = s.Enhancements;
            return new
            {
                running = true,
                game = s.Title,
                sha1 = s.ImageSha1,
                canEnhance = s.EnhanceRefusal == null,
                refusal = s.EnhanceRefusal,
                profile = p == null ? null : new { title = p.Title, source = p.Source, verified = p.VerifiedDate, how = p.VerifiedHow, recommended = p.Recommended.Select(WmsProfile.Key), issues = p.Issues },
                profileStatus = look.Describe(),
                on = new { widescreen = e.Widescreen, noFlicker = e.NoFlicker, betterSound = e.BetterSound, fastCpu = e.FastCpu },
                offered = Enum.GetValues<WmsEnhancement>().ToDictionary(WmsProfile.Key, x => new
                {
                    verdict = p != null ? WmsProfile.VerdictText(p.Verdict(x)) : x == WmsEnhancement.Widescreen ? "needs a profile" : "not verified",
                    refusal = s.CannotEnhance(x),
                }),
                note = s.EnhanceNote,
                frameWidth = s.FrameWidth,
            };
        }
    }
}

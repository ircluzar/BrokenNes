using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NesEmulator.Snes;
using NesEmulator.Systems;

namespace BrokenNes
{
    /// <summary>
    /// BrokenNes Lite (WideMess milestone 7a): the WideMess milestone-6 enhancements of a SNES game on the WideMess console, as the desktop's Console > WideMess enhancements has them
    /// (docs/projects/widemess-spec.md section 6.3): the game's profile line, "Use the profile", one switch per enhancement with the profile's verdict and why one cannot be switched on, the known
    /// issues. The choices are kept per game (by the image's SHA-1) in IndexedDB and applied when the game starts again; fast CPU is never kept (spec 6.3). The profiles are the repository's
    /// WideMess/profiles, built into the app and written into the browser's in-memory file system at start-up, where the shared profile reader finds them.
    /// </summary>
    public partial class Emulator
    {
        /// <summary>The running SNES game on the WideMess console (the only kind of game that has enhancements), or null.</summary>
        private SnesSession? WideMessSnesGame => session is SnesSession { WideMessPpu: not null } s ? s : null;

        private static bool wmsProfilesReady;
        private static string wmsProfilesProblem = "";

        /// <summary>Writes the built-in profiles into the in-memory file system once (the profile reader reads a folder), and points the reader there. A failure is said on the enhancements line, never hidden.</summary>
        private static void EnsureWideMessProfiles()
        {
            if (wmsProfilesReady) return;
            wmsProfilesReady = true;
            try
            {
                var asm = typeof(Emulator).Assembly;
                string dir = Path.Combine(Path.GetTempPath(), "WideMess", "profiles");
                Directory.CreateDirectory(dir);
                int n = 0;
                foreach (var name in asm.GetManifestResourceNames().Where(r => r.StartsWith("WideMess.profiles.", StringComparison.Ordinal)))
                {
                    using var src = asm.GetManifestResourceStream(name)!;
                    using var dst = File.Create(Path.Combine(dir, name["WideMess.profiles.".Length..]));
                    src.CopyTo(dst);
                    n++;
                }
                WmsProfiles.Directory = dir;
                if (n == 0) wmsProfilesProblem = "this build of Lite carries no WideMess profiles";
            }
            catch (Exception ex) { wmsProfilesProblem = "the WideMess profiles could not be read in this browser: " + ex.Message; }
        }

        /// <summary>Whether the enhancements row applies: the WideMess console is the active one.</summary>
        public bool ShowsWideMessEnhancements => Consoles.IsAvailable(ConsoleKind.WideMess) && ActiveConsole == ConsoleKind.WideMess;

        /// <summary>The profile line (or why there are no enhancements).</summary>
        public string WideMessEnhanceStatus
        {
            get
            {
                EnsureWideMessProfiles();
                var s = WideMessSnesGame;
                if (s == null) return "Enhancements: start a SNES game on the WideMess console";
                string line = s.EnhanceRefusal ?? s.EnhanceProfile.Describe();
                if (wmsProfilesProblem.Length > 0) line += $" ({wmsProfilesProblem})";
                if (s.EnhanceNote.Length > 0) line += $" ({s.EnhanceNote})";
                return line;
            }
        }

        public static readonly string[] WideMessEnhanceLabels = { "Widescreen", "No flicker", "Better sound", "Fast CPU" };

        /// <summary>Whether an enhancement is on.</summary>
        public bool WideMessEnhanceOn(int which) => WideMessSnesGame is { } s && s.Enhancements[(WmsEnhancement)which];

        /// <summary>Why an enhancement cannot be switched on now (null: it can), and the profile's verdict for it.</summary>
        public string? WideMessEnhanceWhyNot(int which) { EnsureWideMessProfiles(); var s = WideMessSnesGame; return s == null ? "start a SNES game on the WideMess console" : s.CannotEnhance((WmsEnhancement)which); }

        public string WideMessEnhanceVerdict(int which)
        {
            var p = WideMessSnesGame?.EnhanceProfile.Profile;
            var e = (WmsEnhancement)which;
            return p != null ? WmsProfile.VerdictText(p.Verdict(e)) : e == WmsEnhancement.Widescreen ? "needs a profile" : "not verified for this game";
        }

        /// <summary>The profile's known issues, one per line ("" when none).</summary>
        public string WideMessEnhanceIssues => WideMessSnesGame?.EnhanceProfile.Profile is { } p ? string.Join("\n", p.Issues.Select(i => "- " + i)) : "";

        /// <summary>Whether "Use the profile" is what is on now (its recommendations, nothing else).</summary>
        public bool WideMessProfileOn
        {
            get
            {
                var s = WideMessSnesGame; var p = s?.EnhanceProfile.Profile;
                if (s == null || p == null || p.Recommended.Length == 0) return false;
                for (int i = 0; i < 4; i++) if (s.Enhancements[(WmsEnhancement)i] != p.Recommended.Contains((WmsEnhancement)i)) return false;
                return true;
            }
        }

        /// <summary>Switches one enhancement (the page's checkbox): kept for the game, or refused with the reason on the status line.</summary>
        public async Task SetWideMessEnhancementAsync(int which, bool on)
        {
            EnsureWideMessProfiles();
            var s = WideMessSnesGame;
            if (s == null) return;
            string? why = (WmsEnhancement)which switch
            {
                WmsEnhancement.Widescreen => s.SetEnhancements(widescreen: on),
                WmsEnhancement.NoFlicker => s.SetEnhancements(noFlicker: on),
                WmsEnhancement.BetterSound => s.SetEnhancements(betterSound: on),
                _ => s.SetEnhancements(fastCpu: on),
            };
            await AfterWideMessChangeAsync(s, why);
        }

        /// <summary>"Use the profile" (on: what the profile recommends and nothing else; off: every enhancement off).</summary>
        public async Task ToggleWideMessProfileAsync()
        {
            EnsureWideMessProfiles();
            var s = WideMessSnesGame;
            if (s == null) return;
            string? why = s.UseProfile(!WideMessProfileOn);
            await AfterWideMessChangeAsync(s, why);
        }

        /// <summary>The WideMess test cartridge (WideMess/testcart, built into the app) on the WideMess console, as the desktop's Console > WideMess test cartridge. Said on the status line if it cannot start.</summary>
        public async Task StartWideMessTestCartAsync()
        {
            byte[]? cart = null;
            try
            {
                using var s = typeof(Emulator).Assembly.GetManifestResourceStream("widemess-testcart.wms");
                if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); cart = ms.ToArray(); }
            }
            catch (Exception ex) { Logger.LogWarning(ex, "WideMess test cartridge"); }
            if (cart == null) { Status.Set("This build of Lite carries no WideMess test cartridge"); StateHasChanged(); return; }
            await StartSessionAsync(ConsoleKind.WideMess, cart, "widemess-testcart.wms");
        }

        private static string WideMessKey(SnesSession s) => "pref_wmsenh_" + s.ImageSha1;

        private async Task AfterWideMessChangeAsync(SnesSession s, string? why)
        {
            if (why != null) { Status.Set("WideMess enhancements: " + why); StateHasChanged(); return; }
            var e = s.Enhancements;
            // fast CPU is never kept (spec 6.3: from power-on it hangs some games at their sound-driver upload)
            string kept = (e.Widescreen ? "w" : "") + (e.NoFlicker ? "n" : "") + (e.BetterSound ? "b" : "");
            try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", WideMessKey(s), kept); } catch (Exception ex) { Logger.LogWarning(ex, "WideMess enhancements: the choice was not kept"); }
            Status.Set($"WideMess: {s.Title}: {(e.Any ? e.ToString() : "no enhancements")}");
            StateHasChanged();
        }

        /// <summary>A game starting on the WideMess console: the enhancements kept for it are switched on again (one its profile no longer offers stays off, and the status line says why).</summary>
        private async Task ApplySavedWideMessEnhancementsAsync()
        {
            EnsureWideMessProfiles();
            var s = WideMessSnesGame;
            if (s == null || !s.CanEnhance) return;
            string? kept = null;
            try { kept = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", WideMessKey(s)); } catch { }
            if (string.IsNullOrEmpty(kept)) return;
            string? why = null;
            if (kept.Contains('w')) why ??= s.SetEnhancements(widescreen: true);
            if (kept.Contains('n')) why ??= s.SetEnhancements(noFlicker: true);
            if (kept.Contains('b')) why ??= s.SetEnhancements(betterSound: true);
            Status.Set(why != null ? $"WideMess: a kept enhancement stays off: {why}" : $"WideMess: {s.Title}: kept enhancements {s.Enhancements}");
        }
    }
}

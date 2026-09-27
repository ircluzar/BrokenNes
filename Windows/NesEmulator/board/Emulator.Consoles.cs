using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NesEmulator.Systems;

namespace BrokenNes
{
    /// <summary>
    /// BrokenNes 2 Lite: the console selector (NES / SNES / Game Boy / Game Boy Color), SNES and Game Boy games running as
    /// NesEmulator.Systems console sessions through the same JS frame loop, their cores per console (own family first) and
    /// their battery saves in IndexedDB. The NES keeps its original path; while a session runs, the NES object idles.
    /// </summary>
    public partial class Emulator
    {
        private IConsoleSession? session;
        private byte[]? sessionRom;
        private string sessionRomName = "";
        private byte[]? sessionLastSave;
        private long sessionFrames;
        private readonly bool[] extraP1 = new bool[4], extraP2 = new bool[4];   // X, Y, L, R
        private readonly short[] sessionAudio = new short[32768];
        private readonly Dictionary<string, string> consoleCores = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>BrokenNes 1 behaviour: cores and shaders limited to what the Deck Builder campaign unlocked. Off.</summary>
        public static bool LegacyUnlockLocks = false;

        public ConsoleKind SelectedConsole { get; private set; } = ConsoleKind.Nes;
        /// <summary>The console actually running: a session's console, or the NES.</summary>
        public ConsoleKind ActiveConsole => session?.Console ?? SelectedConsole;
        public bool SessionActive => session != null;
        public string? SessionTitle => session?.Title;
        public string? SessionDescription => session?.Description;
        public string ActiveConsoleName => Consoles.DisplayName(ActiveConsole);

        /// <summary>Restore the console and its core picks (IndexedDB prefs), once at start-up.</summary>
        public async Task LoadConsolePrefsAsync()
        {
            try
            {
                var c = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", "pref_console");
                if (!string.IsNullOrEmpty(c)) SelectedConsole = Consoles.FromKey(c);
                foreach (var k in Consoles.All.Where(k => k != ConsoleKind.Nes))
                    foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
                    {
                        var v = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", CoreKey(k, slot));
                        if (!string.IsNullOrEmpty(v)) consoleCores[CoreKey(k, slot)] = v;
                    }
            }
            catch (Exception ex) { Logger.LogWarning(ex, "console prefs"); }
        }

        private static string CoreKey(ConsoleKind k, CoreSlot slot) => $"pref_core_{Consoles.Key(k)}_{slot.ToString().ToLowerInvariant()}";

        /// <summary>The SNES / Game Boy core picked for a slot (validated against the catalog).</summary>
        public string ConsoleCore(ConsoleKind console, CoreSlot slot) =>
            CoreCatalog.Resolve(console, slot, consoleCores.TryGetValue(CoreKey(console, slot), out var v) ? v : null);

        public IReadOnlyList<CoreOption> ConsoleCoreOptions(CoreSlot slot) => CoreCatalog.Options(ActiveConsole, slot);

        /// <summary>Pick a SNES / Game Boy core; a running game restarts on it (those cores are wired at start).</summary>
        public async Task SetConsoleCoreAsync(CoreSlot slot, string id)
        {
            var console = ActiveConsole;
            if (console == ConsoleKind.Nes) return;
            consoleCores[CoreKey(console, slot)] = id;
            try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", CoreKey(console, slot), id); } catch { }
            if (session != null && sessionRom != null) await StartSessionAsync(session.Console, sessionRom, sessionRomName);
            StateHasChanged();
        }

        /// <summary>Switch the base console. A Game Boy game restarts on the Game Boy Color (and back); a game the new
        /// console cannot run is closed and the NES idle screen shows until a ROM is loaded.</summary>
        public async Task SelectConsoleAsync(ConsoleKind console)
        {
            SelectedConsole = console;
            try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_console", Consoles.Key(console)); } catch { }
            if (session != null && sessionRom != null)
            {
                if (RomDetect.Runs(console, session.Console)) { await StartSessionAsync(console, sessionRom, sessionRomName); return; }
                await StopSessionAsync();
            }
            Status.Set(console == ConsoleKind.Nes ? "NES selected" : $"{Consoles.DisplayName(console)} selected - load a ROM from the ROM Manager");
            await JS.InvokeVoidAsync("nesInterop.setTouchLayout", Consoles.Key(ActiveConsole));
            StateHasChanged();
        }

        /// <summary>An uploaded ROM for another console than the NES starts a session; returns false for NES ROMs.</summary>
        private async Task<bool> TryStartSessionAsync(string romName, byte[] data)
        {
            byte[] rom; string inner;
            try { rom = RomDetect.Unwrap(data, romName, out inner); } catch { return false; }
            var kind = RomDetect.Detect(rom, inner);
            if (kind == null || kind == ConsoleKind.Nes)
            {
                if (session != null) await StopSessionAsync();
                if (kind == ConsoleKind.Nes && SelectedConsole != ConsoleKind.Nes) { SelectedConsole = ConsoleKind.Nes; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_console", "nes"); } catch { } }
                return false;
            }
            var console = kind.Value;
            // A Game Boy cartridge runs on whichever of Game Boy / Game Boy Color is selected (Color-only carts: Color).
            if (Consoles.IsGameBoy(console) && Consoles.IsGameBoy(SelectedConsole) && !(console == ConsoleKind.GameBoyColor && (rom[0x143] & 0xC0) == 0xC0))
                console = SelectedConsole;
            await StartSessionAsync(console, rom, romName);
            return true;
        }

        private async Task StartSessionAsync(ConsoleKind console, byte[] rom, string romName)
        {
            bool wasRunning = nesController.IsRunning;
            if (wasRunning) await PauseEmulation();
            await SaveSessionBatteryAsync(force: true);
            IConsoleSession s;
            try
            {
                s = ConsoleSessions.Create(console, rom, ConsoleCore(console, CoreSlot.Cpu), ConsoleCore(console, CoreSlot.Ppu), ConsoleCore(console, CoreSlot.Apu));
            }
            catch (Exception ex)
            {
                nesController.ErrorMessage = $"Could not start {Consoles.DisplayName(console)}: {ex.Message}";
                Status.Set(nesController.ErrorMessage);
                StateHasChanged();
                return;
            }
            session?.Dispose();
            session = s; sessionRom = rom; sessionRomName = romName; sessionFrames = 0;
            SelectedConsole = console;
            try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_console", Consoles.Key(console)); } catch { }
            nesController.CurrentRomName = romName;
            nesController.ErrorMessage = "";
            await LoadSessionBatteryAsync();
            try { await JS.InvokeVoidAsync("nesInterop.setTouchLayout", Consoles.Key(console)); } catch { }
            Status.Set($"{Consoles.DisplayName(console)}: {s.Title}");
            await StartEmulation();
            StateHasChanged();
        }

        private async Task StopSessionAsync()
        {
            if (session == null) return;
            bool wasRunning = nesController.IsRunning;
            if (wasRunning) await PauseEmulation();
            await SaveSessionBatteryAsync(force: true);
            session.Dispose();
            session = null; sessionRom = null; sessionLastSave = null;
            nesController.CurrentRomName = nes?.RomName ?? "test.nes";
            try { await JS.InvokeVoidAsync("nesInterop.setTouchLayout", Consoles.Key(ActiveConsole)); } catch { }
            if (wasRunning) await StartEmulation();
        }

        // ------------------------------------------------------------------ battery saves (IndexedDB kv)
        private string SessionSaveKey(IConsoleSession s) => s.Console == ConsoleKind.Snes ? $"battery_v1:{s.GameId}.srm" : $"battery_v1:gb{s.GameId}.sav";

        private async Task LoadSessionBatteryAsync()
        {
            var s = session;
            if (s == null || !s.HasBattery) return;
            try
            {
                var data = await JS.InvokeAsync<byte[]?>("nesInterop.idbGetBytes", SessionSaveKey(s));
                if (data != null && data.Length > 0) s.ImportSave(data);
            }
            catch (Exception ex) { Logger.LogWarning(ex, "session save load"); }
            sessionLastSave = s.ExportSave();
        }

        private async Task SaveSessionBatteryAsync(bool force)
        {
            var s = session;
            if (s == null || !s.HasBattery) return;
            var data = s.ExportSave();
            if (!force && sessionLastSave != null && data.AsSpan().SequenceEqual(sessionLastSave)) return;
            try { if (await JS.InvokeAsync<bool>("nesInterop.idbSetBytes", SessionSaveKey(s), data)) sessionLastSave = data; }
            catch (Exception ex) { Logger.LogWarning(ex, "session save write"); }
        }

        /// <summary>Flush the running SNES / Game Boy game's save (page hidden, ROM switch).</summary>
        public Task FlushSessionSaveAsync() => SaveSessionBatteryAsync(force: false);

        // ------------------------------------------------------------------ one frame
        private static PadButtons PadFrom(bool[] nes, bool[] extra)
        {
            PadButtons b = 0;
            if (nes[0]) b |= PadButtons.A; if (nes[1]) b |= PadButtons.B; if (nes[2]) b |= PadButtons.Select; if (nes[3]) b |= PadButtons.Start;
            if (nes[4]) b |= PadButtons.Up; if (nes[5]) b |= PadButtons.Down; if (nes[6]) b |= PadButtons.Left; if (nes[7]) b |= PadButtons.Right;
            if (extra[0]) b |= PadButtons.X; if (extra[1]) b |= PadButtons.Y; if (extra[2]) b |= PadButtons.L; if (extra[3]) b |= PadButtons.R;
            return b;
        }

        private FramePayload RunSessionFrame(IConsoleSession s)
        {
            try
            {
                s.SetPad(0, PadFrom(inputState, extraP1));
                s.SetPad(1, PadFrom(inputStateP2, extraP2));
                int runs = nesController.FastForward ? 3 : 1;
                for (int i = 0; i < runs; i++) s.RunFrame();
                sessionFrames += runs;
                if (sessionFrames % 600 < runs) _ = SaveSessionBatteryAsync(force: false);

                // Stereo -> mono float at the session's own rate (the browser resamples).
                var mono = new List<float>(2048);
                int n;
                while ((n = s.ReadSamples(sessionAudio)) > 0)
                    for (int i = 0; i + 1 < n; i += 2) mono.Add((sessionAudio[i] + sessionAudio[i + 1]) * (0.5f / 32768f));

                int w = s.FrameWidth, h = s.FrameHeight;
                var src = s.Frame;
                var fb = nesController.sessionFrameBuffer;
                if (fb == null || fb.Length != w * h * 4) nesController.sessionFrameBuffer = fb = new byte[w * h * 4];
                for (int i = 0, o = 0; i < w * h; i++, o += 4)
                {
                    uint c = src[i];
                    fb[o] = (byte)(c >> 16); fb[o + 1] = (byte)(c >> 8); fb[o + 2] = (byte)c; fb[o + 3] = 255;
                }

                nesController.FrameCount++;
                if (nesController.FrameCount % nesController.StatsUpdateDivider == 0)
                {
                    var now = DateTime.Now;
                    if ((now - nesController.LastFpsUpdate).TotalSeconds >= 0.5)
                    {
                        nesController.Fps = (nesController.FrameCount - nesController.LastFrameCount) / (float)(now - nesController.LastFpsUpdate).TotalSeconds;
                        nesController.LastFrameCount = nesController.FrameCount;
                        nesController.LastFpsUpdate = now;
                    }
                    StateHasChanged();
                }
                return new FramePayload
                {
                    Framebuffer = fb, Audio = mono.Count > 0 ? mono.ToArray() : null, SampleRate = s.SampleRate,
                    Width = w, Height = h, DisplayWidth = s.DisplayWidth,
                };
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Console session frame failed");
                nesController.ErrorMessage = $"Runtime error: {ex.Message}";
                _ = PauseEmulation();
                StateHasChanged();
                return new FramePayload();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NesEmulator.Mix;
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
                // The Game Boy look.
                GbLook.Palette = GbLook.FromKey(await JS.InvokeAsync<string?>("nesInterop.idbGetItem", "pref_gblook"));
                GbLook.InvertBackground = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", "pref_gbinvbg") == "1";
                GbLook.InvertSprites = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", "pref_gbinvobj") == "1";
                // The core picks. A pick of a retired core moves to its successor and is stored back (CoreIdMigration: the big-screen Game Boy chip
                // NES:DMGX is the enhanced chip NES:DMGS now; the retired DMGXI / DMGSI also switch the sprite inversion on) - never a reset to the default.
                bool invertSprites = false;
                foreach (var k in Consoles.All.Where(k => k != ConsoleKind.Nes))
                    foreach (var slot in new[] { CoreSlot.Cpu, CoreSlot.Ppu, CoreSlot.Apu })
                    {
                        var v = await JS.InvokeAsync<string?>("nesInterop.idbGetItem", CoreKey(k, slot));
                        if (string.IsNullOrEmpty(v)) continue;
                        bool moved = false;
                        consoleCores[CoreKey(k, slot)] = CoreIdMigration.Migrate(k, slot, v, ref moved, ref invertSprites)!;
                        if (moved) await JS.InvokeVoidAsync("nesInterop.idbSetItem", CoreKey(k, slot), consoleCores[CoreKey(k, slot)]);
                    }
                {
                    bool moved = false;
                    Controller.PpuCoreSel = CoreIdMigration.Migrate(ConsoleKind.Nes, CoreSlot.Ppu, Controller.PpuCoreSel, ref moved, ref invertSprites)!;
                    if (moved) await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_ppuCore", Controller.PpuCoreSel);
                }
                if (invertSprites) { GbLook.InvertSprites = true; await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_gbinvobj", "1"); }
            }
            catch (Exception ex) { Logger.LogWarning(ex, "console prefs"); }
        }

        private static string CoreKey(ConsoleKind k, CoreSlot slot) => $"pref_core_{Consoles.Key(k)}_{slot.ToString().ToLowerInvariant()}";

        // ---- the Game Boy look (the same setting as the desktop's Console > Game Boy look)
        public GbPaletteChoice GbLookChoice => GbLook.Palette;
        public bool GbInvertBackground => GbLook.InvertBackground;
        public bool GbInvertSprites => GbLook.InvertSprites;

        /// <summary>A Game Boy picture is in play: a Game Boy game, or any other console's game (NES, SNES, Master System, Game Gear, Genesis) through a Game Boy
        /// picture chip, stock or enhanced - the look and its two inversions are shown whenever they apply.</summary>
        public bool ShowsGameBoyPicture => CoreCatalog.IsGameBoyPicture(ActiveConsole, ActiveConsole == ConsoleKind.Nes ? Controller.PpuCoreSel : ConsoleCore(ActiveConsole, CoreSlot.Ppu));

        /// <summary>Change the Game Boy look (null = unchanged). Going to or from Game Boy Color changes the Game Boy
        /// model, so a game using a Game Boy picture restarts; the rest applies live.</summary>
        public async Task SetGbLookAsync(GbPaletteChoice? look, bool? invertBackground, bool? invertSprites)
        {
            bool wasColor = GbLook.Color;
            if (look is GbPaletteChoice l) { GbLook.Palette = l; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_gblook", GbLook.Key(l)); } catch { } }
            if (invertBackground is bool b) { GbLook.InvertBackground = b; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_gbinvbg", b ? "1" : "0"); } catch { } }
            if (invertSprites is bool o) { GbLook.InvertSprites = o; try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", "pref_gbinvobj", o ? "1" : "0"); } catch { } }
            if (wasColor != GbLook.Color && ShowsGameBoyPicture)
            {
                if (session != null && sessionRom != null) await StartSessionAsync(session.Console, sessionRom, sessionRomName);
                else if (nes != null) ApplySelectedCores();   // rebuilds the Game Boy picture chip as a DMG or a GBC
            }
            StateHasChanged();
        }

        /// <summary>The SNES / Game Boy core picked for a slot (validated against the catalog).</summary>
        public string ConsoleCore(ConsoleKind console, CoreSlot slot) =>
            CoreCatalog.Resolve(console, slot, consoleCores.TryGetValue(CoreKey(console, slot), out var v) ? v : null);

        public IReadOnlyList<CoreOption> ConsoleCoreOptions(CoreSlot slot) => CoreCatalog.Options(ActiveConsole, slot);

        /// <summary>Pick a SNES / Game Boy core: hot-swapped into the running game (only a swap the session cannot do in
        /// place restarts it).</summary>
        public async Task SetConsoleCoreAsync(CoreSlot slot, string id)
        {
            var console = ActiveConsole;
            if (console == ConsoleKind.Nes) return;
            consoleCores[CoreKey(console, slot)] = id;
            try { await JS.InvokeVoidAsync("nesInterop.idbSetItem", CoreKey(console, slot), id); } catch { }
            if (session != null && sessionRom != null)
            {
                string label = $"{slot.ToString().ToUpperInvariant()} {id}";
                bool swapped; string why = "";
                swapped = session.TrySwapCore(slot, id);
                if (!swapped) why = session.SwapRefusal(slot, id);      // asked before the restart: it is the old session that knows
                if (swapped) Status.Set($"{Consoles.DisplayName(console)}: {label} swapped in, the game runs on");
                else
                {
                    // never silent: this session cannot swap that part under a running game, so the game starts again on it and the status says why
                    await StartSessionAsync(session.Console, sessionRom, sessionRomName);
                    Status.Set($"{Consoles.DisplayName(console)} cannot swap {label} while a game runs: the game was restarted on it ({why})");
                }
            }
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
                // the game itself decides where it can go, as on the desktop: a SNES game on the WideMess goes back to the SNES; a WideMess game cannot
                var romKind = RomDetect.Detect(sessionRom, sessionRomName) ?? session.Console;
                if (RomDetect.Runs(console, romKind)) { await StartSessionAsync(console, sessionRom, sessionRomName); return; }
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
            try { rom = RomDetect.Unwrap(data, romName, out inner); }
            catch (System.IO.InvalidDataException ex)
            {
                // a 7z / RAR archive, or a .zip with no ROM in it: the page says what it is instead of trying to run it as a NES ROM
                nesController.ErrorMessage = ex.Message; Status.Set(ex.Message); StateHasChanged();
                return true;
            }
            catch { return false; }
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
            // a SNES game runs on the WideMess when the WideMess is the selected console (it is a superset of the SNES: locked, it is a SNES), as on the desktop
            if (console == ConsoleKind.Snes && SelectedConsole == ConsoleKind.WideMess && Consoles.IsAvailable(ConsoleKind.WideMess)) console = ConsoleKind.WideMess;
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
                s = ConsoleSessions.Create(console, rom, ConsoleCore(console, CoreSlot.Cpu), ConsoleCore(console, CoreSlot.Ppu), ConsoleCore(console, CoreSlot.Apu), fileName: romName);
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
            nesController.CurrentRomName = romName; nesController.LastLoadedRomSize = rom.Length;
            nesController.ErrorMessage = "";
            await LoadSessionBatteryAsync();
            try { await JS.InvokeVoidAsync("nesInterop.setTouchLayout", Consoles.Key(console)); } catch { }
            try { await JS.InvokeVoidAsync("nesInterop.setSessionFps", s.FramesPerSecond); } catch { }   // a PAL game runs at 50, not the page's 60
            Status.Set($"{Consoles.DisplayName(console)}: {s.Title}");
            if (console == ConsoleKind.WideMess) await ApplySavedWideMessEnhancementsAsync();     // a SNES game on the WideMess: the enhancements kept for it, said on the status line (Emulator.WideMess.cs)
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
            SessionSpeedNote = ""; slowSeconds = 0;
            try { await JS.InvokeVoidAsync("nesInterop.setSessionFps", 0.0); } catch { }   // the NES paces at 60 again
            try { await JS.InvokeVoidAsync("nesInterop.setTouchLayout", Consoles.Key(ActiveConsole)); } catch { }
            if (wasRunning) await StartEmulation();
        }

        // ------------------------------------------------------------------ save states of a running session (the shared mechanism: IConsoleSession.SaveState / LoadState)
        // The page's Save / Load buttons reach these through SaveState() / LoadState() in StatePersistence.cs while a console session runs; the NES keeps its own path there. One slot per game, in IndexedDB.

        private async Task SaveSessionStateAsync()
        {
            var s = session;
            if (s == null) return;
            if (!s.SupportsState) { nesController.ErrorMessage = s.StateUnsupportedReason; Status.Set(s.StateUnsupportedReason); StateHasChanged(); return; }
            try
            {
                byte[] state = s.SaveState();   // a frame is one synchronous call in this page, so this is always between frames
                if (await JS.InvokeAsync<bool>("nesInterop.idbSetBytes", ConsoleStateFile.SlotKey(s.Console, s.GameId), state)) Status.Set($"State saved ({state.Length / 1024} KB)");
                else { nesController.ErrorMessage = "Save error: the browser refused to store the state."; Status.Set(nesController.ErrorMessage); }
            }
            catch (Exception ex) { nesController.ErrorMessage = "Save error: " + ex.Message; Status.Set(nesController.ErrorMessage); }
            StateHasChanged();
        }

        private async Task LoadSessionStateAsync()
        {
            var s = session;
            if (s == null) return;
            if (!s.SupportsState) { nesController.ErrorMessage = s.StateUnsupportedReason; Status.Set(s.StateUnsupportedReason); StateHasChanged(); return; }
            try
            {
                var data = await JS.InvokeAsync<byte[]?>("nesInterop.idbGetBytes", ConsoleStateFile.SlotKey(s.Console, s.GameId));
                if (data == null || data.Length == 0) { Status.Set("No saved state for this game"); StateHasChanged(); return; }
                s.LoadState(data);   // refuses a state of another game / sound unit with the reason; the game keeps running as it was
                BugBreak("a state was loaded");
                try { await JS.InvokeVoidAsync("nesInterop.resetAudioTimeline"); } catch { }
                Status.Set("State loaded");
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or NotSupportedException) { nesController.ErrorMessage = "Load error: " + ex.Message; Status.Set(nesController.ErrorMessage); }
            catch (Exception ex) { nesController.ErrorMessage = "Load error: " + ex.Message; Status.Set(nesController.ErrorMessage); }
            StateHasChanged();
        }

        // ------------------------------------------------------------------ battery saves (IndexedDB kv)
        private string SessionSaveKey(IConsoleSession s) => s.Console switch
        {
            // the WideMess as on the desktop: a SNES game on it keeps its SNES save (its id is sfc<sha1>, the same key), a WideMess game has its own (wms<sha1>)
            ConsoleKind.Snes or ConsoleKind.WideMess => $"battery_v1:{s.GameId}.srm",
            ConsoleKind.MasterSystem => $"battery_v1:sms{s.GameId}.sav",
            ConsoleKind.GameGear => $"battery_v1:gg{s.GameId}.sav",
            ConsoleKind.Genesis => $"battery_v1:md{s.GameId}.srm",
            _ => $"battery_v1:gb{s.GameId}.sav",
        };

        private async Task LoadSessionBatteryAsync()
        {
            var s = session;
            // as on the desktop: a Genesis cartridge with no SRAM header reports a battery only after its game has written to it, and a Master System / Game Gear game whose RAM mapping the ROM scan
            // missed still gets its save back, so a save made on an earlier run is looked up regardless
            if (s == null || (!s.HasBattery && s.Console is not (ConsoleKind.MasterSystem or ConsoleKind.GameGear or ConsoleKind.Genesis))) return;
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

        // ---- where a session frame's time goes (read by the speed suite through GetSessionProfile; the cost of the bookkeeping is a few timestamps a frame)
        private long profRun, profAudio, profPix, profBody, profFrames;

        /// <summary>
        /// How a session frame crosses to the page. 1 (default): the audio goes as raw float bytes (the interop's byte-array path) and the pixels are converted a whole word at a time. 0: the original path, a JSON
        /// array of floats and four byte writes a pixel, kept as the twin the speed suite measures against (the two produce the same picture bytes and the same samples).
        /// </summary>
        public static int PayloadMode = 1;

        private float[] monoScratch = new float[4096];
        private readonly Dictionary<int, byte[]> pcmBuffers = new();

        /// <summary>The frame cost split, in milliseconds since the last reset: [frames, RunFrame, audio read and mix-down, pixel conversion, the whole frame body].</summary>
        [JSInvokable]
        public double[] GetSessionProfile()
        {
            double ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return new[] { (double)profFrames, ms(profRun), ms(profAudio), ms(profPix), ms(profBody) };
        }

        [JSInvokable]
        public void ResetSessionProfile() { profRun = profAudio = profPix = profBody = profFrames = 0; }

        [JSInvokable]
        public int SetPayloadMode(int mode) { PayloadMode = mode == 0 ? 0 : 1; return PayloadMode; }

        /// <summary>
        /// The speed suite's twins, switchable from the page so a browser measurement can alternate an optimised path and its reference inside one build (the answer is the same either way; see docs/projects/sega-track-c4a.md):
        /// <c>md-loop</c> (the Genesis sound Z80's polling loops counted, not ticked), <c>idle-frame</c> (the picture bridges' NES run as one picture from the blank),
        /// <c>payload</c> (the page's frame crossing). Returns what each is now, e.g. "md-loop=1 idle-frame=1 payload=1".
        /// </summary>
        [JSInvokable]
        public string SetTwin(string name, bool on)
        {
            switch (name)
            {
                case "md-loop": NesEmulator.Sega.MdSoundUnit.FastLoops = on; break;
                case "idle-frame": NesEmulator.Mix.NesPictureSink.IdleMode = on ? 2 : 0; break;
                case "payload": PayloadMode = on ? 1 : 0; break;
                case "vdp-run": NesEmulator.Sega.MdVdp.FastRun = on; break;
                case "vdp-draw": NesEmulator.Sega.MdVdp.FastDraw = on; break;
                case "vdp-swar": NesEmulator.Sega.MdVdp.FastSwar = on; break;
                case "vdp": NesEmulator.Sega.MdVdp.FastRun = NesEmulator.Sega.MdVdp.FastDraw = NesEmulator.Sega.MdVdp.FastSwar = on; break;
            }
            return $"md-loop={(NesEmulator.Sega.MdSoundUnit.FastLoops ? 1 : 0)} idle-frame={(NesEmulator.Mix.NesPictureSink.IdleMode == 2 ? 1 : 0)} payload={PayloadMode} vdp-run={(NesEmulator.Sega.MdVdp.FastRun ? 1 : 0)} vdp-draw={(NesEmulator.Sega.MdVdp.FastDraw ? 1 : 0)} vdp-swar={(NesEmulator.Sega.MdVdp.FastSwar ? 1 : 0)}";
        }

        // ---- a speed note, never a silent drop: when this device cannot keep the console at real time, the page says so (with what it manages) instead of just running slow
        private double slowSeconds;
        /// <summary>Empty while the game runs at its real frame rate; otherwise a sentence for the status badge ("Running at 41 of 60 frames a second on this device").</summary>
        public string SessionSpeedNote { get; private set; } = "";

        private void UpdateSpeedNote(double fps, double target, double seconds)
        {
            // the page's loop is throttled in a hidden tab (a few frames a second): that is not the device being slow, and the badge would only be noise
            if (nesController.IsRunning && fps < target * 0.9 && fps >= 1.0 && !PageHidden) slowSeconds += seconds; else slowSeconds = 0;
            SessionSpeedNote = slowSeconds >= 3 ? $"Slow: {fps:F0} of {target:F0} frames a second on this device" : "";
        }

        /// <summary>True while the browser reports the page hidden (set from the page's visibility hook).</summary>
        public bool PageHidden { get; set; }

        private FramePayload RunSessionFrame(IConsoleSession s)
        {
            try
            {
                long tBody = System.Diagnostics.Stopwatch.GetTimestamp();
                var bp1 = PadFrom(inputState, extraP1); var bp2 = PadFrom(inputStateP2, extraP2);
                s.SetPad(0, bp1);
                s.SetPad(1, bp2);
                int runs = nesController.FastForward ? 3 : 1;
                for (int i = 0; i < runs; i++) { BugBeforeSessionFrame(s, bp1, bp2); s.RunFrame(); }   // the bug capture (D6) first: it returns at once when off
                sessionFrames += runs;
                if (sessionFrames % 600 < runs) _ = SaveSessionBatteryAsync(force: false);
                long tRun = System.Diagnostics.Stopwatch.GetTimestamp();

                // Stereo -> mono float at the session's own rate (the browser resamples).
                float[]? monoArray = null; byte[]? pcm = null; int monoCount = 0;
                int n;
                if (PayloadMode == 0)
                {
                    var mono = new List<float>(2048);
                    while ((n = s.ReadSamples(sessionAudio)) > 0)
                        for (int i = 0; i + 1 < n; i += 2) mono.Add((sessionAudio[i] + sessionAudio[i + 1]) * (0.5f / 32768f));
                    monoCount = mono.Count;
                    if (monoCount > 0) monoArray = mono.ToArray();
                    BugAudio(monoArray, monoCount);
                }
                else
                {
                    while ((n = s.ReadSamples(sessionAudio)) > 0)
                    {
                        int add = n / 2;
                        if (monoCount + add > monoScratch.Length) Array.Resize(ref monoScratch, Math.Max(monoScratch.Length * 2, monoCount + add));
                        var sc = monoScratch;
                        for (int i = 0, o = monoCount; i + 1 < n; i += 2, o++) sc[o] = (sessionAudio[i] + sessionAudio[i + 1]) * (0.5f / 32768f);
                        monoCount += add;
                    }
                    BugAudio(monoScratch, monoCount);
                    if (monoCount > 0)
                    {
                        // one exact-size buffer per sample count (a frame is 734 to 736 samples, a fast-forward three of them): the interop serialises the whole array
                        if (!pcmBuffers.TryGetValue(monoCount, out pcm)) pcmBuffers[monoCount] = pcm = new byte[monoCount * 4];
                        Buffer.BlockCopy(monoScratch, 0, pcm, 0, monoCount * 4);
                    }
                }
                long tAudio = System.Diagnostics.Stopwatch.GetTimestamp();

                int w = s.FrameWidth, h = s.FrameHeight;
                var src = s.Frame;
                var fb = nesController.sessionFrameBuffer;
                if (fb == null || fb.Length != w * h * 4) nesController.sessionFrameBuffer = fb = new byte[w * h * 4];
                if (PayloadMode == 0)
                {
                    for (int i = 0, o = 0; i < w * h; i++, o += 4)
                    {
                        uint c = src[i];
                        fb[o] = (byte)(c >> 16); fb[o + 1] = (byte)(c >> 8); fb[o + 2] = (byte)c; fb[o + 3] = 255;
                    }
                }
                else
                {
                    // 0xAARRGGBB -> the bytes R, G, B, 255 in memory order, one 32-bit write a pixel
                    var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(fb.AsSpan());
                    int count = w * h;
                    for (int i = 0; i < count; i++)
                    {
                        uint c = src[i];
                        dst[i] = 0xFF000000u | ((c & 0xFFu) << 16) | (c & 0x0000FF00u) | ((c >> 16) & 0xFFu);
                    }
                }
                long tPix = System.Diagnostics.Stopwatch.GetTimestamp();
                profRun += tRun - tBody; profAudio += tAudio - tRun; profPix += tPix - tAudio; profBody += tPix - tBody; profFrames++;

                nesController.FrameCount++;
                if (nesController.FrameCount % nesController.StatsUpdateDivider == 0)
                {
                    var now = DateTime.Now;
                    if ((now - nesController.LastFpsUpdate).TotalSeconds >= 0.5)
                    {
                        double span = (now - nesController.LastFpsUpdate).TotalSeconds;
                        nesController.Fps = (nesController.FrameCount - nesController.LastFrameCount) / (float)span;
                        nesController.LastFrameCount = nesController.FrameCount;
                        nesController.LastFpsUpdate = now;
                        int ff = nesController.FastForward ? 3 : 1;   // one call of this method runs 3 console frames while fast-forwarding: the note counts console frames, the target is the console's rate times 3
                        UpdateSpeedNote(nesController.Fps * ff, s.FramesPerSecond * ff, span);
                    }
                    StateHasChanged();
                }
                return new FramePayload
                {
                    Framebuffer = fb, Audio = monoArray, AudioBytes = pcm, SampleRate = s.SampleRate,
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

# BrokenNes cross-platform survey (Windows dependencies)

Date: 2026-11-20. Read-only survey of `main` (ae30742b) in PROJECTS\BrokenNes. No code changed, nothing built.
Method: `git grep` counts over tracked `*.cs`/`*.csproj`. Line counts are `wc -l` of whole matching files, so they over-state the Windows-specific part. Items marked **(guess)** were not verified by reading the code.

Target stack under evaluation (not decided): Avalonia (app UI), SDL3 or Silk.NET (video/audio/input), CEF off-screen (in-game web overlays), native web views (WebView2 / WKWebView / WebKitGTK) for full panels.

Size of the desktop project: `Windows/` = 742 tracked .cs files, ~230k lines. Most of that is the emulator cores, which are portable C#.

---

## 1-2. Windows-specific dependencies by group

| # | Group | Files (lines incl. non-Win code) | Key files | Maps to | Effort | Risk |
|---|---|---|---|---|---|---|
| A | WinForms UI | 78 files / ~23k (37 MainForm/, 13 DebugWindows/, 5 Workshop, 5 webapi, 4 Synth) | `Windows/MainForm/*.cs` (42 partials, 12.8k), `DebugWindows/`, `SpeedControlForm.cs`, `ControllerConfigWindow.cs`, `Synth/SynthForm.cs`, `Synth/InstallVstDialog.cs`; 19 files declare Form/UserControl/Control classes | Avalonia Windows/UserControls; menus -> Avalonia `Menu`/`NativeMenu`; file dialogs -> `StorageProvider` | XL | Medium: mostly mechanical, but MainForm mixes UI and session logic (section 3) |
| B | WebView2 | 23 files / ~9.5k | see table B | CEF OSR for overlays; native web view for panels and pop-outs | L | High: overlay-over-game compositing is the hardest piece on every OS |
| C | Video: Direct3D 11 + Direct2D via SharpDX 4.2 (unmaintained) | 47 files / ~10.6k (28 in Rendering/Backgrounds) | `Rendering/NesDirectXRenderer.cs` (swap chain, D2D FPS overlay, `Application.DoEvents` inside Present), `NesShaderManager.cs`, `HlslMetadataParser.cs`, `Shaders/*.hlsl` (26 files), `Rendering/Backgrounds/*` | SDL3 GPU API or Silk.NET (Vulkan/Metal/D3D). HLSL -> SPIR-V/MSL via SDL_shadercross / SPIRV-Cross, or rewrite in GLSL | L | Medium-High: 26 shaders + metadata parser must look identical |
| D | Audio: NAudio (WasapiOut, WaveOutEvent x10, CoreAudioApi, NAudio.Midi), one `Mmdevapi.dll` import | 9 files / ~3.7k | `AudioEngine.cs` (442), `AudioManager.cs` (711), `Synth/SynthAudioOutput.cs`, `Synth/SynthMidiInput.cs`, 2 APU files | SDL3 audio streams (or miniaudio). MIDI in: RtMidi / portmidi binding (SDL has no MIDI) | M | Low-Medium; latency re-tuned per OS. MIDI is the gap |
| E | Input | `InputManager.cs` (339), `MainForm.Input.cs`, `MainForm.X86Keyboard.cs` + `X86Mouse.cs` (~190), `ControllerConfigWindow.cs`, `PlayerControllerConfig.cs`; SharpDX.XInput; `GetAsyncKeyState`, `MapVirtualKey`, `GetMessageExtraInfo`. Plugin calls `xinput1_4.dll` directly | SDL3 gamepad + scancodes; x86 PS/2 table re-keyed from VK codes to SDL scancodes; relative mouse via SDL | M | Low-Medium |
| F | P/Invoke / Win32 | 33 files / ~13k. user32 (64 imports), gdi32 (28), kernel32 (35), shell32 (10), advapi32 (7), winmm (7, `timeBeginPeriod`), ole32, comdlg32, comctl32, msimg32, ntdll | In Windows/: AllocConsole/FreeConsole/GetConsoleWindow, job objects (CreateJobObject/AssignProcessToJobObject/TerminateJobObject: child-process lifetime), ExtractIconEx/DestroyIcon, SendMessage, ShowWindow, GetClientRect, GetAncestor, SetProcessInformation, `TimerResolution.cs` | Console: no-op on Unix. Job objects -> process groups + prctl(PR_SET_PDEATHSIG) on Linux, kqueue NOTE_EXIT on mac. Timer resolution: not needed on Unix (guess) | M | Low per site, many sites |
| G | Executable memory (cor64 x64 emitter) | `third_party/cor64/src/cor64/Mips/X64/ExecMem.cs` (86), `X64Asm.cs` (201) | CreateFileMappingW/MapViewOfFile/FlushInstructionCache. `Supported => OperatingSystem.IsWindows() && X64`, so other platforms already fall back | mmap+mprotect (Linux), `MAP_JIT` + `pthread_jit_write_protect_np` (mac). ARM64 needs a new emitter or stays on the IL JIT | S (Linux, mac Intel); XL (ARM64 emitter) | Low (fallback exists); ARM speed unknown |
| H | Reflection.Emit JITs | 18 files / ~10.9k: cor64 JitIL/Rsp/Analysis/JitCommon, `Windows/NesEmulator/x86/I386.Jit.cs` | DynamicMethod/ILGenerator | Portable as is on every OS/arch. Not usable under NativeAOT | S | Low |
| I | cor64 native graphics | DllImports of `cor64-GLideN64.so` (30), `rsp.so` (41), `cor64_prdp` (14, parallel-rdp on Vulkan), `SDL2` (6); RunN64 uses Veldrid/OpenTK/glfw/ImGui | Library names are already Unix-style (guess: Windows builds rely on name probing to load .dll). Mac: parallel-rdp needs MoltenVK; GLideN64 needs GL (mac max 4.1) | M-L | High on mac: never tried |
| J | Files / paths / instances | 12 files use LocalApplicationData / %LOCALAPPDATA% (MainForm x2, Helpers, Diagnostics, webapi, debug capture, BrokenDev/mcp x4, Workshop/Bugs) | | `SpecialFolder.LocalApplicationData` maps to `~/.local/share` (Linux) and `~/Library/Application Support` (mac) in .NET 8+. Audit hard-coded `\` separators and drive letters (T:, P:, C:\BrokenNes-tools) in tools | S-M | Low |
| K | HTTP web API | `Windows/webapi/*.cs` (37 files, ~11.4k), `Virtual/VirtualCartServer.cs`, `DebugPagesLanShare.cs` | ASP.NET Core Kestrel (`WebApplication.CreateBuilder`, HTTPS on loopback) | Already portable. Coupling is to MainForm (UI-thread marshalling, guess) and HTTPS dev cert setup per OS | S-M | Low |
| L | System.Drawing / GDI+ | 85 files / ~28k (26 MainForm, 13 DebugWindows, ~25 Workshop incl. Sega/MixLab/Gb) + gdi32/msimg32 imports in the plugin UI | Bitmap/Graphics for screenshots, debug viewers, PNG output | Windows-only since .NET 7. SkiaSharp (ships with Avalonia) or ImageSharp for headless PNG | L | Medium: spread thin over many files incl. Workshop |
| M | Clipboard / shell / processes | `MainForm.LanShare.cs` (clipboard); shell32; 26 files start processes (`UseShellExecute`, explorer.exe, cmd/powershell), mostly Workshop/Sega, FruityHost, Workshop/X86, BrokenDev/mcp | Avalonia `IClipboard`, `Launcher`; `xdg-open` / `open` | S | Low |
| N | Registry | Only `Plugin/BrokenNes.FruityHost/SelfTest.Prefs.cs` uses Microsoft.Win32 (other "Registry" hits are the emulator's own CoreRegistry etc.); advapi32 imports in plugin | Not needed off Windows | S | Low |
| O | Installer / VST / FL Studio | `Windows/Synth/` (FlStudioInstall, InstallVstDialog/Flow, PluginInstaller, SelfLaunch, SynthForm, SynthMode, SynthAudioOutput, SynthMidiInput), `Program.cs` (`--install-vst`, `--synth`), `Plugin/BrokenNes2.Plugin` (PublishAot, RID win-x64, Win32/GDI UI), `Plugin/BrokenNes.Fruity*` | FL Studio exists on mac (VST3/AU), not on Linux. Needs NativeAOT osx-arm64/x64, mac plugin bundle, Cocoa/NSView UI. `--synth` standalone -> SDL3 audio + RtMidi | XL | High: mac plugin GUI is its own project |
| P | x86-PC tooling | Emulated PC hardware (`Windows/NesEmulator/x86/`: Fdc82077, DiskImage, FatImage, BIOS) is pure C#. Floppy writing / disc ripping are PowerShell scripts (writefloppy.ps1); no `\\.\PhysicalDrive` or DeviceIoControl in C# | Leave Windows-only, or `dd`-based scripts later | S | Low |
| Q | Workshop / MCP | `Workshop`: net10.0-windows + WinForms by default, `WorkshopCli=true` gives net10.0. `BrokenDev/mcp/BrokenNes.Mcp.csproj`: net10.0-windows + UseWindowsForms | Drop WinForms from MCP (guess: it links Windows/ sources that pull WinForms in) | M | Medium |

### Table B: WebView2 uses

| File | Lines | WebView2 refs | Classification |
|---|---|---|---|
| `Windows/Helpers/WebViewHelper.cs` | 525 | 64 | Infrastructure: creation, `SetVirtualHostNameToFolderMapping` (serves `Webmodules/`), `ExecuteScriptAsync`, `NavigateToString`. JS<->C# bridge |
| `MainForm.Initialization.cs` | 987 | 26 | Creates the main web view over the game area and its message hookup: JS<->C# bridge + host for in-game overlay (guess: transparent web view above the D3D panel) |
| `MainForm.ViewModes.cs` | 753 | 19 | Switches between game and web modules (Home, Options, RomManager, DeckBuilder, Cores, Continue...): full panel. Card/overlay scripts (`clearCard` etc.): in-game overlay |
| `MainForm.ButcherPopout.cs` | 185 | 14 | Own window with its own WebView2 for ButcheringMode: pop-out window + script polling (bridge) |
| `Virtual/VirtualCartSession.cs` | 351 | 18 | `new WebView2 { Dock = Fill }` hosting a virtual cart page: pop-out window (guess: own Form), bridge via ExecuteScriptAsync |
| `MainForm.cs`, `Fields`, `RomLoading`, `Audio`, `Config`, `Continue`, `Cores`, `Display`, `Emulation`, `Input`, `Speed`, `UI`, `WideMessVirtual` | - | 1-8 each | Push state into the page: bridge |
| `WebModuleInfo.cs`, `webapi/WebApiServer.cs`, `Endpoints.Navigation.cs`, `Endpoints.Emulator.cs` | - | 1-5 | API drives the web view's navigation: bridge (test automation depends on it) |
| `Workshop/WorkshopForm.cs` | - | 1 | Workshop GUI only |

Web modules (`Windows/Webmodules/`): AchievementsRuntime, ButcheringMode, Continue, Cores, CorruptionSlop, DeckBuilder(+Crud), GlitchHarvester, HexEditor, Home, ImagineBug, Options, Overlay, RomManager, Story, TimeJump, test pages, shared. Plain HTML/JS, portable. The VST-style controller library (docs/projects/controller-standard-plan.md; Slop faceplate, Butchering Build view) is web-side (guess, from notes), so it ports with the web views. The FL plugin's own UI is Win32 GDI and does not.

Suggested split: Overlay / CorruptionSlop / achievement pop-ups drawn over the game -> CEF off-screen into a texture the renderer composites. Home/Options/RomManager/DeckBuilder/Cores etc. -> native web view as a full panel. Butcher popout and VirtualCart -> native web view in its own Avalonia window. One thin bridge interface (`PostMessage`, `ExecuteScript`, `OnMessage`, virtual-host/custom-scheme folder mapping) hides WebView2/WKWebView/WebKitGTK/CEF.

---

## 3. Core vs UI separation today

From csproj TargetFramework/UseWindowsForms (nothing built):

- Portable (net10.0): `Core/BrokenNes.Core` (links NES board files from Windows/NesEmulator), `Plugin/BrokenNes.Fruity`, `FruityHost`, `SynthHost`, `BrokenNes2.Plugin` (but PublishAot + RID win-x64 + Win32 imports), `Web`, `WebLite` (Blazor), cor64 + RunN64 + SessionProbe + BareMetal, all Nano parity tools, `tools/apu-bench`, `butcher-fuzz/replay/scenarios`, `LiteStaticServer`, `SnesWasmBench`. `SubProjects/65-o-borked` and `OfflineRA` are net8.0.
- Windows-only: `Windows/BrokenNes.Windows` (WinExe, net10.0-windows, WinForms, RID win-x64, SharpDX), `Workshop` (default build), `BrokenDev/mcp/BrokenNes.Mcp`.
- The cores physically live in `Windows/NesEmulator/` and are linked into several projects with `<Compile Include>`. Lite, the Workshop CLI and the butcher tools show that most cores already compile without WinForms.

Move out of MainForm into a portable session layer (new net10.0 library, e.g. `BrokenNes.Session`):
- `MainForm.Emulation`, `Speed`, `Consoles`, `Cores`, `RomLoading`, `RomCompatibility`, `SaveStates`, `SessionStates`, `BatteryRam`, `Continue`, `Progression`, `Corruption`, `Timeflow*`, `AutoTas`, `Extract`, `Synth` (logic), `WideMess*`, `ConsoleFarm`, `DebugPort`, `BugCapture`, `Config`: frame loop, core switching, state I/O, progression gates.
- Also `TimeJumpManager.cs`, `EmulatorConfig.cs`, `ImagineEngine.cs`, and `webapi/*` (the API should talk to the session, not the Form).
- Host interfaces: `IVideoSink` (frame + shader), `IAudioSink`, `IInputSource`, `IWebSurface` (bridge above), `IDialogs`, `IClock`. WinForms is the first implementation, so the current app keeps working during the port.
- Guess: 60-70% of MainForm's 12.8k lines is session logic.

---

## 4. Mac and Linux issues

macOS
- Apple Silicon: .NET 10 runs natively on osx-arm64. All managed cores and both Reflection.Emit JITs (cor64 JitIL/RSP, I386.Jit) work there. cor64's x64 emitter is x64-only and already gated off; on ARM64 it falls back to the IL JIT. An ARM64 emitter is new work (XL). Under Rosetta 2 an osx-x64 build could use the x64 emitter (guess: slower overall than native arm64).
- W^X: the x64 emitter on mac needs `mmap(MAP_JIT)` + `pthread_jit_write_protect_np` and the `com.apple.security.cs.allow-jit` entitlement under the hardened runtime (the .NET runtime needs that entitlement anyway).
- parallel-rdp (Vulkan) needs MoltenVK; GLideN64 needs OpenGL, deprecated on mac (max 4.1). Risk that one or both do not work; softrdp stays as fallback.
- Signing/notarization: Developer ID certificate (paid Apple account), hardened runtime, entitlements (JIT; microphone only if MIDI/audio input asks), notarize the .app/.dmg. Every bundled native lib (SDL3, CEF ~200 MB, MoltenVK, cor64 natives) must be signed. CEF on mac needs helper app bundles, a known pain point.
- FL Studio exists on mac: a VST3/AU plugin needs mac NativeAOT + a Cocoa UI.

Linux
- Wayland vs X11: SDL3 and Avalonia support both (guess: X11/XWayland is the safer default today). Embedding a native web view as a child of an Avalonia window is simple on X11, harder on Wayland.
- WebKitGTK: version varies by distro; no WebView2-style virtual host mapping (use a custom URI scheme); WebGL/WebAudio/gamepad support can lag; JS bridge via `window.webkit.messageHandlers`. CEF avoids the differences at the cost of size.
- Audio: SDL3 picks PipeWire/PulseAudio/ALSA. MIDI: ALSA sequencer via RtMidi.
- No FL Studio on Linux: VST install is Windows + mac only.

---

## 5. Phased plan

1. **P0 Portable session layer (Windows, no visible change).** Extract `BrokenNes.Session` from MainForm + webapi, define host interfaces, WinForms implements them. Replace System.Drawing in session/API/headless paths with SkiaSharp or ImageSharp. Milestone: golden-hash battery and HTTP API tests unchanged; Workshop CLI and MCP build as plain net10.0. Effort L.
2. **P1 Avalonia app on Windows at parity.** Avalonia shell, SDL3/Silk.NET renderer (shaders ported off HLSL/SharpDX), SDL3 audio + input, web bridge over WebView2 (panels) and CEF OSR (overlays). Keep the WinForms app until parity. Milestone: every web module, debug window, pop-out, x86 keyboard/mouse and API endpoint works; frame hashes match side by side. Effort XL.
3. **P2 Linux x64.** WebKitGTK or CEF panels, PipeWire audio, packaging (AppImage or Flatpak, guess). cor64 ExecMem via mmap so the x64 emitter runs. Milestone: battery green on Linux, one game per console playable. Effort M.
4. **P3 Mac Intel.** WKWebView, MAP_JIT, MoltenVK for parallel-rdp, signing + notarization pipeline. Effort M-L.
5. **P4 Mac ARM64.** Native arm64 build, cor64 on the IL JIT (measure), decide on an ARM64 emitter; VST3/AU plugin as a separate follow-up. Effort L (XL with ARM emitter or plugin).

Biggest unknowns
- Web overlay over the game texture per OS: CEF OSR speed and input routing. Prototype this first.
- How much MainForm and webapi rely on `Control.Invoke` / UI-thread assumptions (not measured).
- Porting 26 HLSL shaders + background effects without changing their look.
- cor64 N64 speed on ARM64 without the x64 emitter; parallel-rdp on MoltenVK; GLideN64 on mac GL 4.1.
- CEF bundle size and mac helper-app signing.
- FL plugin on mac (UI + NativeAOT + AU/VST3), and whether it is wanted.
- Test hardware: no Mac or Linux machine is known in the fleet notes (guess); CI runners needed.

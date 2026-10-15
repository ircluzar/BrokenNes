# BrokenNes Web API

The local HTTP control API hosted by `BrokenNes.Windows.exe`. It exists so webmodules (the
WebView2-hosted front end) can drive the emulator, but it is a general-purpose control surface:
anything that can make an HTTP request on loopback can load ROMs, swap cores, read the
framebuffer, peek/poke memory and fire corruption blasts, with no UI automation involved.

## Server details

- **Ports**: 42067 (HTTP) / 42068 (HTTPS) when available. If those are already taken - normally
  because another BrokenNes instance is running - the server falls back to OS-assigned ephemeral
  ports for **both**, so every instance gets its own working API. A lone instance therefore
  behaves exactly as it always did.
- **Address**: 127.0.0.1 (loopback only)
- **Protocol**: HTTP, plus HTTPS with a self-signed cert generated at startup (for WebView2)
- **Framework**: ASP.NET Core Minimal API, logging suppressed below Warning
- **CORS**: `AllowAnyOrigin/Method/Header` - safe only because the listener is loopback-bound
- Started from `MainForm.EnsureWebApiServerRunningAsync()`; stopped/disposed on shutdown.

### Discovering an instance's port

Each running instance writes a discovery file once it is genuinely listening:

```
%LOCALAPPDATA%\BrokenNes\instances\<pid>.json
```

```json
{ "pid": 1234, "httpPort": 42067, "httpsPort": 42068, "startedUtc": "2026-09-02T12:34:56.7890123Z", "exePath": "C:\\...\\BrokenNes.Windows.exe" }
```

The file is deleted on graceful shutdown, and `WebApiServer` prunes provably-dead PIDs' files on
startup. Readers must still tolerate stale files: a force-killed process leaves its file behind,
so check that the PID is alive before trusting one.

`WebApiServer` also exposes `HttpPort` / `HttpsPort` / `BaseUrl` / `UsingFallbackPorts` /
`LastStartError` in-process, and the process-wide `WebApiEndpoint.BaseUrl` mirrors them.

Inside the desktop shell, page scripts do not need the discovery file: the shell injects
`window.BROKENNES_API_BASE` (e.g. `"http://127.0.0.1:42067"`) via
`AddScriptToExecuteOnDocumentCreatedAsync` before any page script runs, and `shared/webapi.js`
uses it automatically, falling back to `http://127.0.0.1:42067`.

**Startup caveat:** initialization shows blocking `MessageBox`es (DirectX failure, "Audio
Warning") *before* the API server is started. On a machine where audio init fails, nothing binds
and no discovery file appears until that dialog is dismissed.

## Conventions

- Responses are JSON. Most carry `success: true/false`; failures return either **400** with
  `{ "success": false, "error": "..." }` or **200** with `{ "success": false, ... }` depending on
  whether the handler treats it as a bad request or a failed operation. **Check `success`, not
  just the status code.**
- Request bodies are `application/json` and bind to the DTOs in `WebApiModels.cs`. Property names
  are matched case-insensitively.
- Handlers that touch UI state marshal onto the WinForms UI thread via `Control.Invoke`.
- **Encoding gotcha:** `/api/ppu/framebuffer` returns `data` as a **base64 string** (it is a C#
  `byte[]`, which `System.Text.Json` base64-encodes), while `/api/memory/peek-range` deliberately
  converts to `int[]` first and returns a **JSON array of numbers**. Same server, two encodings.

### Progression gate

`WebApiServer.ProgressionGate.cs` is middleware in front of everything. It answers **403**
`{ "success": false, "error": "<feature> is locked" }` for:

| Path prefix | Requires webmodule unlock |
| --- | --- |
| `/api/rtc/...` | `GlitchHarvester` |
| `/api/gh/...` | `GlitchHarvester` |
| `/api/timejump/...` | `TimeJump` |
| `/api/imagine/...` | `ImagineBug` |

The gate matches on the **path**, not on which file registered the route - so `/api/ppu/oam`,
`/api/apu/channels` and `POST /api/cpu/registers`, which are registered inside
`WebApiServer.Endpoints.Rtc.cs`, are **not** gated.

### Destructive endpoints

- `POST /api/save/reset` resets progression to a fresh canonical save and clears continue
  artifacts. Do not call it casually against a real save.
- `POST /api/progression/unlock-everything` grants everything.
- `DELETE /api/gh/stash`, `DELETE /api/gh/stockpile/{id}` delete user data.
- `POST /api/memory/poke*`, `/api/rtc/*` and `/api/imagine/*` mutate live emulator state by design.

## Endpoint index

About 160 routes across 20 registration files (the tables below were generated for the first 148; the Console, Emulation volume and Input injection tables were added for BrokenNes 2). Generated from the `app.MapX("...")` registrations in
`WebApiServer.Endpoints.*.cs`; each file's `Register*Endpoints(app)` is called from
`WebApiServer.BuildApp`.

### Health & memory - `WebApiServer.Endpoints.Memory.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/health` | Basic health check (`{ success, status, timestamp }`); the only endpoint that works before a NES exists |
| GET | `/api/memory/domains` | List memory domains with name/size/description |
| GET | `/api/memory/domain/{domainName}/size` | Size of one domain |
| GET | `/api/memory/peek?domain=&address=` | Read a single byte |
| POST | `/api/memory/poke` | Write a single byte; echoes `beforeValue`/`afterValue`/`verified` |
| GET | `/api/memory/peek-range?domain=&address=&length=` | Read N bytes as `int[]` |
| POST | `/api/memory/poke-range` | Write N bytes (`Data` is `int[]`) |
| GET | `/api/memory/test-poke?domain=&address=&value=` | Diagnostic write/read-back/restore round trip |

Memory domains (from `NesMemoryExtensions`): `System RAM` (2 KB), `CPU Bus` (64 KB), `PRG ROM`,
`PRG RAM` (8 KB), `CHR` (8 KB). Names contain spaces and must be URL-encoded in query strings.

### Emulator control - `WebApiServer.Endpoints.Emulator.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| POST | `/api/emulator/pause` | Pause emulation |
| POST | `/api/emulator/resume` | Resume emulation |
| POST | `/api/emulator/load-rom` | **Load a ROM by absolute disk path** (`{ Path }`) - no file dialog |
| POST | `/api/emulator/load-rom-key` | Load a ROM by browser-storage key / built-in name (`{ RomKey }`) |
| POST | `/api/emulator/load-rom-base64` | Load ROM bytes inline (`{ Name, Base64 }`) |
| POST | `/api/emulator/load-builtin-rom` | Load a bundled ROM by filename (`{ Filename, PreserveShader }`) |
| POST | `/api/emulator/close-rom` | Unload and fall back to the embedded test ROM |
| GET | `/api/emulator/current-rom` | `{ success, path, name, isTestRom }` |
| POST | `/api/emulator/quick-save-state` | Quick save (F7); `{ success, gated }` |
| POST | `/api/emulator/quick-load-state` | Quick load (F5); `{ success, gated }` |
| POST | `/api/emulator/save-continue-state` | Capture the persistent continue checkpoint |
| POST | `/api/emulator/load-continue-state` | Restore it (`{ ExpectedRomName }`, rejects a ROM mismatch) |
| GET | `/api/emulator/backgrounds` | List available backgrounds |
| POST | `/api/emulator/background` | Set the background (`{ Name }`; refuses locked ones) |
| GET | `/api/emulator/null-providers` | List available null/static providers |
| POST | `/api/emulator/null-provider` | Set the null provider (`{ Name }`; refuses locked ones) |

### CPU - `WebApiServer.Endpoints.Cpu.cs` (plus one route in `...Rtc.cs`)

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/cpu/registers` | Registers as **hex strings** (`{ PC:"0xC16C", A, X, Y, P, SP }`) |
| POST | `/api/cpu/registers` | Set registers (`SetRegistersRequest`; all fields optional) - registered in `...Rtc.cs`, not gated |
| GET | `/api/cpu/core` | Active CPU core id |
| GET | `/api/cpu/cores` | Available CPU core ids |
| GET | `/api/cpu/state` | Full CPU state snapshot |

### PPU - `WebApiServer.Endpoints.Ppu.cs` (plus one route in `...Rtc.cs`)

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/ppu/framebuffer` | `{ width:256, height:240, format:"RGBA", data:<base64 of 245760 bytes> }` |
| GET | `/api/ppu/core` | Active PPU core id |
| GET | `/api/ppu/cores` | Available PPU core ids |
| GET | `/api/ppu/state` | PPU state snapshot |
| GET | `/api/ppu/oam` | OAM / sprite data - registered in `...Rtc.cs`, not gated |

### APU - `WebApiServer.Endpoints.Apu.cs` (plus one route in `...Rtc.cs`)

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/apu/core` | Active APU core id |
| GET | `/api/apu/cores` | Available APU core ids |
| POST | `/api/apu/channels/enable` | Set the channel enable mask (bit0 P1, 1 P2, 2 Tri, 3 Noise, 4 DMC) |
| GET | `/api/apu/channels` | APU channel state - registered in `...Rtc.cs`, not gated |

### Cores - `WebApiServer.Endpoints.Cores.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/cores` | Metadata for every CPU/PPU/APU/clock/shader core (id, name, description, performance, rating, category). Returns the object directly - no `success` field |
| POST | `/api/cores/apply` | Hot-swap cores (`{ CpuId, PpuId, ApuId, OverrideReason }`). `OverrideReason` of `deck-enforced` or `story-cutscene` routes to the progression-bypassing setters |

### Shader - `WebApiServer.Endpoints.Shader.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/shader/current` | `{ shader, enabled }` |
| POST | `/api/shader/set` | Set by name (`{ ShaderName, OverrideReason }`) |
| POST | `/api/shader/enable` | Enable shaders |
| POST | `/api/shader/disable` | No-op - shaders are always on |

### Navigation / view mode - `WebApiServer.Endpoints.Navigation.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| POST | `/api/navigation/navigate` | Navigate the WebView2 to a URL (`{ Url }`) |
| GET | `/api/navigation/query-params` | Query parameters of the current page |
| GET | `/api/navigation/build-url` | Build a URL with parameters |
| GET | `/api/navigation/current-route` | Current page path |
| POST | `/api/navigation/go-to-emulator` | Switch to the native emulator view (hides the webform) |
| POST | `/api/navigation/go-to-overlay` | Transparent WebView over the emulator |
| POST | `/api/navigation/go-to-widget` | Side-by-side WebView + emulator |
| POST | `/api/navigation/go-to-web` | Full webmodule view |

### UI shell - `WebApiServer.Endpoints.Ui.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| POST | `/api/ui/close-menus` | Close all open menus |
| POST | `/api/ui/toggle-fullscreen` | Toggle fullscreen |
| POST | `/api/ui/hide-menu` | Hide the native menu bar |
| POST | `/api/ui/show-menu` | Show the native menu bar |
| POST | `/api/ui/controller/{playerNumber:int}/config` | Open a player's controller config dialog (**opens a modal window**) |

### Input - `WebApiServer.Endpoints.Input.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/input/button-event` | Poll for the last webmodule X/Y button event (only if <100 ms old; reading clears it) |
| POST | `/api/input/set-buttons` | Hold buttons for a player (`{ player: 1 or 2, buttons: ["A","Right"], holdMs }`): names `A B Select Start Up Down Left Right X Y L R C Z Mode`, plus the aliases `sel`, `pause` (= Select, the Master System's Pause), `reset` (= Mode, the Master System's Reset), `u`, `d`, or an index 0-14. The NES reads the first eight; a console session reads all of them (the Genesis pad's C, Z and Mode are the last three) |
| POST | `/api/input/clear` | Release everything for both players |
| GET | `/api/input/state` | The held buttons (for a console session too, as injected) |

### Console - `WebApiServer.Endpoints.Console.cs` (BrokenNes 2: the NES, SNES, Game Boy / Color, Master System, Game Gear and Genesis)

The console keys are `nes`, `snes`, `gb`, `gbc`, `sms`, `gg`, `md`. Core ids are the ones in the menus (`GET /api/console/cores`): a console's own cores first, then the other consoles' parts that can stand in; an entry with `"wall": true` is an instruction-set wall (the chip really runs the game's machine code, which is another processor's, so the game stops almost at once; the label says so).

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/console` | The running console, game, cores, frame size, fps; for a picture drawn by a bridge its name, loss counters and a one-line summary; for a Sega game `region` and `model`; `hasBattery`, `supportsState` (and `stateUnsupportedReason` when it has none); `note` after a core swap that had to restart the game, and why |
| GET | `/api/console/cores` | The CPU / PPU / APU menus of the selected console (`id`, `label`, `family`, `wall`) |
| POST | `/api/console/select/{console}` | Switch the base console (a game the new console can run restarts on it) |
| POST | `/api/console/core/{slot}/{id}` | Pick a core (`cpu`, `ppu`, `apu`; an id from the menu, 400 otherwise). The game keeps running when the session can swap the core in place; otherwise it restarts on it and `status.note` says why |
| GET / POST | `/api/console/gb-look` | The Game Boy look (`look`: green, grey, pocket, color; `invertBackground`, `invertSprites`) |
| GET / POST | `/api/console/sega` | The Sega machine settings: `region` (`auto`, `ntsc`, `pal`), `genesisModel` (`auto`, `1`, `2`, `3`), `masterSystemModel` (`auto`, `markiii`, `sms1`, `sms2`), the choices with their labels, and what the running game is. A change restarts a running game of a console it concerns |
| GET | `/api/console/framebuffer` | The picture at its real size (RGBA), whichever console runs |

### Emulation volume - `WebApiServer.Endpoints.Audio.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET / POST | `/api/audio/emulation-volume` | The menu bar's volume panel: `volume` 0-100, `muted`, `removeHighPitched`, `highPitchCeilingNote`, the Genesis FM patch (`genesisFmPatch` 0-15, used by the NES APU core MD) and the Master System FM instrument (`smsFmPatch` 0-21, used by the core SMSFM). Any field may be left out |

### Audio - `WebApiServer.Endpoints.Audio.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/audio/music/current` | Currently playing music track |
| GET | `/api/audio/music/list` | Available music files |
| GET | `/api/audio/sfx/list` | Available SFX files |
| POST | `/api/audio/sfx/play` | Play a sound effect |
| POST | `/api/audio/music/play` | Play music directly |
| POST | `/api/audio/music/request` | Request music with crossfade |
| POST | `/api/audio/music/stop` | Stop music with fade-out |
| GET | `/api/audio/volume` | Current music/SFX volumes |
| POST | `/api/audio/volume` | Set volumes (`{ MusicVolume, SfxVolume }`) |
| GET | `/api/audio/status` | Audio engine status |

### RTC (Real-Time Corruptor) - `WebApiServer.Endpoints.Rtc.cs` - **gated on `GlitchHarvester`**

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/rtc/domains` | Domains available for corruption |
| POST | `/api/rtc/domains/selection` | Set the selected domains (`{ SelectedDomains }`) |
| GET | `/api/rtc/intensity` | Get corruption intensity |
| POST | `/api/rtc/intensity` | Set corruption intensity (`{ Intensity }`) |
| GET | `/api/rtc/blast-type` | Get blast type |
| POST | `/api/rtc/blast-type` | Set blast type (`{ BlastType }`) |
| POST | `/api/rtc/blast` | Fire one blast; `{ success, message, writesApplied }` |
| GET | `/api/rtc/auto-corrupt` | Auto-corrupt on/off |
| POST | `/api/rtc/auto-corrupt` | Toggle auto-corrupt (`{ Enabled }`) |
| POST | `/api/rtc/let-it-rip` | Apply the "Let It Rip" preset |
| GET | `/api/rtc/crash-behavior` | Current crash-handling mode |
| POST | `/api/rtc/crash-behavior` | Set crash-handling mode (`{ Behavior }`) |
| GET | `/api/rtc/stubborn-mode` | Stubborn mode state |
| POST | `/api/rtc/stubborn-mode` | Set stubborn mode (`{ Enabled }`) |
| GET | `/api/rtc/last-blast` | Info about the last corruption operation |

(`/api/ppu/oam`, `/api/apu/channels` and `POST /api/cpu/registers` are also registered in this
file but sit outside the `/api/rtc` prefix and are therefore ungated.)

### Glitch Harvester - `WebApiServer.Endpoints.GlitchHarvester.cs` - **gated on `GlitchHarvester`**

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/gh/base-states` | List base states |
| POST | `/api/gh/base-state` | Add a base state (`{ Name }`) |
| DELETE | `/api/gh/base-state/{id}` | Delete a base state |
| GET | `/api/gh/selected-base` | Selected base-state id |
| POST | `/api/gh/select-base` | Select a base state (`{ Id }`) |
| POST | `/api/gh/load-base` | Load a base state (by id, or the selected one) |
| GET | `/api/gh/load-on-operation` | "Load on operation" setting |
| POST | `/api/gh/load-on-operation` | Set it (`{ Enabled }`) |
| POST | `/api/gh/corrupt-and-stash` | Corrupt and push the result onto the stash |
| GET | `/api/gh/stash` | List stash entries |
| POST | `/api/gh/stash/{id}/replay` | Replay a stash entry |
| POST | `/api/gh/stash/{id}/promote` | Promote a stash entry to the stockpile |
| DELETE | `/api/gh/stash/{id}` | Delete one stash entry |
| DELETE | `/api/gh/stash` | Clear the stash |
| GET | `/api/gh/stockpile` | List stockpile entries |
| POST | `/api/gh/stockpile/{id}/replay` | Replay a stockpile entry |
| PUT | `/api/gh/stockpile/{id}/rename` | Rename a stockpile entry (`{ Name }`) |
| DELETE | `/api/gh/stockpile/{id}` | Delete a stockpile entry |
| GET | `/api/gh/stockpile/export` | Export the stockpile as JSON |
| POST | `/api/gh/stockpile/import` | Import a stockpile from JSON (`{ Json }`) |

### Imagine (AI corruption) - `WebApiServer.Endpoints.Imagine.cs` - **gated on `ImagineBug`**

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/imagine/model-loaded` | Is a model loaded |
| GET | `/api/imagine/epoch` | Current epoch |
| POST | `/api/imagine/epoch` | Set the epoch to load (`{ Epoch }`) |
| POST | `/api/imagine/load-model` | Load the model for the selected epoch |
| GET | `/api/imagine/generation-params` | Get generation parameters |
| POST | `/api/imagine/generation-params` | Set them (`{ BytesToGenerate, Temperature, TopK }`) |
| POST | `/api/imagine/freeze-and-fetch` | Capture a CPU state snapshot |
| GET | `/api/imagine/cpu-snapshot` | Read the captured snapshot |
| POST | `/api/imagine/run-prediction` | Generate predicted bytes |
| GET | `/api/imagine/predicted-bytes` | Last prediction result |
| POST | `/api/imagine/apply-patch` | Write predicted bytes to memory (`{ Pc, Bytes }`) |
| POST | `/api/imagine/imagine-a-bug` | Automatic AI-driven corruption |
| POST | `/api/imagine/imagine-targeted-bug` | Targeted scanline corruption |
| POST | `/api/imagine/set-targeted-mode` | Configure scanline targeting |
| GET | `/api/imagine/targeted-status` | Targeted-mode status |
| GET | `/api/imagine/last-error` | Last Imagine error message |

### TimeJump - `WebApiServer.Endpoints.TimeJump.cs` - **gated on `TimeJump`**

| Verb | Route | Purpose |
| --- | --- | --- |
| POST | `/api/timejump/start` | Start TimeJump mode (hides the menu, resets the game) |
| POST | `/api/timejump/reset` | Clear states, reset the game, return to level 0 |
| POST | `/api/timejump/stop` | Stop TimeJump mode |
| GET | `/api/timejump/validate-rom` | Check that a valid ROM is loaded |
| POST | `/api/timejump/capture` | Capture state atomically at a frame boundary |
| POST | `/api/timejump/jump` | Perform a time jump |
| POST | `/api/timejump/query` | Query and load a similar state |
| GET | `/api/timejump/stats` | TimeJump statistics |

### Achievements - `WebApiServer.Endpoints.Achievements.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| POST | `/api/achievements/init` | Initialize the achievements engine (`{ GameTitle, MaxAchievements, LoadAll }`) |
| GET | `/api/achievements/list` | Achievements for the current game |
| GET | `/api/achievements/state/{id}` | Unlock status of one achievement |
| GET | `/api/achievements/progress/{id}` | Progress (hits, measured values) |
| GET | `/api/achievements/conditions/{id}` | Condition details, for debugging |
| POST | `/api/achievements/force-complete` | Debug: manually unlock (`{ Id }`) |
| POST | `/api/achievements/evaluate-frame` | Run one evaluation step |
| POST | `/api/achievements/reset` | Reset achievements for the current game |

### Progression & save - `WebApiServer.Endpoints.Progression.cs`, `...Save.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/progression` | Unlocked webmodules/backgrounds/null providers, pending unlocks, legacy feature flags |
| GET | `/api/progression/roster` | Full roster of webmodules with unlock state |
| POST | `/api/progression/claim-pending` | Claim pending unlock bundles |
| POST | `/api/progression/acknowledge` | Mark reward bundles as presented (`{ RewardIds }`) |
| POST | `/api/progression/unlock-everything` | **Grants everything** |
| POST | `/api/progression/equip-background` | Equip an unlocked background (`{ Name }`) |
| POST | `/api/progression/equip-null-provider` | Equip an unlocked null provider (`{ Name }`) |
| GET | `/api/save` | Read the canonical game save |
| POST | `/api/save` | Merge and persist a save through the native progression authority |
| POST | `/api/save/reset` | **Destructive** - reset progression to a fresh save |
| GET | `/api/save/continue-preview?romKey=` | Trusted continue screenshot for a ROM |

### Cards - `WebApiServer.Endpoints.Card.cs`

| Verb | Route | Purpose |
| --- | --- | --- |
| GET | `/api/card/catalog` | Authored card definitions (domain, id, names, description, rating, performance, category) |
| GET | `/api/card/{domain}/{id}` | Rendered SVG for one card |

## Usage examples

### From a webmodule (JavaScript)

```javascript
// This instance's API base, injected by the shell (falls back to the legacy fixed port).
const API = window.BROKENNES_API_BASE || 'http://127.0.0.1:42067';

const health = await (await fetch(`${API}/api/health`)).json();

const peek = await (await fetch(`${API}/api/memory/peek?domain=System%20RAM&address=0`)).json();

await fetch(`${API}/api/memory/poke`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ Domain: 'System RAM', Address: 0, Value: 255 })
});
```

Webmodules should normally just use `window.webapi` from `shared/webapi.js`, which already
resolves the right base URL.

### From outside the app (PowerShell)

`UAT/lib/ApiClient.ps1` is a documented PowerShell client that does discovery, health-waiting,
ROM loading by path, framebuffer decoding and memory access. See `UAT/README.md`.

```powershell
. .\UAT\lib\ApiClient.ps1
$inst = Start-BrokenNesInstance
Show-BrokenNesEmulator -ProcessId $inst.ProcessId
Load-BrokenNesRom      -ProcessId $inst.ProcessId -Path 'C:\roms\game.nes'
Get-BrokenNesFramebuffer -ProcessId $inst.ProcessId -OutPng .\frame.png
Stop-BrokenNesInstance -ProcessId $inst.ProcessId
```

## Testing

An interactive test webmodule lives at `Windows/Webmodules/ApiTest/index.html` (memory endpoints,
hex viewer, automated runner).

## Architecture

| File | Contents |
| --- | --- |
| `WebApiServer.cs` | Host setup, port selection + ephemeral fallback, instance discovery file, self-signed cert, `WebApiEndpoint` |
| `WebApiServer.ProgressionGate.cs` | Middleware that 403s locked feature areas |
| `WebApiServer.Endpoints.*.cs` | One partial-class file per route group (19 files) |
| `WebApiModels.cs` | Request DTOs |
| `AuthoredCardCatalog.cs` | Card definitions served by `/api/card/*` |
| `ProgressionSaveService.cs` | Save load/merge/reset, unlock checks |
| `../NesExtensions.cs`, `../NesEmulator/board/NesMemoryExtensions.cs` | The NES-side accessors the endpoints call |

The server is started when BrokenNes launches and stopped when the application closes.

## Security

The listener is bound to 127.0.0.1 only:

- only the local machine can reach it
- no admin privileges are required
- no firewall configuration is needed
- external network access is impossible

Note that it is **unauthenticated**: any local process can drive the emulator, poke memory and
overwrite the progression save. That is acceptable for a loopback-only desktop control API but is
worth remembering before exposing it through any kind of proxy.

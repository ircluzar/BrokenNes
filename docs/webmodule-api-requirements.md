# WebModule API Requirements

This document lists all features/calls that need to be implemented as an API in order to restore full functionality to the standalone webmodules. The features are separated by type based on their interaction scope.

---

## 1. Interaction with Inside of Emulation
*Direct memory/state access within the running NES emulator instance*

### Memory Access (Peek/Poke)
- [x] **Peek Memory Domain** - Read single byte from specific memory domain (PRG ROM, System RAM, PPU, etc.)
- [x] **Poke Memory Domain** - Write single byte to specific memory domain
- [x] **Peek Range** - Read multiple bytes from memory domain
- [x] **Poke Range** - Write multiple bytes to memory domain
- [x] **Get Memory Domain List** - Retrieve available memory domains with sizes
- [x] **Get Memory Domain Size** - Get size of specific memory domain

### CPU State Access
- [x] **Get CPU Registers** - Read A, X, Y, PC, SP, P registers
- [x] **Set CPU Registers** - Write CPU registers
- [x] **Get CPU Core ID** - Get current CPU core implementation name
- [x] **Get CPU State Snapshot** - Capture full CPU state for analysis

### PPU State Access
- [x] **Get Framebuffer** - Read current screen buffer
- [x] **Get PPU Core ID** - Get current PPU core implementation name
- [x] **Get OAM Data** - Read sprite data

### APU State Access
- [x] **Get APU Core ID** - Get current APU core implementation name
- [x] **Get APU Channels State** - Read state of audio channels

### Real-Time Corruptor (RTC)
- [x] **Get Memory Domains for Corruption** - List available domains with selection state
- [x] **Set Memory Domain Selection** - Enable/disable domains for corruption
- [x] **Get Corruption Intensity** - Read current intensity value
- [x] **Set Corruption Intensity** - Set intensity (1-65535)
- [x] **Get Blast Type** - Read current blast type
- [x] **Set Blast Type** - Set blast type (RANDOM, TILT, RANDOMTILT, NOP, BITFLIP, IMAGINE_NEXT, IMAGINE_RANDOM)
- [x] **Execute Blast** - Apply one-time corruption
- [x] **Get Auto-Corrupt State** - Check if auto-corrupt is enabled
- [x] **Toggle Auto-Corrupt** - Enable/disable per-frame corruption
- [x] **Let It Rip** - Preset corruption configuration (intensity=1, select PRG ROM + System RAM, enable auto)
- [x] **Get Crash Behavior** - Read current crash handling mode
- [x] **Set Crash Behavior** - Set crash mode (RedScreen, IgnoreErrors, ImagineFix)
- [x] **Get Stubborn Mode** - Check if stubborn mode is enabled (retries predictions during freezes)
- [x] **Set Stubborn Mode** - Enable/disable stubborn mode
- [x] **Get Last Blast Info** - Read information about last corruption operation

### Glitch Harvester (GH)
- [x] **Add Base State** - Capture current state as named base
- [x] **Get Base States List** - Retrieve all saved base states
- [x] **Get Selected Base ID** - Get currently selected base state
- [x] **Set Selected Base** - Select a base state
- [x] **Load Base State** - Load selected base state
- [x] **Delete Base State** - Remove a base state
- [x] **Get Load on Operation** - Check if auto-load before operations is enabled
- [x] **Set Load on Operation** - Toggle auto-load behavior
- [x] **Corrupt and Stash** - Apply corruption and save to stash
- [x] **Get Stash List** - Retrieve temporary corruption results
- [x] **Replay Stash Entry** - Apply stashed corruption
- [x] **Promote to Stockpile** - Move stash entry to permanent stockpile
- [x] **Delete Stash Entry** - Remove from stash
- [x] **Clear Stash** - Remove all stash entries
- [x] **Get Stockpile List** - Retrieve saved corruptions
- [x] **Replay Stockpile Entry** - Apply saved corruption
- [x] **Rename Stockpile Entry** - Change name of saved corruption
- [x] **Delete Stockpile Entry** - Remove from stockpile
- [x] **Export Stockpile** - Export stockpile as JSON
- [x] **Import Stockpile** - Import stockpile from JSON

### Imagine (AI-Powered Corruption)
- [x] **Get Model Loaded State** - Check if model is loaded
- [x] **Get Current Epoch** - Get loaded model epoch number
- [x] **Set Model Epoch** - Set epoch to load
- [x] **Load Model** - Load AI model by epoch
- [x] **Get Generation Parameters** - Read bytes, temperature, topK settings
- [x] **Set Generation Parameters** - Configure AI generation settings
- [x] **Freeze and Fetch Next Instruction** - Capture CPU state snapshot
- [x] **Get CPU Snapshot** - Read captured CPU state for analysis
- [x] **Run Prediction** - Generate predicted bytes from current state
- [x] **Apply Patch** - Write predicted bytes to memory
- [x] **Imagine a Bug** - Automatic corruption using AI prediction
- [x] **Get Predicted Bytes** - Read last AI prediction result
- [x] **Get Last Error** - Read last Imagine error message

### Achievements
- [ ] **Get Achievement List** - Retrieve achievements for current game
- [ ] **Get Achievement State** - Check unlock status
- [ ] **Get Achievement Progress** - Read progress (hits, measured values, etc.)
- [ ] **Get Achievement Conditions** - Read condition details for debugging
- [ ] **Force Complete Achievement** - Debug: manually unlock achievement
- [ ] **Evaluate Achievements Frame** - Run achievement evaluation step

---

## 2. Interaction with Outside of Emulation
*Game lifecycle control, ROM management, and state persistence*

### Emulation Control
- [ ] **Start Emulation** - Begin/resume running the game
- [ ] **Pause Emulation** - Stop running the game
- [ ] **Reset Emulation** - Soft reset (restart ROM)
- [ ] **Hard Reset** - Full reset (reload ROM from scratch)
- [ ] **Get Emulation State** - Check if running/paused
- [ ] **Get FPS** - Read current frames per second
- [ ] **Get Frame Count** - Read total frames executed
- [ ] **Get Error Message** - Read last error from emulator

### ROM Management
- [ ] **Load ROM by Name** - Load built-in ROM by filename
- [ ] **Import ROM** - Upload and register custom ROM file
- [ ] **Delete ROM** - Remove uploaded ROM from storage
- [ ] **Get ROM List** - Retrieve available ROMs (built-in + uploaded)
- [ ] **Get Current ROM Name** - Get name of loaded ROM
- [ ] **Get ROM Size** - Get size of ROM in bytes
- [ ] **Search ROMs** - Filter ROM list by search term
- [ ] **Reload Current ROM** - Reload the currently selected ROM
- [ ] **Get ROM Header Signature** - Read iNES header info
- [ ] **Get Uploaded ROM Data** - Read binary data of uploaded ROM

### State Persistence (Save/Load)
- [ ] **Save State** - Create emulator savestate
- [ ] **Load State** - Restore emulator savestate
- [ ] **Get State Exists** - Check if savestate exists
- [ ] **Get State Size** - Get size of savestate
- [ ] **Export State** - Download savestate as file
- [ ] **Import State** - Upload and load savestate file
- [ ] **Get State Metadata** - Read timestamp, ROM, cores used

### Input Management
- [ ] **Get Input Settings** - Read configured input mappings
- [ ] **Set Input Settings** - Configure keyboard/gamepad mappings
- [ ] **Send Input State** - Update button state for Player 1/2
- [ ] **Get Touch Controller State** - Check if touch controls are active

### Display Settings
- [ ] **Set Scale** - Change canvas scale (0.5x, 1x, 2x, etc.)
- [ ] **Get Scale** - Read current scale
- [ ] **Toggle Fullscreen** - Enter/exit fullscreen mode
- [ ] **Get Fullscreen State** - Check if in fullscreen
- [ ] **Get Canvas Buffer** - Read rendered frame data

---

## 3. Interaction with Emulator Framework
*Core selection, shaders, settings, and application-level features*

### Core Selection
- [ ] **Get Available CPU Cores** - List all CPU implementations
- [ ] **Get Selected CPU Core** - Get current CPU core ID
- [ ] **Set CPU Core** - Change CPU implementation
- [ ] **Get Available PPU Cores** - List all PPU implementations
- [ ] **Get Selected PPU Core** - Get current PPU core ID
- [ ] **Set PPU Core** - Change PPU implementation
- [ ] **Get Available APU Cores** - List all APU implementations
- [ ] **Get Selected APU Core** - Get current APU core ID
- [ ] **Set APU Core** - Change APU implementation
- [ ] **Get Available Clock Cores** - List all clock/frame loop implementations
- [ ] **Get Selected Clock Core** - Get current clock core ID
- [ ] **Set Clock Core** - Change clock implementation
- [ ] **Get Core Metadata** - Read core name, description, performance, rating, category

### Shader/Video Processing
- [ ] **Get Available Shaders** - List all shader options
- [ ] **Get Active Shader** - Get current shader ID
- [ ] **Set Shader** - Change video shader
- [ ] **Get Shader Metadata** - Read shader name, description, performance, rating, category

### Audio Settings
- [ ] **Get Master Volume** - Read master volume (0-1)
- [ ] **Set Master Volume** - Change master volume and persist
- [ ] **Get Music Volume** - Read music volume (0-1)
- [ ] **Set Music Volume** - Change music volume and persist
- [ ] **Get SFX Volume** - Read SFX volume (0-1)
- [ ] **Set SFX Volume** - Change SFX volume and persist
- [ ] **Get SoundFont Mode** - Check if SoundFont mode is enabled
- [ ] **Toggle SoundFont Mode** - Switch between PCM and SoundFont audio
- [ ] **Get Sample Font State** - Check if sampled instruments are enabled
- [ ] **Toggle Sample Font** - Enable/disable external SoundFont samples
- [ ] **Get Layering State** - Check if dual-core layering is enabled
- [ ] **Toggle Layering** - Enable/disable parallel SoundFont processing
- [ ] **Flush SoundFont** - Silence all active synths
- [ ] **Get Active SoundFont Core** - Get current SoundFont core in use

### Game Save/Progress
- [ ] **Load Game Save** - Read player progress from brokenNesGameSave
- [ ] **Save Game Save** - Write player progress
- [ ] **Get Current Level** - Read progression level
- [ ] **Get Achievement Stars** - Read total achievement count
- [ ] **Get Owned Cores** - Read unlocked cores list
- [ ] **Unlock Core** - Add core to owned list
- [ ] **Clear Save** - Reset progress to defaults
- [ ] **Unlock All Cores** - Grant all cores for testing
- [ ] **Unlock Feature** - Enable advanced feature (Savestates, RTC, GH, Imagine, Debug)
- [ ] **Get Feature Unlock State** - Check if feature is unlocked
- [ ] **Get Masquerade Map** - Read ROM-to-game ID mappings
- [ ] **Set Masquerade** - Map ROM to different game for achievements

### Database (Continue/Deck Builder)
- [ ] **Open Database** - Initialize IndexedDB connection
- [ ] **Get All Games** - Retrieve game records
- [ ] **Get Game by ID** - Read single game record
- [ ] **Add/Update Game** - Create or update game record
- [ ] **Get All Achievements** - Retrieve achievement definitions
- [ ] **Get Achievements by Game** - Filter achievements for specific game
- [ ] **Get All Cards** - Retrieve card/core definitions
- [ ] **Get Level Data** - Read level configuration
- [ ] **Get Meta Achievements** - Query meta achievement database by game title

### Background/Theme Management
- [ ] **Start Animated Background** - Initialize pixel animation (Home/DeckBuilder)
- [ ] **Stop Animated Background** - Cleanup animation
- [ ] **Set Background Color** - Change app background
- [ ] **Get Background State** - Check current background settings

### Music/SFX (App-Level)
- [ ] **Play Title Music** - Start menu background music
- [ ] **Stop Music** - Stop background music
- [ ] **Fade Out Music** - Graceful music transition
- [ ] **Play Sound Effect** - Play UI sound
- [ ] **Get Music Track List** - List available music tracks
- [ ] **Set Random Track** - Choose random background track

### Navigation/Routing
- [ ] **Navigate To Page** - Change page (e.g., /nes, /continue, /deck-builder)
- [ ] **Get Query Parameters** - Read URL parameters
- [ ] **Build URL with Parameters** - Construct navigation URL
- [ ] **Get Current Route** - Read current page path

### Debug/Benchmarking
- [ ] **Get Debug Unlocked** - Check if debug features are enabled
- [ ] **Run Benchmarks** - Execute performance benchmark suite
- [ ] **Get Benchmark Results** - Read benchmark data
- [ ] **Get Benchmark History** - Read saved benchmark runs
- [ ] **Clear Benchmark History** - Remove saved benchmarks
- [ ] **Get Event Scheduler State** - Check if experimental scheduler is enabled
- [ ] **Toggle Event Scheduler** - Enable/disable event-driven scheduling
- [ ] **Dump State** - Export full emulator state for debugging
- [ ] **Get SF Dev Logging** - Check if SoundFont logging is enabled
- [ ] **Toggle SF Dev Logging** - Enable/disable verbose SoundFont logs
- [ ] **Get SF Overlay** - Check if SoundFont overlay is visible
- [ ] **Toggle SF Overlay** - Show/hide SoundFont diagnostics overlay

### Preferences/Storage
- [ ] **Get Preference** - Read stored preference by key (IndexedDB)
- [ ] **Set Preference** - Write stored preference by key (IndexedDB)
- [ ] **Get Local Storage Item** - Read from localStorage
- [ ] **Set Local Storage Item** - Write to localStorage
- [ ] **Clear Preferences** - Reset all stored preferences

---

## Implementation Notes

### Priority Levels (Suggested)
1. **Critical (MVP)**: Emulation control, ROM loading, core selection, game save
2. **High**: State persistence, input, audio settings, database operations
3. **Medium**: RTC, achievements, shader selection, display settings
4. **Low**: GH, Imagine, benchmarking, advanced debug features

### API Design Considerations
- **RESTful Endpoints** or **WebSocket** for real-time features
- **Authentication/Authorization** if exposing externally
- **CORS Configuration** for cross-origin webmodule access
- **Error Handling** with consistent error codes and messages
- **Rate Limiting** to prevent abuse of memory access operations
- **Versioning** to support future API evolution

### Data Format
- Use **JSON** for request/response bodies
- Binary data (ROM, state) should support **Base64** encoding or multipart upload
- Memory operations should support **hexadecimal address notation**

### Security
- **Validate all input** to prevent memory corruption attacks
- **Sanitize ROM uploads** to prevent malicious code execution
- **Limit memory access** to valid address ranges
- **Implement timeouts** for long-running operations (AI model loading, etc.)

---

## Example API Call Structure

```javascript
// Example: Peek memory
const response = await fetch('/api/emulator/memory/peek', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    domain: 'System RAM',
    address: 0x0000,
    length: 1
  })
});
const data = await response.json();
// { success: true, value: 0x42 }
```

```javascript
// Example: Load ROM
const response = await fetch('/api/emulator/rom/load', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    name: 'test.nes'
  })
});
const data = await response.json();
// { success: true, romName: 'test.nes', size: 40976 }
```

```javascript
// Example: Set CPU Core
const response = await fetch('/api/emulator/cores/cpu', {
  method: 'PUT',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    coreId: 'FMC'
  })
});
const data = await response.json();
// { success: true, currentCore: 'FMC' }
```

---

*Generated: 2026-01-21*

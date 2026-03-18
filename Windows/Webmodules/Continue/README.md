# Continue / Deck Builder - Web Module

## Overview

The Continue / Deck Builder module is the main interface for building your NES console configuration, selecting ROMs, and viewing achievements. This is a simplified standalone version of the full Continue.razor page.

## Features

### Level Progression
- Display current level with title and enforced cores
- Track star progress (earned achievements)
- Level cleared status indicator
- Progress button to advance to next level (when requirements met)

### Console Building
- **4 Core Slots**: CPU, PPU, APU, and Shader
- Click empty slots or existing cores to open picker modal
- Visual indicators for enforced cores (grayed out, not changeable)
- Owned cores are selectable; locked cores are disabled
- Selections are saved to localStorage preferences

### Cartridge Library
- Filterable list of installed ROMs that have achievement data
- Search by game title
- Display completed and total stars for each installed cartridge
- Click to select a game and view its real achievement list

### Achievement Tracking
- View all `continueDb` achievements for the selected installed game
- Shows completed vs total count
- Individual achievement items with checkmarks
- Completion is driven by `gameSave.Achievements`

### Collapsible Panels
- Cartridge panel can be toggled open/closed
- Saves screen space when not actively selecting ROMs

## File Structure

```
Continue/
├── index.html          # Main HTML structure
├── continue.js         # Core logic and state management
├── styles.css          # Complete styling (based on Continue.razor.css)
├── lib/                # Shared libraries
│   ├── storage.js      # localStorage wrapper
│   └── homePixelBg.js  # Animated background
└── assets/             # Audio files
    ├── music/          # Background tracks (DeckBuilder1-4.mp3)
    └── sfx/            # Sound effects
```

## State Management

### LocalStorage Keys
- `brokenNesGameSave` - Main save data with:
  - `Level` - Current progression level (1+)
  - `Achievements` - Array of unlocked achievement IDs
  - `OwnedCores` - Object with CPU/PPU/APU/Shader arrays
  - `LevelCleared` - Boolean for current level status
  - `Preferences` - Last selected cores (CPU/PPU/APU/Shader)

### Session State
- Selected cores (CPU, PPU, APU, Shader)
- Enforced cores (from level data)
- Selected game ID
- Cartridge panel collapsed state
- Search value

## Level System

The module includes a simple 3-level progression system:

1. **Tutorial** - No enforced cores, 5 stars required
2. **CPU Focus** - Enforced CPU:EXE, 12 stars required
3. **Visual Arts** - Enforced PPU:LOW, 20 stars required

Levels are defined in `levelData` object in continue.js and can be easily extended.

## Core Data

Simplified core lists are included:
- **CPU**: FMC, EXE, LAT, QUK, HAW
- **PPU**: FMC, LOW, MED, HI, OPT
- **APU**: FMC, LOW, MED, HI
- **Shader**: 25+ shader options (PX, TV, VHS, etc.)

## Cartridge Data

The cartridge list is built from the real ROM storage plus achievement metadata:
- Installed ROMs come from `nesStorage` / `nesInterop` with localStorage fallback
- Game and achievement metadata come from `continueDb`
- If `continueDb` is empty, the module falls back to `../shared/models/default-db.json`
- Only installed games with at least one achievement are shown

## Differences from Full Application

### Simplified Features
- No SVG card rendering (using simple text labels)
- No masquerade system
- No connection to actual emulator backend

### Retained Functionality
- Core selection with ownership checks
- Level progression system
- Achievement tracking
- Save data persistence
- Installed-cartridge filtering and search
- Responsive layout

## Usage

### Selecting Cores
1. Click an empty core slot or existing core
2. Modal opens showing available cores
3. Owned cores are clickable, locked cores are grayed out
4. Select a core to update the slot
5. Enforced cores cannot be changed

### Selecting a Game
1. Search the installed cartridge list
2. Click a game row to select it
3. View game details and achievements below

### Advancing Levels
1. Complete at least one achievement to clear the level
2. Earn enough stars to meet the requirement
3. Click "Progression" button when enabled
4. Level advances and resets cleared status

### Starting a Game
1. Build a valid console (all 4 cores selected)
2. Select an installed game with achievements
3. "Start the game" button becomes enabled
4. Click to see launch message (placeholder in web module)

## Browser Compatibility
- Modern browsers with ES6+ support
- localStorage API required
- Web Audio API for music playback
- CSS Grid and Flexbox support

## Debug API

Access the module's internal state via console:

```javascript
// Get current save data
window.continueBuilder.getGameSave()

// Get current session state
window.continueBuilder.getState()

// Reload module
window.continueBuilder.reload()
```

## Notes

- Pixel background animation runs continuously
- Background music randomly selects from 4 DeckBuilder tracks
- All interactions respect owned core limitations from save data
- Progress is auto-saved to localStorage on changes

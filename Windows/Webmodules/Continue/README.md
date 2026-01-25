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

### ROM Library
- Filterable list of available games
- "Only show compatible" filter (with achievements)
- Search by game title
- Display compatibility and achievement counts
- Click to select game and view details

### Achievement Tracking
- View all achievements for selected game
- Shows completed vs total count
- Individual achievement items with checkmarks
- Achievement descriptions

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
- Filter and search values

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

## Game Database

Sample games are included for demonstration:
- Super Mario Bros.
- The Legend of Zelda
- Metroid
- Mega Man
- Castlevania

Each has compatibility flag and achievement counts. Achievements are procedurally generated for demo purposes.

## Differences from Full Application

### Simplified Features
- No actual ROM file management (import disabled in web version)
- No SVG card rendering (using simple text labels)
- No masquerade system
- Simplified achievement data (procedurally generated)
- No connection to actual emulator backend

### Retained Functionality
- Core selection with ownership checks
- Level progression system
- Achievement tracking
- Save data persistence
- ROM filtering and search
- Responsive layout

## Usage

### Selecting Cores
1. Click an empty core slot or existing core
2. Modal opens showing available cores
3. Owned cores are clickable, locked cores are grayed out
4. Select a core to update the slot
5. Enforced cores cannot be changed

### Selecting a Game
1. Use "Only show compatible" filter to narrow list
2. Search by game title
3. Click a game row to select it
4. View game details and achievements below

### Advancing Levels
1. Complete at least one achievement to clear the level
2. Earn enough stars to meet the requirement
3. Click "Progression" button when enabled
4. Level advances and resets cleared status

### Starting a Game
1. Build a valid console (all 4 cores selected)
2. Select a compatible game
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

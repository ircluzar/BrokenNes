# DeckBuilder WebModule

A standalone web module for the BrokenNes Deck Builder interface.

## Overview

This module provides the Deck Builder summary page without any emulator functionality. It displays player progress, achievement stars, owned cores, and provides navigation to other game sections.

## Features

- **Progress Summary**: Shows owned cores, achievement stars, and current level
- **Animated Background**: Retro pixel tile animation via `homePixelBg.js`
- **Background Music**: Random DeckBuilder theme music with fade-in
- **Responsive Design**: Works on desktop and mobile devices
- **LocalStorage Integration**: Reads game save data from browser storage

## Structure

```
DeckBuilder/
├── index.html           # Main HTML page
├── styles.css           # All styles (deck builder + pixel background)
├── deckbuilder.js       # Main logic and initialization
├── README.md            # This file
├── lib/
│   ├── homePixelBg.js   # Animated pixel background
│   └── storage.js       # LocalStorage wrapper
└── assets/
    ├── music/
    │   ├── DeckBuilder1.mp3
    │   ├── DeckBuilder2.mp3
    │   ├── DeckBuilder3.mp3
    │   └── DeckBuilder4.mp3
    └── sfx/
        └── (various sound effects)
```

## Dependencies

- **Press Start 2P Font**: Loaded from Google Fonts
- **Audio Engine**: Music and SFX handled via webAPI (`/api/audio/*`)
- **homePixelBg.js**: Creates animated pixel background effect
- **storage.js**: Simple localStorage wrapper

## Usage

### Standalone
Simply open `index.html` in a web browser. The page will:
1. Initialize the pixel background animation
2. Load game save data from localStorage
3. Update the UI with player stats
4. Play random background music

### Integration
To integrate into a larger application:
```html
<!-- Include in your main HTML -->
<!-- Audio is now handled by the audio engine via webAPI -->
<script src="webmodules/DeckBuilder/lib/homePixelBg.js"></script>
<script src="webmodules/DeckBuilder/lib/storage.js"></script>
<script src="webmodules/DeckBuilder/deckbuilder.js"></script>
```

## Configuration

### Storage Key
Game save data is stored under the key: `brokenNesGameSave`

Expected format:
```javascript
{
  Level: 1,                    // Current player level
  Achievements: [],            // Array of achievement IDs
  OwnedCores: {
    CPU: [],                   // Array of owned CPU core IDs
    PPU: [],                   // Array of owned PPU core IDs
    APU: [],                   // Array of owned APU core IDs
    Shader: []                 // Array of owned Shader IDs
  }
}
```

### Music Volume
Music volume is controlled via the Options webmodule or through the audio engine API:
```javascript
// Set volume via webAPI
await window.webapi.request('/api/audio/volume', {
  method: 'POST',
  json: { musicVolume: 0.5, sfxVolume: 0.8 }
});
```

## Navigation

The page provides links to:
- **Continue Deck Builder** → `../Continue/index.html`
- **Watch Story Again** → `../Story/index.html`
- **View Unlocked Cores** → `../Cores/index.html`
- **Return** → `../index.html`

Update these paths as needed for your project structure.

## Browser Compatibility

- Modern browsers (Chrome, Firefox, Safari, Edge)
- Requires Web Audio API support
- Requires localStorage support
- Mobile-friendly responsive design

## Known Limitations

- Music autoplay may be blocked by browser policies until user interaction
- Total cores count is currently a placeholder (100) - should be calculated from actual game data
- Navigation links assume a specific webmodule structure

## Development

### Testing Locally
1. Ensure all music files are in `assets/music/`
2. Open `index.html` in a web server (not `file://` protocol for best results)
3. Check browser console for any errors

### Debugging
Access the debug API in browser console:
```javascript
// Get current game save data
window.deckBuilder.getGameSave();

// Reload the module
window.deckBuilder.reload();
```

## License

Part of the BrokenNes project. See main project LICENSE.txt for details.

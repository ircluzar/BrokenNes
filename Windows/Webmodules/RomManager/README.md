# ROM Manager WebModule

A WinForms webmodule for managing the ROM collection in BrokenNes.

## Overview

The ROM Manager provides a comprehensive interface for:
- Viewing all ROMs in the library
- Importing new ROMs via file browser or drag-and-drop
- Viewing detailed information about each ROM
- Deleting unwanted ROMs
- Filtering by compatibility and searching by title

## Features

### ROM Library View
- **Table Layout**: Displays ROMs in a sortable table with Title, System, Compatible status, and compatible star count from continueDb
- **Filtering**: Defaults to showing all challenge-compatible games from continueDb, even before their ROMs are imported, so players can see what they should load
- **Search**: Real-time search filtering by ROM title
- **Selection**: Click any ROM to view detailed information

### ROM Details Panel
When a ROM is selected, displays:
- Full title and subtitle
- File name (ROM key)
- File size
- System/Platform
- Game ID (for database lookup)
- Achievement count
- Compatibility status
- Delete button for removal

### Import Functionality
- **File Browser**: Browse and select multiple .nes files
- **Drag & Drop**: Drag .nes files directly onto the drop area
- **Batch Import**: Import multiple ROMs at once
- **Auto-Detection**: Computes game ID from PRG+CHR ROM data
- **Database Integration**: Automatically adds entries to continueDb for unknown ROMs

### Delete Functionality
- **Confirmation Dialog**: Prevents accidental deletions
- **Storage Cleanup**: Removes ROM from nesStorage
- **UI Update**: Immediately reflects changes in the ROM list

## Theming

Styled after the DeckBuilder webmodule with:
- Retro pixel art background animation
- "Press Start 2P" font for authentic NES feel
- Orange (#ff5a26) and white color scheme
- Responsive design for desktop and mobile

## Music

Requests `RomManager.mp3` at startup with fallback to `TitleScreen.mp3` if not available.

## Configuration

The module is configured via `config.json`:
- **displayMode**: "web" (full screen)
- **showInToolsMenu**: true (appears in Tools menu)
- **hideMenuBar**: true (immersive experience)
- **pauseEmulatorOnOpen**: true (pauses game when opened)

## Technical Details

### Dependencies
- `../shared/common.css` - Common styling
- `../shared/fonts.css` - Press Start 2P font
- `../shared/storage.js` - Storage utilities
- `../shared/gameSave.js` - Save management
- `../shared/webapi.js` - WebView2 communication
- `../shared/homePixelBg.js` - Background animation

### Storage Integration
- Uses `nesStorage` (localStorage) for ROM data
- Uses `continueDb` (IndexedDB) for game metadata and achievements
- Uses the `achievements` store in `continueDb` to derive each ROM's compatible star total
- Computes SHA-1 hashes for game identification

### ROM ID Computation
ROMs are identified by computing SHA-1 of PRG+CHR data:
1. Parse iNES header (first 16 bytes)
2. Locate PRG ROM and CHR ROM based on header
3. Concatenate PRG+CHR data
4. Compute SHA-1 hash
5. Format as `nes_<hash>`

## Usage

1. **Open ROM Manager** from the Tools menu in BrokenNes
2. **View ROMs**: Browse your collection in the main table
3. **Import ROMs**: Click "Import ROMs" button or drag-and-drop .nes files
4. **Select ROM**: Click any ROM to view details
5. **Delete ROM**: Select a ROM and click "Delete ROM" button

## Browser Compatibility

Requires modern browser features:
- FileReader API for file uploads
- Crypto API for SHA-1 hashing
- IndexedDB for database operations
- LocalStorage for ROM storage

## Future Enhancements

Potential improvements:
- Bulk delete functionality
- ROM rename/organize features
- Save state management per ROM
- Achievement progress visualization
- ROM metadata editing
- Export/backup functionality

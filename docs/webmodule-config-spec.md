# Web Module Configuration

## Overview

Each web module can include a `config.json` file in its root directory (next to `index.html`) to specify how the WinForms host should display it.

## Configuration Schema

### Basic Structure

```json
{
  "displayMode": "web|widget|overlay",
  "title": "Module Name",
  "description": "Module description",
  "version": "1.0.0",
  "widget": { ... },
  "overlay": { ... }
}
```

## Display Modes

### 1. `"web"` - Full Screen Mode
Takes the entire application window, replacing the main view.

**Use cases:**
- Main menus and navigation hubs
- Full-screen utilities and tools
- Primary game interfaces

**Example:**
```json
{
  "displayMode": "web",
  "title": "Home",
  "description": "Main menu and navigation"
}
```

### 2. `"widget"` - Windowed Mode
Opens in a separate, resizable window alongside the main application.

**Use cases:**
- Tools that need to stay visible while using the emulator
- Editors and configuration panels
- Real-time monitors

**Example:**
```json
{
  "displayMode": "widget",
  "title": "Glitch Harvester",
  "description": "Corruption workflow manager",
  "widget": {
    "defaultWidth": 1200,
    "defaultHeight": 800,
    "minWidth": 800,
    "minHeight": 600,
    "resizable": true,
    "startPosition": "centerScreen"
  }
}
```

**Widget Configuration Options:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `defaultWidth` | number | 800 | Initial window width in pixels |
| `defaultHeight` | number | 600 | Initial window height in pixels |
| `minWidth` | number | 400 | Minimum window width |
| `minHeight` | number | 300 | Minimum window height |
| `maxWidth` | number | null | Maximum window width (null = no limit) |
| `maxHeight` | number | null | Maximum window height (null = no limit) |
| `resizable` | boolean | true | Whether window can be resized |
| `startPosition` | string | "centerScreen" | Initial position: "centerScreen", "centerParent", "manual" |
| `showInTaskbar` | boolean | true | Show window in Windows taskbar |
| `topMost` | boolean | false | Keep window always on top |

### 3. `"overlay"` - Transparent Overlay Mode
Appears as a transparent or semi-transparent layer on top of the emulator view.

**Use cases:**
- HUD elements and real-time displays
- Debug information overlays
- Performance monitors
- Input displays

**Example:**
```json
{
  "displayMode": "overlay",
  "title": "Performance Monitor",
  "description": "Real-time FPS and performance stats",
  "overlay": {
    "position": "topRight",
    "opacity": 0.8,
    "clickThrough": false,
    "width": 300,
    "height": 200,
    "draggable": true
  }
}
```

**Overlay Configuration Options:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `position` | string | "topRight" | Initial position: "topLeft", "topRight", "bottomLeft", "bottomRight", "center" |
| `opacity` | number | 1.0 | Transparency level (0.0 - 1.0) |
| `clickThrough` | boolean | false | Allow mouse clicks to pass through to emulator |
| `width` | number | 300 | Overlay width in pixels |
| `height` | number | 200 | Overlay height in pixels |
| `draggable` | boolean | true | Allow user to reposition overlay |
| `collapsible` | boolean | true | Show collapse/expand button |
| `hideable` | boolean | true | Allow user to temporarily hide overlay |

## Full Configuration Properties

### Core Properties

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `displayMode` | string | Yes | Display mode: "web", "widget", or "overlay" |
| `title` | string | Yes | Module title shown in window title bar |
| `description` | string | No | Brief description of module functionality |
| `version` | string | No | Module version (semver format) |
| `icon` | string | No | Path to icon file relative to module directory |

### Optional Properties

| Property | Type | Description |
|----------|------|-------------|
| `requiresEmulator` | boolean | Module needs active emulator instance |
| `requiresRom` | boolean | Module needs loaded ROM |
| `hotkey` | string | Global hotkey to open/toggle module (e.g., "Ctrl+H") |
| `autoStart` | boolean | Launch module on application startup |
| `singleton` | boolean | Only allow one instance of this module |
| `showInToolsMenu` | boolean | Show this module in the Tools menu above native tools |

## Default Behavior

If no `config.json` exists, the module defaults to:
- **Display Mode:** `"web"` (full screen)
- **Widget Settings:** Default dimensions (800x600), resizable, centered
- **Title:** Directory name of the module

## Example Configurations

### Full-Screen Navigation Module
```json
{
  "displayMode": "web",
  "title": "DeckBuilder",
  "description": "Core and shader collection manager",
  "version": "1.0.0",
  "requiresEmulator": false
}
```

### Windowed Tool
```json
{
  "displayMode": "widget",
  "title": "Hex Editor",
  "description": "Real-time memory editor",
  "version": "1.0.0",
  "requiresEmulator": true,
  "requiresRom": true,
  "hotkey": "Ctrl+Shift+H",
  "singleton": true,
  "showInToolsMenu": true,
  "widget": {
    "defaultWidth": 900,
    "defaultHeight": 700,
    "minWidth": 600,
    "minHeight": 400,
    "resizable": true,
    "topMost": true
  }
}
```

### Overlay HUD
```json
{
  "displayMode": "overlay",
  "title": "FPS Counter",
  "description": "Performance metrics overlay",
  "version": "1.0.0",
  "requiresEmulator": true,
  "hotkey": "F11",
  "autoStart": true,
  "overlay": {
    "position": "topRight",
    "opacity": 0.75,
    "clickThrough": true,
    "width": 200,
    "height": 100,
    "draggable": true,
    "collapsible": true
  }
}
```

## Implementation Notes

### For WinForms Host

The WinForms application should:

1. **Load config on module registration:**
   - Check for `config.json` in module directory
   - Parse and validate against schema
   - Store configuration in module registry

2. **Apply mode on module launch:**
   - **Web mode:** Navigate WebView2 control to module
   - **Widget mode:** Create Form with WebView2, apply dimensions
   - **Overlay mode:** Create layered Form, apply transparency

3. **Respect configuration constraints:**
   - Check `requiresEmulator` before allowing launch
   - Check `requiresRom` if emulator is required
   - Enforce `singleton` if specified
   - Register `hotkey` if provided

### For Web Module Developers

1. Create `config.json` in module root directory
2. Choose appropriate `displayMode` for your use case
3. Configure mode-specific options as needed
4. Test module in target display mode
5. Update `version` when making changes

## File Location

```
Webmodules/
├── YourModule/
│   ├── config.json      ← Configuration file
│   ├── index.html
│   ├── styles.css
│   └── script.js
```

## Validation

The WinForms host should validate:
- ✅ `displayMode` is one of: "web", "widget", "overlay"
- ✅ Numeric values are within reasonable ranges
- ✅ Required properties are present
- ✅ Widget/overlay config matches selected mode
- ⚠️ Invalid config → fall back to defaults with warning

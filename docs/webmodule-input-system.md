# Webmodule Input System (X/Y Buttons)

## Overview

The webmodule input system provides two dedicated controller buttons (X and Y) that are specifically for webmodule control and are **not routed to the NES emulator**. This allows webmodules to respond to controller input without interfering with gameplay.

## Architecture

### Backend Components

1. **InputConfig Models** (`Models/InputConfig.cs`)
   - Extended `KeyboardMapping` with `X` and `Y` properties
   - Extended `GamepadMapping` with `X` and `Y` properties
   - Default mappings:
     - Keyboard: `KeyA` (X), `KeyS` (Y)
     - Gamepad: Button 2 (X), Button 3 (Y) - maps to Xbox X/Y or PlayStation Square/Triangle

2. **WebModuleInputManager** (`Windows/WebModuleInputManager.cs`)
   - Polls X/Y button state separately from NES input
   - Fires events when buttons are pressed/released
   - Supports both keyboard and gamepad input
   - Uses the same input configuration as Player 1

3. **WebAPI Endpoints** (`Windows/webapi/WebApiServer.Endpoints.Input.cs`)
   - `GET /api/input/button-event` - Poll for recent button events
   - Events are cached for 100ms to ensure webmodules can pick them up
   - Returns: `{ success, hasEvent, eventType, button }`

### Frontend Components

1. **webapi.js** (`Windows/Webmodules/shared/webapi.js`)
   - `webapi.input.startPolling(intervalMs)` - Start polling for button events
   - `webapi.input.stopPolling()` - Stop polling
   - Dispatches custom events:
     - `buttonPressed` - When a button is pressed
     - `buttonReleased` - When a button is released
     - `webmoduleButton` - Combined event with pressed state

2. **Event Details**
   ```javascript
   event.detail = {
     button: "X" | "Y",
     pressed: true | false  // only on webmoduleButton event
   }
   ```

## Usage in Webmodules

### Basic Setup

```javascript
// Start polling for button events during initialization
document.addEventListener('DOMContentLoaded', () => {
  if (window.webapi && window.webapi.input) {
    window.webapi.input.startPolling(50); // Poll every 50ms
    
    // Listen for button press events
    window.addEventListener('buttonPressed', (event) => {
      const button = event.detail.button; // "X" or "Y"
      
      if (button === 'X') {
        handleXButton();
      } else if (button === 'Y') {
        handleYButton();
      }
    });
    
    // Optional: Listen for button release events
    window.addEventListener('buttonReleased', (event) => {
      const button = event.detail.button;
      // Handle button release
    });
  }
});

function handleXButton() {
  console.log('X button pressed!');
  // Perform action
}

function handleYButton() {
  console.log('Y button pressed!');
  // Perform action
}
```

### Combined Event Listener

```javascript
// Alternative: Listen to single event with pressed state
window.addEventListener('webmoduleButton', (event) => {
  const { button, pressed } = event.detail;
  
  if (pressed) {
    console.log(`${button} button pressed`);
  } else {
    console.log(`${button} button released`);
  }
});
```

### TimeJump Example

```javascript
function setupWebmoduleButtons() {
  window.webapi.input.startPolling(50);
  
  window.addEventListener('buttonPressed', (event) => {
    const button = event.detail.button;
    
    if (button === 'X') {
      // X button = Perform Jump
      if (isRunning && availableStatesCount > 0) {
        performJump();
      }
    } else if (button === 'Y') {
      // Y button = Reset
      if (isRunning) {
        resetTimeJump();
      }
    }
  });
}
```

## Configuration

Users can configure the X/Y button mappings through the WinForms Controller Configuration window:

1. Open the Controller Configuration window from the Options menu (or press the Player 1/2 controller config buttons)
2. The window now includes X and Y button configuration fields
3. Click on an X or Y button field and press a key (keyboard mode) or gamepad button (gamepad mode)
4. The configuration is saved to `config.json` and persists across sessions

## Default Mappings

### Keyboard
- **X Button**: `A` key
- **Y Button**: `S` key

### Gamepad (Standard Xbox/PlayStation layout)
- **X Button**: GamepadButtonFlags.X (Xbox X / PlayStation Square)
- **Y Button**: GamepadButtonFlags.Y (Xbox Y / PlayStation Triangle)

## Button Mapping Reference

Standard gamepad button indices:
- 0: A / Cross (South)
- 1: B / Circle (East)
- 2: X / Square (West)
- 3: Y / Triangle (North)
- 4: Left Bumper
- 5: Right Bumper
- 6: Left Trigger
- 7: Right Trigger
- 8: Select / Back / View
- 9: Start / Menu
- 12: D-Pad Up
- 13: D-Pad Down
- 14: D-Pad Left
- 15: D-Pad Right

## Best Practices

1. **Start polling on DOMContentLoaded**: Ensures webapi is ready
2. **Stop polling on cleanup**: Call `webapi.input.stopPolling()` if needed
3. **Check module state**: Only respond to buttons when your module is in the appropriate state
4. **Provide visual feedback**: Show users which buttons do what (tooltips, help text, etc.)
5. **Handle errors gracefully**: Check if webapi.input exists before using

## Benefits

- ✅ **No gameplay interference**: X/Y buttons don't affect NES emulation
- ✅ **Unified input**: Works with both keyboard and gamepad
- ✅ **Configurable**: Users can remap buttons to their preference
- ✅ **Simple API**: Easy to integrate into any webmodule
- ✅ **Event-driven**: No need for webmodules to manually poll
- ✅ **Low latency**: 50ms polling interval provides responsive input

## Future Enhancements

Potential future improvements:
- Add more button types (L/R triggers, shoulder buttons)
- Support button combinations (Ctrl+X, etc.)
- Add button hold detection (long press vs. tap)
- Per-webmodule button mapping overrides
- Visual button binding UI in webmodules

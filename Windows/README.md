# BrokenNes - Windows Edition

A Windows desktop version of BrokenNes, built with WinForms and reusing the core emulator logic from the main Blazor project.

## Features

- **File Menu**: Load ROM, Close ROM, Reset Emulator, Recent ROMs list, Exit
- **Emulation Menu**: Pause/Resume emulation (Space bar)
- **Cores Menu**: Switch between different CPU, PPU, and APU cores at runtime
- **Help Menu**: About information

### Keyboard Controls

- **Z** = A Button
- **X** = B Button
- **A** = Select
- **S** = Start
- **Arrow Keys** = D-Pad
- **Space** = Pause/Resume
- **Ctrl+O** = Load ROM
- **Ctrl+R** = Reset Emulator

## Building

From the Windows directory:

```bash
cd Windows
dotnet build
```

Or from the solution root:

```bash
dotnet build Windows/BrokenNes.Windows.csproj
```

## Running

From the Windows directory:

```bash
cd Windows
dotnet run
```

Or run the executable directly:

```bash
Windows\bin\Debug\net9.0-windows\BrokenNes.Windows.exe
```

## Architecture

The Windows project references the emulator core files directly from the parent project:

### Included Files
- `NesEmulator/board/` - Core NES emulation (NES.cs, Bus.cs, Input.cs, Cartridge.cs)
- `NesEmulator/cpus/` - All CPU cores (FMC, SPD, FIX, etc.)
- `NesEmulator/ppus/` - All PPU cores (FMC, SPD, CUBE, EIL, BFR, LQ, LOW)
- `NesEmulator/apus/` - All APU cores
- `NesEmulator/mappers/` - Cartridge mappers
- `NesEmulator/expansion/` - Expansion audio support
- `NesEmulator/Shaders/` - Shader system
- `NesEmulator/RetroAchievements/` - RetroAchievements support
- `NesEmulator/corruptor/` - Memory corruption/glitching (excluding UI parts)
- `Models/GameSave.cs` - Save state data model

### Excluded Files (Blazor/Web-specific)
- `NesEmulator/board/Emulator*.cs` - Blazor UI controller
- `NesEmulator/board/NesController.cs` - Blazor UI state
- `NesEmulator/board/Benchmark.cs` - Blazor benchmarking UI
- `NesEmulator/board/StatePersistence.cs` - Blazor save/load UI
- `NesEmulator/board/ClockRegistry.cs` - Blazor clock system
- `NesEmulator/clocks/` - Blazor clock system
- `NesEmulator/UI.cs` - Blazor UI helpers
- `NesEmulator/corruptor/GlitchHarvester.cs` - Blazor corruptor UI
- `NesEmulator/StatusService.cs` - Blazor status service
- `Services/GameSaveService.cs` - Blazor save service

This allows us to maintain a single codebase for the emulation logic while supporting both web (Blazor) and desktop (WinForms) interfaces.

## Implementation Notes

- **Framebuffer Rendering**: Currently done directly to a Bitmap for simplicity. Performance optimization (e.g., OpenGL/Direct2D) can be added later.
- **Timing**: The emulator runs at ~60 FPS using a Windows Forms Timer (16ms interval).
- **Input Handling**: Keyboard events are captured via KeyDown/KeyUp with KeyPreview enabled on the form.
- **Recent ROMs**: Stored in `%APPDATA%\BrokenNes\recent.txt` for persistence across sessions.
- **Core Selection**: All available CPU, PPU, and APU cores are dynamically populated in the Cores menu at runtime.
- **Reset**: Reloads the current ROM to reset the emulator state.

## Future Enhancements

Potential improvements that can be added:

1. **Performance**: Use OpenGL/Direct2D for faster framebuffer rendering
2. **Audio**: Integrate audio playback using NAudio or similar
3. **Save States**: Add save/load state functionality
4. **Cheats**: RetroAchievements and cheat code support
5. **Controller Support**: Add gamepad/controller input support
6. **Video Recording**: Add screen recording capabilities
7. **Settings**: Add configuration UI for input mapping, video filters, etc.
8. **Debugger**: Add CPU/PPU/APU debugging tools


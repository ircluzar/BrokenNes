# AudioEngine

High-level audio playback engine for BrokenNes Windows using NAudio.

## Features

- **Music Playback**: Play music files with optional looping from `Data/music/`
- **Sound Effects**: Play one-shot sound effects from `Data/sfx/`
- **Crossfading**: Smooth transitions between music tracks with configurable fade duration
- **Async Operation**: All operations run on background threads to avoid blocking the UI thread
- **Format Support**: MP3 and M4A audio files
- **Volume Control**: Independent volume control for music and SFX

## API

### Music Methods

- `PlayMusicAsync(filename, loop)` - Play music immediately, stopping any current music
- `RequestMusicAsync(filename, loop, fadeDurationMs)` - Crossfade to new music track
- `StopMusicAsync(fadeDurationMs)` - Stop music with fade-out

### SFX Methods

- `PlaySfxAsync(filename)` - Play sound effect once (multiple can play simultaneously)

### Properties

- `CurrentMusicFile` - Currently playing music file name
- `IsMusicPlaying` - Whether music is currently playing
- `MusicVolume` - Music volume (0.0 - 1.0)
- `SfxVolume` - SFX volume (0.0 - 1.0)

## WebAPI Endpoints

All endpoints are available via the WebAPI server on `http://127.0.0.1:42067`:

### GET Endpoints

- `/api/audio/music/current` - Get currently playing music
- `/api/audio/music/list` - List available music files
- `/api/audio/sfx/list` - List available SFX files
- `/api/audio/volume` - Get current volume levels

### POST Endpoints

- `/api/audio/music/play` - Play music directly
  ```json
  { "filename": "TitleScreen.mp3", "loop": true }
  ```

- `/api/audio/music/request` - Request music with crossfade
  ```json
  { "filename": "Story.mp3", "loop": true, "fadeDurationMs": 1000 }
  ```

- `/api/audio/music/stop` - Stop music with fade-out
  ```json
  { "fadeDurationMs": 1000 }
  ```

- `/api/audio/sfx/play` - Play sound effect
  ```json
  { "filename": "SFX01.mp3" }
  ```

- `/api/audio/volume` - Set volume levels
  ```json
  { "musicVolume": 0.7, "sfxVolume": 0.8 }
  ```

## Testing

Use the **AudioTest** webmodule to test all functionality:
- Navigate to the AudioTest module from the web modules menu
- Browse and play available music and SFX files
- Test crossfading by clicking "Request" on a different track while music is playing
- Adjust fade duration and volume controls
- Toggle music looping

## Implementation Details

### Thread Safety
- All public methods are async and run on background threads
- Uses locks to protect shared state
- No blocking of the WinForms UI thread

### Crossfading
1. Current music begins fading out
2. After (fadeDuration - 500ms), new music starts fading in
3. Both tracks play together during the 500ms overlap
4. Old track stops and disposes after fade-out completes

### Memory Management
- `IDisposable` implementation properly cleans up all resources
- NAudio readers and output devices are disposed after use
- SFX automatically clean up when playback completes

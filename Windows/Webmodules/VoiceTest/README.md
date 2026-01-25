# VoiceTest Webmodule

Interactive test page for the speak.js text-to-speech library.

## Features

- **Text Input**: Enter any text to be spoken
- **Speed Control**: Adjust speaking rate (80-450, default 125)
- **Pitch Control**: Adjust voice pitch (0.1-5.0, default 1.0)
- **Volume Control**: Adjust amplitude (0-2, default 1.0)
- **Variant Selection**: Choose from multiple voice variants (croak, whisper, klatt, m1-m7, f1-f5, etc.)
- **Voice Selection**: Choose language/accent (en-us, en-gb, en-sc)
- **Real-time Updates**: See parameter values update as you adjust sliders

## Usage

1. Open the VoiceTest webmodule
2. Enter text in the text area
3. Adjust the parameters using sliders and dropdowns
4. Click "🔊 Speak" to hear the text (or press Ctrl+Enter)
5. Use "🔄 Reset to Defaults" to restore default values
6. Use "⏹️ Stop" to attempt to interrupt playback

## Dependencies

- `speak.js` - Wrapper around meSpeak TTS library
- `mespeak.js` - Core TTS engine
- `mespeak_config.json` - Configuration file
- Voice files in `voices/en/` directory

All dependencies are located in `../shared/lib/mespeak/`

## Technical Details

The speak.js library uses the meSpeak TTS engine for in-browser text-to-speech synthesis without requiring the browser's native Speech API. This provides consistent cross-browser TTS capabilities.

The library routes audio through the WebAudio API and can integrate with the NES audio context if available.

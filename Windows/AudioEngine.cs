using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace BrokenNes.Windows
{
    /// <summary>
    /// High-level audio engine for playing music and sound effects.
    /// Uses NAudio for playback with support for MP3 and M4A formats.
    /// Runs on background threads to avoid blocking the WinForms UI thread.
    /// </summary>
    public class AudioEngine : IDisposable
    {
        private readonly string _musicFolder;
        private readonly string _sfxFolder;
        private readonly object _musicLock = new object();
        private readonly object _sfxLock = new object();
        
        // Music playback state
        private AudioFileReader? _currentMusicReader;
        private WaveOutEvent? _musicOutput;
        private string? _currentMusicFile;
        private bool _currentMusicLooping;
        private float _musicVolume = 0.7f;
        private CancellationTokenSource? _fadeCts;
        
        // SFX playback (multiple simultaneous)
        private readonly List<SfxPlayback> _activeSfxs = new List<SfxPlayback>();
        private float _sfxVolume = 0.8f;
        
        // Crossfade settings
        private const int DefaultFadeDurationMs = 1000;
        private const int CrossfadeOverlapMs = 500;
        
        public string? CurrentMusicFile => _currentMusicFile;
        public bool IsMusicPlaying => _currentMusicFile != null && _musicOutput?.PlaybackState == PlaybackState.Playing;
        
        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = Math.Clamp(value, 0f, 1f);
                lock (_musicLock)
                {
                    if (_currentMusicReader != null)
                        _currentMusicReader.Volume = _musicVolume;
                }
            }
        }
        
        public float SfxVolume
        {
            get => _sfxVolume;
            set => _sfxVolume = Math.Clamp(value, 0f, 1f);
        }

        public AudioEngine(string? dataFolder = null)
        {
            var baseFolder = dataFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _musicFolder = Path.Combine(baseFolder, "music");
            _sfxFolder = Path.Combine(baseFolder, "sfx");
            
            // Create folders if they don't exist
            Directory.CreateDirectory(_musicFolder);
            Directory.CreateDirectory(_sfxFolder);
        }

        #region SFX Playback

        /// <summary>
        /// Plays a sound effect once (no looping) without blocking.
        /// Multiple SFX can play simultaneously.
        /// </summary>
        public async Task PlaySfxAsync(string filename)
        {
            await Task.Run(() =>
            {
                try
                {
                    var filePath = Path.Combine(_sfxFolder, filename);
                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"[AudioEngine] SFX file not found: {filename}");
                        return;
                    }

                    var sfx = new SfxPlayback
                    {
                        Reader = new AudioFileReader(filePath) { Volume = _sfxVolume },
                        Output = new WaveOutEvent()
                    };

                    sfx.Output.Init(sfx.Reader);
                    sfx.Output.PlaybackStopped += (s, e) =>
                    {
                        // Clean up when done
                        lock (_sfxLock)
                        {
                            _activeSfxs.Remove(sfx);
                        }
                        sfx.Dispose();
                    };

                    lock (_sfxLock)
                    {
                        _activeSfxs.Add(sfx);
                    }

                    sfx.Output.Play();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error playing SFX '{filename}': {ex.Message}");
                }
            });
        }

        #endregion

        #region Music Playback

        /// <summary>
        /// Plays a music file directly with optional looping.
        /// Stops any currently playing music immediately without fade.
        /// </summary>
        public async Task PlayMusicAsync(string filename, bool loop = true)
        {
            await Task.Run(() =>
            {
                try
                {
                    var filePath = Path.Combine(_musicFolder, filename);
                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"[AudioEngine] Music file not found: {filename}");
                        return;
                    }

                    // Cancel any ongoing fade
                    _fadeCts?.Cancel();

                    lock (_musicLock)
                    {
                        // Stop current music
                        StopMusicInternal();

                        // Start new music
                        _currentMusicReader = new AudioFileReader(filePath) { Volume = _musicVolume };
                        _musicOutput = new WaveOutEvent();
                        _musicOutput.Init(_currentMusicReader);
                        
                        _currentMusicFile = filename;
                        _currentMusicLooping = loop;

                        if (loop)
                        {
                            _musicOutput.PlaybackStopped += OnMusicPlaybackStopped;
                        }

                        _musicOutput.Play();
                        Console.WriteLine($"[AudioEngine] Playing music: {filename} (loop: {loop})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error playing music '{filename}': {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Requests a music change with smooth crossfade transition.
        /// Fades out current music, then fades in new music with overlap.
        /// If the requested song is already playing, does nothing.
        /// </summary>
        public async Task RequestMusicAsync(string filename, bool loop = true, int fadeDurationMs = DefaultFadeDurationMs)
        {
            await Task.Run(async () =>
            {
                try
                {
                    var filePath = Path.Combine(_musicFolder, filename);
                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"[AudioEngine] Music file not found: {filename}");
                        return;
                    }

                    // If the requested song is already playing, do nothing
                    if (_currentMusicFile != null && 
                        string.Equals(_currentMusicFile, filename, StringComparison.OrdinalIgnoreCase) &&
                        _musicOutput?.PlaybackState == PlaybackState.Playing)
                    {
                        Console.WriteLine($"[AudioEngine] '{filename}' is already playing, no action needed");
                        return;
                    }

                    // Cancel any previous fade operation
                    _fadeCts?.Cancel();
                    _fadeCts = new CancellationTokenSource();
                    var token = _fadeCts.Token;

                    // If nothing is playing, just start the new track with fade-in
                    if (_currentMusicFile == null || _musicOutput == null)
                    {
                        await PlayMusicWithFadeInAsync(filename, loop, fadeDurationMs, token);
                        return;
                    }

                    Console.WriteLine($"[AudioEngine] Crossfading from '{_currentMusicFile}' to '{filename}'");

                    // Capture current music for fade-out
                    AudioFileReader? oldReader;
                    WaveOutEvent? oldOutput;
                    lock (_musicLock)
                    {
                        oldReader = _currentMusicReader;
                        oldOutput = _musicOutput;
                        _currentMusicReader = null;
                        _musicOutput = null;
                        _currentMusicFile = null;
                    }

                    // Start fade-out of old track
                    var fadeOutTask = FadeOutAsync(oldReader, oldOutput, fadeDurationMs, token);

                    // Wait for half the fade duration to start crossfade
                    await Task.Delay(fadeDurationMs - CrossfadeOverlapMs, token);

                    // Start new track with fade-in (crossfade begins)
                    await PlayMusicWithFadeInAsync(filename, loop, fadeDurationMs, token);

                    // Wait for old track to finish fading out
                    await fadeOutTask;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[AudioEngine] Crossfade cancelled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error during crossfade: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Stops the currently playing music with a fade-out.
        /// </summary>
        public async Task StopMusicAsync(int fadeDurationMs = DefaultFadeDurationMs)
        {
            await Task.Run(async () =>
            {
                try
                {
                    // Cancel any ongoing fade
                    _fadeCts?.Cancel();
                    _fadeCts = new CancellationTokenSource();
                    var token = _fadeCts.Token;

                    AudioFileReader? reader;
                    WaveOutEvent? output;
                    lock (_musicLock)
                    {
                        if (_currentMusicReader == null || _musicOutput == null)
                            return;

                        reader = _currentMusicReader;
                        output = _musicOutput;
                        _currentMusicReader = null;
                        _musicOutput = null;
                        _currentMusicFile = null;
                    }

                    Console.WriteLine("[AudioEngine] Stopping music with fade-out");
                    await FadeOutAsync(reader, output, fadeDurationMs, token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[AudioEngine] Stop music cancelled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error stopping music: {ex.Message}");
                }
            });
        }

        #endregion

        #region Private Helper Methods

        private async Task PlayMusicWithFadeInAsync(string filename, bool loop, int fadeDurationMs, CancellationToken token)
        {
            var filePath = Path.Combine(_musicFolder, filename);
            
            lock (_musicLock)
            {
                _currentMusicReader = new AudioFileReader(filePath) { Volume = 0f };
                _musicOutput = new WaveOutEvent();
                _musicOutput.Init(_currentMusicReader);
                
                _currentMusicFile = filename;
                _currentMusicLooping = loop;

                if (loop)
                {
                    _musicOutput.PlaybackStopped += OnMusicPlaybackStopped;
                }

                _musicOutput.Play();
                Console.WriteLine($"[AudioEngine] Starting music with fade-in: {filename}");
            }

            // Fade in
            await FadeVolumeAsync(_currentMusicReader, 0f, _musicVolume, fadeDurationMs, token);
        }

        private async Task FadeOutAsync(AudioFileReader? reader, WaveOutEvent? output, int durationMs, CancellationToken token)
        {
            if (reader == null || output == null)
                return;

            try
            {
                var startVolume = reader.Volume;
                await FadeVolumeAsync(reader, startVolume, 0f, durationMs, token);
                
                output.Stop();
                output.Dispose();
                reader.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioEngine] Error during fade-out: {ex.Message}");
                output?.Dispose();
                reader?.Dispose();
            }
        }

        private async Task FadeVolumeAsync(AudioFileReader? reader, float fromVolume, float toVolume, int durationMs, CancellationToken token)
        {
            if (reader == null || durationMs <= 0)
                return;

            const int steps = 50;
            var stepDelayMs = durationMs / steps;
            var volumeStep = (toVolume - fromVolume) / steps;

            for (int i = 0; i < steps && !token.IsCancellationRequested; i++)
            {
                reader.Volume = fromVolume + (volumeStep * i);
                await Task.Delay(stepDelayMs, token);
            }

            if (!token.IsCancellationRequested)
                reader.Volume = toVolume;
        }

        private void OnMusicPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            lock (_musicLock)
            {
                if (!_currentMusicLooping || _currentMusicFile == null)
                    return;

                // Restart the music for looping
                try
                {
                    _currentMusicReader?.Seek(0, SeekOrigin.Begin);
                    _musicOutput?.Play();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioEngine] Error restarting music loop: {ex.Message}");
                    StopMusicInternal();
                }
            }
        }

        private void StopMusicInternal()
        {
            // Must be called within musicLock
            if (_musicOutput != null)
            {
                _musicOutput.PlaybackStopped -= OnMusicPlaybackStopped;
                _musicOutput.Stop();
                _musicOutput.Dispose();
                _musicOutput = null;
            }

            _currentMusicReader?.Dispose();
            _currentMusicReader = null;
            _currentMusicFile = null;
            _currentMusicLooping = false;
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            _fadeCts?.Cancel();
            
            lock (_musicLock)
            {
                StopMusicInternal();
            }

            lock (_sfxLock)
            {
                foreach (var sfx in _activeSfxs.ToList())
                {
                    sfx.Dispose();
                }
                _activeSfxs.Clear();
            }

            _fadeCts?.Dispose();
        }

        #endregion

        private class SfxPlayback : IDisposable
        {
            public AudioFileReader? Reader { get; set; }
            public WaveOutEvent? Output { get; set; }

            public void Dispose()
            {
                Output?.Dispose();
                Reader?.Dispose();
            }
        }
    }
}

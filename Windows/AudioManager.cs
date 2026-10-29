using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Manages audio output using NAudio for the NES emulator.
    /// Handles buffering and playback of audio samples from the APU.
    /// </summary>
    public class AudioManager : IDisposable
    {
        private readonly BufferedWaveProvider waveProvider;
        private readonly int sampleRate;
        private readonly int channels;
        private readonly int bytesPerSample;
        private bool disposed;
        
        // Audio buffer to accumulate samples before writing to NAudio
        private readonly float[] sampleBuffer;
        private const int BufferSize = 4096;
        private int bufferPosition;
        
        // Speed adjustment for fast-forwarding
        private float speedMultiplier = 1.0f;
        
        // Latency control
        private const int DesiredLatencyMs = 50;

        /// <summary>
        /// Emulation output volume, 0..1, set from the menu bar's volume button. App-wide rather than per
        /// instance so it survives the audio device being rebuilt (sound quality changes, console switches).
        /// </summary>
        public static volatile float MasterVolume = 1f;

        /// <summary>Silences emulation output without losing <see cref="MasterVolume"/>.</summary>
        public static volatile bool Muted;
        
        public AudioManager(int sampleRate = 44100, int channels = 1)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.bytesPerSample = 2; // 16-bit audio
            
            // Initialize NAudio wave format (16-bit PCM)
            var waveFormat = new WaveFormat(sampleRate, 16, channels);
            
            // Create buffered wave provider with appropriate buffer size
            waveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferLength = sampleRate * bytesPerSample * channels * 2, // 2 seconds buffer
                DiscardOnBufferOverflow = true // Drop samples if we can't keep up
            };
            
            diag = new UnderrunMeter(waveProvider, () => Interlocked.Exchange(ref lastReadTicks, Environment.TickCount64));

            // Initialize sample buffer
            sampleBuffer = new float[BufferSize];
            bufferPosition = 0;

            // The playback device: opened now if there is one. With none (no sound card, its power off, the headset
            // unplugged) the emulator runs silently and the watcher below opens one as soon as it appears; a device that
            // goes away mid-game is closed and reopened the same way. Nothing here throws for a missing device.
            wantPlaying = true;
            lock (deviceLock) TryOpenDevice("start-up");
            monitor = new System.Threading.Timer(_ => WatchDevice(), null, MonitorIntervalMs, MonitorIntervalMs);

            Console.WriteLine($"AudioManager initialized: {sampleRate}Hz, {channels} channel(s), {DesiredLatencyMs}ms latency, device: {DeviceState}");
        }

        // ------------------------------------------------------------------ the playback device: open, missing, lost, back
        /// <summary>Open: sound plays. NoDevice: Windows has no playback device; silent until one appears.
        /// Lost: the open device failed or stopped answering; silent until a device can be opened again.</summary>
        public enum AudioDeviceState { Open, NoDevice, Lost }

        private readonly object deviceLock = new();
        private WaveOutEvent? waveOut;
        private volatile bool deviceOpen;
        private volatile bool deviceFailed;     // set by the device's own PlaybackStopped-with-an-error
        private volatile bool wantPlaying;      // what the app asked (Play / Stop), applied to whatever device is open
        private readonly System.Threading.Timer monitor;
        private long lastReadTicks;             // the device's last Read (a device that stops reading while playing is gone)
        private const int MonitorIntervalMs = 1000;
        private const int StallMs = 2500;

        public AudioDeviceState DeviceState { get; private set; } = AudioDeviceState.NoDevice;
        /// <summary>Times a device was opened, and times an open device was lost (for the API and the tests).</summary>
        public int DeviceOpens { get; private set; }
        public int DeviceLosses { get; private set; }
        public string? LastDeviceError { get; private set; }
        /// <summary>Raised (on a worker thread) when <see cref="DeviceState"/> changes.</summary>
        public event Action<AudioManager>? DeviceStateChanged;

        /// <summary>Test hook: BROKENNES_TEST_AUDIO_DEVICE_FILE names a file whose text "none" makes Windows look as if it had no
        /// playback device (anything else, or no file: the real device count). Used by UAT/entrypoints/audio-device-desktop-smoke.ps1
        /// to take the device away and give it back without touching the machine's sound settings.</summary>
        private static readonly string? TestDeviceFile = Environment.GetEnvironmentVariable("BROKENNES_TEST_AUDIO_DEVICE_FILE");

        /// <summary>How many playback devices Windows has now (waveOutGetNumDevs), or 0 when the test hook says "none".</summary>
        public static int PlaybackDeviceCount()
        {
            if (!string.IsNullOrEmpty(TestDeviceFile))
            {
                try { if (File.Exists(TestDeviceFile) && File.ReadAllText(TestDeviceFile).Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return 0; }
                catch (IOException) { }   // being rewritten by the test: read it again next time
            }
            try { return WaveOut.DeviceCount; } catch { return 0; }
        }

        private void SetState(AudioDeviceState s, string why)
        {
            if (DeviceState == s) return;
            DeviceState = s;
            Console.WriteLine($"[Audio] playback device: {s} ({why})");
            try { DeviceStateChanged?.Invoke(this); } catch (Exception ex) { Console.WriteLine($"[Audio] state listener failed: {ex.Message}"); }
        }

        /// <summary>Open the default playback device (under deviceLock). False, and silent running, when there is none or it refuses.</summary>
        private bool TryOpenDevice(string why)
        {
            if (disposed) return false;
            if (PlaybackDeviceCount() == 0)
            {
                LastDeviceError = "Windows has no playback device";
                SetState(DeviceState == AudioDeviceState.Lost ? AudioDeviceState.Lost : AudioDeviceState.NoDevice, LastDeviceError);
                return false;
            }
            WaveOutEvent? w = null;
            try
            {
                w = new WaveOutEvent { DesiredLatency = DesiredLatencyMs, NumberOfBuffers = 3 };
                w.PlaybackStopped += OnPlaybackStopped;
                w.Init(diag);
                waveProvider.ClearBuffer();              // nothing stale from the silent time
                Interlocked.Exchange(ref lastReadTicks, Environment.TickCount64);
                if (wantPlaying) w.Play();
                waveOut = w; deviceOpen = true; deviceFailed = false; DeviceOpens++;
                LastDeviceError = null;
                SetState(AudioDeviceState.Open, why);
                return true;
            }
            catch (Exception ex)
            {
                LastDeviceError = ex.Message;
                Console.WriteLine($"[Audio] could not open the playback device ({why}): {ex.Message} - running silently, trying again every {MonitorIntervalMs} ms");
                if (w != null) { w.PlaybackStopped -= OnPlaybackStopped; try { w.Dispose(); } catch { } }
                SetState(DeviceState == AudioDeviceState.Lost ? AudioDeviceState.Lost : AudioDeviceState.NoDevice, ex.Message);
                return false;
            }
        }

        /// <summary>Close the open device after it went away (under deviceLock): silent running until it can be opened again.</summary>
        private void CloseLostDevice(string why)
        {
            var w = waveOut; waveOut = null; deviceOpen = false;
            if (w != null)
            {
                w.PlaybackStopped -= OnPlaybackStopped;
                // a removed device can make waveOutReset slow: let it go off this thread
                System.Threading.Tasks.Task.Run(() => { try { w.Dispose(); } catch { } });
            }
            waveProvider.ClearBuffer();
            DeviceLosses++;
            LastDeviceError = why;
            SetState(AudioDeviceState.Lost, why);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null && ReferenceEquals(sender, waveOut))
            {
                Console.WriteLine($"[Audio] the playback device stopped with an error: {e.Exception.Message}");
                deviceFailed = true;   // the watcher closes it and reopens when it can
            }
        }

        /// <summary>The watcher (a timer thread, once a second): a lost device is closed, a missing one is opened when it appears.</summary>
        private void WatchDevice()
        {
            if (disposed || !Monitor.TryEnter(deviceLock)) return;
            try
            {
                if (disposed) return;
                if (deviceOpen)
                {
                    string? lost = null;
                    if (deviceFailed) lost = LastDeviceError ?? "the device reported an error";
                    else if (PlaybackDeviceCount() == 0) lost = "the playback device went away";
                    else if (wantPlaying && waveOut?.PlaybackState == PlaybackState.Playing
                             && Environment.TickCount64 - Interlocked.Read(ref lastReadTicks) > StallMs)
                        lost = $"the playback device stopped taking sound for more than {StallMs} ms";
                    if (lost != null) { CloseLostDevice(lost); TryOpenDevice("reopened after: " + lost); }
                }
                else TryOpenDevice("a playback device appeared");
            }
            catch (Exception ex) { Console.WriteLine($"[Audio] device watcher: {ex.Message}"); }
            finally { Monitor.Exit(deviceLock); }
        }
        
        /// <summary>
        /// Queue audio samples from the APU for playback.
        /// </summary>
        /// <param name="samples">Float samples in range [-1.0, 1.0]</param>
        public void QueueSamples(float[] samples)
        {
            if (disposed || samples == null || samples.Length == 0 || sampleBuffer == null)
                return;
            
            try
            {
                // Resample audio based on speed multiplier
                // For speeds != 1.0, we need to resample to maintain correct pitch
                float[] processedSamples;
                if (Math.Abs(speedMultiplier - 1.0f) > 0.05f)
                {
                    // Speed changed significantly - resample audio
                    processedSamples = ResampleAudio(samples, speedMultiplier);
                }
                else
                {
                    // Normal play - queue samples directly without modification
                    processedSamples = samples;
                }
                
                // Accumulate samples in our buffer with bounds checking
                for (int i = 0; i < processedSamples.Length; i++)
                {
                    // Safety check to prevent overflow during shutdown/corruption
                    if (bufferPosition >= sampleBuffer.Length)
                    {
                        FlushBuffer();
                        if (disposed) return; // Exit if disposed during flush
                    }
                    
                    if (bufferPosition < sampleBuffer.Length)
                    {
                        sampleBuffer[bufferPosition++] = processedSamples[i];
                    }
                    
                    // When buffer is full, convert and send to NAudio
                    if (bufferPosition >= BufferSize)
                    {
                        FlushBuffer();
                        if (disposed) return; // Exit if disposed during flush
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error queueing samples: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Resample audio to match the current emulation speed.
        /// </summary>
        private float[] ResampleAudio(float[] samples, float speed)
        {
            try
            {
                // Safety checks for invalid inputs
                if (samples == null || samples.Length == 0)
                    return Array.Empty<float>();
                    
                if (speed <= 0 || !float.IsFinite(speed))
                    return samples;
                    
                if (Math.Abs(speed - 1.0f) < 0.01f)
                    return samples; // No resampling needed at normal speed
                
                // Clamp speed to reasonable range to prevent overflow
                speed = Math.Clamp(speed, 0.1f, 10.0f);
                
                long outputLengthLong = (long)(samples.Length / speed);
                if (outputLengthLong <= 0 || outputLengthLong > int.MaxValue)
                    return Array.Empty<float>();
                    
                int outputLength = (int)outputLengthLong;
                
                // Prevent excessive memory allocation
                if (outputLength > 1000000)
                    return samples;
                
                float[] output = new float[outputLength];
                
                for (int i = 0; i < outputLength; i++)
                {
                    float sourceIndex = i * speed;
                    int index0 = (int)sourceIndex;
                    
                    // Bounds check
                    if (index0 >= samples.Length)
                        break;
                        
                    int index1 = Math.Min(index0 + 1, samples.Length - 1);
                    float fraction = sourceIndex - index0;
                    
                    // Linear interpolation between samples
                    output[i] = samples[index0] * (1 - fraction) + samples[index1] * fraction;
                }
                
                return output;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error resampling audio: {ex.Message}");
                return samples; // Return original on error
            }
        }
        
        /// <summary>
        /// Set the playback speed multiplier (1.0 = normal speed, 2.0 = 2x speed, etc.).
        /// Affects audio resampling for both fast and slow speeds.
        /// </summary>
        /// <param name="multiplier">Target speed multiplier</param>
        /// <param name="preserveBuffer">If true, existing buffer is not cleared (useful for smooth speed changes)</param>
        public void SetSpeedMultiplier(float multiplier, bool preserveBuffer = false)
        {
            float newMultiplier = Math.Max(0.1f, Math.Min(multiplier, 10.0f)); // Clamp to reasonable range
            
            // Allow smaller updates if we are preserving buffer (smoother transition)
            float threshold = preserveBuffer ? 0.01f : 0.05f;

            // Only act on significant changes to avoid constant buffer clearing
            if (Math.Abs(newMultiplier - speedMultiplier) > threshold)
            {
                float oldMultiplier = speedMultiplier;
                speedMultiplier = newMultiplier;
                
                if (!preserveBuffer)
                {
                    // Always clear audio buffer when changing speed to prevent desync
                    // Old samples were generated at a different emulation rate and will sound wrong
                    waveProvider.ClearBuffer();
                    bufferPosition = 0;
                }

                
                Console.WriteLine($"Speed changed: {oldMultiplier:F2}x -> {newMultiplier:F2}x (audio buffer cleared)");
            }
        }
        
        /// <summary>
        /// Flush any remaining samples in the buffer to NAudio.
        /// </summary>
        private void FlushBuffer()
        {
            if (bufferPosition == 0 || sampleBuffer == null)
                return;
            
            // Safety bounds check for corrupted state during shutdown
            int safeBufferPosition = Math.Min(bufferPosition, sampleBuffer.Length);
            
            if (safeBufferPosition <= 0)
                return;
            
            // Convert float samples to 16-bit PCM
            byte[] audioData = new byte[safeBufferPosition * bytesPerSample * channels];
            int byteIndex = 0;
            float gain = Muted ? 0f : MasterVolume;

            for (int i = 0; i < safeBufferPosition; i++)
            {
                // Clamp to [-1.0, 1.0] and convert to 16-bit signed integer
                float sample = Math.Clamp(sampleBuffer[i] * gain, -1.0f, 1.0f);
                short pcmSample = (short)(sample * short.MaxValue);
                
                // Write as little-endian 16-bit
                audioData[byteIndex++] = (byte)(pcmSample & 0xFF);
                audioData[byteIndex++] = (byte)((pcmSample >> 8) & 0xFF);
                
                // For stereo, duplicate the sample
                if (channels == 2)
                {
                    audioData[byteIndex++] = (byte)(pcmSample & 0xFF);
                    audioData[byteIndex++] = (byte)((pcmSample >> 8) & 0xFF);
                }
            }
            
            // Add samples to NAudio buffer (with safety check)
            // With no device open the samples are dropped (nothing would play them; queued up they would only be stale when one appears).
            if (waveProvider != null && audioData.Length > 0 && deviceOpen)
            {
                try
                {
                    waveProvider.AddSamples(audioData, 0, audioData.Length);
                }
                catch
                {
                    // Ignore errors during shutdown
                }
            }
            
            // Reset buffer position
            bufferPosition = 0;
        }
        
        /// <summary>
        /// Get the current buffered duration in milliseconds.
        /// </summary>
        /// <returns>-1 while no device is open: there is no queue to steer by, so the emulation keeps its own clock.</returns>
        public int GetBufferedDurationMs()
        {
            if (!deviceOpen) return -1;
            int bufferedBytes = waveProvider.BufferedBytes;
            int bytesPerSecond = sampleRate * bytesPerSample * channels;
            return (bufferedBytes * 1000) / bytesPerSecond;
        }
        
        /// <summary>
        /// Clear all buffered audio samples.
        /// </summary>
        public void ClearBuffer()
        {
            waveProvider.ClearBuffer();
            bufferPosition = 0;
            Array.Clear(sampleBuffer, 0, sampleBuffer.Length);
        }
        
        /// <summary>
        /// Stop audio playback.
        /// </summary>
        public void Stop()
        {
            wantPlaying = false;
            lock (deviceLock)
            {
                try { waveOut?.Stop(); } catch (Exception ex) { Console.WriteLine($"[Audio] stop: {ex.Message}"); deviceFailed = true; }
            }
        }
        
        /// <summary>
        /// Resume audio playback.
        /// </summary>
        public void Play()
        {
            wantPlaying = true;
            lock (deviceLock)
            {
                Interlocked.Exchange(ref lastReadTicks, Environment.TickCount64);
                try { if (waveOut != null && waveOut.PlaybackState != PlaybackState.Playing) waveOut.Play(); }
                catch (Exception ex) { Console.WriteLine($"[Audio] play: {ex.Message}"); deviceFailed = true; }
            }
        }
        
        /// <summary>
        /// Check if audio is currently playing.
        /// </summary>
        /// <summary>True while the app wants sound (between Play and Stop), whether or not a device is open to play it.</summary>
        public bool IsPlaying => wantPlaying;
        
        /// <summary>
        /// Get the current playback state.
        /// </summary>
        public PlaybackState PlaybackState => waveOut?.PlaybackState ?? PlaybackState.Stopped;
        
        private readonly UnderrunMeter diag;

        /// <summary>Counts device reads that found the queue short (audible dropouts) and logs them once a second
        /// to %LOCALAPPDATA%\BrokenNes\audio-diag.log when BROKENNES_AUDIO_DIAG=1.</summary>
        private sealed class UnderrunMeter : IWaveProvider
        {
            private readonly BufferedWaveProvider inner;
            private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            private readonly bool enabled = Environment.GetEnvironmentVariable("BROKENNES_AUDIO_DIAG") == "1";
            private readonly string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrokenNes", "audio-diag.log");
            private long lastLog, reads, underruns, minBuffered = long.MaxValue;
            public long TotalUnderruns;
            public WaveFormat WaveFormat => inner.WaveFormat;
            private readonly Action onRead;
            public UnderrunMeter(BufferedWaveProvider p, Action onRead) { inner = p; this.onRead = onRead; }

            public int Read(byte[] buffer, int offset, int count)
            {
                onRead();
                int buffered = inner.BufferedBytes;
                reads++;
                if (buffered < count) { underruns++; TotalUnderruns++; }
                if (buffered < minBuffered) minBuffered = buffered;
                int n = inner.Read(buffer, offset, count);
                if (enabled && clock.ElapsedMilliseconds - lastLog >= 1000)
                {
                    lastLog = clock.ElapsedMilliseconds;
                    try
                    {
                        System.IO.File.AppendAllText(logPath,
                            $"{DateTime.Now:HH:mm:ss.fff} reads={reads} underruns={underruns} total={TotalUnderruns} minBufferedBytes={minBuffered} readBytes={count}\n");
                    }
                    catch { }
                    reads = underruns = 0; minBuffered = long.MaxValue;
                }
                return n;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            
            disposed = true;
            try { monitor.Dispose(); } catch { }
            
            // Flush any remaining samples (with safety checks)
            try
            {
                FlushBuffer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error flushing buffer during dispose: {ex.Message}");
            }
            
            // Stop and dispose of NAudio components
            try
            {
                lock (deviceLock)
                {
                    var w = waveOut; waveOut = null; deviceOpen = false;
                    if (w != null) { w.PlaybackStopped -= OnPlaybackStopped; w.Stop(); w.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error disposing WaveOut: {ex.Message}");
            }
            
            Console.WriteLine("AudioManager disposed");
        }
    }
}

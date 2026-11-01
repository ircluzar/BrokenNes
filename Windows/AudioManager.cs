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

        // ------------------------------------------------------------------ output and pacing (2026-10-29)
        // WHY THIS CHANGED. The device used to be a WinMM WaveOutEvent (3 buffers of 17 ms = 750 samples) and the emulation loop
        // made frames only when this queue drained (audio-driven pacing). On the dev machine's Focusrite USB device the WinMM path
        // took each 750-sample buffer in whole device periods of 512 samples, so every buffer played as 1024 samples' worth of
        // time (2 x 11.6 ms = 23.2 ms for 17 ms of sound): measured 43 reads a second, 32,150 samples a second taken for 44,100
        // produced (73 %), a silent gap after every buffer (the "choppy" sound), and - since the queue then drained at 73 % -
        // the game ran at 44 fps. Now: WASAPI shared mode, event-driven (the device takes exactly what it plays, at its own
        // clock), the frames are paced by the clock at the console's own rate, and the queue is held near TargetQueueMs by
        // resampling by at most +-0.5 % (dynamic rate control: a device clock a little off from ours never moves the frame rate).
        // BROKENNES_AUDIO_LEGACY=1 brings back all of the old behaviour (WinMM, audio-driven pacing, 4096-sample chunks) for the
        // negative control (UAT/entrypoints/audio-pacing-desktop-smoke.ps1); BROKENNES_AUDIO_OUTPUT=waveout keeps the new pacing
        // on the old WinMM device.
        /// <summary>BROKENNES_AUDIO_LEGACY=1: the old WinMM output, audio-driven frame pacing and 4096-sample chunks.</summary>
        public static readonly bool Legacy = Environment.GetEnvironmentVariable("BROKENNES_AUDIO_LEGACY") == "1";
        /// <summary>True when the emulation loop should make frames by the audio queue (the old way) instead of the clock.</summary>
        public static bool LegacyPacing => Legacy;
        private static readonly bool UseWaveOut = Legacy
            || string.Equals(Environment.GetEnvironmentVariable("BROKENNES_AUDIO_OUTPUT"), "waveout", StringComparison.OrdinalIgnoreCase);
        private const int WasapiLatencyMs = 30;   // the WASAPI buffer (the device period, 10 ms here, is taken out of it)
        private const int TargetQueueMs = 60;     // our own queue in front of it (measured: ~80 ms just after a frame is added, + the 30 = ~110 ms; the old path ran ~120 + 50)
        private const int PrimeBelowMs = 8;       // queue nearly empty (start-up, after a stall): top it up to the target with silence
        private const int TrimAboveMs = 100;      // queue this deep (piled up after a stall + catch-up, or a device not taking sound): drop the frame's samples
        private const double MaxRateAdjust = 0.005;   // +-0.5 % = at most 8.6 cents: inaudible
        private double queueEma = -1;             // smoothed queue depth (ms) the rate control steers by
        private double resamplePos;               // the streaming resampler: next output position, in input samples after `resamplePrev`
        private float resamplePrev;
        /// <summary>The output in use: "wasapi", "waveout" or "" with no device.</summary>
        public string OutputName { get; private set; } = "";
        private long primes, overflows, samplesSubmitted;
        private double lastRate = 1.0;
        private readonly System.Diagnostics.Stopwatch submitClock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>BROKENNES_AUDIO_SUBMIT_LOG names a file: every submission is logged there as "ms samples queueMs" (for the
        /// pacing smoke's gap analysis; off by default).</summary>
        private static readonly string? SubmitLogPath = Environment.GetEnvironmentVariable("BROKENNES_AUDIO_SUBMIT_LOG");
        private StreamWriter? submitLog;
        /// <summary>BROKENNES_AUDIO_SUBMIT_WAV names a file: the samples the emulation handed over (before the rate conversion), as a
        /// float mono WAV at their own rate, to compare with a headless render of the same ROM (off by default).</summary>
        private static readonly string? SubmitWavPath = Environment.GetEnvironmentVariable("BROKENNES_AUDIO_SUBMIT_WAV");
        private WaveFileWriter? submitWav;

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
            if (!string.IsNullOrEmpty(SubmitLogPath))
            {
                try { submitLog = new StreamWriter(SubmitLogPath, append: false) { AutoFlush = false }; } catch (Exception ex) { Console.WriteLine($"[Audio] submit log: {ex.Message}"); }
            }
            lock (deviceLock) TryOpenDevice("start-up");
            monitor = new System.Threading.Timer(_ => WatchDevice(), null, MonitorIntervalMs, MonitorIntervalMs);

            Console.WriteLine($"AudioManager initialized: {sampleRate}Hz, {channels} channel(s), output {(OutputName == "" ? "none" : OutputName)}, pacing {(Legacy ? "legacy (audio-driven)" : "clock + rate control")}, device: {DeviceState}");
        }

        // ------------------------------------------------------------------ the playback device: open, missing, lost, back
        /// <summary>Open: sound plays. NoDevice: Windows has no playback device; silent until one appears.
        /// Lost: the open device failed or stopped answering; silent until a device can be opened again.</summary>
        public enum AudioDeviceState { Open, NoDevice, Lost }

        private readonly object deviceLock = new();
        private IWavePlayer? waveOut;
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
            IWavePlayer? w = null;
            try
            {
                string name = "waveout";
                if (!UseWaveOut)
                {
                    // WASAPI's objects are made, started, stopped and freed on MTA threads only (OnMta), never on the STA UI
                    // thread, where a call from another thread would need a COM proxy NAudio's interop does not have (and made off
                    // the UI thread, NAudio raises PlaybackStopped on its own thread instead of posting it to the UI's).
                    IWavePlayer? made = null;
                    try
                    {
                        OnMta(() =>
                        {
                            var wo = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, WasapiLatencyMs);
                            try { wo.Init(diag); made = wo; } catch { wo.Dispose(); throw; }
                        });
                    }
                    catch (Exception ex)
                    {
                        // not hidden: said here, and GET /api/audio/device's stats.output says "waveout"
                        Console.WriteLine($"[Audio] WASAPI would not open the device ({ex.Message}): using WinMM (waveOut) instead");
                    }
                    if (made != null) { w = made; name = "wasapi"; w.PlaybackStopped += OnPlaybackStopped; }
                }
                if (w == null)
                {
                    w = new WaveOutEvent { DesiredLatency = DesiredLatencyMs, NumberOfBuffers = 3 };
                    w.PlaybackStopped += OnPlaybackStopped;
                    w.Init(diag);
                }
                waveProvider.ClearBuffer();              // nothing stale from the silent time
                queueEma = -1;
                Interlocked.Exchange(ref lastReadTicks, Environment.TickCount64);
                if (wantPlaying) { var p = w; OnMta(() => p.Play()); }
                waveOut = w; deviceOpen = true; deviceFailed = false; DeviceOpens++; OutputName = name;
                LastDeviceError = null;
                SetState(AudioDeviceState.Open, why);
                return true;
            }
            catch (Exception ex)
            {
                LastDeviceError = ex.Message;
                Console.WriteLine($"[Audio] could not open the playback device ({why}): {ex.Message} - running silently, trying again every {MonitorIntervalMs} ms");
                if (w != null) { w.PlaybackStopped -= OnPlaybackStopped; var d = w; try { OnMta(() => d.Dispose()); } catch { } }
                OutputName = "";
                SetState(DeviceState == AudioDeviceState.Lost ? AudioDeviceState.Lost : AudioDeviceState.NoDevice, ex.Message);
                return false;
            }
        }

        /// <summary>Close the open device after it went away (under deviceLock): silent running until it can be opened again.</summary>
        private void CloseLostDevice(string why)
        {
            var w = waveOut; waveOut = null; deviceOpen = false; OutputName = "";
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
        
        /// <summary>Runs <paramref name="a"/> on an MTA thread (this one when it is MTA already): the WASAPI objects never meet the UI thread's STA.</summary>
        private static void OnMta(Action a)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) { a(); return; }
            System.Threading.Tasks.Task.Run(a).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Queue audio samples from the APU for playback.
        /// </summary>
        /// <param name="samples">Float samples in range [-1.0, 1.0]</param>
        /// <param name="sourceRate">The rate the samples were made at (0 = the output's own rate): the NES APUs make 44100 whatever
        /// Sound Quality says, so at 22050 / 48000 they are converted here rather than played at the wrong pitch and speed.</param>
        public void QueueSamples(float[] samples, int sourceRate = 0)
        {
            if (disposed || samples == null || samples.Length == 0 || sampleBuffer == null)
                return;
            if (!Legacy) { QueueSamplesPaced(samples, sourceRate <= 0 ? sampleRate : sourceRate); return; }

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
        
        /// <summary>The clock-paced path: rate-convert (source rate, speed, and the +-0.5 % that holds the queue at its target) with a
        /// streaming linear resampler that carries its phase from frame to frame, then hand the frame's samples to the device at once
        /// (no 4096-sample chunks: the queue moves by one frame at a time, so the rate control sees a smooth level).</summary>
        private void QueueSamplesPaced(float[] samples, int sourceRate)
        {
            try
            {
                if (!deviceOpen) { queueEma = -1; return; }   // nothing would play them; queued up they would only be stale when one appears
                int bytesPerFrame = bytesPerSample * channels;
                double queueMs = waveProvider.BufferedBytes * 1000.0 / (sampleRate * bytesPerFrame);
                if (queueMs > TrimAboveMs) { overflows++; Log(0, queueMs); return; }   // do not let latency pile up: skip this frame's sound (transients only)
                if (queueMs < PrimeBelowMs)
                {
                    // start-up, after a pause, after a stall: start again from the target depth instead of creeping up to it
                    int pad = (int)((TargetQueueMs - queueMs) * sampleRate / 1000.0);
                    if (pad > 0) { AddPcm(new float[pad], pad, 0f); }
                    queueMs += pad * 1000.0 / sampleRate;
                    queueEma = queueMs;
                    primes++;
                }
                queueEma = queueEma < 0 ? queueMs : queueEma + 0.05 * (queueMs - queueEma);
                double adjust = Math.Clamp((TargetQueueMs - queueEma) / TargetQueueMs * MaxRateAdjust * 2, -MaxRateAdjust, MaxRateAdjust);
                double speed = Math.Clamp(speedMultiplier, 0.1f, 10f);
                double rate = (double)sampleRate / sourceRate / speed * (1.0 + adjust);   // output samples per input sample
                lastRate = rate;
                double step = 1.0 / rate;
                int n = samples.Length;
                int cap = (int)(n * rate) + 4;
                float[] outBuf = cap <= sampleBuffer.Length ? sampleBuffer : new float[cap];
                int o = 0;
                double t = resamplePos;   // in input indices, -1 = resamplePrev (the last sample of the frame before)
                while (t <= n - 1 && o < outBuf.Length)
                {
                    int i0 = (int)Math.Floor(t);
                    float frac = (float)(t - i0);
                    float a = i0 < 0 ? resamplePrev : samples[i0];
                    float b = i0 + 1 < n ? samples[i0 + 1] : a;
                    outBuf[o++] = a + (b - a) * frac;
                    t += step;
                }
                resamplePos = Math.Max(-1.0, t - n);
                resamplePrev = samples[n - 1];
                if (SubmitWavPath is { Length: > 0 } && !disposed)
                {
                    try { submitWav ??= new WaveFileWriter(SubmitWavPath, WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 1)); submitWav.WriteSamples(samples, 0, n); } catch { }
                }
                AddPcm(outBuf, o, Muted ? 0f : MasterVolume);
                Log(o, queueMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error queueing samples: {ex.Message}");
            }
        }

        private void Log(int count, double queueMs)
        {
            samplesSubmitted += count;
            if (submitLog == null) return;
            try { submitLog.WriteLine($"{submitClock.Elapsed.TotalMilliseconds:F2} {count} {queueMs:F1}"); } catch { }
        }

        private void AddPcm(float[] src, int count, float gain)
        {
            if (count <= 0) return;
            byte[] audioData = new byte[count * bytesPerSample * channels];
            int bi = 0;
            for (int i = 0; i < count; i++)
            {
                short pcm = (short)(Math.Clamp(src[i] * gain, -1.0f, 1.0f) * short.MaxValue);
                for (int c = 0; c < channels; c++) { audioData[bi++] = (byte)(pcm & 0xFF); audioData[bi++] = (byte)((pcm >> 8) & 0xFF); }
            }
            try { waveProvider.AddSamples(audioData, 0, audioData.Length); } catch { }
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
            queueEma = -1;
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
                try { var w = waveOut; if (w != null) OnMta(() => w.Stop()); } catch (Exception ex) { Console.WriteLine($"[Audio] stop: {ex.Message}"); deviceFailed = true; }
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
                try { var w = waveOut; if (w != null && w.PlaybackState != PlaybackState.Playing) OnMta(() => w.Play()); }
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

        public object Stats()
        {
            double secs = (System.Diagnostics.Stopwatch.GetTimestamp() - diag.StartTs) / (double)System.Diagnostics.Stopwatch.Frequency;
            return new
            {
                output = OutputName, pacing = Legacy ? "legacy" : "clock", sampleRate,
                reads = diag.TotalReads, bytesRead = diag.TotalBytesRead, underruns = diag.TotalUnderruns, seconds = secs,
                // how fast the device really takes sound, in samples a second (44100 at 44100 when it plays in real time)
                readRateHz = diag.TotalBytesRead / (double)(bytesPerSample * channels) / Math.Max(secs, 1e-6),
                maxReadGapMs = diag.MaxGapTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                samplesSubmitted, primes, overflows, rate = lastRate, queueEmaMs = queueEma,
            };
        }
        
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
            public long TotalUnderruns, TotalReads, TotalBytesRead, MaxGapTicks, lastReadTs;
            public readonly long StartTs = System.Diagnostics.Stopwatch.GetTimestamp();
            public WaveFormat WaveFormat => inner.WaveFormat;
            private readonly Action onRead;
            public UnderrunMeter(BufferedWaveProvider p, Action onRead) { inner = p; this.onRead = onRead; }

            public int Read(byte[] buffer, int offset, int count)
            {
                onRead();
                long ts = System.Diagnostics.Stopwatch.GetTimestamp();
                if (lastReadTs != 0 && ts - lastReadTs > MaxGapTicks) MaxGapTicks = ts - lastReadTs;
                lastReadTs = ts; TotalReads++; TotalBytesRead += count;
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
                    if (w != null) { w.PlaybackStopped -= OnPlaybackStopped; OnMta(() => { w.Stop(); w.Dispose(); }); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error disposing WaveOut: {ex.Message}");
            }
            
            try { submitLog?.Flush(); submitLog?.Dispose(); } catch { }
            try { submitWav?.Dispose(); } catch { }
            Console.WriteLine("AudioManager disposed");
        }
    }
}

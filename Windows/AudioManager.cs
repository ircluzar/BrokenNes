using System;
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
        private readonly WaveOutEvent waveOut;
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
            
            // Initialize WaveOut
            waveOut = new WaveOutEvent
            {
                DesiredLatency = DesiredLatencyMs,
                NumberOfBuffers = 3
            };
            
            waveOut.Init(waveProvider);
            waveOut.Play();
            
            // Initialize sample buffer
            sampleBuffer = new float[BufferSize];
            bufferPosition = 0;
            
            Console.WriteLine($"AudioManager initialized: {sampleRate}Hz, {channels} channel(s), {DesiredLatencyMs}ms latency");
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
            
            for (int i = 0; i < safeBufferPosition; i++)
            {
                // Clamp to [-1.0, 1.0] and convert to 16-bit signed integer
                float sample = Math.Clamp(sampleBuffer[i], -1.0f, 1.0f);
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
            if (waveProvider != null && audioData.Length > 0)
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
        public int GetBufferedDurationMs()
        {
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
            waveOut?.Stop();
        }
        
        /// <summary>
        /// Resume audio playback.
        /// </summary>
        public void Play()
        {
            if (waveOut?.PlaybackState != PlaybackState.Playing)
            {
                waveOut?.Play();
            }
        }
        
        /// <summary>
        /// Check if audio is currently playing.
        /// </summary>
        public bool IsPlaying => waveOut?.PlaybackState == PlaybackState.Playing;
        
        /// <summary>
        /// Get the current playback state.
        /// </summary>
        public PlaybackState PlaybackState => waveOut?.PlaybackState ?? PlaybackState.Stopped;
        
        public void Dispose()
        {
            if (disposed)
                return;
            
            disposed = true;
            
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
                waveOut?.Stop();
                waveOut?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioManager] Error disposing WaveOut: {ex.Message}");
            }
            
            Console.WriteLine("AudioManager disposed");
        }
    }
}

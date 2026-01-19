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
            if (disposed || samples == null || samples.Length == 0)
                return;
            
            // Check buffer level and drop samples if too much is buffered
            // This prevents audio from lagging behind video
            int bufferedMs = GetBufferedDurationMs();
            int maxBufferMs = speedMultiplier > 1.5f ? 50 : 150; // Tighter buffer at high speeds
            
            if (bufferedMs > maxBufferMs)
            {
                // Buffer is too full, skip these samples to stay in sync
                return;
            }
            
            // Resample based on speed multiplier
            float[] resampledSamples = ResampleAudio(samples, speedMultiplier);
            
            // Accumulate samples in our buffer
            for (int i = 0; i < resampledSamples.Length; i++)
            {
                sampleBuffer[bufferPosition++] = resampledSamples[i];
                
                // When buffer is full, convert and send to NAudio
                if (bufferPosition >= BufferSize)
                {
                    FlushBuffer();
                }
            }
        }
        
        /// <summary>
        /// Resample audio to match the current emulation speed.
        /// </summary>
        private float[] ResampleAudio(float[] samples, float speed)
        {
            if (speed <= 0 || Math.Abs(speed - 1.0f) < 0.01f)
                return samples; // No resampling needed at normal speed
            
            int outputLength = (int)(samples.Length / speed);
            if (outputLength <= 0)
                return Array.Empty<float>();
            
            float[] output = new float[outputLength];
            
            for (int i = 0; i < outputLength; i++)
            {
                float sourceIndex = i * speed;
                int index0 = (int)sourceIndex;
                int index1 = Math.Min(index0 + 1, samples.Length - 1);
                float fraction = sourceIndex - index0;
                
                // Linear interpolation between samples
                if (index0 < samples.Length)
                {
                    output[i] = samples[index0] * (1 - fraction) + samples[index1] * fraction;
                }
            }
            
            return output;
        }
        
        /// <summary>
        /// Set the playback speed multiplier (1.0 = normal speed, 2.0 = 2x speed, etc.).
        /// </summary>
        public void SetSpeedMultiplier(float multiplier)
        {
            speedMultiplier = Math.Max(0.1f, Math.Min(multiplier, 10.0f)); // Clamp to reasonable range
            
            // When speed changes significantly, clear buffer to prevent desync
            if (Math.Abs(multiplier - 1.0f) > 0.1f)
            {
                // Keep buffer very small during speed changes to minimize lag
                int bufferedMs = GetBufferedDurationMs();
                if (bufferedMs > 100) // More than 100ms is too much
                {
                    waveProvider.ClearBuffer();
                    bufferPosition = 0;
                }
            }
        }
        
        /// <summary>
        /// Flush any remaining samples in the buffer to NAudio.
        /// </summary>
        private void FlushBuffer()
        {
            if (bufferPosition == 0)
                return;
            
            // Convert float samples to 16-bit PCM
            byte[] audioData = new byte[bufferPosition * bytesPerSample * channels];
            int byteIndex = 0;
            
            for (int i = 0; i < bufferPosition; i++)
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
            
            // Add samples to NAudio buffer
            waveProvider.AddSamples(audioData, 0, audioData.Length);
            
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
            
            // Flush any remaining samples
            FlushBuffer();
            
            // Stop and dispose of NAudio components
            waveOut?.Stop();
            waveOut?.Dispose();
            
            Console.WriteLine("AudioManager disposed");
        }
    }
}

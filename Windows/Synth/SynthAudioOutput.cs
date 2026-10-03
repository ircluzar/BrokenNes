using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BrokenNes.SynthHost;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BrokenNes.Windows.Synth
{
    /// <summary>
    /// The audio device the synth plays through: WASAPI (shared mode, event driven), pulling samples from the rack on the device's own thread.
    /// <para>
    /// Test hook: <c>BROKENNES_SYNTH_AUDIO=null</c> replaces the device with a real-time clock that renders the rack and plays nothing, so the whole
    /// path from key press to rendered level can be certified on a machine with no sound card. It is a switch you set on purpose, said so on the
    /// status line, and never a fallback: with no device and no switch the synth reports "no audio output" and renders nothing.
    /// </para>
    /// </summary>
    internal sealed class SynthAudioOutput : IDisposable
    {
        /// <summary>The synth's way into NAudio: the device asks for samples, the rack renders them.</summary>
        private sealed class RackProvider : ISampleProvider
        {
            private readonly SynthRack rack;
            private readonly SynthAudioOutput owner;
            public RackProvider(SynthRack rack, SynthAudioOutput owner, int rate) { this.rack = rack; this.owner = owner; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2); }
            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                try { rack.Render(buffer.AsSpan(offset, count)); }
                catch (Exception ex)
                {
                    // the audio thread has nobody to throw to: say so on the status line, and give the device silence for this block
                    owner.Error = "the synth engine failed: " + ex.Message;
                    Array.Clear(buffer, offset, count);
                }
                return count;
            }
        }

        public static bool NullRequested => Environment.GetEnvironmentVariable("BROKENNES_SYNTH_AUDIO") == "null";

        private readonly WasapiOut? output;
        private readonly MMDevice? device;
        private readonly Thread? clock;
        private volatile bool stopClock;

        /// <summary>Set (from the audio thread) when the engine threw while rendering; the status line shows it.</summary>
        public volatile string? Error;

        public string DeviceName { get; }
        public int SampleRate { get; }

        /// <summary>Opens <paramref name="deviceName"/> (empty: the system default) and starts playing the rack. Throws when the device cannot be opened.</summary>
        public SynthAudioOutput(SynthRack rack, string deviceName)
        {
            if (NullRequested)
            {
                DeviceName = "null output (BROKENNES_SYNTH_AUDIO=null: renders in real time, plays nothing)";
                SampleRate = rack.SampleRate;
                clock = new Thread(() => ClockLoop(rack)) { IsBackground = true, Name = "synth null clock", Priority = ThreadPriority.AboveNormal };
                clock.Start();
                return;
            }

            device = Find(deviceName) ?? throw new InvalidOperationException(deviceName.Length > 0 ? $"audio output \"{deviceName}\" is not available" : "no audio output device");
            DeviceName = device.FriendlyName;
            SampleRate = device.AudioClient.MixFormat.SampleRate;
            if (SampleRate != rack.SampleRate) rack.SetSampleRate(SampleRate);
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            output.Init(new RackProvider(rack, this, SampleRate));
            output.Play();
        }

        /// <summary>The null output's clock: 10 ms blocks, paced against a stopwatch so it keeps real time on average.</summary>
        private void ClockLoop(SynthRack rack)
        {
            var buffer = new float[SampleRate / 100 * 2];
            var watch = Stopwatch.StartNew();
            long frames = 0;
            while (!stopClock)
            {
                try { rack.Render(buffer); }
                catch (Exception ex) { Error = "the synth engine failed: " + ex.Message; }
                frames += buffer.Length / 2;
                long wait = frames * 1000 / SampleRate - watch.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
            }
        }

        /// <summary>The active playback devices by friendly name ("" is the default and is not in this list).</summary>
        public static List<string> Devices()
        {
            using var e = new MMDeviceEnumerator();
            return e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => { using (d) return d.FriendlyName; }).ToList();
        }

        /// <summary>The rate a device runs at, so the rack can be made to match before it plays (the plugin takes any rate from 22 to 96 kHz).</summary>
        public static int RateOf(string deviceName)
        {
            if (NullRequested) return 48000;
            using var d = Find(deviceName);
            return d?.AudioClient.MixFormat.SampleRate ?? 48000;
        }

        private static MMDevice? Find(string name)
        {
            using var e = new MMDeviceEnumerator();
            try
            {
                if (name.Length == 0) return e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (d.FriendlyName == name) return d;
                    d.Dispose();
                }
            }
            catch (System.Runtime.InteropServices.COMException) { /* no default endpoint */ }
            return null;
        }

        public void Dispose()
        {
            stopClock = true;
            clock?.Join(1000);
            if (output != null)
            {
                try { output.Stop(); } catch (Exception) { }
                output.Dispose();
            }
            device?.Dispose();
        }
    }
}

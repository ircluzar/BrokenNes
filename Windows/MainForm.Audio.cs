using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Threading.Tasks;
using BrokenNes;
using BrokenNes.CorruptorModels;
using NesEmulator;
using NesEmulator.Shaders;
using BrokenNes.Windows.Rendering;
using BrokenNes.Windows.Tools;
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        private void ToggleSoundChannel(int channelIndex)
        {
            // Toggle the bit for this channel
            int mask = 1 << channelIndex;
            config.EnabledChannels ^= mask;
            config.Save();
            
            // Apply to NES if available
            ApplySoundSettings();
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void SetSoundQuality(int sampleRate)
        {
            config.SoundQuality = sampleRate;
            config.Save();
            
            // Reinitialize audio manager with new settings
            ReinitializeAudio();
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void SetSoundBuffer(int bufferSize)
        {
            config.SoundBuffer = bufferSize;
            config.Save();
            
            // Reinitialize audio manager with new settings
            ReinitializeAudio();
            
            // Update menu checkmarks
            UpdateConfigMenus();
        }
        
        private void ReinitializeAudio()
        {
            try
            {
                bool wasPlaying = audioManager?.IsPlaying ?? false;
                
                // Dispose old audio manager
                audioManager?.Dispose();
                
                // Create new audio manager with updated settings
                audioManager = new AudioManager(sampleRate: config.SoundQuality, channels: 1);
                
                // Resume playback if it was playing
                if (wasPlaying && isEmulationRunning && !isPaused)
                {
                    audioManager.Play();
                }
                
                Console.WriteLine($"Audio reinitialized: {config.SoundQuality}Hz, buffer={config.SoundBuffer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to reinitialize audio: {ex.Message}");
                MessageBox.Show($"Audio reinitialization failed: {ex.Message}\n\nThe emulator will continue without sound.",
                    "Audio Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        
        private void ApplySoundSettings()
        {
            if (nes != null)
            {
                // Apply channel enable/disable to the APU via the NES bus
                try
                {
                    // Get the APU through reflection or a public method
                    var busField = nes.GetType().GetField("bus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (busField != null)
                    {
                        var bus = busField.GetValue(nes);
                        if (bus != null)
                        {
                            var busType = bus.GetType();

                            void ApplyMask(IAPU? apuInstance)
                            {
                                apuInstance?.SetEnabledChannels(config.EnabledChannels);
                            }

                            // Prefer a public property if one exists (future-proof), else fall back to fields.
                            var apuFromProperty = busType.GetProperty("Apu", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase)?.GetValue(bus) as IAPU;
                            ApplyMask(apuFromProperty);

                            // Common fields on Bus: apu, activeApu, apuJank, apuQN
                            var apuField = busType.GetField("apu", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            var activeApuField = busType.GetField("activeApu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            var apuJankField = busType.GetField("apuJank", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            var apuQnField = busType.GetField("apuQN", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(bus) as IAPU;
                            ApplyMask(apuField);
                            ApplyMask(activeApuField);
                            ApplyMask(apuJankField);
                            ApplyMask(apuQnField);

                            // Also push the mask to any cached APU instances to keep future hot-swaps consistent.
                            var apuInstancesField = busType.GetField("_apuInstances", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (apuInstancesField?.GetValue(bus) is System.Collections.Generic.IDictionary<string, IAPU> apuDict)
                            {
                                foreach (var apu in apuDict.Values)
                                {
                                    ApplyMask(apu);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error applying sound settings: {ex.Message}");
                }
            }
        }
    }
}

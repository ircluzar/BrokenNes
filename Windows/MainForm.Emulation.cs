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
        private void StartEmulation()
        {
            if (nes == null) return;
            
            // Ensure form has focus so keyboard input works immediately
            this.Activate(); 
            this.Focus();
            
            isEmulationRunning = true;
            isPaused = false;
            
            // Start emulation thread
            emulatorThread = new Thread(EmulationThreadProc)
            {
                Name = "NES Emulation Thread",
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true
            };
            emulatorThread.Start();
            
            // Start render timer on UI thread
            audioManager?.Play();
        }
        
        private void StopEmulation()
        {
            isEmulationRunning = false;
            
            // Wait for emulation thread to finish
            if (emulatorThread != null && emulatorThread.IsAlive)
            {
                emulatorThread.Join(1000);
            }
            
            audioManager?.Stop();
        }
        
        private void EmulationThreadProc()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            const double targetFrameTime = 1.0 / 60.0; // 60 FPS
            double accumulator = 0;
            int framesSinceReport = 0;
            var reportStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Initialize FPS tracking for audio speed adjustment
            fpsStopwatch = System.Diagnostics.Stopwatch.StartNew();
            fpsFrameCount = 0;
            
            // Audio-driven timing: target buffer level in ms
            const int TargetAudioBufferMs = 60;  // Sweet spot: not too laggy, not too tight
            const int MinAudioBufferMs = 30;     // Run more frames if below this
            const int MaxAudioBufferMs = 100;    // Skip frames if above this
            
            // High-resolution timer for more precise frame timing
            long ticksPerSecond = System.Diagnostics.Stopwatch.Frequency;
            long targetFrameTicks = (long)(ticksPerSecond / 60.0);
            long nextFrameTime = stopwatch.ElapsedTicks;
            
            while (isEmulationRunning)
            {
                while (emulationActions.TryDequeue(out var act))
                {
                    try { act(); }
                    catch (Exception ex) { Console.WriteLine($"Emu action error: {ex.Message}"); }
                }

                using (PerformanceProfiler.Time("Frame"))
                {
                    if (isPaused)
                    {
                        Thread.Sleep(10);
                        stopwatch.Restart();
                        nextFrameTime = stopwatch.ElapsedTicks;
                        accumulator = 0;
                        continue;
                    }
                    
                    // Check if we need to reset timing (after speed change)
                    if (resetTimingAccumulator)
                    {
                        accumulator = 0;
                        stopwatch.Restart();
                        nextFrameTime = stopwatch.ElapsedTicks;
                        resetTimingAccumulator = false;
                    }
                
                // Get current audio buffer level to guide timing
                int audioBufferMs = audioManager?.GetBufferedDurationMs() ?? TargetAudioBufferMs;
                
                // Determine how many frames to run based on audio buffer state
                int framesToRun = 0;
                
                if (config.NoSpeedLimit)
                {
                    // Run as fast as possible - always run at least one frame
                    framesToRun = 1;
                }
                else if (hasSpeedOverride)
                {
                    // Speed override - use time-based accumulator
                    double deltaTime = stopwatch.Elapsed.TotalSeconds;
                    stopwatch.Restart();
                    accumulator += deltaTime;
                    double effectiveFrameTime = targetFrameTime / speedOverride;
                    framesToRun = 0;
                    while (accumulator >= effectiveFrameTime)
                    {
                        framesToRun++;
                        accumulator -= effectiveFrameTime;
                    }
                    
                    // For slow-motion, add a small sleep when no frames need to run
                    if (framesToRun == 0)
                    {
                        Thread.Sleep(1); // Small sleep to prevent CPU spinning
                    }
                }
                else
                {
                    // AUDIO-DRIVEN TIMING: Let audio buffer level guide frame production
                    // This prevents the doppler effect and audio pops
                    
                    long now = stopwatch.ElapsedTicks;
                    
                    if (audioBufferMs < MinAudioBufferMs)
                    {
                        // Audio buffer is getting low - produce frames immediately!
                        // This prevents underrun pops
                        framesToRun = 2; // Catch up quickly
                        nextFrameTime = now + targetFrameTicks;
                    }
                    else if (audioBufferMs > MaxAudioBufferMs)
                    {
                        // Audio buffer is too full - skip this cycle
                        // This prevents audio lag without dropping samples
                        framesToRun = 0;
                        nextFrameTime = now + targetFrameTicks / 2; // Check again soon
                    }
                    else if (now >= nextFrameTime)
                    {
                        // Normal case: time for a new frame
                        framesToRun = 1;
                        
                        // Adjust timing slightly based on buffer level to stay centered
                        if (audioBufferMs < TargetAudioBufferMs)
                        {
                            // Running a bit behind, speed up slightly
                            nextFrameTime = now + (long)(targetFrameTicks * 0.95);
                        }
                        else if (audioBufferMs > TargetAudioBufferMs + 20)
                        {
                            // Running a bit ahead, slow down slightly
                            nextFrameTime = now + (long)(targetFrameTicks * 1.05);
                        }
                        else
                        {
                            // Buffer is at ideal level
                            nextFrameTime = now + targetFrameTicks;
                        }
                    }
                    else
                    {
                        // Not yet time for next frame
                        framesToRun = 0;
                    }
                }
                
                // Run the calculated number of frames
                for (int f = 0; f < framesToRun && isEmulationRunning && !isPaused; f++)
                {
                    if (nes != null)
                    {
                        try
                        {
                            // Poll input managers for both players and update NES button states
                            using (PerformanceProfiler.Time("Input.Poll"))
                            {
                                bool[] p1Inputs = new bool[8];
                                bool[] p2Inputs = new bool[8];
                                
                                if (inputManager != null)
                                {
                                    inputManager.Poll();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        p1Inputs[i] = inputManager.GetButton(i);
                                    }
                                }
                                
                                if (inputManager2 != null)
                                {
                                    inputManager2.Poll();
                                    for (int i = 0; i < 8; i++)
                                    {
                                        p2Inputs[i] = inputManager2.GetButton(i);
                                    }
                                }
                                
                                nes.SetInputs(p1Inputs, p2Inputs);
                            }
                            
                            // Enable static for test.nes ROM (like in web version)
                            bool isTestRom = string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
                            nes.EnableStatic(isTestRom);
                            
                            // Run one frame of emulation
                            using (PerformanceProfiler.Time("NES.RunFrame"))
                            {
                                nes.RunFrame();
                            }

                            if (corruptor.AutoCorrupt)
                            {
                                lock (corruptorLock)
                                {
                                    try { corruptor.Blast(nes); }
                                    catch (Exception ex) { Console.WriteLine($"Auto-corrupt error: {ex.Message}"); }
                                }
                            }
                            
                            // Track FPS for display and audio speed adjustment
                            fpsFrameCount++;
                            if (fpsStopwatch != null && fpsStopwatch.Elapsed.TotalSeconds >= 0.5)
                            {
                                currentFps = fpsFrameCount / fpsStopwatch.Elapsed.TotalSeconds;
                                fpsFrameCount = 0;
                                fpsStopwatch.Restart();
                                
                                // During no speed limit, adjust audio speed based on actual FPS
                                if (config.NoSpeedLimit)
                                {
                                    float actualSpeed = (float)(currentFps / 60.0);
                                    audioManager?.SetSpeedMultiplier(actualSpeed);
                                }
                            }
                            
                            // Process audio samples from APU
                            if (audioManager != null)
                            {
                                using (PerformanceProfiler.Time("Audio.Queue"))
                                {
                                    try
                                    {
                                        float[] audioSamples = nes.GetAudioBuffer();
                                        if (audioSamples != null && audioSamples.Length > 0)
                                        {
                                            audioManager.QueueSamples(audioSamples);
                                        }
                                    }
                                    catch (Exception audioEx)
                                    {
                                        Console.WriteLine($"Audio error: {audioEx.Message}");
                                    }
                                }
                            }
                            
                            // Get the framebuffer from the PPU and copy to backbuffer
                            // Only lock when actually copying to reduce contention
                            using (PerformanceProfiler.Time("FrameBuffer.Copy"))
                            {
                                byte[]? nesFrameBuffer = nes.GetFrameBuffer();
                                if (nesFrameBuffer != null && nesFrameBuffer.Length == NES_WIDTH * NES_HEIGHT * 4 && backBuffer != null)
                                {
                                    lock (emulationLock)
                                    {
                                        backBuffer.CopyFromBytes(nesFrameBuffer);
                                    }
                                    
                                    // Render immediately after frame is ready (marshal to UI thread)
                                    if (InvokeRequired)
                                    {
                                        BeginInvoke(new Action(RenderFrame));
                                    }
                                    else
                                    {
                                        RenderFrame();
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Emulation error: {ex.Message}");
                            Console.WriteLine($"Stack trace: {ex.StackTrace}");
                            isEmulationRunning = false;
                        }
                    }
                }
                
                // Check if emulator has crashed and throttle appropriately
                if (nes != null && nes.IsCrashed())
                {
                    // When crashed, cap at 60 FPS to prevent UI thread flooding
                    Thread.Sleep(16); // ~60 FPS
                    accumulator = 0; // Reset accumulator to prevent catchup frames
                    
                    // Render the crash screen once per frame
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(RenderFrame));
                    }
                    else
                    {
                        RenderFrame();
                    }
                    continue; // Skip normal throttling logic
                }
                
                // Save periodic profiling report every 10 seconds
                framesSinceReport++;
                if (PerformanceProfiler.Enabled && reportStopwatch.Elapsed.TotalSeconds >= 10.0)
                {
                    var reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"performance_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    PerformanceProfiler.SaveReport(reportPath);
                    reportStopwatch.Restart();
                    framesSinceReport = 0;
                }
                
                // Precision wait using spin-wait for final microseconds
                // This avoids Windows timer granularity issues
                if (!config.NoSpeedLimit && !hasSpeedOverride && framesToRun == 0)
                {
                    long now = stopwatch.ElapsedTicks;
                    long ticksToWait = nextFrameTime - now;
                    
                    if (ticksToWait > 0)
                    {
                        // If more than 2ms to wait, sleep for most of it
                        double msToWait = (double)ticksToWait / ticksPerSecond * 1000.0;
                        if (msToWait > 2)
                        {
                            Thread.Sleep((int)(msToWait - 1));
                        }
                        
                        // Spin-wait for remaining time (sub-millisecond precision)
                        while (stopwatch.ElapsedTicks < nextFrameTime)
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
                else if (config.NoSpeedLimit)
                {
                    // Tiny yield to prevent complete CPU lockup but still run fast
                    Thread.Sleep(0);
                }
                } // End of PerformanceProfiler.Time("Frame") using block
            }
        }
        
        private void RenderFrame()
        {
            if (backBuffer == null || frameBuffer == null || !useDirectX || dxRenderer?.IsReady != true) return;
            
            using (PerformanceProfiler.Time("RenderFrame"))
            {
                try
                {
                    // Copy backBuffer to frameBuffer and render
                    lock (emulationLock)
                    {
                        Array.Copy(backBuffer.Bits, frameBuffer.Bits, backBuffer.Bits.Length);
                    }
                    
                    // Capture current inputs for display
                    bool[] currentInputs = new bool[8];
                    if (inputManager != null)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            currentInputs[i] = inputManager.GetButton(i);
                        }
                    }
                    
                    // Render using DirectX
                    using (PerformanceProfiler.Time("DirectX.DrawFrame"))
                    {
                        dxRenderer.DrawFrame(frameBuffer, currentInputs);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Render error: {ex.Message}");
                }
            }
        }
        
        private void EmulatorTimer_Tick(object? sender, EventArgs e)
        {
            if (nes == null || frameBuffer == null) return;
            
            try
            {
                // Run one frame of emulation
                nes.RunFrame();
                
                // Process audio samples from APU
                if (audioManager != null)
                {
                    try
                    {
                        float[] audioSamples = nes.GetAudioBuffer();
                        if (audioSamples != null && audioSamples.Length > 0)
                        {
                            audioManager.QueueSamples(audioSamples);
                        }
                    }
                    catch (Exception audioEx)
                    {
                        Console.WriteLine($"Audio error: {audioEx.Message}");
                    }
                }
                
                // Get the framebuffer from the PPU
                byte[]? nesFrameBuffer = nes.GetFrameBuffer();
                
                if (nesFrameBuffer != null && nesFrameBuffer.Length == NES_WIDTH * NES_HEIGHT * 4)
                {
                    // Copy framebuffer data to DirectBitmap (convert byte[] to expected format)
                    frameBuffer.CopyFromBytes(nesFrameBuffer);
                    
                    // Render the frame
                    if (useDirectX && dxRenderer?.IsReady == true)
                    {
                        dxRenderer.DrawFrame(frameBuffer, new bool[8]);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EmulatorTimer_Tick error: {ex.Message}");
            }
        }
    }
}

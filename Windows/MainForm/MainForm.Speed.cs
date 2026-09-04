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
using PngPayloadEmbedding;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        // Real NTSC runs 89341.5 PPU dots per frame (29780.5 CPU cycles); BrokenNes' default frame
        // budget is the round 60.000fps figure, 29829.55, handing the ROM ~49 cycles a frame that
        // hardware never gives it. Harmless for most games, but it quietly widens the frame budget
        // for anything written to fit inside a real one - which means a frame that overruns on a
        // real NES can fit comfortably here, masking exactly the class of bug such a game guards
        // against. This exposes the correction that the trace and TAS tooling already opt into, so
        // the accurate configuration is reachable from the app and not only from the command line.
        // It lives on the NES instance's SpeedConfig, so it takes effect without a reload.
        private void ToggleNtscAccurateFrameRate_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.NtscAccurateFrameRate = menuItem.Checked);
                ApplyNtscAccurateFrameRate();
                UpdateConfigMenus();
                audioManager?.ClearBuffer(); // frame length changed - drop queued samples
            }
        }

        private void ApplyNtscAccurateFrameRate()
        {
            lock (emulationLock)
            {
                var speed = nes?.GetSpeedConfig();
                if (speed != null) speed.NtscAccurateFrameRate = config.NtscAccurateFrameRate;
            }
        }

        private void ToggleNoSpeedLimit_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                Helpers.ConfigHelper.Update(config, c => c.NoSpeedLimit = menuItem.Checked);
                UpdateConfigMenus();
                
                // Clear audio buffer to prevent desync
                audioManager?.ClearBuffer();
                
                if (!config.NoSpeedLimit)
                {
                    // Speed limit restored - reset to appropriate speed
                    if (hasSpeedOverride)
                    {
                        audioManager?.SetSpeedMultiplier(speedOverride, preserveBuffer: true);
                    }
                    else
                    {
                        audioManager?.SetSpeedMultiplier(1.0f);
                    }
                }
            }
        }
        
        private void OpenSpeedControl_Click(object? sender, EventArgs e)
        {
            if (speedControlForm == null || speedControlForm.IsDisposed)
            {
                speedControlForm = new SpeedControlForm();
                
                if (inputManager != null)
                {
                    speedControlForm.SetInputManager(inputManager);
                }
                
                speedControlForm.SpeedChanged += SpeedControlForm_SpeedChanged;
                speedControlForm.SpeedChangeComplete += SpeedControlForm_SpeedChangeComplete;
                speedControlForm.FormClosed += (s, args) =>
                {
                    hasSpeedOverride = false;
                    speedOverride = 1.0f;
                    
                    // Reset audio speed and clear buffer to prevent desync
                    audioManager?.SetSpeedMultiplier(1.0f, preserveBuffer: false);
                    audioManager?.ClearBuffer();
                    resetTimingAccumulator = true;
                };
            }
            
            hasSpeedOverride = true;
            resetTimingAccumulator = true;
            speedControlForm.Show(this);
            speedControlForm.Focus();
        }
        
        private void SpeedControlForm_SpeedChanged(object? sender, float speed)
        {
            speedOverride = speed;
            hasSpeedOverride = true;
            
            // Update audio manager immediately for responsive speed changes.
            // Pass preserveBuffer=true to avoid cutting audio during dynamic speed changes (rubber banding effect)
            audioManager?.SetSpeedMultiplier(speed, preserveBuffer: true);
        }
        
        private void SpeedControlForm_SpeedChangeComplete(object? sender, EventArgs e)
        {
            // User released the trackbar - clear audio buffer to resync
            audioManager?.ClearBuffer();
            
            // Reset timing accumulator to prevent fast-forward burst
            resetTimingAccumulator = true;
        }
    }
}

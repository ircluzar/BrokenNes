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
        private void ToggleNoSpeedLimit_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.NoSpeedLimit = menuItem.Checked;
                config.Save();
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

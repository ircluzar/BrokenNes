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
        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            // Handle Alt+Enter for fullscreen toggle
            if (e.Alt && e.KeyCode == Keys.Enter)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            
            if (inputManager != null)
            {
                inputManager.OnKeyDown(e.KeyCode);
                e.Handled = true;
            }
        }
        
        private void MainForm_KeyUp(object? sender, KeyEventArgs e)
        {
            if (inputManager != null)
            {
                inputManager.OnKeyUp(e.KeyCode);
                e.Handled = true;
            }
        }
        
        private void OpenControllerConfig(int playerNumber)
        {
            var playerConfig = config.GetPlayerController(playerNumber);
            
            using (var configWindow = new ControllerConfigWindow(playerConfig))
            {
                if (configWindow.ShowDialog(this) == DialogResult.OK)
                {
                    // Save the updated configuration
                    Helpers.ConfigHelper.Save(config);
                    
                    // Reload input mappings for the configured player
                    if (playerNumber == 1)
                    {
                        if (inputManager != null)
                        {
                            inputManager.SetPlayerConfig(playerConfig);
                        }
                    }
                    else if (playerNumber == 2)
                    {
                        if (inputManager2 == null)
                        {
                            inputManager2 = new InputManager(SharpDX.XInput.UserIndex.Two);
                        }
                        inputManager2.SetPlayerConfig(playerConfig);
                    }
                    
                    MessageBox.Show(
                        $"Player {playerNumber} controller configuration saved!",
                        "Configuration Saved",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
        }
    }
}

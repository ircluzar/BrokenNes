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
        // Config menu event handlers
        private void TogglePixelPerfect_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ForcePixelPerfect = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleNativeAspect_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ForceNativeAspectRatio = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleNearestNeighbor_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.ScalingNearestNeighbor = menuItem.Checked;
                config.Save();
                ApplyImageSettings();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleHideMenuBar_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem menuItem)
            {
                config.HideMenuBarInFullscreen = menuItem.Checked;
                config.Save();
                UpdateConfigMenus();
            }
        }
        
        private void ToggleScanlines_Click(object? sender, EventArgs e)
        {
            config.RenderScanlines = !config.RenderScanlines;
            config.Save();
            
            // Apply to DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.RenderScanlines = config.RenderScanlines;
            }
            
            UpdateConfigMenus();
            Console.WriteLine($"Render Scanlines: {config.RenderScanlines}");
        }
        
        private void ToggleViewportShadow_Click(object? sender, EventArgs e)
        {
            config.RenderViewportShadow = !config.RenderViewportShadow;
            config.Save();
            
            // Apply to DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.RenderViewportShadow = config.RenderViewportShadow;
            }
            
            UpdateConfigMenus();
            Console.WriteLine($"Render Viewport Shadow: {config.RenderViewportShadow}");
        }
        
        private void SetBackground(string backgroundName)
        {
            config.SelectedBackground = backgroundName;
            config.Save();
            
            // Apply to DirectX renderer if available
            if (useDirectX && dxRenderer != null)
            {
                dxRenderer.SetBackground(backgroundName);
            }
            
            UpdateConfigMenus();
            
            Console.WriteLine($"Background set to: {backgroundName}");
        }
        
        private void ApplyImageSettings()
        {
            if (dxRenderer != null && useDirectX)
            {
                // Apply pixel perfect setting
                // Force Pixel Perfect if running the embedded Test ROM (Null Provider)
                bool isTestRom = nes != null && string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
                dxRenderer.PixelPerfect = isTestRom || config.ForcePixelPerfect;
                
                // Apply interpolation mode based on ScalingNearestNeighbor
                dxRenderer.InterpolationMode = config.ScalingNearestNeighbor 
                    ? SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor
                    : SharpDX.Direct2D1.BitmapInterpolationMode.Linear;
                
                // Apply aspect ratio setting
                dxRenderer.ForceNativeAspectRatio = config.ForceNativeAspectRatio;
                
                // Apply FPS display setting
                dxRenderer.ShowFps = config.ShowFps;
                
                // Recalculate layout if we're in Widget mode (viewport size changed)
                if (currentViewMode == ViewMode.Widget)
                {
                    SwitchViewMode(ViewMode.Widget, skipNavigation: true);
                }
                
                // Apply background effects settings
                dxRenderer.RenderScanlines = config.RenderScanlines;
                dxRenderer.RenderViewportShadow = config.RenderViewportShadow;
                
                // Force a redraw
                if (frameBuffer != null)
                {
                    dxRenderer.DrawFrame(frameBuffer);
                }
            }
        }
    }
}

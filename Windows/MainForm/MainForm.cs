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
    /// <summary>
    /// View mode options for the emulator display and WebView2 integration
    /// </summary>
    public enum ViewMode
    {
        Emulator,  // Only emulator, no WebView2
        Widget,    // Emulator + WebView2 side by side
        Web,       // Only WebView2, emulator hidden
        Overlay    // WebView2 transparent overlay on top of emulator
    }
    
    /// <summary>
    /// Main form for the BrokenNes emulator.
    /// This class is split into multiple partial class files for better organization:
    /// - MainForm.Fields.cs: Field declarations
    /// - MainForm.Initialization.cs: Constructor and initialization
    /// - MainForm.Config.cs: Configuration management
    /// - MainForm.Cores.cs: Core (CPU/PPU/APU) management
    /// - MainForm.RomLoading.cs: ROM loading operations
    /// - MainForm.SaveStates.cs: Save state management
    /// - MainForm.ViewModes.cs: WebView2 view mode handling
    /// - MainForm.UI.cs: General UI handlers
    /// - MainForm.Display.cs: Display settings
    /// - MainForm.Audio.cs: Audio configuration
    /// - MainForm.Input.cs: Input handling
    /// - MainForm.Emulation.cs: Emulation core loop
    /// - MainForm.Corruption.cs: Corruptor integration
    /// - MainForm.Tools.cs: Tool window launchers
    /// - MainForm.Speed.cs: Speed control
    /// - MainForm.Continue.cs: Continue feature
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>
        /// Gets the current corruptor instance
        /// </summary>
        internal Corruptor Corruptor => corruptor;
        
        /// <summary>
        /// Gets the current Imagine engine instance
        /// </summary>
        internal ImagineEngine? ImagineEngineInstance => imagineEngine;
        
        /// <summary>
        /// Checks if the emulator is ready for operations
        /// </summary>
        internal bool IsEmulatorReady => nes != null;
        
        /// <summary>
        /// Gets the current NES emulator instance
        /// </summary>
        internal NES? CurrentNes => nes;

        /// <summary>
        /// Notifies corruptor state observers of changes
        /// </summary>
        private void NotifyCorruptorChanged()
        {
            try { CorruptorStateChanged?.Invoke(); }
            catch (Exception ex) { Console.WriteLine($"CorruptorStateChanged error: {ex.Message}"); }
        }

        /// <summary>
        /// Publicly raises the corruptor changed event
        /// </summary>
        internal void RaiseCorruptorChangedPublic() => NotifyCorruptorChanged();

        /// <summary>
        /// Gets a snapshot of the current emulator framebuffer.
        /// </summary>
        /// <returns>A Bitmap containing the current frame, or null if not available.</returns>
        public Bitmap? GetScreenshot()
        {
            if (frameBuffer == null) return null;
            return frameBuffer.ToBitmap();
        }

        /// <summary>
        /// Queue an action to be executed on the emulation thread
        /// </summary>
        private void QueueEmuAction(Action action)
        {
            emulationActions.Enqueue(action);
        }

        /// <summary>
        /// Run a function on the emulation thread and return the result asynchronously
        /// </summary>
        internal Task<T> RunOnEmulationThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            QueueEmuAction(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Run an action on the emulation thread asynchronously
        /// </summary>
        internal Task RunOnEmulationThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            QueueEmuAction(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Execute an action with the NES instance on the emulation thread
        /// </summary>
        internal void WithNes(Action<NES> action)
        {
            QueueEmuAction(() =>
            {
                var n = nes;
                if (n == null) return;
                try { action(n); }
                catch (Exception ex) { Console.WriteLine($"WithNes action error: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Called when the form is loaded
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ShowContinueButton();
        }

        /// <summary>
        /// Called when the form is closing - saves state and cleans up resources
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Check if we're on the test ROM
            bool isTestRom = nes != null && string.Equals(nes.RomName, "test.nes", StringComparison.OrdinalIgnoreCase);
            
            if (!isTestRom && nes != null && !string.IsNullOrEmpty(currentRomPath))
            {
                // Auto-save "continue.png" on exit for non-test ROMs
                SaveContinueState();
            }

            StopEmulation();
            
            // Shut down Web API server
            if (webApiServer != null)
            {
                _ = webApiServer.StopAsync();
                webApiServer.Dispose();
            }
            
            audioManager?.Dispose();
            inputManager?.Dispose();
            frameBuffer?.Dispose();
            backBuffer?.Dispose();
            dxRenderer?.Dispose();
            base.OnFormClosing(e);
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace BrokenNes.Windows
{
    public partial class MainForm
    {
        /// <summary>
        /// Get the effective menu height - returns 0 if menu is hidden (fullscreen + HideMenuBarInFullscreen)
        /// </summary>
        private int GetEffectiveMenuHeight()
        {
            if (this.MainMenuStrip != null && this.MainMenuStrip.Visible)
            {
                return this.MainMenuStrip.Height;
            }
            return 0;
        }
        
        /// <summary>
        /// Toggle between fullscreen and windowed mode
        /// </summary>
        private void ToggleFullscreen()
        {
            // Restore menu bar visibility first if we're exiting fullscreen
            if (isFullscreen && this.MainMenuStrip != null)
            {
                this.MainMenuStrip.Visible = true;
            }

            // Use buffer variables to pass by ref since properties can't be passed by ref
            var borderStyle = previousBorderStyle;
            var windowState = previousWindowState;
            var bounds = previousBounds;
            
            isFullscreen = Helpers.FullscreenHelper.ToggleFullscreen(
                this, 
                isFullscreen, 
                Helpers.ConfigHelper.HideMenuBarInFullscreen(config),
                ref borderStyle,
                ref windowState,
                ref bounds);
                
            // Update the fields
            previousBorderStyle = borderStyle;
            previousWindowState = windowState;
            previousBounds = bounds;

            // Hide menu bar if configured and we are now in fullscreen
            if (this.MainMenuStrip != null && Helpers.ConfigHelper.ShouldHideMenuBarNow(config, isFullscreen))
            {
                this.MainMenuStrip.Visible = false;
            }
            
            // Re-apply the current view mode to recalculate layout with new menu visibility
            SwitchViewMode(currentViewMode);
            
            // Force layout and rendering refresh
            this.PerformLayout();
            displayPanel?.PerformLayout();
            dxRenderer?.Invalidate();
            this.Refresh();
        }
        
        /// <summary>
        /// Switch between view modes (Emulator, Widget, Web)
        /// </summary>
        /// <param name="mode">The view mode to switch to</param>
        /// <param name="skipNavigation">If true, don't navigate WebView (useful when caller will navigate)</param>
        private async void SwitchViewMode(ViewMode mode, bool skipNavigation = false)
        {
            // Check availability - only show message if we are trying to use a web mode
            bool isWebMode = (mode == ViewMode.Widget || mode == ViewMode.Overlay || mode == ViewMode.Web);
            if (isWebMode)
            {
               if (!Helpers.WebViewHelper.IsAvailable(webView, isWebViewInitialized)) return;
            }
            else if (webView == null) 
            {
                // If checking only for existence (not initialization) when not in web mode, we might skip the message
                // but original code checked webView == null at the start.
                // However, IsAvailable handles both checks.
                // If we are in Emulator mode, we might not care if it is initialized, but we need webView object to hide it.
                if (webView == null) 
                {
                     MessageBox.Show("WebView2 is not available.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                     return;
                }
            }
            
            currentViewMode = mode;
            
            // Restore menu bar when switching to Emulator mode
            if (mode == ViewMode.Emulator && this.MainMenuStrip != null)
            {
                this.MainMenuStrip.Visible = true;
            }
            
            // Suspend layout during control rearrangement
            this.SuspendLayout();
            
            ApplyViewModeLayout(mode, shouldNavigate: !skipNavigation, log: true);
            
            this.ResumeLayout();
            this.PerformLayout();
            this.Refresh();
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            // Reapply layout when window is resized to maintain proper positioning
            if (webView != null && displayPanel != null)
            {
                ApplyViewModeLayout(currentViewMode, shouldNavigate: false, log: false);
            }
        }

        private void ApplyViewModeLayout(ViewMode mode, bool shouldNavigate, bool log)
        {
            int menuHeight = GetEffectiveMenuHeight();
            int availableHeight = this.ClientSize.Height - menuHeight;

            switch (mode)
            {
                case ViewMode.Emulator:
                    // Only emulator visible, below menu bar
                    if (displayPanel != null)
                    {
                        displayPanel.Visible = true;
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    }

                    if (webView != null) webView.Visible = false;

                    // Center the viewport
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }

                    if (log)
                    {
                        Console.WriteLine("Switched to Emulator mode");
                    }
                    break;

                case ViewMode.Widget:
                    // Widget mode - background renders full width, WebView2 panel on right side
                    // Display panel fills entire area (background visible everywhere)
                    if (displayPanel != null)
                    {
                        displayPanel.Visible = true;
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    }

                    // Calculate NES optimal width for the panel positioning
                    float nesAspectRatio = (float)NES_WIDTH / NES_HEIGHT;
                    int nesWidth = (int)(availableHeight * nesAspectRatio);
                    int maxNesWidth = (int)(this.ClientSize.Width * 0.75f);
                    if (nesWidth > maxNesWidth)
                    {
                        nesWidth = maxNesWidth;
                    }

                    // Align NES viewport flush to the left
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.0f; // Flush left

                        // Get actual viewport width from renderer (calculates on-demand)
                        var viewportRect = dxRenderer.GetViewportRect();
                        int actualViewportWidth = (int)Math.Ceiling(viewportRect.Right);
                        if (actualViewportWidth > 0 && actualViewportWidth < this.ClientSize.Width)
                        {
                            nesWidth = actualViewportWidth;
                        }
                    }

                    // WebView2 starts right after the emulator viewport (no gap needed since we removed ceiling)
                    int webViewXPosition = nesWidth;

                    if (webView != null)
                    {
                        // WebView2 overlays on the right side, flush to the viewport edge
                        Helpers.WebViewHelper.SetLayout(webView,
                            new Point(webViewXPosition, menuHeight),
                            new Size(this.ClientSize.Width - webViewXPosition, availableHeight));

                        // Load transparent HTML content with modal-like panel (unless caller will navigate)
                        if (shouldNavigate)
                        {
                            Helpers.WebViewHelper.NavigateToString(webView, Helpers.HtmlContentHelper.GetWidgetModeHtml());
                        }
                    }

                    if (log)
                    {
                        Console.WriteLine($"Switched to Widget mode - Background full width, WebView panel width: {this.ClientSize.Width - nesWidth}px");
                    }
                    break;

                case ViewMode.Overlay:
                    // Overlay mode - WebView2 transparent on top of emulator
                    if (displayPanel != null)
                    {
                        displayPanel.Visible = true;
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    }

                    // Center the viewport
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }

                    // WebView2 overlays the entire display panel
                    if (webView != null)
                    {
                        Helpers.WebViewHelper.SetLayout(webView,
                            new Point(0, menuHeight),
                            new Size(this.ClientSize.Width, availableHeight));

                        // Load HTML with transparent background and a floating box
                        if (shouldNavigate)
                        {
                            Helpers.WebViewHelper.NavigateToString(webView, Helpers.HtmlContentHelper.GetOverlayModeHtml());
                        }
                    }

                    if (log)
                    {
                        Console.WriteLine("Switched to Overlay mode - Transparent HTML over NES");
                    }
                    break;

                case ViewMode.Web:
                    // Only webview visible, emulator hidden, below menu bar
                    if (displayPanel != null)
                    {
                        displayPanel.Visible = false;
                    }

                    if (webView != null)
                    {
                        Helpers.WebViewHelper.SetLayout(webView,
                            new Point(0, menuHeight),
                            new Size(this.ClientSize.Width, availableHeight));
                    }

                    // Reset viewport alignment (won't be visible anyway)
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }

                    if (log)
                    {
                        Console.WriteLine("Switched to Web mode");
                    }
                    break;
            }
        }
        
        /// <summary>
        /// Loads a web module into the WebView2 control
        /// </summary>
        private void LoadWebModule(WebModuleInfo module)
        {
            if (!Helpers.WebViewHelper.IsAvailable(webView, isWebViewInitialized)) return;
            
            try
            {
                // Get the module URI
                string uri = module.GetVirtualHostUri();
                Console.WriteLine($"[LoadWebModule] Loading module: {module.Name}");
                Console.WriteLine($"[LoadWebModule] URI: {uri}");
                Console.WriteLine($"[LoadWebModule] Display Mode: {module.DisplayMode}");
                Console.WriteLine($"[LoadWebModule] Hide Menu Bar: {module.HideMenuBar}");
                
                // Hide or show menu bar based on module config
                if (this.MainMenuStrip != null)
                {
                    this.MainMenuStrip.Visible = !module.HideMenuBar;
                }
                
                // Switch to appropriate ViewMode based on config FIRST (but don't navigate yet)
                ViewMode targetMode = module.DisplayMode switch
                {
                    WebModuleDisplayMode.Widget => ViewMode.Widget,
                    WebModuleDisplayMode.Overlay => ViewMode.Overlay,
                    WebModuleDisplayMode.Web => ViewMode.Web,
                    _ => ViewMode.Web
                };
                
                Console.WriteLine($"[LoadWebModule] Switching to ViewMode: {targetMode}");
                SwitchViewMode(targetMode, skipNavigation: true);
                
                // NOW navigate to the module (after layout is set up)
                Helpers.WebViewHelper.NavigateToUri(webView, uri);
                Console.WriteLine($"[LoadWebModule] Successfully loaded web module: {module.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoadWebModule] Error: {ex.Message}");
                MessageBox.Show($"Failed to load web module '{module.Name}': {ex.Message}", 
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Loads the Home webmodule
        /// </summary>
        private void LoadHomeWebModule()
        {
            try
            {
                Console.WriteLine("[LoadHomeWebModule] Searching for Home webmodule...");
                var webModules = WebModuleManager.DiscoverModules();
                var homeModule = System.Linq.Enumerable.FirstOrDefault(webModules, m => 
                    string.Equals(m.FolderName, "Home", StringComparison.OrdinalIgnoreCase));
                
                if (homeModule != null && homeModule.IsValid)
                {
                    Console.WriteLine("[LoadHomeWebModule] Found Home webmodule, loading...");
                    LoadWebModule(homeModule);
                }
                else
                {
                    Console.WriteLine("[LoadHomeWebModule] Home webmodule not found or invalid");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoadHomeWebModule] Error: {ex.Message}");
            }
        }
    }
}

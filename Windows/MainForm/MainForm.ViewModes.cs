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
            // Skip navigation to preserve the current WebView content (webmodule or page)
            SwitchViewMode(currentViewMode, skipNavigation: true);
            
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
            if (mode != ViewMode.Emulator)
            {
                overlayPreviewMenuOpenCount = 0;
                isOverlayPreviewVisible = false;
                isOverlayPreloadedForEmulator = false;
            }

            // Check availability - only show message if we are trying to use a web mode
            bool isWebMode = (mode == ViewMode.Widget || mode == ViewMode.Overlay || mode == ViewMode.Web);
            if (isWebMode)
            {
                await EnsureWebApiServerRunningAsync();
                
                // If WebView2 is still initializing, wait up to 3 seconds for it to finish
                if (webView != null && !isWebViewInitialized)
                {
                    Console.WriteLine("[SwitchViewMode] WebView2 still initializing, waiting...");
                    for (int i = 0; i < 30; i++) // 30 * 100ms = 3 seconds max
                    {
                        await Task.Delay(100);
                        if (isWebViewInitialized) break;
                    }
                }
                
                if (!Helpers.WebViewHelper.IsAvailable(webView, isWebViewInitialized, isWebViewInitializationFailed)) return;
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

            if (mode == ViewMode.Emulator && currentViewMode != ViewMode.Emulator)
            {
                UnloadWebModuleContent();
                currentToolOrActivityModule = null; // Clear tracked module
                ClearRuntimeCoreAndShaderOverrides(reapplyPersistedSelections: true);
                
                // Resume emulation if there's a loaded game and it's paused
                bool hasLoadedGame = nes != null && !string.IsNullOrEmpty(currentRomPath);
                bool isTestRom = currentRomPath.Contains("test.nes", StringComparison.OrdinalIgnoreCase);
                
                if (hasLoadedGame && !isTestRom && isPaused && isEmulationRunning)
                {
                    Console.WriteLine("[SwitchViewMode] Resuming paused emulator when switching to Emulator mode");
                    isPaused = false;
                    audioManager?.Play();
                    Console.WriteLine("Emulator Resumed (ViewMode change)");
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

            if (mode == ViewMode.Emulator)
            {
                _ = EnsureOverlayPreloadedForEmulatorAsync();
            }
        }

        /// <summary>
        /// Unloads any active webmodule content so it does not remain in memory
        /// when switching to emulator-only mode.
        /// </summary>
        private void UnloadWebModuleContent()
        {
            if (webView?.CoreWebView2 == null) return;

            try
            {
                webView.CoreWebView2.Stop();
                webView.CoreWebView2.Navigate("about:blank");
                isOverlayPreloadedForEmulator = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnloadWebModuleContent] Error: {ex.Message}");
            }
        }

        private WebModuleInfo? FindOverlayWebModule()
        {
            var webModules = WebModuleManager.DiscoverModules();
            return System.Linq.Enumerable.FirstOrDefault(webModules, m =>
                string.Equals(m.FolderName, "Overlay", StringComparison.OrdinalIgnoreCase));
        }

        private async Task EnsureOverlayPreloadedForEmulatorAsync()
        {
            if (currentViewMode != ViewMode.Emulator || webView == null || !isWebViewInitialized)
            {
                return;
            }

            if (isOverlayPreloadedForEmulator)
            {
                return;
            }

            try
            {
                await EnsureWebApiServerRunningAsync();

                var overlayModule = FindOverlayWebModule();
                if (overlayModule == null || !overlayModule.IsValid)
                {
                    return;
                }

                webView.DefaultBackgroundColor = Color.Transparent;
                Helpers.WebViewHelper.NavigateToUri(webView, overlayModule.GetVirtualHostUri());
                isOverlayPreloadedForEmulator = true;
                isOverlayPreviewVisible = false;

                int menuHeight = GetEffectiveMenuHeight();
                int availableHeight = this.ClientSize.Height - menuHeight;
                Helpers.WebViewHelper.SetLayout(webView,
                    new Point(0, menuHeight),
                    new Size(this.ClientSize.Width, availableHeight));
                webView.Visible = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OverlayPreview] Preload failed: {ex.Message}");
                isOverlayPreloadedForEmulator = false;
            }
        }

        private async Task<bool> EnsureOverlayPreviewVisibleAsync()
        {
            if (currentViewMode != ViewMode.Emulator || webView == null || webView.CoreWebView2 == null)
            {
                return false;
            }

            await EnsureOverlayPreloadedForEmulatorAsync();
            if (!isOverlayPreloadedForEmulator)
            {
                return false;
            }

            int menuHeight = GetEffectiveMenuHeight();
            int availableHeight = this.ClientSize.Height - menuHeight;
            webView.DefaultBackgroundColor = Color.Transparent;
            Helpers.WebViewHelper.SetLayout(webView,
                new Point(0, menuHeight),
                new Size(this.ClientSize.Width, availableHeight));
            webView.Visible = true;
            isOverlayPreviewVisible = true;
            return true;
        }

        private async Task HideOverlayPreviewAsync()
        {
            if (currentViewMode != ViewMode.Emulator || !isOverlayPreviewVisible || webView == null)
            {
                return;
            }

            try
            {
                if (webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync("if (typeof window.clearCard === 'function') { window.clearCard(); }");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OverlayPreview] Clear failed: {ex.Message}");
            }

            webView.Visible = false;
            isOverlayPreviewVisible = false;
        }

        private async void BeginOverlayPreviewForMenu(object? sender, EventArgs e)
        {
            if (currentViewMode != ViewMode.Emulator)
            {
                return;
            }

            overlayPreviewMenuOpenCount++;
            if (overlayPreviewMenuOpenCount == 1)
            {
                await EnsureOverlayPreviewVisibleAsync();
            }
        }

        private async void EndOverlayPreviewForMenu(object? sender, EventArgs e)
        {
            RequestOverlayClearCard();

            if (currentViewMode != ViewMode.Emulator)
            {
                overlayPreviewMenuOpenCount = 0;
                return;
            }

            if (overlayPreviewMenuOpenCount > 0)
            {
                overlayPreviewMenuOpenCount--;
            }

            if (overlayPreviewMenuOpenCount == 0)
            {
                await HideOverlayPreviewAsync();
            }
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

                    if (webView != null)
                    {
                        if (isOverlayPreviewVisible)
                        {
                            Helpers.WebViewHelper.SetLayout(webView,
                                new Point(0, menuHeight),
                                new Size(this.ClientSize.Width, availableHeight));
                        }
                        webView.Visible = isOverlayPreviewVisible;
                    }

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
                        // Ensure transparency is enabled
                        webView.DefaultBackgroundColor = Color.Transparent;

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
        private async void LoadWebModule(WebModuleInfo module)
        {
            if (!Helpers.WebViewHelper.IsAvailable(webView, isWebViewInitialized, isWebViewInitializationFailed)) return;
            if (!IsWebModuleUnlocked(module))
            {
                var targetModuleName = !string.IsNullOrWhiteSpace(module.Config.LoadModule)
                    ? module.Config.LoadModule
                    : module.FolderName;
                Console.WriteLine($"[Progression] Blocked locked webmodule launch: {targetModuleName}");
                MessageBox.Show(
                    $"{module.Name} is still locked in progression.",
                    "Module Locked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            
            try
            {
                await EnsureWebApiServerRunningAsync();

                // Get the module URI
                string uri = module.GetVirtualHostUri();
                Console.WriteLine($"[LoadWebModule] Loading module: {module.Name}");
                Console.WriteLine($"[LoadWebModule] URI: {uri}");
                Console.WriteLine($"[LoadWebModule] Display Mode: {module.DisplayMode}");
                Console.WriteLine($"[LoadWebModule] Hide Menu Bar: {module.HideMenuBar}");
                Console.WriteLine($"[LoadWebModule] Pause Emulator On Open: {module.Config.PauseEmulatorOnOpen}");
                
                // Pause emulator if configured and conditions are met
                if (module.Config.PauseEmulatorOnOpen)
                {
                    // Check if there's a loaded game (not test ROM) and it's currently running
                    bool hasLoadedGame = nes != null && !string.IsNullOrEmpty(currentRomPath);
                    bool isTestRom = currentRomPath.Contains("test.nes", StringComparison.OrdinalIgnoreCase);
                    bool isCurrentlyRunning = isEmulationRunning && !isPaused;
                    
                    if (hasLoadedGame && !isTestRom && isCurrentlyRunning)
                    {
                        Console.WriteLine($"[LoadWebModule] Pausing emulator as configured for module: {module.Name}");
                        isPaused = true;
                        audioManager?.Stop();
                        Console.WriteLine("Emulator Paused (ViewMode change)");
                    }
                }
                
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
                
                // Track if this is a tool or activity module
                if (module.Config.ShowInToolsMenu)
                {
                    currentToolOrActivityModule = module;
                }
                
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
        
        /// <summary>
        /// Requests the overlay to display a card for the specified core
        /// </summary>
        /// <param name="domain">Core domain (cpu, ppu, apu, shader)</param>
        /// <param name="coreId">The core ID to display</param>
        private async void RequestOverlayDisplayCard(string domain, string coreId)
        {
            bool isDirectOverlayMode = currentViewMode == ViewMode.Overlay;
            bool isEmulatorPreviewMode = currentViewMode == ViewMode.Emulator;
            if (!isDirectOverlayMode && !isEmulatorPreviewMode)
            {
                return;
            }

            if (isEmulatorPreviewMode)
            {
                var activated = await EnsureOverlayPreviewVisibleAsync();
                if (!activated)
                {
                    return;
                }
            }

            if (webView?.CoreWebView2 == null)
                return;
                
            try
            {
                // Call the JavaScript displayCard function exposed by overlay.js
                string safeDomain = domain.Replace("\\", "\\\\").Replace("'", "\\'");
                string safeCoreId = coreId.Replace("\\", "\\\\").Replace("'", "\\'");
                string script = $"if (typeof window.displayCard === 'function') {{ window.displayCard('{safeDomain}', '{safeCoreId}'); }}";
                await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to request overlay card display: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Requests the overlay to clear the displayed card
        /// </summary>
        private async void RequestOverlayClearCard()
        {
            bool isDirectOverlayMode = currentViewMode == ViewMode.Overlay;
            bool isEmulatorPreviewMode = currentViewMode == ViewMode.Emulator && isOverlayPreviewVisible;
            if (!isDirectOverlayMode && !isEmulatorPreviewMode)
            {
                return;
            }

            if (webView?.CoreWebView2 == null)
                return;
                
            try
            {
                // Call the JavaScript clearCard function exposed by overlay.js
                string script = "if (typeof window.clearCard === 'function') { window.clearCard(); }";
                await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to request overlay card clear: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Closes all open menus in the menu strip
        /// </summary>
        private void CloseAllMenus()
        {
            if (this.MainMenuStrip == null)
                return;
                
            try
            {
                // Close all drop-down menus by iterating through top-level items
                foreach (ToolStripMenuItem item in this.MainMenuStrip.Items.OfType<ToolStripMenuItem>())
                {
                    if (item.DropDown.Visible)
                    {
                        item.HideDropDown();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to close menus: {ex.Message}");
            }
        }

        /// <summary>
        /// Hides the top menu bar
        /// </summary>
        private void HideMenu()
        {
            if (this.MainMenuStrip != null)
            {
                this.MainMenuStrip.Visible = false;
                Console.WriteLine("[MainForm] Menu bar hidden");
                
                // Re-apply the current view mode to recalculate layout with new menu visibility
                // Skip navigation to preserve the current WebView content (webmodule or page)
                SwitchViewMode(currentViewMode, skipNavigation: true);
                
                // Force layout and rendering refresh
                this.PerformLayout();
                displayPanel?.PerformLayout();
                dxRenderer?.Invalidate();
                this.Refresh();
            }
        }

        /// <summary>
        /// Shows the top menu bar
        /// </summary>
        private void ShowMenu()
        {
            if (this.MainMenuStrip != null)
            {
                this.MainMenuStrip.Visible = true;
                Console.WriteLine("[MainForm] Menu bar shown");
                
                // Re-apply the current view mode to recalculate layout with new menu visibility
                // Skip navigation to preserve the current WebView content (webmodule or page)
                SwitchViewMode(currentViewMode, skipNavigation: true);
                
                // Force layout and rendering refresh
                this.PerformLayout();
                displayPanel?.PerformLayout();
                dxRenderer?.Invalidate();
                this.Refresh();
            }
        }
    }
}

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
            if (isFullscreen)
            {
                // Restore menu bar visibility first
                if (this.MainMenuStrip != null)
                {
                    this.MainMenuStrip.Visible = true;
                }
                
                // Exit fullscreen
                this.FormBorderStyle = previousBorderStyle;
                this.WindowState = previousWindowState;
                this.Bounds = previousBounds;
                isFullscreen = false;
            }
            else
            {
                // Enter fullscreen
                previousBorderStyle = this.FormBorderStyle;
                previousWindowState = this.WindowState;
                previousBounds = this.Bounds;
                
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Normal;
                this.Bounds = Screen.FromControl(this).Bounds;
                isFullscreen = true;
                
                // Hide menu bar if configured
                if (this.MainMenuStrip != null && config.HideMenuBarInFullscreen)
                {
                    this.MainMenuStrip.Visible = false;
                }
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
        private async void SwitchViewMode(ViewMode mode)
        {
            if (webView == null)
            {
                MessageBox.Show("WebView2 is not available.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            // Wait for WebView2 to be initialized if switching to Widget, Overlay or Web mode
            if ((mode == ViewMode.Widget || mode == ViewMode.Overlay || mode == ViewMode.Web) && !isWebViewInitialized)
            {
                MessageBox.Show("WebView2 is still initializing. Please try again in a moment.", 
                    "Please Wait", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            currentViewMode = mode;
            
            // Suspend layout during control rearrangement
            this.SuspendLayout();
            
            int menuHeight = GetEffectiveMenuHeight();
            int availableHeight = this.ClientSize.Height - menuHeight;
            
            switch (mode)
            {
                case ViewMode.Emulator:
                    // Only emulator visible, below menu bar
                    displayPanel.Visible = true;
                    displayPanel.Location = new Point(0, menuHeight);
                    displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    webView.Visible = false;
                    
                    // Center the viewport
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }
                    
                    Console.WriteLine("Switched to Emulator mode");
                    break;
                    
                case ViewMode.Widget:
                    // Widget mode - background renders full width, WebView2 panel on right side
                    // Display panel fills entire area (background visible everywhere)
                    displayPanel.Visible = true;
                    displayPanel.Location = new Point(0, menuHeight);
                    displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    
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
                    
                    // WebView2 overlays on the right side, flush to the viewport edge
                    webView.Visible = true;
                    webView.Location = new Point(nesWidth, menuHeight);
                    webView.Size = new Size(this.ClientSize.Width - nesWidth, availableHeight);
                    webView.BringToFront();
                    
                    // Load transparent HTML content with modal-like panel
                    if (isWebViewInitialized && webView.CoreWebView2 != null)
                    {
                        string htmlContent = $@"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                body {{
                                    margin: 0;
                                    padding: 20px;
                                    background: transparent;
                                    font-family: 'Segoe UI', Arial, sans-serif;
                                    color: white;
                                    overflow: hidden;
                                    display: flex;
                                    align-items: stretch;
                                    height: calc(100vh - 40px);
                                    box-sizing: border-box;
                                }}
                                .widget-panel {{
                                    flex: 1;
                                    background: rgba(20, 20, 30, 0.85);
                                    backdrop-filter: blur(10px);
                                    display: flex;
                                    justify-content: center;
                                    align-items: center;
                                    flex-direction: column;
                                    border-radius: 16px;
                                    border: 2px solid rgba(255, 255, 255, 0.1);
                                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
                                }}
                                .widget-content {{
                                    text-align: center;
                                    padding: 30px;
                                }}
                                h1 {{
                                    font-size: 32px;
                                    margin-bottom: 15px;
                                    font-weight: 600;
                                }}
                                p {{
                                    font-size: 16px;
                                    opacity: 0.8;
                                    line-height: 1.6;
                                }}
                            </style>
                        </head>
                        <body>
                            <div class='widget-panel'>
                                <div class='widget-content'>
                                    <h1>Widget Panel</h1>
                                    <p>Background renders underneath<br/>with transparent HTML overlay</p>
                                </div>
                            </div>
                        </body>
                        </html>";
                        
                        webView.CoreWebView2.NavigateToString(htmlContent);
                    }
                    Console.WriteLine($"Switched to Widget mode - Background full width, WebView panel width: {this.ClientSize.Width - nesWidth}px");
                    break;
                    
                case ViewMode.Overlay:
                    // Overlay mode - WebView2 transparent on top of emulator
                    displayPanel.Visible = true;
                    displayPanel.Location = new Point(0, menuHeight);
                    displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                    
                    // Center the viewport
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }
                    
                    // WebView2 overlays the entire display panel
                    webView.Visible = true;
                    webView.Location = new Point(0, menuHeight);
                    webView.Size = new Size(this.ClientSize.Width, availableHeight);
                    webView.BringToFront(); // Ensure WebView2 is on top
                    
                    // Load HTML with transparent background and a floating box
                    if (isWebViewInitialized && webView.CoreWebView2 != null)
                    {
                        string htmlContent = @"
                        <!DOCTYPE html>
                        <html>
                        <head>
                            <style>
                                body {
                                    margin: 0;
                                    padding: 0;
                                    background: transparent;
                                    font-family: 'Segoe UI', Arial, sans-serif;
                                }
                                .floating-box {
                                    position: absolute;
                                    top: 50%;
                                    left: 50%;
                                    transform: translate(-50%, -50%);
                                    background: rgba(30, 144, 255, 0.9);
                                    color: white;
                                    padding: 30px 50px;
                                    border-radius: 15px;
                                    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
                                    text-align: center;
                                    font-size: 24px;
                                    font-weight: bold;
                                    backdrop-filter: blur(10px);
                                    border: 2px solid rgba(255, 255, 255, 0.3);
                                }
                                .subtitle {
                                    font-size: 14px;
                                    margin-top: 10px;
                                    opacity: 0.9;
                                    font-weight: normal;
                                }
                            </style>
                        </head>
                        <body>
                            <div class='floating-box'>
                                HTML Overlay
                                <div class='subtitle'>Floating over DirectX render</div>
                            </div>
                        </body>
                        </html>";
                        
                        webView.CoreWebView2.NavigateToString(htmlContent);
                    }
                    Console.WriteLine($"Switched to Overlay mode - Transparent HTML over NES");
                    break;
                    
                case ViewMode.Web:
                    // Only webview visible, emulator hidden, below menu bar
                    displayPanel.Visible = false;
                    webView.Visible = true;
                    webView.Location = new Point(0, menuHeight);
                    webView.Size = new Size(this.ClientSize.Width, availableHeight);
                    
                    // Reset viewport alignment (won't be visible anyway)
                    if (useDirectX && dxRenderer != null)
                    {
                        dxRenderer.ViewportAlignmentX = 0.5f;
                    }
                    
                    // Load Google for testing
                    if (isWebViewInitialized && webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.Navigate("https://www.google.com");
                    }
                    Console.WriteLine("Switched to Web mode");
                    break;
            }
            
            this.ResumeLayout();
            this.PerformLayout();
            this.Refresh();
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            // Reapply layout when window is resized to maintain proper positioning
            if (webView != null && displayPanel != null)
            {
                int menuHeight = GetEffectiveMenuHeight();
                int availableHeight = this.ClientSize.Height - menuHeight;
                
                switch (currentViewMode)
                {
                    case ViewMode.Emulator:
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.5f;
                        }
                        break;
                        
                    case ViewMode.Widget:
                        // Background renders full width, WebView panel overlays on right
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        // Calculate NES width for panel positioning
                        float nesAspectRatio = (float)NES_WIDTH / NES_HEIGHT;
                        int nesWidth = (int)(availableHeight * nesAspectRatio);
                        int maxNesWidth = (int)(this.ClientSize.Width * 0.75f);
                        if (nesWidth > maxNesWidth)
                        {
                            nesWidth = maxNesWidth;
                        }
                        
                        // Align viewport flush to left side
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.0f; // Flush left
                            
                            // Get actual viewport width from renderer
                            var viewportRect = dxRenderer.GetViewportRect();
                            int actualViewportWidth = (int)Math.Ceiling(viewportRect.Right);
                            if (actualViewportWidth > 0 && actualViewportWidth < this.ClientSize.Width)
                            {
                                nesWidth = actualViewportWidth;
                            }
                        }
                        
                        webView.Location = new Point(nesWidth, menuHeight);
                        webView.Size = new Size(this.ClientSize.Width - nesWidth, availableHeight);
                        webView.BringToFront();
                        break;
                        
                    case ViewMode.Overlay:
                        // Overlay mode - both occupy the same space
                        displayPanel.Location = new Point(0, menuHeight);
                        displayPanel.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.5f;
                        }
                        
                        webView.Location = new Point(0, menuHeight);
                        webView.Size = new Size(this.ClientSize.Width, availableHeight);
                        webView.BringToFront();
                        break;
                        
                    case ViewMode.Web:
                        webView.Location = new Point(0, menuHeight);
                        webView.Size = new Size(this.ClientSize.Width, availableHeight);
                        
                        if (useDirectX && dxRenderer != null)
                        {
                            dxRenderer.ViewportAlignmentX = 0.5f;
                        }
                        break;
                }
            }
        }
    }
}

using System;
using System.Windows.Forms;
using BrokenNes.Windows.Rendering;
using NesEmulator;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Example demonstrating how to use the DirectX rendering library for NES emulation.
    /// This shows the basic setup and common usage patterns.
    /// </summary>
    public class DirectXRendererExample
    {
        /// <summary>
        /// Example: Basic setup of the DirectX renderer
        /// </summary>
        public static void BasicSetupExample()
        {
            // Create a Windows Form
            var form = new Form
            {
                Text = "NES DirectX Renderer Example",
                Size = new System.Drawing.Size(800, 600),
                StartPosition = FormStartPosition.CenterScreen
            };

            // Create the DirectX renderer control
            var renderer = new NesDirectXRenderer
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.Black
            };

            form.Controls.Add(renderer);

            // Initialize the renderer when the form loads
            form.Load += (s, e) =>
            {
                // Initialize with NES dimensions (256x240)
                renderer.Initialize(256, 240);

                // Initialize shader control
                NesShaderControl.Initialize(renderer);

                // Optional: Set initial shader
                renderer.SwitchShader(NesShaderManager.ShaderType.BLD);
                renderer.ShaderStrength = 2.0f;
            };

            // Create a framebuffer for rendering
            var frameBuffer = new DirectBitmap(256, 240);

            // Setup a timer for rendering frames (60 FPS)
            var timer = new System.Windows.Forms.Timer { Interval = 16 };
            timer.Tick += (s, e) =>
            {
                // Your NES emulator would provide this data
                // For demo, create a test pattern
                GenerateTestPattern(frameBuffer);

                // Render the frame
                if (renderer.IsReady)
                {
                    renderer.DrawFrame(frameBuffer);
                }
            };

            timer.Start();

            // Cleanup
            form.FormClosing += (s, e) =>
            {
                timer.Stop();
                frameBuffer.Dispose();
                renderer.Dispose();
            };

            Application.Run(form);
        }

        /// <summary>
        /// Example: Shader control from keyboard input
        /// </summary>
        public static void ShaderControlExample(Form form, NesDirectXRenderer renderer)
        {
            form.KeyDown += (s, e) =>
            {
                switch (e.KeyCode)
                {
                    case Keys.F1:
                        // Toggle shaders on/off
                        bool enabled = NesShaderControl.ToggleShaders();
                        Console.WriteLine($"Shaders: {(enabled ? "ON" : "OFF")}");
                        break;

                    case Keys.F2:
                        // Cycle to next shader
                        string nextShader = NesShaderControl.CycleToNextShader();
                        Console.WriteLine($"Switched to: {nextShader}");
                        break;

                    case Keys.F3:
                        // Cycle to previous shader
                        string prevShader = NesShaderControl.CycleToPreviousShader();
                        Console.WriteLine($"Switched to: {prevShader}");
                        break;

                    case Keys.Oemplus:
                    case Keys.Add:
                        // Increase shader strength
                        float currentStrength = NesShaderControl.GetShaderStrength();
                        NesShaderControl.SetShaderStrength(Math.Min(currentStrength + 0.25f, 3.0f));
                        Console.WriteLine($"Shader strength: {NesShaderControl.GetShaderStrength():F2}");
                        break;

                    case Keys.OemMinus:
                    case Keys.Subtract:
                        // Decrease shader strength
                        currentStrength = NesShaderControl.GetShaderStrength();
                        NesShaderControl.SetShaderStrength(Math.Max(currentStrength - 0.25f, 0.5f));
                        Console.WriteLine($"Shader strength: {NesShaderControl.GetShaderStrength():F2}");
                        break;

                    case Keys.D1:
                        NesShaderControl.SwitchShader(NesShaderManager.ShaderType.RF);
                        break;
                    case Keys.D2:
                        NesShaderControl.SwitchShader(NesShaderManager.ShaderType.BLD);
                        break;
                    case Keys.D3:
                        NesShaderControl.SwitchShader(NesShaderManager.ShaderType.VHS);
                        break;
                    case Keys.D4:
                        NesShaderControl.SwitchShader(NesShaderManager.ShaderType.TV);
                        break;
                    case Keys.D5:
                        NesShaderControl.SwitchShader(NesShaderManager.ShaderType.RGBX);
                        break;
                }
            };
        }

        /// <summary>
        /// Example: Creating a shader menu
        /// </summary>
        public static void CreateShaderMenu(MenuStrip menuStrip, NesDirectXRenderer renderer)
        {
            var shaderMenu = new ToolStripMenuItem("&Shaders");

            // Add enable/disable toggle
            var toggleItem = new ToolStripMenuItem("Enable Shaders", null, (s, e) =>
            {
                renderer.UseShader = !renderer.UseShader;
                ((ToolStripMenuItem)s).Checked = renderer.UseShader;
            })
            {
                Checked = true
            };
            shaderMenu.DropDownItems.Add(toggleItem);
            shaderMenu.DropDownItems.Add(new ToolStripSeparator());

            // Add all available shaders
            foreach (var shaderName in NesDirectXRenderer.GetAvailableShaders())
            {
                var shaderType = (NesShaderManager.ShaderType)Enum.Parse(typeof(NesShaderManager.ShaderType), shaderName);
                var info = NesShaderControl.GetShaderInfo(shaderType);

                var item = new ToolStripMenuItem(info.DisplayName, null, (s, e) =>
                {
                    NesShaderControl.SwitchShader(shaderName);
                })
                {
                    ToolTipText = info.Description
                };
                shaderMenu.DropDownItems.Add(item);
            }

            shaderMenu.DropDownItems.Add(new ToolStripSeparator());

            // Add strength submenu
            var strengthMenu = new ToolStripMenuItem("Strength");
            foreach (var strength in new[] { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f })
            {
                var strengthItem = new ToolStripMenuItem($"{strength:F1}x", null, (s, e) =>
                {
                    NesShaderControl.SetShaderStrength(strength);
                });
                strengthMenu.DropDownItems.Add(strengthItem);
            }
            shaderMenu.DropDownItems.Add(strengthMenu);

            menuStrip.Items.Add(shaderMenu);
        }

        /// <summary>
        /// Example: Integrating with NES emulator
        /// </summary>
        public static void NesIntegrationExample(NES nes, DirectBitmap frameBuffer, NesDirectXRenderer renderer)
        {
            // This is typically called in a timer tick event (60 FPS)

            // Run one frame of the NES emulator
            nes.RunFrame();

            // Get the framebuffer from the emulator
            byte[]? nesOutput = nes.GetFrameBuffer();

            if (nesOutput != null && nesOutput.Length == 256 * 240 * 4)
            {
                // Copy NES output to DirectBitmap
                frameBuffer.CopyFromBytes(nesOutput);

                // Render with DirectX (with or without shaders)
                if (renderer.IsReady)
                {
                    renderer.DrawFrame(frameBuffer);
                }
            }
        }

        /// <summary>
        /// Generate a test pattern for demonstration
        /// </summary>
        private static void GenerateTestPattern(DirectBitmap bitmap)
        {
            int time = Environment.TickCount / 16;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    // Create a colorful animated test pattern
                    int r = (x * 255 / bitmap.Width);
                    int g = (y * 255 / bitmap.Height);
                    int b = ((x + y + time) * 255 / (bitmap.Width + bitmap.Height)) % 255;
                    int a = 255;

                    int color = (a << 24) | (r << 16) | (g << 8) | b;
                    bitmap.SetPixel(x, y, color);
                }
            }
        }

        /// <summary>
        /// Example: Performance monitoring
        /// </summary>
        public static void PerformanceMonitoringExample()
        {
            var frameCount = 0;
            var fpsTimer = new System.Diagnostics.Stopwatch();
            fpsTimer.Start();

            // In your render loop:
            // After each frame:
            frameCount++;

            if (fpsTimer.ElapsedMilliseconds >= 1000)
            {
                double fps = frameCount / (fpsTimer.ElapsedMilliseconds / 1000.0);
                Console.WriteLine($"FPS: {fps:F2}");

                frameCount = 0;
                fpsTimer.Restart();
            }
        }
    }
}

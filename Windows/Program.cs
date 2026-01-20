using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BrokenNes.Windows
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();
        
        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();
        
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();
        
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        
        /// <summary>
        /// Set console window visibility
        /// </summary>
        public static void SetConsoleVisibility(bool visible)
        {
            IntPtr consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
            {
                ShowWindow(consoleWindow, visible ? SW_SHOW : SW_HIDE);
            }
        }
        
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Allocate a console for debug output
            AllocConsole();
            Console.WriteLine("BrokenNes Windows Starting...");
            
            // Load config and apply console visibility
            var config = BrokenNes.Windows.EmulatorConfig.Load();
            SetConsoleVisibility(config.ShowConsole);
            
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                
                // Add global exception handler
                Application.ThreadException += (sender, e) =>
                {
                    Console.WriteLine($"Thread Exception: {e.Exception}");
                    MessageBox.Show($"Application Error:\n\n{e.Exception.Message}\n\nStack Trace:\n{e.Exception.StackTrace}",
                        "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                {
                    var ex = e.ExceptionObject as Exception;
                    Console.WriteLine($"Unhandled Exception: {ex}");
                    MessageBox.Show($"Fatal Error:\n\n{ex?.Message ?? e.ExceptionObject?.ToString()}\n\nStack Trace:\n{ex?.StackTrace}",
                        "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                
                Console.WriteLine("Creating MainForm...");
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Main Exception: {ex}");
                MessageBox.Show($"Failed to start application:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}\n\nInner Exception:\n{ex.InnerException}",
                    "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();

            }
            

        }
    }
}

using System;
using System.IO;
using System.Text.Json;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Display mode for web modules - maps to ViewMode enum
    /// </summary>
    public enum WebModuleDisplayMode
    {
        /// <summary>Full screen mode - only WebView2</summary>
        Web,
        /// <summary>Side-by-side mode - emulator + WebView2</summary>
        Widget,
        /// <summary>Transparent overlay on top of emulator</summary>
        Overlay
    }

    /// <summary>
    /// Module configuration from config.json
    /// </summary>
    public class WebModuleConfig
    {
        public string DisplayMode { get; set; } = "web";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "";
        public bool ShowInToolsMenu { get; set; } = false;
    }

    /// <summary>
    /// Represents metadata about a web module available for loading
    /// </summary>
    public class WebModuleInfo
    {
        /// <summary>
        /// Gets the display name of the web module
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the folder name of the web module
        /// </summary>
        public string FolderName { get; }

        /// <summary>
        /// Gets the full path to the module's index.html file
        /// </summary>
        public string IndexPath { get; }

        /// <summary>
        /// Gets the full path to the module's directory
        /// </summary>
        public string DirectoryPath { get; }

        /// <summary>
        /// Gets a value indicating whether the module is valid and can be loaded
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// Gets the display mode for this module
        /// </summary>
        public WebModuleDisplayMode DisplayMode { get; }

        /// <summary>
        /// Gets the module configuration
        /// </summary>
        public WebModuleConfig Config { get; }

        public WebModuleInfo(string directoryPath)
        {
            DirectoryPath = directoryPath;
            FolderName = Path.GetFileName(directoryPath);
            IndexPath = Path.Combine(directoryPath, "index.html");
            IsValid = File.Exists(IndexPath);

            // Load config.json if it exists
            string configPath = Path.Combine(directoryPath, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    Config = JsonSerializer.Deserialize<WebModuleConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new WebModuleConfig();
                    
                    // Use title from config if available, otherwise use folder name
                    Name = !string.IsNullOrWhiteSpace(Config.Title) ? Config.Title : FolderName;
                    
                    // Parse display mode
                    DisplayMode = Config.DisplayMode?.ToLower() switch
                    {
                        "widget" => WebModuleDisplayMode.Widget,
                        "overlay" => WebModuleDisplayMode.Overlay,
                        _ => WebModuleDisplayMode.Web
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebModuleInfo] Failed to load config for {FolderName}: {ex.Message}");
                    Config = new WebModuleConfig();
                    Name = FolderName;
                    DisplayMode = WebModuleDisplayMode.Web;
                }
            }
            else
            {
                Config = new WebModuleConfig();
                Name = FolderName;
                DisplayMode = WebModuleDisplayMode.Web;
            }
        }

        /// <summary>
        /// Gets the HTTPS URI for loading in WebView2 using shared virtual host mapping
        /// All modules share the same domain (app.brokennes) so they can communicate via localStorage
        /// </summary>
        public string GetVirtualHostUri()
        {
            return $"https://{WebModuleManager.SharedVirtualHostName}/{FolderName}/index.html";
        }

        /// <summary>
        /// Gets the file URI for loading in WebView2 (legacy, prefer GetVirtualHostUri)
        /// </summary>
        public string GetFileUri()
        {
            return new Uri(IndexPath).AbsoluteUri;
        }
    }

    /// <summary>
    /// Helper class for discovering and managing web modules
    /// </summary>
    public static class WebModuleManager
    {
        /// <summary>
        /// Shared virtual host name for all web modules
        /// </summary>
        public const string SharedVirtualHostName = "app.brokennes";

        private static readonly string WebModulesPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, 
            "Webmodules"
        );

        /// <summary>
        /// Discovers all available web modules in the Webmodules directory
        /// </summary>
        /// <returns>An array of WebModuleInfo for each valid module found</returns>
        public static WebModuleInfo[] DiscoverModules()
        {
            var modules = new System.Collections.Generic.List<WebModuleInfo>();

            Console.WriteLine($"[WebModuleManager] Searching for modules in: {WebModulesPath}");

            if (!Directory.Exists(WebModulesPath))
            {
                // Create the directory if it doesn't exist
                try
                {
                    Console.WriteLine($"[WebModuleManager] Creating Webmodules directory...");
                    Directory.CreateDirectory(WebModulesPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebModuleManager] Failed to create Webmodules directory: {ex.Message}");
                    return modules.ToArray();
                }
            }

            try
            {
                var directories = Directory.GetDirectories(WebModulesPath);
                Console.WriteLine($"[WebModuleManager] Found {directories.Length} subdirectories");
                
                foreach (var dir in directories)
                {
                    var moduleInfo = new WebModuleInfo(dir);
                    Console.WriteLine($"[WebModuleManager] Checking module: {moduleInfo.FolderName}");
                    Console.WriteLine($"[WebModuleManager]   Index path: {moduleInfo.IndexPath}");
                    Console.WriteLine($"[WebModuleManager]   Valid: {moduleInfo.IsValid}");
                    
                    if (moduleInfo.IsValid)
                    {
                        modules.Add(moduleInfo);
                        Console.WriteLine($"[WebModuleManager] Added module: {moduleInfo.Name}");
                    }
                    else
                    {
                        Console.WriteLine($"[WebModuleManager] Skipping invalid web module: {moduleInfo.FolderName} (no index.html found)");
                    }
                }
                
                Console.WriteLine($"[WebModuleManager] Total valid modules: {modules.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebModuleManager] Error discovering web modules: {ex.Message}");
            }

            return modules.ToArray();
        }

        /// <summary>
        /// Gets the full path to the Webmodules directory
        /// </summary>
        public static string GetWebModulesDirectory() => WebModulesPath;
    }
}

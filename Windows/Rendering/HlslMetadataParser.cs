using System;
using System.Collections.Generic;
using System.IO;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Parses metadata from HLSL shader file comments.
    /// Metadata format follows the same convention as GLSL shaders:
    /// // DisplayName: Shader Name
    /// // CoreName: Full Shader Name
    /// // Description: A description of the shader effect.
    /// // Performance: -15
    /// // Rating: 4
    /// // Category: Retro
    /// </summary>
    public static class HlslMetadataParser
    {
        /// <summary>
        /// Parsed shader metadata
        /// </summary>
        public class ShaderMetadata
        {
            public string? DisplayName { get; set; }
            public string? CoreName { get; set; }
            public string? Description { get; set; }
            public int Performance { get; set; }
            public int Rating { get; set; }
            public string? Category { get; set; }
        }

        // Cache to avoid re-reading files
        private static readonly Dictionary<string, ShaderMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new();

        /// <summary>
        /// Parse metadata from HLSL shader file content.
        /// </summary>
        public static ShaderMetadata ParseFromSource(string source)
        {
            var result = new ShaderMetadata();
            
            if (string.IsNullOrWhiteSpace(source))
                return result;

            using var reader = new StringReader(source);
            string? line;
            bool inBlock = false;

            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.TrimStart();
                
                // Handle block comments
                if (!inBlock && trimmed.StartsWith("/*"))
                {
                    inBlock = true;
                }
                
                // Stop at first non-comment line when not in a block comment
                if (!(trimmed.StartsWith("//") || inBlock))
                {
                    break;
                }

                // Normalize comment lines
                string work = trimmed;
                if (inBlock)
                {
                    if (work.StartsWith("/*")) work = work.Substring(2);
                    if (work.StartsWith("*")) work = work.Substring(1);
                    if (work.Contains("*/"))
                    {
                        var endIdx = work.IndexOf("*/", StringComparison.Ordinal);
                        work = work.Substring(0, endIdx);
                        inBlock = false;
                    }
                }
                else if (work.StartsWith("//"))
                {
                    work = work.Substring(2);
                }

                // Parse key: value pairs
                var idx = work.IndexOf(':');
                if (idx > 0)
                {
                    var key = work.Substring(0, idx).Trim();
                    var value = work.Substring(idx + 1).Trim();

                    if (key.Equals("DisplayName", StringComparison.OrdinalIgnoreCase))
                        result.DisplayName = value;
                    else if (key.Equals("CoreName", StringComparison.OrdinalIgnoreCase))
                        result.CoreName = value;
                    else if (key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                        result.Description = value;
                    else if (key.Equals("Performance", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out var p))
                            result.Performance = p;
                    }
                    else if (key.Equals("Rating", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out var r))
                            result.Rating = Math.Clamp(r, 0, 5);
                    }
                    else if (key.Equals("Category", StringComparison.OrdinalIgnoreCase))
                        result.Category = value;
                }
            }

            return result;
        }

        /// <summary>
        /// Parse metadata from an HLSL shader file path.
        /// Results are cached for performance.
        /// </summary>
        public static ShaderMetadata ParseFromFile(string filePath)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(filePath, out var cached))
                    return cached;
            }

            var result = new ShaderMetadata();
            
            try
            {
                if (File.Exists(filePath))
                {
                    var source = File.ReadAllText(filePath);
                    result = ParseFromSource(source);
                }
            }
            catch
            {
                // Return empty metadata on error
            }

            lock (_cacheLock)
            {
                _cache[filePath] = result;
            }

            return result;
        }

        /// <summary>
        /// Get metadata for a shader type by looking up its HLSL file.
        /// </summary>
        public static ShaderMetadata GetMetadataForShaderType(NesShaderManager.ShaderType shaderType)
        {
            var shaderName = shaderType.ToString();
            var fileName = GetShaderFileName(shaderType);
            var filePath = FindShaderFile(fileName);
            
            if (filePath != null)
            {
                return ParseFromFile(filePath);
            }

            // Return defaults if file not found
            return new ShaderMetadata
            {
                DisplayName = shaderName,
                CoreName = shaderName,
                Description = "Shader effect",
                Performance = 0,
                Rating = 1,
                Category = "SHADER"
            };
        }

        /// <summary>
        /// Get all shader metadata for all known shader types.
        /// </summary>
        public static Dictionary<string, ShaderMetadata> GetAllShaderMetadata()
        {
            var result = new Dictionary<string, ShaderMetadata>(StringComparer.OrdinalIgnoreCase);
            
            foreach (NesShaderManager.ShaderType shaderType in Enum.GetValues(typeof(NesShaderManager.ShaderType)))
            {
                var id = shaderType.ToString();
                result[id] = GetMetadataForShaderType(shaderType);
            }

            return result;
        }

        /// <summary>
        /// Clear the metadata cache (useful if shader files are modified at runtime).
        /// </summary>
        public static void ClearCache()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
            }
        }

        private static string GetShaderFileName(NesShaderManager.ShaderType shaderType)
        {
            // Map shader type to file name following the naming convention
            return shaderType switch
            {
                NesShaderManager.ShaderType.RF => "RFShader.hlsl",
                NesShaderManager.ShaderType.TV => "TvShader.hlsl",
                NesShaderManager.ShaderType.MUSK => "MuskShader.hlsl",
                NesShaderManager.ShaderType.TTF => "TtfShader.hlsl",
                NesShaderManager.ShaderType.BLD => "BldShader.hlsl",
                NesShaderManager.ShaderType.VHS => "VhsShader.hlsl",
                NesShaderManager.ShaderType.EXE => "ExeShader.hlsl",
                NesShaderManager.ShaderType.BUMP => "BumpShader.hlsl",
                NesShaderManager.ShaderType.RGBX => "RgbxShader.hlsl",
                NesShaderManager.ShaderType.CCC => "CccShader.hlsl",
                NesShaderManager.ShaderType.CRY => "CryShader.hlsl",
                NesShaderManager.ShaderType.CRZ => "CrzShader.hlsl",
                NesShaderManager.ShaderType.DOT => "DotShader.hlsl",
                NesShaderManager.ShaderType.LCD => "LcdShader.hlsl",
                NesShaderManager.ShaderType.PX => "PxShader.hlsl",
                NesShaderManager.ShaderType.CNMA => "CnmaShader.hlsl",
                NesShaderManager.ShaderType.HUE => "HueShader.hlsl",
                NesShaderManager.ShaderType.LAT => "LatShader.hlsl",
                NesShaderManager.ShaderType.LSD => "LsdShader.hlsl",
                NesShaderManager.ShaderType.MSH => "MshShader.hlsl",
                NesShaderManager.ShaderType.SPK => "SpkShader.hlsl",
                NesShaderManager.ShaderType.TRI => "TriShader.hlsl",
                NesShaderManager.ShaderType.WARM => "WarmShader.hlsl",
                NesShaderManager.ShaderType.WTR => "WtrShader.hlsl",
                _ => $"{shaderType}Shader.hlsl"
            };
        }

        private static string? FindShaderFile(string fileName)
        {
            // Search in multiple possible locations
            var basePaths = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                Environment.CurrentDirectory
            };

            foreach (var basePath in basePaths)
            {
                var searchPaths = new[]
                {
                    Path.Combine(basePath, "Shaders", fileName),
                    Path.Combine(basePath, fileName),
                    Path.Combine(basePath, "..", "Shaders", fileName),
                    Path.Combine(basePath, "..", "..", "Shaders", fileName),
                    Path.Combine(basePath, "..", "..", "..", "Windows", "Shaders", fileName)
                };

                foreach (var path in searchPaths)
                {
                    if (File.Exists(path))
                        return Path.GetFullPath(path);
                }
            }

            return null;
        }
    }
}

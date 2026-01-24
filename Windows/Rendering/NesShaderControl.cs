using System;
using System.Linq;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Helper class for controlling shader effects in the NES DirectX renderer.
    /// Provides convenient methods for shader selection and configuration.
    /// </summary>
    public static class NesShaderControl
    {
        private static NesDirectXRenderer currentRenderer;

        /// <summary>
        /// Initialize the shader control with a renderer instance
        /// </summary>
        /// <param name="renderer">The DirectX renderer to control</param>
        public static void Initialize(NesDirectXRenderer renderer)
        {
            currentRenderer = renderer;
        }

        /// <summary>
        /// Get the currently active renderer
        /// </summary>
        public static NesDirectXRenderer CurrentRenderer => currentRenderer;

        /// <summary>
        /// Enable shader effects
        /// </summary>
        public static void EnableShaders()
        {
            if (currentRenderer != null)
            {
                currentRenderer.UseShader = true;
            }
        }

        /// <summary>
        /// Disable shader effects (use Direct2D rendering only)
        /// </summary>
        public static void DisableShaders()
        {
            if (currentRenderer != null)
            {
                currentRenderer.UseShader = false;
            }
        }

        /// <summary>
        /// Toggle shader effects on/off
        /// </summary>
        /// <returns>True if shaders are now enabled</returns>
        public static bool ToggleShaders()
        {
            if (currentRenderer != null)
            {
                currentRenderer.UseShader = !currentRenderer.UseShader;
                return currentRenderer.UseShader;
            }
            return false;
        }

        /// <summary>
        /// Switch to a specific shader by name
        /// </summary>
        /// <param name="shaderName">Name of the shader (RF, BLD, VHS, etc.)</param>
        /// <returns>True if shader was switched successfully</returns>
        public static bool SwitchShader(string shaderName)
        {
            if (currentRenderer == null) return false;

            try
            {
                var alias = shaderName?.Trim();
                if (Enum.TryParse<NesShaderManager.ShaderType>(alias, true, out var shaderType))
                {
                    currentRenderer.SwitchShader(shaderType);
                    return true;
                }
            }
            catch
            {
                // Shader switch failed
            }

            return false;
        }

        /// <summary>
        /// Switch to a specific shader by enum
        /// </summary>
        /// <param name="shaderType">The shader type to switch to</param>
        public static void SwitchShader(NesShaderManager.ShaderType shaderType)
        {
            currentRenderer?.SwitchShader(shaderType);
        }

        /// <summary>
        /// Set the shader effect strength
        /// </summary>
        /// <param name="strength">Strength value (typically 0.5 - 3.0)</param>
        public static void SetShaderStrength(float strength)
        {
            if (currentRenderer != null)
            {
                currentRenderer.ShaderStrength = strength;
            }
        }

        /// <summary>
        /// Get the current shader strength
        /// </summary>
        public static float GetShaderStrength()
        {
            return currentRenderer?.ShaderStrength ?? 2.0f;
        }

        /// <summary>
        /// Get a list of all available shader names
        /// </summary>
        public static string[] GetAvailableShaders()
        {
            return Enum.GetNames(typeof(NesShaderManager.ShaderType));
        }

        /// <summary>
        /// Get the currently active shader name
        /// </summary>
        public static string GetCurrentShaderName()
        {
            return currentRenderer?.CurrentShaderType.ToString() ?? "None";
        }

        /// <summary>
        /// Cycle to the next shader in the list
        /// </summary>
        /// <returns>The name of the new shader</returns>
        public static string CycleToNextShader()
        {
            if (currentRenderer == null) return "None";

            var allShaders = Enum.GetValues(typeof(NesShaderManager.ShaderType))
                .Cast<NesShaderManager.ShaderType>()
                .ToArray();

            var currentIndex = Array.IndexOf(allShaders, currentRenderer.CurrentShaderType);
            var nextIndex = (currentIndex + 1) % allShaders.Length;
            var nextShader = allShaders[nextIndex];

            currentRenderer.SwitchShader(nextShader);
            return nextShader.ToString();
        }

        /// <summary>
        /// Cycle to the previous shader in the list
        /// </summary>
        /// <returns>The name of the new shader</returns>
        public static string CycleToPreviousShader()
        {
            if (currentRenderer == null) return "None";

            var allShaders = Enum.GetValues(typeof(NesShaderManager.ShaderType))
                .Cast<NesShaderManager.ShaderType>()
                .ToArray();

            var currentIndex = Array.IndexOf(allShaders, currentRenderer.CurrentShaderType);
            var prevIndex = (currentIndex - 1 + allShaders.Length) % allShaders.Length;
            var prevShader = allShaders[prevIndex];

            currentRenderer.SwitchShader(prevShader);
            return prevShader.ToString();
        }

        /// <summary>
        /// Get shader information including description, performance, and rating.
        /// Metadata is parsed from HLSL shader file comments.
        /// </summary>
        public static ShaderInfo GetShaderInfo(NesShaderManager.ShaderType shaderType)
        {
            var meta = HlslMetadataParser.GetMetadataForShaderType(shaderType);
            var name = shaderType.ToString();
            
            return new ShaderInfo
            {
                Name = name,
                DisplayName = !string.IsNullOrWhiteSpace(meta.DisplayName) ? meta.DisplayName : name,
                Description = !string.IsNullOrWhiteSpace(meta.Description) ? meta.Description : "Shader effect",
                Performance = meta.Performance,
                Rating = meta.Rating,
                Category = !string.IsNullOrWhiteSpace(meta.Category) ? meta.Category : "SHADER"
            };
        }

        /// <summary>
        /// Get shader information by shader ID string.
        /// </summary>
        public static ShaderInfo? GetShaderInfoById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
                
            if (Enum.TryParse<NesShaderManager.ShaderType>(id, ignoreCase: true, out var shaderType))
            {
                return GetShaderInfo(shaderType);
            }
            
            return null;
        }

        /// <summary>
        /// Information about a shader effect
        /// </summary>
        public class ShaderInfo
        {
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public int Performance { get; set; }
            public int Rating { get; set; }
            public string Category { get; set; } = "SHADER";
        }
    }
}

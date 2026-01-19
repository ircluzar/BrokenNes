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
                if (string.Equals(alias, "16B", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentRenderer != null)
                    {
                        currentRenderer.SwitchShader(NesShaderManager.ShaderType.SixteenB);
                        return true;
                    }
                }

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
        /// Get shader information including description
        /// </summary>
        public static ShaderInfo GetShaderInfo(NesShaderManager.ShaderType shaderType)
        {
            return shaderType switch
            {
                NesShaderManager.ShaderType.RF => new ShaderInfo 
                { 
                    Name = "RF", 
                    DisplayName = "Analog RF",
                    Description = "Mild analog RF simulation with chroma misalignment and shimmer"
                },
                NesShaderManager.ShaderType.SNES => new ShaderInfo 
                { 
                    Name = "SNES", 
                    DisplayName = "16-Bit Upgrade",
                    Description = "SNES-style color enhancement"
                },
                NesShaderManager.ShaderType.TV => new ShaderInfo 
                { 
                    Name = "TV", 
                    DisplayName = "CRT TV",
                    Description = "Classic CRT television look"
                },
                NesShaderManager.ShaderType.MUSK => new ShaderInfo 
                { 
                    Name = "MUSK", 
                    DisplayName = "Mars Horizon",
                    Description = "Atmospheric Mars-themed shader"
                },
                NesShaderManager.ShaderType.TTF => new ShaderInfo 
                { 
                    Name = "TTF", 
                    DisplayName = "Subpixel Clean",
                    Description = "Sharp subpixel rendering"
                },
                NesShaderManager.ShaderType.BLD => new ShaderInfo 
                { 
                    Name = "BLD", 
                    DisplayName = "4-Way Color Bleed",
                    Description = "Directional color bleed effect"
                },
                NesShaderManager.ShaderType.VHS => new ShaderInfo 
                { 
                    Name = "VHS", 
                    DisplayName = "Broken VCR",
                    Description = "VHS tape distortion effect"
                },
                NesShaderManager.ShaderType.EXE => new ShaderInfo 
                { 
                    Name = "EXE", 
                    DisplayName = "Creepy Look",
                    Description = "Unsettling visual effect"
                },
                NesShaderManager.ShaderType.BUMP => new ShaderInfo 
                { 
                    Name = "BUMP", 
                    DisplayName = "Pseudo Bump",
                    Description = "Fake 3D bump mapping"
                },
                NesShaderManager.ShaderType.RGBX => new ShaderInfo 
                { 
                    Name = "RGBX", 
                    DisplayName = "Chromatic Vector",
                    Description = "RGB separation effect"
                },
                NesShaderManager.ShaderType.CCC => new ShaderInfo
                {
                    Name = "CCC",
                    DisplayName = "Color Cycle",
                    Description = "Hue rotation with inverted breaths"
                },
                NesShaderManager.ShaderType.CRY => new ShaderInfo
                {
                    Name = "CRY",
                    DisplayName = "Crystalline",
                    Description = "Facet-driven refraction with dispersion"
                },
                NesShaderManager.ShaderType.CRZ => new ShaderInfo
                {
                    Name = "CRZ",
                    DisplayName = "Crystal Glass",
                    Description = "Sharp glass facets with glints"
                },
                NesShaderManager.ShaderType.DOT => new ShaderInfo
                {
                    Name = "DOT",
                    DisplayName = "Circular Shards",
                    Description = "Overlapping circular refraction field"
                },
                NesShaderManager.ShaderType.LCD => new ShaderInfo
                {
                    Name = "LCD",
                    DisplayName = "Aging LCD",
                    Description = "Horizontal smear, ghost, frost diffusion"
                },
                NesShaderManager.ShaderType.PX => new ShaderInfo
                {
                    Name = "PX",
                    DisplayName = "Passthrough",
                    Description = "Identity shader for baseline"
                },
                NesShaderManager.ShaderType.CNMA => new ShaderInfo
                {
                    Name = "CNMA",
                    DisplayName = "Cinematic",
                    Description = "Filmic exposure and teal/orange grade"
                },
                NesShaderManager.ShaderType.HUE => new ShaderInfo
                {
                    Name = "HUE",
                    DisplayName = "Hue Rotation",
                    Description = "Hue inversion with slow rotation"
                },
                NesShaderManager.ShaderType.LAT => new ShaderInfo
                {
                    Name = "LAT",
                    DisplayName = "Lattice",
                    Description = "Micro-facet tile refraction"
                },
                NesShaderManager.ShaderType.LSD => new ShaderInfo
                {
                    Name = "LSD",
                    DisplayName = "Psychedelic",
                    Description = "Layered warps and chromatic splits"
                },
                NesShaderManager.ShaderType.MSH => new ShaderInfo
                {
                    Name = "MSH",
                    DisplayName = "Pixel Mush",
                    Description = "Temporal block mosh with glitches"
                },
                NesShaderManager.ShaderType.SPK => new ShaderInfo
                {
                    Name = "SPK",
                    DisplayName = "Prism Sparkle",
                    Description = "Edge prism with starfield sparkles"
                },
                NesShaderManager.ShaderType.TRI => new ShaderInfo
                {
                    Name = "TRI",
                    DisplayName = "Faux Extrusion",
                    Description = "Height-from-luma parallax lighting"
                },
                NesShaderManager.ShaderType.WARM => new ShaderInfo
                {
                    Name = "WARM",
                    DisplayName = "Extra Warmth",
                    Description = "Subtle warmth and soft contrast"
                },
                NesShaderManager.ShaderType.WTR => new ShaderInfo
                {
                    Name = "WTR",
                    DisplayName = "Water Ripples",
                    Description = "Compound vector-field warping"
                },
                NesShaderManager.ShaderType.SixteenB => new ShaderInfo
                {
                    Name = "16B",
                    DisplayName = "16-Bit Upgrade",
                    Description = "Edge-aware smoothing with chroma blur"
                },
                _ => new ShaderInfo { Name = "Unknown", DisplayName = "Unknown", Description = "Unknown shader" }
            };
        }

        /// <summary>
        /// Information about a shader effect
        /// </summary>
        public class ShaderInfo
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
        }
    }
}

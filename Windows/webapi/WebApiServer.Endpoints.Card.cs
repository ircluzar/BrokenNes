using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;
using NesEmulator.NullProviders;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        /// <summary>
        /// Register Card SVG API endpoints
        /// </summary>
        private void RegisterCardEndpoints(WebApplication app)
        {
            // GET /api/card/{domain}/{id} - Get SVG for a specific card
            app.MapGet("/api/card/{domain}/{id}", (string domain, string id) =>
            {
                try
                {
                    // Initialize registries
                    CoreRegistry.Initialize();

                    // Build card model from core metadata
                    CoreCardModel? cardModel = null;
                    var normalizedDomain = domain.ToUpperInvariant();

                    if (normalizedDomain == "CPU")
                    {
                        var cpuTypes = CoreRegistry.CpuTypes;
                        if (cpuTypes.TryGetValue(id, out var type))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = SafeGetStaticString(type, "CoreName") ?? id,
                                Description = SafeGetStaticString(type, "Description") ?? string.Empty,
                                Rating = SafeGetStaticInt(type, "Rating") ?? 0,
                                Performance = SafeGetStaticInt(type, "Performance") ?? 0,
                                FooterNote = SafeGetStaticString(type, "Category") ?? "CPU",
                                Domain = "CPU"
                            };
                        }
                    }
                    else if (normalizedDomain == "PPU")
                    {
                        var ppuTypes = CoreRegistry.PpuTypes;
                        if (ppuTypes.TryGetValue(id, out var type))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = SafeGetStaticString(type, "CoreName") ?? id,
                                Description = SafeGetStaticString(type, "Description") ?? string.Empty,
                                Rating = SafeGetStaticInt(type, "Rating") ?? 0,
                                Performance = SafeGetStaticInt(type, "Performance") ?? 0,
                                FooterNote = SafeGetStaticString(type, "Category") ?? "PPU",
                                Domain = "PPU"
                            };
                        }
                    }
                    else if (normalizedDomain == "APU")
                    {
                        var apuTypes = CoreRegistry.ApuTypes;
                        if (apuTypes.TryGetValue(id, out var type))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = SafeGetStaticString(type, "CoreName") ?? id,
                                Description = SafeGetStaticString(type, "Description") ?? string.Empty,
                                Rating = SafeGetStaticInt(type, "Rating") ?? 0,
                                Performance = SafeGetStaticInt(type, "Performance") ?? 0,
                                FooterNote = SafeGetStaticString(type, "Category") ?? "APU",
                                Domain = "APU"
                            };
                        }
                    }
                    else if (normalizedDomain == "CLOCK")
                    {
                        // ClockRegistry not available in Windows project - return placeholder
                        cardModel = new CoreCardModel
                        {
                            Id = id,
                            ShortName = id,
                            DisplayName = id,
                            Description = "Clock timing core",
                            Rating = 0,
                            Performance = 0,
                            FooterNote = "CLOCK",
                            Domain = "CLOCK"
                        };
                    }
                    else if (normalizedDomain == "SHADER")
                    {
                        // Get shader metadata from HLSL file comments
                        var shaderInfo = Rendering.NesShaderControl.GetShaderInfoById(id);
                        if (shaderInfo != null)
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = !string.IsNullOrWhiteSpace(shaderInfo.DisplayName) ? shaderInfo.DisplayName : id,
                                Description = !string.IsNullOrWhiteSpace(shaderInfo.Description) ? shaderInfo.Description : "Shader effect",
                                Rating = shaderInfo.Rating,
                                Performance = shaderInfo.Performance,
                                FooterNote = !string.IsNullOrWhiteSpace(shaderInfo.Category) ? shaderInfo.Category : "SHADER",
                                Domain = "SHADER"
                            };
                        }
                        else
                        {
                            // Fallback for unknown shader IDs
                            cardModel = new CoreCardModel
                            {
                                Id = id,
                                ShortName = id,
                                DisplayName = id,
                                Description = "Shader effect",
                                Rating = 0,
                                Performance = 0,
                                FooterNote = "SHADER",
                                Domain = "SHADER"
                            };
                        }
                    }
                    else if (normalizedDomain == "WEBMODULE")
                    {
                        var module = WebModuleManager.DiscoverModules()
                            .FirstOrDefault(entry => entry.FolderName.Equals(id, StringComparison.OrdinalIgnoreCase));

                        if (module != null)
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = module.FolderName,
                                ShortName = module.FolderName,
                                DisplayName = !string.IsNullOrWhiteSpace(module.Name) ? module.Name : module.FolderName,
                                Description = !string.IsNullOrWhiteSpace(module.Config.Description)
                                    ? module.Config.Description
                                    : "BrokenNes webmodule unlock.",
                                Rating = RatingForWebModule(module.FolderName, module.DisplayMode),
                                Performance = 0,
                                FooterNote = $"WEBMODULE {module.DisplayMode.ToString().ToUpperInvariant()}",
                                Domain = "WEBMODULE"
                            };
                        }
                    }
                    else if (normalizedDomain == "BACKGROUND")
                    {
                        var normalizedId = NormalizeBackgroundId(id);
                        var backgroundName = Rendering.NesDirectXRenderer.GetAvailableBackgrounds()
                            .FirstOrDefault(name => NormalizeBackgroundId(name).Equals(normalizedId, StringComparison.OrdinalIgnoreCase));

                        if (!string.IsNullOrWhiteSpace(backgroundName))
                        {
                            cardModel = new CoreCardModel
                            {
                                Id = backgroundName,
                                ShortName = BuildShortName(backgroundName),
                                DisplayName = backgroundName,
                                Description = BuildBackgroundDescription(backgroundName),
                                Rating = RatingForBackground(backgroundName),
                                Performance = 0,
                                FooterNote = "BACKGROUND",
                                Domain = "BACKGROUND"
                            };
                        }
                    }
                    else if (normalizedDomain == "NULLPROVIDER")
                    {
                        var providerName = NullProviderRegistry.GetAvailableProviders()
                            .FirstOrDefault(name => name.Equals(id, StringComparison.OrdinalIgnoreCase));

                        if (!string.IsNullOrWhiteSpace(providerName))
                        {
                            var provider = NullProviderRegistry.GetProvider(providerName);
                            cardModel = new CoreCardModel
                            {
                                Id = provider.DisplayName,
                                ShortName = BuildShortName(provider.DisplayName),
                                DisplayName = provider.DisplayName,
                                Description = string.IsNullOrWhiteSpace(provider.Description)
                                    ? "Animated null-provider visualizer."
                                    : provider.Description,
                                Rating = RatingForNullProvider(provider.DisplayName),
                                Performance = 0,
                                FooterNote = "NULL PROVIDER",
                                Domain = "NULLPROVIDER"
                            };
                        }
                    }

                    if (cardModel == null)
                    {
                        cardModel = new CoreCardModel
                        {
                            Id = id,
                            ShortName = BuildShortName(id),
                            DisplayName = PrettifyName(id),
                            Description = BuildFallbackDescription(normalizedDomain),
                            Rating = 2,
                            Performance = 0,
                            FooterNote = normalizedDomain,
                            Domain = normalizedDomain
                        };
                    }

                    // Render SVG
                    var svg = CardSvgRenderer.Render(cardModel);

                    // Return as SVG content type
                    return Results.Content(svg, "image/svg+xml");
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });

            // Helper methods for safe reflection access (support both static and instance properties)
            static string? SafeGetStaticString(Type type, string propertyName)
            {
                try
                {
                    // First try static property
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        return prop.GetValue(null) as string;
                    }
                    // Fall back to instance property - use GetUninitializedObject to avoid constructor issues
                    prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        // Create uninitialized instance without calling constructor (avoids dependency issues)
                        var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                        if (instance != null)
                        {
                            return prop.GetValue(instance) as string;
                        }
                    }
                }
                catch { }
                return null;
            }

            static int? SafeGetStaticInt(Type type, string propertyName)
            {
                try
                {
                    // First try static property
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        return (int?)prop.GetValue(null);
                    }
                    // Fall back to instance property - use GetUninitializedObject to avoid constructor issues
                    prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        // Create uninitialized instance without calling constructor (avoids dependency issues)
                        var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                        if (instance != null)
                        {
                            return (int?)prop.GetValue(instance);
                        }
                    }
                }
                catch { }
                return null;
            }

            static string NormalizeBackgroundId(string value)
            {
                var trimmed = value?.Trim() ?? string.Empty;
                if (trimmed.Equals("Gradient", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("Gradient (Default)", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("StaticGradient", StringComparison.OrdinalIgnoreCase))
                {
                    return "Gradient (Default)";
                }

                if (trimmed.Equals("Black", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("None", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals("None (Black)", StringComparison.OrdinalIgnoreCase))
                {
                    return "None (Black)";
                }

                return trimmed;
            }

            static string BuildShortName(string value)
            {
                var compact = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                if (compact.Length == 0)
                {
                    return "CARD";
                }

                return compact.Length <= 4 ? compact : compact.Substring(0, 4);
            }

            static string PrettifyName(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "Unknown Card";
                }

                var text = value.Replace("_", " ").Replace("-", " ").Trim();
                var chars = new System.Collections.Generic.List<char>(text.Length + 8);
                for (var index = 0; index < text.Length; index++)
                {
                    var current = text[index];
                    if (index > 0 && char.IsUpper(current) && char.IsLetter(text[index - 1]) && char.IsLower(text[index - 1]))
                    {
                        chars.Add(' ');
                    }
                    chars.Add(current);
                }

                return new string(chars.ToArray()).Trim();
            }

            static string BuildBackgroundDescription(string backgroundName)
            {
                return backgroundName switch
                {
                    "Gradient (Default)" => "Default menu background with a clean static gradient.",
                    "None (Black)" => "Pure black backdrop for minimal presentation.",
                    _ => $"Procedural renderer background: {PrettifyName(backgroundName)}."
                };
            }

            static string BuildFallbackDescription(string domainName)
            {
                return domainName switch
                {
                    "WEBMODULE" => "BrokenNes webmodule unlock.",
                    "BACKGROUND" => "BrokenNes renderer background unlock.",
                    "NULLPROVIDER" => "BrokenNes null-provider unlock.",
                    _ => $"{domainName} unlock."
                };
            }

            static int RatingForWebModule(string moduleId, WebModuleDisplayMode displayMode)
            {
                return moduleId.ToUpperInvariant() switch
                {
                    "HOME" => 3,
                    "CONTINUE" => 4,
                    "DECKBUILDER" => 4,
                    "CORES" => 3,
                    "OPTIONS" => 2,
                    "STORY" => 2,
                    "ROMMANAGER" => 3,
                    "HEXEDITOR" => 3,
                    "GLITCHHARVESTER" => 5,
                    "TIMEJUMP" => 5,
                    "CORRUPTIONSLOP" => 4,
                    "IMAGINEBUG" => 5,
                    _ => displayMode == WebModuleDisplayMode.Overlay ? 4 : 3
                };
            }

            static int RatingForBackground(string backgroundName)
            {
                return backgroundName switch
                {
                    "Gradient (Default)" => 2,
                    "None (Black)" => 1,
                    _ => 3
                };
            }

            static int RatingForNullProvider(string providerName)
            {
                return providerName.Equals("Static", StringComparison.OrdinalIgnoreCase)
                    || providerName.Equals("Void", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 3;
            }
        }
    }
}

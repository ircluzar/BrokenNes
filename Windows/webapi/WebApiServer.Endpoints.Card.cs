using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;

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

                    if (cardModel == null)
                    {
                        return Results.NotFound(new { success = false, error = $"Card not found: {domain}/{id}" });
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

            static T? SafeGet<T>(Func<T> getter)
            {
                try { return getter(); } catch { return default; }
            }

            static T? SafeGetStruct<T>(Func<T> getter) where T : struct
            {
                try { return getter(); } catch { return null; }
            }
        }
    }
}

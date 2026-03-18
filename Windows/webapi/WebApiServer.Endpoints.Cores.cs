using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NesEmulator;

namespace BrokenNes.Windows.WebApi
{
    public partial class WebApiServer
    {
        private void RegisterCoresEndpoints(WebApplication app)
        {
            // GET /api/cores - Get metadata for all available cores
            app.MapGet("/api/cores", () =>
            {
                try
                {
                    // Initialize core registry
                    CoreRegistry.Initialize();

                    var result = new
                    {
                        cpu = GetCpuMetadata(),
                        ppu = GetPpuMetadata(),
                        apu = GetApuMetadata(),
                        clock = new List<object>(), // ClockRegistry not available in Windows project
                        shader = GetShaderMetadata()
                    };

                    return Results.Ok(result);
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

            // POST /api/cores/apply - Apply selected CPU/PPU/APU cores to the running emulator
            app.MapPost("/api/cores/apply", async (HttpContext context) =>
            {
                try
                {
                    var body = await context.Request.ReadFromJsonAsync<ApplyCoresRequest>();
                    if (body == null)
                    {
                        return Results.BadRequest(new { success = false, error = "Core payload is required" });
                    }

                    void ApplyIfPresent(Action<string>? setter, string? value)
                    {
                        if (setter == null || string.IsNullOrWhiteSpace(value))
                        {
                            return;
                        }

                        setter(value.Trim().ToUpperInvariant());
                    }

                    if (_uiControl != null && _uiControl.InvokeRequired)
                    {
                        _uiControl.Invoke((Action)(() =>
                        {
                            ApplyIfPresent(_setCpuCore, body.CpuId);
                            ApplyIfPresent(_setPpuCore, body.PpuId);
                            ApplyIfPresent(_setApuCore, body.ApuId);
                        }));
                    }
                    else
                    {
                        ApplyIfPresent(_setCpuCore, body.CpuId);
                        ApplyIfPresent(_setPpuCore, body.PpuId);
                        ApplyIfPresent(_setApuCore, body.ApuId);
                    }

                    return Results.Ok(new
                    {
                        success = true,
                        cpu = body.CpuId,
                        ppu = body.PpuId,
                        apu = body.ApuId
                    });
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

            static List<object> GetCpuMetadata()
            {
                try
                {
                    var cpuTypes = CoreRegistry.CpuTypes;
                    var result = new List<object>();
                    
                    foreach (var kvp in cpuTypes)
                    {
                        var id = kvp.Key;
                        var type = kvp.Value;
                        result.Add(new
                        {
                            id = id,
                            name = SafeGetInstanceString(type, "CoreName") ?? id,
                            description = SafeGetInstanceString(type, "Description") ?? $"CPU core {id}",
                            performance = SafeGetInstanceInt(type, "Performance") ?? 0,
                            rating = SafeGetInstanceInt(type, "Rating") ?? 3,
                            category = SafeGetInstanceString(type, "Category") ?? "Processor"
                        });
                    }
                    
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            static List<object> GetPpuMetadata()
            {
                try
                {
                    var ppuTypes = CoreRegistry.PpuTypes;
                    var result = new List<object>();
                    
                    foreach (var kvp in ppuTypes)
                    {
                        var id = kvp.Key;
                        var type = kvp.Value;
                        result.Add(new
                        {
                            id = id,
                            name = SafeGetInstanceString(type, "CoreName") ?? id,
                            description = SafeGetInstanceString(type, "Description") ?? $"PPU core {id}",
                            performance = SafeGetInstanceInt(type, "Performance") ?? 0,
                            rating = SafeGetInstanceInt(type, "Rating") ?? 3,
                            category = SafeGetInstanceString(type, "Category") ?? "Graphics"
                        });
                    }
                    
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            static List<object> GetApuMetadata()
            {
                try
                {
                    var apuTypes = CoreRegistry.ApuTypes;
                    var result = new List<object>();
                    
                    foreach (var kvp in apuTypes)
                    {
                        var id = kvp.Key;
                        var type = kvp.Value;
                        result.Add(new
                        {
                            id = id,
                            name = SafeGetInstanceString(type, "CoreName") ?? id,
                            description = SafeGetInstanceString(type, "Description") ?? $"APU core {id}",
                            performance = SafeGetInstanceInt(type, "Performance") ?? 0,
                            rating = SafeGetInstanceInt(type, "Rating") ?? 3,
                            category = SafeGetInstanceString(type, "Category") ?? "Audio"
                        });
                    }
                    
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            static List<object> GetClockMetadata()
            {
                // Clock cores from Windows/NesEmulator/clocks
                var clockDescriptions = new Dictionary<string, (string Name, string Description, int Performance, int Rating)>
                {
                    ["FMC"] = ("Standard Clock", "Default NES clock timing", 0, 5),
                    ["TRB"] = ("Turbo Clock", "Overclocked CPU timing for faster gameplay", 2, 4),
                    ["CLR"] = ("Clear Clock", "Ultra-precise timing with reduced jitter", 1, 4)
                };

                var result = new List<object>();
                foreach (var kv in clockDescriptions)
                {
                    result.Add(new
                    {
                        id = kv.Key,
                        name = kv.Value.Name,
                        description = kv.Value.Description,
                        performance = kv.Value.Performance,
                        rating = kv.Value.Rating,
                        category = "Clock"
                    });
                }
                return result;
            }

            static List<object> GetShaderMetadata()
            {
                try
                {
                    var result = new List<object>();
                    // Use the HlslMetadataParser to get actual metadata from HLSL shader files
                    foreach (Rendering.NesShaderManager.ShaderType shaderType in Enum.GetValues(typeof(Rendering.NesShaderManager.ShaderType)))
                    {
                        var id = shaderType.ToString();
                        var info = Rendering.NesShaderControl.GetShaderInfo(shaderType);
                        result.Add(new
                        {
                            id = id,
                            name = info.DisplayName,
                            description = info.Description,
                            performance = info.Performance,
                            rating = info.Rating,
                            category = info.Category
                        });
                    }
                    return result;
                }
                catch
                {
                    return new List<object>();
                }
            }

            // Helper to read instance properties from core types by creating a temporary instance
            static string? SafeGetInstanceString(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        // Try to create instance with parameterless constructor or Bus constructor
                        object? instance = null;
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor != null)
                        {
                            instance = ctor.Invoke(null);
                        }
                        else
                        {
                            // Try constructor with Bus parameter (common for cores)
                            var busCtor = type.GetConstructor(new[] { typeof(Bus) });
                            if (busCtor != null)
                            {
                                instance = busCtor.Invoke(new object?[] { null });
                            }
                        }
                        if (instance != null)
                        {
                            return prop.GetValue(instance) as string;
                        }
                    }
                }
                catch { }
                return null;
            }

            static int? SafeGetInstanceInt(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        // Try to create instance with parameterless constructor or Bus constructor
                        object? instance = null;
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        if (ctor != null)
                        {
                            instance = ctor.Invoke(null);
                        }
                        else
                        {
                            // Try constructor with Bus parameter (common for cores)
                            var busCtor = type.GetConstructor(new[] { typeof(Bus) });
                            if (busCtor != null)
                            {
                                instance = busCtor.Invoke(new object?[] { null });
                            }
                        }
                        if (instance != null)
                        {
                            return (int?)prop.GetValue(instance);
                        }
                    }
                }
                catch { }
                return null;
            }

            static string? SafeGetStaticString(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(string))
                    {
                        return prop.GetValue(null) as string;
                    }
                }
                catch { }
                return null;
            }

            static int? SafeGetStaticInt(Type type, string propertyName)
            {
                try
                {
                    var prop = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null && prop.PropertyType == typeof(int))
                    {
                        return (int?)prop.GetValue(null);
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

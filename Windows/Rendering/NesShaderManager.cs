using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using System.Diagnostics;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// Manages HLSL shader compilation, loading, and switching for the NES emulator.
    /// Supports multiple shader effects for retro-style visual enhancements.
    /// </summary>
    public class NesShaderManager : IDisposable
    {
        private D3D11Device device;
        private VertexShader vertexShader;
        private PixelShader pixelShader;
        private InputLayout inputLayout;
        private D3DBuffer vertexBuffer;
        private D3DBuffer constantBuffer;
        private SamplerState samplerState;
        
        private bool disposed = false;
        private string currentShaderName;

        /// <summary>
        /// Shader constant buffer structure matching HLSL cbuffer
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ShaderConstants
        {
            public Vector2 TexSize;      // Texture dimensions
            public float Time;            // Time in seconds for animated effects
            public float Strength;        // Effect strength multiplier
        }

        /// <summary>
        /// Available shader types for NES rendering
        /// </summary>
        public enum ShaderType
        {
            RF,      // Analog RF shader - mild analog RF simulation
            SNES,    // 16-bit upgrade shader
            TV,      // TV upgrade shader
            MUSK,    // Mars Horizon shader
            TTF,     // Subpixel Clean shader
            BLD,     // 4-Way Color Bleed shader
            VHS,     // Broken VCR shader
            EXE,     // Creepy Look shader
            BUMP,    // Pseudo Bump shader
            RGBX     // Chromatic Vector shader
        }

        /// <summary>
        /// Vertex structure for full-screen quad
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public Vector2 TexCoord;
        }

        /// <summary>
        /// Create a new shader manager
        /// </summary>
        /// <param name="device">Direct3D device</param>
        /// <param name="shaderType">Initial shader type to load</param>
        public NesShaderManager(D3D11Device device, ShaderType shaderType = ShaderType.BLD)
        {
            this.device = device;
            InitializeShaders(shaderType);
            CreateVertexBuffer();
            CreateConstantBuffer();
            CreateSamplerState();
        }

        private void InitializeShaders(ShaderType shaderType)
        {
            string pixelShaderPath = GetShaderPath(shaderType);
            string vertexShaderPath = GetShaderPath("VertexShader");
            currentShaderName = shaderType.ToString();

            // Load and compile vertex shader
            byte[] vertexShaderBytes = LoadShaderFromFile(vertexShaderPath, "main", "vs_4_0");
            vertexShader = new VertexShader(device, vertexShaderBytes);
            
            // Create input layout for vertex structure
            var inputElements = new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 12, 0)
            };
            
            inputLayout = new InputLayout(device, vertexShaderBytes, inputElements);

            // Load and compile pixel shader
            byte[] pixelShaderBytes = LoadShaderFromFile(pixelShaderPath, "main", "ps_4_0");
            pixelShader = new PixelShader(device, pixelShaderBytes);
        }

        private string GetShaderPath(ShaderType shaderType)
        {
            string fileName = shaderType switch
            {
                ShaderType.RF => "RFShader.hlsl",
                ShaderType.SNES => "SnesShader.hlsl",
                ShaderType.TV => "TvShader.hlsl",
                ShaderType.MUSK => "MuskShader.hlsl",
                ShaderType.TTF => "TtfShader.hlsl",
                ShaderType.BLD => "BldShader.hlsl",
                ShaderType.VHS => "VhsShader.hlsl",
                ShaderType.EXE => "ExeShader.hlsl",
                ShaderType.BUMP => "BumpShader.hlsl",
                ShaderType.RGBX => "RgbxShader.hlsl",
                _ => "BldShader.hlsl"
            };
            return GetShaderFilePath(fileName);
        }

        private string GetShaderPath(string shaderName)
        {
            return GetShaderFilePath($"{shaderName}.hlsl");
        }

        private string GetShaderFilePath(string fileName)
        {
            // Try multiple locations for shader files
            string[] searchPaths = new[]
            {
                Path.Combine("Shaders", fileName),
                Path.Combine("Windows", "Shaders", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shaders", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Windows", "Shaders", fileName),
                Path.Combine("Examples", "HlslShaders", fileName)
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new FileNotFoundException($"Shader file not found: {fileName}. Searched paths: {string.Join(", ", searchPaths)}");
        }

        private byte[] LoadShaderFromFile(string filePath, string entryPoint, string profile)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Shader file not found: {filePath}");
                }

                string shaderCode = File.ReadAllText(filePath);
                return CompileShader(shaderCode, entryPoint, profile, filePath);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load shader from {filePath}: {ex.Message}", ex);
            }
        }

        private byte[] CompileShader(string shaderCode, string entryPoint, string profile, string sourcePath = "")
        {
            try
            {
                var result = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    shaderCode, 
                    entryPoint, 
                    profile,
                    SharpDX.D3DCompiler.ShaderFlags.None,
                    SharpDX.D3DCompiler.EffectFlags.None,
                    null,
                    null,
                    sourcePath);

                if (result.HasErrors)
                {
                    throw new Exception($"Shader compilation failed: {result.Message}");
                }
                
                return result.Bytecode.Data;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Shader compilation error: {ex.Message}");
                // Try fallback compilation using fxc.exe if available
                return CompileShaderFallback(shaderCode, entryPoint, profile);
            }
        }

        private byte[] CompileShaderFallback(string shaderCode, string entryPoint, string profile)
        {
            var tempFile = Path.GetTempFileName() + ".hlsl";
            var outputFile = Path.GetTempFileName() + ".cso";
            
            try
            {
                File.WriteAllText(tempFile, shaderCode);
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "fxc.exe",
                        Arguments = $"/T {profile} /E {entryPoint} /Fo \"{outputFile}\" \"{tempFile}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    }
                };
                
                process.Start();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                
                if (File.Exists(outputFile))
                {
                    return File.ReadAllBytes(outputFile);
                }
                
                throw new Exception($"FXC compilation failed: {error}");
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
                if (File.Exists(outputFile)) File.Delete(outputFile);
            }
        }

        /// <summary>
        /// Switch to a different shader effect at runtime
        /// </summary>
        /// <param name="shaderType">The shader type to switch to</param>
        public void SwitchShader(ShaderType shaderType)
        {
            if (currentShaderName == shaderType.ToString())
                return;

            // Dispose old pixel shader
            pixelShader?.Dispose();

            // Load new pixel shader
            string pixelShaderPath = GetShaderPath(shaderType);
            byte[] pixelShaderBytes = LoadShaderFromFile(pixelShaderPath, "main", "ps_4_0");
            pixelShader = new PixelShader(device, pixelShaderBytes);
            currentShaderName = shaderType.ToString();
        }

        private void CreateVertexBuffer()
        {
            // Full-screen quad vertices (triangle strip)
            var vertices = new[]
            {
                new Vertex { Position = new Vector3(-1.0f, -1.0f, 0.0f), TexCoord = new Vector2(0.0f, 1.0f) },
                new Vertex { Position = new Vector3(-1.0f,  1.0f, 0.0f), TexCoord = new Vector2(0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f, -1.0f, 0.0f), TexCoord = new Vector2(1.0f, 1.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f, 0.0f), TexCoord = new Vector2(1.0f, 0.0f) }
            };

            vertexBuffer = D3DBuffer.Create(device, BindFlags.VertexBuffer, vertices);
        }

        private void CreateConstantBuffer()
        {
            constantBuffer = new D3DBuffer(
                device, 
                Utilities.SizeOf<ShaderConstants>(), 
                ResourceUsage.Dynamic, 
                BindFlags.ConstantBuffer, 
                CpuAccessFlags.Write, 
                ResourceOptionFlags.None, 
                0);
        }

        private void CreateSamplerState()
        {
            var samplerDesc = new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MipLodBias = 0,
                MaximumAnisotropy = 1,
                ComparisonFunction = Comparison.Never,
                BorderColor = new Color4(0, 0, 0, 0),
                MinimumLod = 0,
                MaximumLod = float.MaxValue
            };

            samplerState = new SamplerState(device, samplerDesc);
        }

        /// <summary>
        /// Apply the current shader to the rendering pipeline
        /// </summary>
        /// <param name="context">Device context</param>
        /// <param name="textureView">The NES framebuffer texture</param>
        /// <param name="constants">Shader constants (time, strength, etc.)</param>
        public void ApplyShader(DeviceContext context, ShaderResourceView textureView, ShaderConstants constants)
        {
            // Update constant buffer
            var dataBox = context.MapSubresource(constantBuffer, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
            Utilities.Write(dataBox.DataPointer, ref constants);
            context.UnmapSubresource(constantBuffer, 0);

            // Setup input assembler
            context.InputAssembler.InputLayout = inputLayout;
            context.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip;
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, Utilities.SizeOf<Vertex>(), 0));
            
            // Setup vertex shader
            context.VertexShader.Set(vertexShader);
            context.VertexShader.SetConstantBuffer(0, constantBuffer);
            
            // Setup pixel shader
            context.PixelShader.Set(pixelShader);
            context.PixelShader.SetConstantBuffer(0, constantBuffer);
            context.PixelShader.SetShaderResource(0, textureView);
            context.PixelShader.SetSampler(0, samplerState);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            vertexShader?.Dispose();
            pixelShader?.Dispose();
            inputLayout?.Dispose();
            vertexBuffer?.Dispose();
            constantBuffer?.Dispose();
            samplerState?.Dispose();
        }
    }
}

# DirectX Rendering Library for BrokenNes

This directory contains a hardware-accelerated DirectX rendering library for the BrokenNes NES emulator using SharpDX.

## Features

- **Hardware-Accelerated Rendering**: Uses Direct3D 11 and Direct2D for GPU-accelerated rendering
- **HLSL Shader Support**: Real-time shader effects for retro visual enhancements
- **Hot-Swappable Shaders**: Switch between different shader effects at runtime
- **Pluggable Background System**: Multiple animated backgrounds for visual enhancement
- **Efficient Memory Management**: DirectBitmap class for fast texture updates
- **Fallback Support**: Gracefully falls back to software rendering if DirectX is unavailable

## Components

### Core Classes

#### `DirectBitmap.cs`
- Fast bitmap class with direct memory access
- Efficient copying to GPU textures
- Pinned memory for zero-copy operations

#### `NesDirectXRenderer.cs`
- Main rendering control (inherits from `Control`)
- Manages DirectX device, swap chain, and render targets
- Supports both Direct2D and shader-based rendering
- Pluggable background system with runtime switching
- Automatic resource cleanup and resize handling

#### `NesShaderManager.cs`
- Manages HLSL shader compilation and loading
- Supports runtime shader switching
- Handles shader constants (time, strength, texture size)
- Multiple shader search paths for flexibility

#### `NesShaderControl.cs`
- Static helper class for shader control
- Provides convenient methods for:
  - Enabling/disabling shaders
  - Switching between shaders
  - Adjusting shader strength
  - Cycling through available shaders

### Background System

#### `IBackground` Interface
- Defines the contract for pluggable background renderers
- Methods: `Initialize()`, `Update()`, `Render()`, `Dispose()`

#### Available Backgrounds (in `Backgrounds/` folder)

**AnimatedWaveBackground**
- Animated wavy pattern with Phong-like shading
- Dynamic color cycling through blue hues
- Smooth sine wave animations with surface normals

**AnimatedBubbleBackground** ✨ NEW
- Chaotic animated bubbles with purple colors
- 25+ bubbles with varying sizes and speeds
- Pulsing effect with resonance
- Sparkle effects on bubble surfaces
- Ideal for a more dynamic, energetic visual experience

**StaticGradientBackground**
- Simple static gradient background
- Lightweight and subtle

**None**
- No background (transparent/black)

To switch backgrounds at runtime:
```csharp
dxRenderer.SetBackground("Bubble"); // Wave, Bubble, Gradient, or None
```

The selected background is saved in the config and restored on startup.

## Available Shaders

The following shaders are ported to HLSL and ready to use:

| Shader | Name | Description |
|--------|------|-------------|
| **RF** | Analog RF | Mild analog RF simulation with chroma misalignment and shimmer |
| **SNES** | 16-Bit Upgrade | SNES-style color enhancement |
| **TV** | CRT TV | Classic CRT television look |
| **MUSK** | Mars Horizon | Atmospheric Mars-themed shader |
| **TTF** | Subpixel Clean | Sharp subpixel rendering |
| **BLD** | 4-Way Color Bleed | Edge-aware directional color diffusion |
| **VHS** | Broken VCR | VHS tape distortion effect |
| **EXE** | Creepy Look | Unsettling visual effect |
| **BUMP** | Pseudo Bump | Fake 3D bump mapping |
| **RGBX** | Chromatic Vector | RGB separation effect |

## Usage

### Basic Setup

The DirectX renderer is automatically initialized when the application starts:

```csharp
// In MainForm.cs
var dxRenderer = new NesDirectXRenderer
{
    Dock = DockStyle.Fill,
    BackColor = Color.Black
};

// Initialize with NES display dimensions
dxRenderer.Initialize(256, 240);

// Initialize shader control
NesShaderControl.Initialize(dxRenderer);
```

### Rendering Frames

```csharp
// Create a DirectBitmap for the NES framebuffer
var frameBuffer = new DirectBitmap(256, 240);

// Copy NES emulator output
byte[] nesOutput = nes.GetFrameBuffer();
frameBuffer.CopyFromBytes(nesOutput);

// Render the frame
dxRenderer.DrawFrame(frameBuffer);
```

### Shader Control

```csharp
// Enable/disable shaders
NesShaderControl.EnableShaders();
NesShaderControl.DisableShaders();
NesShaderControl.ToggleShaders();

// Switch shaders by name
NesShaderControl.SwitchShader("VHS");

// Switch shaders by enum
NesShaderControl.SwitchShader(NesShaderManager.ShaderType.RF);

// Adjust shader strength (0.5 - 3.0)
NesShaderControl.SetShaderStrength(2.0f);

// Cycle through shaders
string nextShader = NesShaderControl.CycleToNextShader();
string prevShader = NesShaderControl.CycleToPreviousShader();

// Get shader information
var info = NesShaderControl.GetShaderInfo(NesShaderManager.ShaderType.BLD);
Console.WriteLine($"{info.DisplayName}: {info.Description}");
```

### Direct Renderer Control

```csharp
// Access the renderer directly
if (NesShaderControl.CurrentRenderer != null)
{
    var renderer = NesShaderControl.CurrentRenderer;
    
    // Change interpolation mode
    renderer.InterpolationMode = BitmapInterpolationMode.NearestNeighbor;
    
    // Check if shaders are available
    bool shadersReady = renderer.UseShader;
    
    // Get current shader
    var currentShader = renderer.CurrentShaderType;
}
```

## Shader Development

### Shader File Structure

All HLSL shaders should be placed in the `Windows/Shaders/` directory. Shaders consist of:

1. **VertexShader.hlsl** - Common vertex shader for all effects
2. **[ShaderName]Shader.hlsl** - Pixel shader for each effect

### Shader Template

```hlsl
// Shader metadata (optional comments)
// DisplayName: My Shader
// CoreName: My Cool Effect
// Description: Does something awesome
// Performance: -5
// Rating: 4
// Category: Retro

cbuffer Constants : register(b0)
{
    float2 uTexSize;   // Texture dimensions
    float uTime;       // Time in seconds
    float uStrength;   // Effect strength (0.5 - 3.0)
};

Texture2D<float4> uTex : register(t0);
SamplerState uSampler : register(s0);

struct PS_INPUT
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    
    // Sample the input texture
    float3 color = uTex.Sample(uSampler, uv).rgb;
    
    // Apply your effect here
    // ...
    
    return float4(color, 1.0);
}
```

### Adding New Shaders

1. Create your shader file in `Windows/Shaders/`
2. Add the shader type to `NesShaderManager.ShaderType` enum
3. Add the mapping in `GetShaderPath()` method
4. Add shader info in `NesShaderControl.GetShaderInfo()`

The shader will automatically be available in the menu!

## Performance Considerations

### DirectBitmap vs Bitmap
- **DirectBitmap**: Uses pinned memory, zero-copy to GPU (~2x faster)
- **System.Drawing.Bitmap**: Requires memory copy, GC pressure

### Shader Performance
- Shaders run on GPU, minimal CPU impact
- Most shaders: 60 FPS on integrated graphics
- Complex shaders (VHS, BLD): May need dedicated GPU

### Optimization Tips
1. Reuse `DirectBitmap` instances (don't recreate each frame)
2. Use `InterpolationMode.NearestNeighbor` for pixel-perfect scaling
3. Disable shaders on low-end hardware if needed

## Dependencies

Required NuGet packages (already added to project):

```xml
<PackageReference Include="SharpDX" Version="4.2.0" />
<PackageReference Include="SharpDX.Direct2D1" Version="4.2.0" />
<PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" />
<PackageReference Include="SharpDX.DXGI" Version="4.2.0" />
<PackageReference Include="SharpDX.D3DCompiler" Version="4.2.0" />
<PackageReference Include="SharpDX.Mathematics" Version="4.2.0" />
```

## Troubleshooting

### DirectX Initialization Failed
- Ensure graphics drivers are up to date
- Check if DirectX 11 is supported
- Application will fall back to software rendering

### Shader Compilation Errors
- Verify shader files are in `Windows/Shaders/` directory
- Check shader syntax (HLSL shader model 4.0)
- Look for error messages in debug output

### Performance Issues
- Try disabling shaders: `NesShaderControl.DisableShaders()`
- Use simpler shaders (RF, TTF)
- Reduce shader strength: `NesShaderControl.SetShaderStrength(1.0f)`

## Architecture

```
┌─────────────────────────────────────────┐
│          MainForm.cs                    │
│  (Windows Forms Application)            │
└──────────────┬──────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│     NesDirectXRenderer (Control)        │
│  ┌───────────────────────────────────┐  │
│  │  Direct3D 11 Device & SwapChain  │  │
│  ├───────────────────────────────────┤  │
│  │  Direct2D RenderTarget (Fallback)│  │
│  ├───────────────────────────────────┤  │
│  │  NesShaderManager                 │  │
│  │  - Vertex Shader                  │  │
│  │  - Pixel Shader (hot-swappable)   │  │
│  │  - Constant Buffer                │  │
│  └───────────────────────────────────┘  │
└──────────────┬──────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│         DirectBitmap                    │
│  (Pinned memory buffer)                 │
│  - int[] Bits (BGRA format)             │
│  - IntPtr BitsPtr (for GPU copy)        │
└─────────────────────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│         NES Emulator                    │
│  byte[] GetFrameBuffer()                │
└─────────────────────────────────────────┘
```

## Credits

Based on DXRenderer from the dotNES emulator project and adapted for BrokenNes.

## License

GNU General Public License v3.0 - Same as BrokenNes project.

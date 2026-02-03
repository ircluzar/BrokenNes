# Project: Targeted Imagine (Scanline-Specific Corruption)

**Status**: Planning  
**Created**: January 31, 2026  
**Estimated Effort**: 32-40 hours  
**Priority**: High  

---

## Overview

Implement scanline-targeted Imagine Bug corruption that allows precise targeting of CPU state at specific points during frame processing, rather than only between frames. This enables users to corrupt code that executes during specific scanlines or during VBlank/post-render periods.

### Current State
- Imagine Bug captures PC state **between frames** (after RunFrame() completes)
- Targets VBlank handler exit points or idle loops
- Cannot reach code that executes mid-frame during active rendering

### Desired State
- Imagine Bug can target **any scanline** (0-239) or post-render period (240-261)
- UI allows specifying exact scanline or range of scanlines
- User can visualize which part of frame processing is being targeted
- Code executing during specific scanlines becomes accessible for corruption

---

## Architecture

### Core Components

#### 1. PPU_IMG (Imagine-Enabled PPU)

**Base**: PPU_LOW (Low Power core)  
**Purpose**: PPU variant with Imagine targeting hooks

**Key Features**:
- Inherits all functionality from PPU_LOW
- Adds scanline callback mechanism
- Supports mid-frame capture triggers
- Zero performance overhead when hooks disabled

**Location**: `Windows/NesEmulator/ppus/PPU_IMG.cs`

#### 2. Scanline Targeting System

**Components**:
- Scanline target configuration (single line, range, or all)
- CPU state capture at target moments
- Deferred corruption application (post-frame)
- Capture buffer management

**Location**: Extensions to `Windows/ImagineEngine.cs` and `Windows/NesEmulator/board/NES.cs`

#### 3. UI Integration

**GlitchHarvester Webmodule Updates**:
- Imagine tab gets new "Targeted Mode" section
- Vertical trackbar/slider for scanline selection (0-261)
- Range selection mode (from-to scanlines)
- Visual representation of frame timing
- Live preview showing targeted region

**Location**: `Windows/Webmodules/GlitchHarvester/`

---

## Technical Design

### Frame Timing Structure

The NES frame consists of 262 scanlines, each taking ~113.67 CPU cycles:

```
Scanline   0-239:  Visible rendering (240 scanlines × 113.67 cycles = 27,280 cycles)
Scanline   240:    Post-render (idle) (113.67 cycles)
Scanline   241:    VBlank start + NMI trigger (113.67 cycles)
Scanline 242-260:  VBlank period (19 scanlines × 113.67 cycles = 2,160 cycles)
Scanline   261:    Pre-render (113.67 cycles)
Total: 262 scanlines = ~29,780 CPU cycles per frame
```

### Target Categories

Users can target three distinct regions:

1. **Active Rendering** (Scanlines 0-239)
   - CPU executing game logic while PPU draws
   - Sprite/background updates
   - Collision detection
   - Audio processing

2. **Post-Render** (Scanline 240)
   - Brief idle period before VBlank
   - Some games use this for setup

3. **VBlank Period** (Scanlines 241-261)
   - NMI handler execution
   - Most game logic occurs here
   - PPU register updates
   - Idle loops waiting for next frame

### Targeting Modes

```csharp
public enum ImagineTargetMode
{
    InterFrame,      // Current behavior: between frames
    SingleScanline,  // Target specific scanline (0-261)
    ScanlineRange,   // Target range of scanlines (e.g., 100-120)
    ActiveRender,    // Target only visible scanlines (0-239)
    VBlankPeriod,    // Target only VBlank (241-261)
    FullFrame        // Capture all 262 scanlines, pick random
}
```

---

## Implementation Plan

### Phase 1: PPU_IMG Core (8 hours)

#### 1.1 Create PPU_IMG Class

**File**: `Windows/NesEmulator/ppus/PPU_IMG.cs`

```csharp
using System;

namespace NesEmulator
{
    /// <summary>
    /// Imagine-enabled PPU based on PPU_LOW with scanline capture hooks.
    /// Adds zero-overhead callback mechanism for CPU state capture during frame processing.
    /// </summary>
    public class PPU_IMG : PPU_LOW
    {
        // Core metadata
        public override string CoreName => "Imagine";
        public override string Description => "Low Power core with Imagine targeting hooks for scanline-specific corruption";
        public override int Performance => 15; // Slightly lower than LOW due to hook checks
        public override int Rating => 4;
        public override string Category => "Debug";

        // Scanline targeting state
        private ImagineTargetConfig? _targetConfig;
        private Action<ImagineCaptureData>? _captureCallback;
        
        // Per-frame capture tracking
        private int _capturesThisFrame = 0;
        private const int MaxCapturesPerFrame = 32; // Prevent excessive captures

        public PPU_IMG(Bus bus) : base(bus)
        {
        }

        /// <summary>
        /// Configure Imagine targeting parameters
        /// </summary>
        public void SetImagineTarget(ImagineTargetConfig? config, Action<ImagineCaptureData>? callback)
        {
            _targetConfig = config;
            _captureCallback = callback;
        }

        /// <summary>
        /// Clear targeting configuration (restore normal operation)
        /// </summary>
        public void ClearImagineTarget()
        {
            _targetConfig = null;
            _captureCallback = null;
            _capturesThisFrame = 0;
        }

        /// <summary>
        /// Override Step to inject scanline hooks
        /// </summary>
        public override void Step(int cycles)
        {
            // Fast path: if no targeting active, use base implementation
            if (_targetConfig == null || _captureCallback == null)
            {
                base.Step(cycles);
                return;
            }

            // Slow path: check for target scanlines during step
            int startScanline = scanline;
            base.Step(cycles);
            int endScanline = scanline;

            // Check if we crossed any target scanlines
            if (startScanline != endScanline && _capturesThisFrame < MaxCapturesPerFrame)
            {
                CheckAndFireImagineCapture(endScanline);
            }
        }

        /// <summary>
        /// Check if current scanline matches target configuration
        /// </summary>
        private void CheckAndFireImagineCapture(int currentScanline)
        {
            if (_targetConfig == null || _captureCallback == null) return;

            bool shouldCapture = _targetConfig.Mode switch
            {
                ImagineTargetMode.SingleScanline => currentScanline == _targetConfig.TargetScanline,
                ImagineTargetMode.ScanlineRange => currentScanline >= _targetConfig.RangeStart && 
                                                   currentScanline <= _targetConfig.RangeEnd,
                ImagineTargetMode.ActiveRender => currentScanline >= 0 && currentScanline <= 239,
                ImagineTargetMode.VBlankPeriod => currentScanline >= 241 && currentScanline <= 261,
                ImagineTargetMode.FullFrame => true,
                _ => false
            };

            if (shouldCapture)
            {
                try
                {
                    var capture = new ImagineCaptureData
                    {
                        Scanline = currentScanline,
                        FramePhase = DetermineFramePhase(currentScanline),
                        Timestamp = DateTime.UtcNow
                    };
                    
                    _captureCallback(capture);
                    _capturesThisFrame++;
                }
                catch { /* Ignore capture errors */ }
            }
        }

        /// <summary>
        /// Reset per-frame counters (call at frame start)
        /// </summary>
        public void ResetFrameCaptures()
        {
            _capturesThisFrame = 0;
        }

        private static FramePhase DetermineFramePhase(int scanline)
        {
            if (scanline >= 0 && scanline <= 239) return FramePhase.ActiveRender;
            if (scanline == 240) return FramePhase.PostRender;
            if (scanline >= 241 && scanline <= 261) return FramePhase.VBlank;
            return FramePhase.Unknown;
        }
    }

    /// <summary>
    /// Configuration for Imagine targeting
    /// </summary>
    public class ImagineTargetConfig
    {
        public ImagineTargetMode Mode { get; set; } = ImagineTargetMode.InterFrame;
        public int TargetScanline { get; set; } = 120; // Default: middle of screen
        public int RangeStart { get; set; } = 0;
        public int RangeEnd { get; set; } = 239;
        public bool Enabled { get; set; } = false;
    }

    public enum ImagineTargetMode
    {
        InterFrame,      // Between frames (current behavior)
        SingleScanline,  // Specific scanline
        ScanlineRange,   // Range of scanlines
        ActiveRender,    // Visible scanlines (0-239)
        VBlankPeriod,    // VBlank period (241-261)
        FullFrame        // All scanlines
    }

    /// <summary>
    /// Data captured at target scanline
    /// </summary>
    public class ImagineCaptureData
    {
        public int Scanline { get; set; }
        public FramePhase FramePhase { get; set; }
        public DateTime Timestamp { get; set; }
        
        // CPU state (populated by NES layer)
        public ushort PC { get; set; }
        public byte A { get; set; }
        public byte X { get; set; }
        public byte Y { get; set; }
        public byte P { get; set; }
        public ushort SP { get; set; }
    }

    public enum FramePhase
    {
        Unknown,
        ActiveRender,   // Scanlines 0-239
        PostRender,     // Scanline 240
        VBlank,         // Scanlines 241-261
        PreRender       // Scanline 261
    }
}
```

#### 1.2 Register PPU_IMG in CoreRegistry

The core will be automatically discovered by the existing `CoreRegistry` system since it follows the `PPU_XXX` naming convention and implements `IPPU`.

**Verification**: Check that PPU_IMG appears in PPU selection dropdown after compilation.

---

### Phase 2: NES Layer Integration (6 hours)

#### 2.1 Add Imagine Capture Buffer to NES Class

**File**: `Windows/NesEmulator/board/NES.cs`

Add after existing Imagine-related fields (around line 50):

```csharp
// === Targeted Imagine Support ===
private readonly List<ImagineCaptureData> _imagineCaptureBuffer = new List<ImagineCaptureData>(262);
public ImagineTargetConfig? ImagineTargetConfig { get; set; }
public Action<ushort, ImagineCaptureData>? ImagineTargetedShot { get; set; } // (PC, CaptureData)
```

#### 2.2 Hook PPU_IMG During Frame Processing

**File**: `Windows/NesEmulator/board/NES.cs`

In `RunFrame()` method, add at frame start (around line 610):

```csharp
public void RunFrame()
{
    if (bus == null || crashed) return;
    
    // --- Targeted Imagine: Reset frame capture buffer ---
    if (bus.ppu is PPU_IMG imgPpu)
    {
        imgPpu.ResetFrameCaptures();
        _imagineCaptureBuffer.Clear();
    }
    
    // --- Atomic snapshot capture at frame boundary (before any CPU cycles execute) ---
    if (_snapshotRequested)
    {
        // ... existing code ...
    }
```

Add capture callback registration (one-time setup, in constructor or SetupImagine method):

```csharp
private void SetupTargetedImagine()
{
    if (bus?.ppu is PPU_IMG imgPpu)
    {
        imgPpu.SetImagineTarget(ImagineTargetConfig, (captureData) =>
        {
            // Capture CPU state at this moment
            try
            {
                var regs = bus!.cpu!.GetRegisters();
                captureData.PC = regs.PC;
                captureData.A = regs.A;
                captureData.X = regs.X;
                captureData.Y = regs.Y;
                captureData.P = regs.P;
                captureData.SP = regs.SP;
                
                _imagineCaptureBuffer.Add(captureData);
            }
            catch { /* Ignore capture errors */ }
        });
    }
}
```

#### 2.3 Apply Captures at Frame End

In `RunFrame()`, after frame completes (around line 741, after `UpdateFrameBuffer()`):

```csharp
// Always update frame buffer (no frameskip) for smoother perceived motion
if (!crashed) bus!.ppu!.UpdateFrameBuffer();

// === Targeted Imagine: Apply captures if any were collected ===
if (_imagineCaptureBuffer.Count > 0 && ImagineTargetedShot != null)
{
    // Select capture based on mode
    ImagineCaptureData selectedCapture = SelectCaptureFromBuffer(_imagineCaptureBuffer);
    ImagineTargetedShot(selectedCapture.PC, selectedCapture);
    _imagineCaptureBuffer.Clear();
}

// Freeze detection (Imagine Fix mode only)
if (crashBehavior == CrashBehavior.ImagineFix && !crashed)
{
    // ... existing code ...
}
```

#### 2.4 Capture Selection Strategy

```csharp
private ImagineCaptureData SelectCaptureFromBuffer(List<ImagineCaptureData> captures)
{
    if (captures.Count == 1) return captures[0];
    
    // Strategy: Random selection (can be made configurable later)
    int index = CorruptRnd.Next(captures.Count);
    return captures[index];
    
    // Alternative strategies (future):
    // - First capture (earliest in frame)
    // - Last capture (latest in frame)
    // - Most unique PC (highest variance)
    // - Weighted by frame phase
}
```

---

### Phase 3: ImagineEngine Integration (8 hours)

#### 3.1 Add Targeted Imagine Support

**File**: `Windows/ImagineEngine.cs`

Add new method for targeted imagine:

```csharp
/// <summary>
/// Targeted Imagine Bug: Apply corruption at specific PC captured during scanline
/// </summary>
public bool ImagineTargetedBug(ushort pc, ImagineCaptureData captureData)
{
    if (!ModelLoaded)
    {
        LastError = "Model not loaded";
        return false;
    }

    // Validate PC is in PRG ROM
    if (pc < 0x8000 || pc > 0xFFFF)
    {
        LastError = $"PC ${pc:X4} not in PRG ROM range";
        return false;
    }

    try
    {
        // Generate patch at captured PC
        var bytes = GeneratePatch(pc, BytesToGenerate);
        
        // Apply patch
        bool applied = ApplyPatch(pc, bytes);
        
        if (applied)
        {
            // Add metadata to stash entry about scanline targeting
            try
            {
                var lastEntry = corruptor.GhStash.LastOrDefault();
                if (lastEntry != null)
                {
                    lastEntry.Name = $"IMG SL{captureData.Scanline} PC=${pc:X4} " +
                                   $"{captureData.FramePhase} L={bytes.Length} E{Epoch}";
                }
            }
            catch { }
        }
        
        return applied;
    }
    catch (Exception ex)
    {
        LastError = ex.Message;
        return false;
    }
}
```

#### 3.2 Wire to NES Layer

**File**: `Windows/ImagineEngine.cs`

Update constructor or add setup method:

```csharp
public void SetupTargetedImagine()
{
    nes.ImagineTargetedShot = (pc, captureData) =>
    {
        ImagineTargetedBug(pc, captureData);
    };
}
```

---

### Phase 4: UI - Scanline Selector (10 hours)

#### 4.1 Update GlitchHarvester HTML

**File**: `Windows/Webmodules/GlitchHarvester/index.html`

Add new section to Imagine tab (after existing Imagine controls):

```html
<!-- Targeted Imagine Section -->
<div class="gh-section-subtitle">Targeted Imagine (Scanline Mode)</div>

<div class="gh-input-row">
  <label>
    <input type="checkbox" id="chkTargetedImagine">
    <span>Enable Scanline Targeting</span>
  </label>
  <div class="gh-tooltip">ⓘ
    <span class="gh-tooltip-text">
      Target specific scanlines during frame processing instead of between frames.
      Allows corruption of code executing during active rendering or VBlank.
    </span>
  </div>
</div>

<!-- Target Mode Selection -->
<div class="gh-input-row" id="targetModeRow" style="display: none;">
  <label for="targetModeSelect">Target Mode:</label>
  <select id="targetModeSelect" class="gh-select">
    <option value="SingleScanline">Single Scanline</option>
    <option value="ScanlineRange">Scanline Range</option>
    <option value="ActiveRender">Active Render (0-239)</option>
    <option value="VBlankPeriod">VBlank Period (241-261)</option>
    <option value="FullFrame">Full Frame (All)</option>
  </select>
</div>

<!-- Single Scanline Selector -->
<div class="gh-scanline-selector" id="singleScanlineSelector" style="display: none;">
  <div class="gh-input-row">
    <label for="targetScanline">Target Scanline:</label>
    <input type="range" id="targetScanline" class="gh-slider" 
           min="0" max="261" value="120" step="1">
    <span id="targetScanlineValue" class="gh-value">120</span>
  </div>
  
  <!-- Visual Frame Map -->
  <div class="gh-frame-map">
    <canvas id="frameMapCanvas" width="400" height="262"></canvas>
    <div class="gh-frame-legend">
      <span class="legend-active">Active Render (0-239)</span>
      <span class="legend-post">Post (240)</span>
      <span class="legend-vblank">VBlank (241-261)</span>
      <span class="legend-target">● Target</span>
    </div>
  </div>
</div>

<!-- Range Scanline Selector -->
<div class="gh-scanline-range" id="scanlineRangeSelector" style="display: none;">
  <div class="gh-input-row">
    <label for="rangeStart">Start Scanline:</label>
    <input type="range" id="rangeStart" class="gh-slider" 
           min="0" max="261" value="100" step="1">
    <span id="rangeStartValue" class="gh-value">100</span>
  </div>
  
  <div class="gh-input-row">
    <label for="rangeEnd">End Scanline:</label>
    <input type="range" id="rangeEnd" class="gh-slider" 
           min="0" max="261" value="139" step="1">
    <span id="rangeEndValue" class="gh-value">139</span>
  </div>
  
  <div class="gh-range-summary">
    Targeting <span id="rangeCount">40</span> scanlines
  </div>
</div>

<!-- Status Display -->
<div class="gh-targeted-status" id="targetedImagineStatus">
  <strong>Status:</strong> <span id="targetStatusText">Ready</span>
  <div class="target-info">
    <small>Last capture: <span id="lastCaptureInfo">None</span></small>
  </div>
</div>
```

#### 4.2 Add CSS Styles

**File**: `Windows/Webmodules/GlitchHarvester/styles.css`

```css
/* Targeted Imagine Controls */
.gh-scanline-selector,
.gh-scanline-range {
  margin-top: 1rem;
  padding: 1rem;
  background: rgba(0, 0, 0, 0.2);
  border-radius: 4px;
}

.gh-frame-map {
  margin-top: 1rem;
  text-align: center;
}

#frameMapCanvas {
  border: 2px solid var(--primary);
  border-radius: 4px;
  background: #000;
  display: block;
  margin: 0 auto;
  cursor: crosshair;
}

.gh-frame-legend {
  margin-top: 0.5rem;
  display: flex;
  justify-content: center;
  gap: 1rem;
  font-size: 0.85rem;
}

.legend-active::before { content: '■ '; color: #4CAF50; }
.legend-post::before { content: '■ '; color: #FF9800; }
.legend-vblank::before { content: '■ '; color: #2196F3; }
.legend-target { color: #FF00FF; font-weight: bold; }

.gh-range-summary {
  margin-top: 0.5rem;
  text-align: center;
  font-weight: bold;
  color: var(--yellow);
}

.gh-targeted-status {
  margin-top: 1rem;
  padding: 0.75rem;
  background: rgba(0, 0, 0, 0.3);
  border-radius: 4px;
  border-left: 3px solid var(--purple);
}

.target-info {
  margin-top: 0.5rem;
  opacity: 0.7;
}
```

#### 4.3 JavaScript Implementation

**File**: `Windows/Webmodules/GlitchHarvester/glitch-harvester.js`

Add to elements object:

```javascript
// Targeted Imagine elements
chkTargetedImagine: null,
targetModeSelect: null,
targetModeRow: null,
singleScanlineSelector: null,
scanlineRangeSelector: null,
targetScanline: null,
targetScanlineValue: null,
rangeStart: null,
rangeStartValue: null,
rangeEnd: null,
rangeEndValue: null,
rangeCount: null,
frameMapCanvas: null,
targetStatusText: null,
lastCaptureInfo: null,
```

Initialize in `initializeElements()`:

```javascript
// Targeted Imagine
elements.chkTargetedImagine = document.getElementById('chkTargetedImagine');
elements.targetModeSelect = document.getElementById('targetModeSelect');
elements.targetModeRow = document.getElementById('targetModeRow');
elements.singleScanlineSelector = document.getElementById('singleScanlineSelector');
elements.scanlineRangeSelector = document.getElementById('scanlineRangeSelector');
elements.targetScanline = document.getElementById('targetScanline');
elements.targetScanlineValue = document.getElementById('targetScanlineValue');
elements.rangeStart = document.getElementById('rangeStart');
elements.rangeStartValue = document.getElementById('rangeStartValue');
elements.rangeEnd = document.getElementById('rangeEnd');
elements.rangeEndValue = document.getElementById('rangeEndValue');
elements.rangeCount = document.getElementById('rangeCount');
elements.frameMapCanvas = document.getElementById('frameMapCanvas');
elements.targetStatusText = document.getElementById('targetStatusText');
elements.lastCaptureInfo = document.getElementById('lastCaptureInfo');
```

Add event listeners in `attachEventListeners()`:

```javascript
// Targeted Imagine
elements.chkTargetedImagine.addEventListener('change', toggleTargetedImagine);
elements.targetModeSelect.addEventListener('change', updateTargetMode);
elements.targetScanline.addEventListener('input', () => {
  elements.targetScanlineValue.textContent = elements.targetScanline.value;
  drawFrameMap();
});
elements.rangeStart.addEventListener('input', () => {
  elements.rangeStartValue.textContent = elements.rangeStart.value;
  updateRangeCount();
  drawFrameMap();
});
elements.rangeEnd.addEventListener('input', () => {
  elements.rangeEndValue.textContent = elements.rangeEnd.value;
  updateRangeCount();
  drawFrameMap();
});

// Frame map canvas click for direct scanline selection
if (elements.frameMapCanvas) {
  elements.frameMapCanvas.addEventListener('click', onFrameMapClick);
}
```

Implement functions:

```javascript
// ==================== Targeted Imagine ====================

async function toggleTargetedImagine() {
  const enabled = elements.chkTargetedImagine.checked;
  console.log('[Imagine] Targeted mode:', enabled);
  
  // Show/hide controls
  elements.targetModeRow.style.display = enabled ? 'flex' : 'none';
  
  if (enabled) {
    updateTargetMode();
    drawFrameMap();
  } else {
    // Hide all selectors
    elements.singleScanlineSelector.style.display = 'none';
    elements.scanlineRangeSelector.style.display = 'none';
  }
  
  // Update API
  await api.imagine.setTargetedMode(enabled, getTargetConfig());
}

function updateTargetMode() {
  const mode = elements.targetModeSelect.value;
  console.log('[Imagine] Target mode:', mode);
  
  // Show/hide appropriate selector
  elements.singleScanlineSelector.style.display = 
    mode === 'SingleScanline' ? 'block' : 'none';
  elements.scanlineRangeSelector.style.display = 
    mode === 'ScanlineRange' ? 'block' : 'none';
  
  drawFrameMap();
}

function updateRangeCount() {
  const start = parseInt(elements.rangeStart.value);
  const end = parseInt(elements.rangeEnd.value);
  const count = Math.abs(end - start) + 1;
  elements.rangeCount.textContent = count;
}

function drawFrameMap() {
  const canvas = elements.frameMapCanvas;
  if (!canvas) return;
  
  const ctx = canvas.getContext('2d');
  const width = canvas.width;
  const height = canvas.height;
  
  // Clear
  ctx.fillStyle = '#000';
  ctx.fillRect(0, 0, width, height);
  
  // Draw scanline regions
  const lineHeight = height / 262;
  
  for (let i = 0; i < 262; i++) {
    const y = i * lineHeight;
    
    // Color by phase
    if (i <= 239) {
      ctx.fillStyle = '#4CAF50'; // Active render - green
    } else if (i === 240) {
      ctx.fillStyle = '#FF9800'; // Post-render - orange
    } else {
      ctx.fillStyle = '#2196F3'; // VBlank - blue
    }
    
    ctx.fillRect(0, y, width, lineHeight);
  }
  
  // Draw target overlay
  ctx.fillStyle = 'rgba(255, 0, 255, 0.5)'; // Purple highlight
  
  const mode = elements.targetModeSelect.value;
  
  if (mode === 'SingleScanline') {
    const line = parseInt(elements.targetScanline.value);
    const y = line * lineHeight;
    ctx.fillRect(0, y, width, lineHeight * 2); // Draw slightly thicker
  } else if (mode === 'ScanlineRange') {
    const start = parseInt(elements.rangeStart.value);
    const end = parseInt(elements.rangeEnd.value);
    const y1 = Math.min(start, end) * lineHeight;
    const y2 = Math.max(start, end) * lineHeight;
    ctx.fillRect(0, y1, width, (y2 - y1) + lineHeight);
  } else if (mode === 'ActiveRender') {
    ctx.fillRect(0, 0, width, 240 * lineHeight);
  } else if (mode === 'VBlankPeriod') {
    ctx.fillRect(0, 241 * lineHeight, width, 21 * lineHeight);
  } else if (mode === 'FullFrame') {
    ctx.fillRect(0, 0, width, height);
  }
  
  // Draw scanline numbers at key points
  ctx.fillStyle = '#fff';
  ctx.font = '10px monospace';
  ctx.fillText('0', 5, 10);
  ctx.fillText('120', 5, 120 * lineHeight + 10);
  ctx.fillText('239', 5, 239 * lineHeight + 10);
  ctx.fillText('240', 5, 240 * lineHeight + 10);
  ctx.fillText('241', 5, 241 * lineHeight + 10);
  ctx.fillText('261', 5, 261 * lineHeight + 10);
}

function onFrameMapClick(event) {
  const canvas = elements.frameMapCanvas;
  const rect = canvas.getBoundingClientRect();
  const y = event.clientY - rect.top;
  const scanline = Math.floor((y / rect.height) * 262);
  
  console.log('[Imagine] Frame map clicked at scanline', scanline);
  
  // Set target scanline
  elements.targetScanline.value = scanline;
  elements.targetScanlineValue.textContent = scanline;
  
  // Switch to single scanline mode
  elements.targetModeSelect.value = 'SingleScanline';
  updateTargetMode();
  
  // Redraw
  drawFrameMap();
}

function getTargetConfig() {
  const mode = elements.targetModeSelect.value;
  
  return {
    mode: mode,
    targetScanline: parseInt(elements.targetScanline.value),
    rangeStart: parseInt(elements.rangeStart.value),
    rangeEnd: parseInt(elements.rangeEnd.value),
    enabled: elements.chkTargetedImagine.checked
  };
}
```

---

### Phase 5: Web API Endpoints (4 hours)

#### 5.1 Add Targeted Imagine Endpoints

**File**: `Windows/webapi/WebApiServer.Endpoints.Imagine.cs`

Add after existing Imagine endpoints:

```csharp
// POST /api/imagine/set-targeted-mode - Configure scanline targeting
app.MapPost("/api/imagine/set-targeted-mode", async (HttpContext context) =>
{
    try
    {
        var body = await context.Request.ReadFromJsonAsync<TargetedImagineRequest>();
        if (body == null)
        {
            return Results.BadRequest(new { success = false, error = "Invalid request body" });
        }

        var nes = _getNes();
        if (nes == null)
        {
            return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
        }

        // Create target config
        var config = body.Enabled ? new ImagineTargetConfig
        {
            Mode = Enum.Parse<ImagineTargetMode>(body.Mode),
            TargetScanline = body.TargetScanline,
            RangeStart = body.RangeStart,
            RangeEnd = body.RangeEnd,
            Enabled = true
        } : null;

        nes.ImagineTargetConfig = config;

        // Switch to PPU_IMG if targeting enabled and not already using it
        if (body.Enabled && nes.GetPpuCoreId() != "PPU_IMG")
        {
            try
            {
                // Save current PPU state
                var ppuState = nes.GetPpuState();
                
                // Switch to IMG core
                nes.SetPpuCore("IMG");
                
                // Restore state
                if (ppuState != null)
                {
                    nes.SetPpuState(ppuState);
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new 
                { 
                    success = false, 
                    error = $"Failed to switch to PPU_IMG: {ex.Message}" 
                });
            }
        }

        return Results.Ok(new
        {
            success = true,
            config = config,
            ppuCore = nes.GetPpuCoreId()
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = ex.Message });
    }
});

// GET /api/imagine/targeted-status - Get current targeted imagine status
app.MapGet("/api/imagine/targeted-status", () =>
{
    var nes = _getNes();
    if (nes == null)
    {
        return Results.BadRequest(new { success = false, error = "Emulator not initialized" });
    }

    return Results.Ok(new
    {
        success = true,
        enabled = nes.ImagineTargetConfig != null,
        config = nes.ImagineTargetConfig,
        ppuCore = nes.GetPpuCoreId(),
        isImgCore = nes.GetPpuCoreId() == "PPU_IMG"
    });
});

// Request DTO
private record TargetedImagineRequest(
    bool Enabled,
    string Mode,
    int TargetScanline,
    int RangeStart,
    int RangeEnd
);
```

#### 5.2 Update Web API Client

**File**: `Windows/Webmodules/shared/webapi.js`

Add to `api.imagine` object:

```javascript
imagine: {
  // ... existing methods ...
  
  async setTargetedMode(enabled, config) {
    return await fetch(`${BASE_URL}/api/imagine/set-targeted-mode`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        enabled: enabled,
        mode: config.mode || 'SingleScanline',
        targetScanline: config.targetScanline || 120,
        rangeStart: config.rangeStart || 0,
        rangeEnd: config.rangeEnd || 239
      })
    }).then(r => r.json());
  },
  
  async getTargetedStatus() {
    return await fetch(`${BASE_URL}/api/imagine/targeted-status`)
      .then(r => r.json());
  }
}
```

---

### Phase 6: Testing & Validation (4 hours)

#### Test Cases

1. **Basic Functionality**
   - [ ] PPU_IMG loads and runs without errors
   - [ ] Targeted mode can be enabled/disabled
   - [ ] UI shows/hides controls appropriately

2. **Single Scanline Targeting**
   - [ ] Selecting scanline 0 captures during first visible scanline
   - [ ] Selecting scanline 120 captures during middle of screen
   - [ ] Selecting scanline 239 captures during last visible scanline
   - [ ] Selecting scanline 241 captures during VBlank start

3. **Range Targeting**
   - [ ] Range 0-10 captures from early scanlines
   - [ ] Range 230-239 captures from late visible scanlines
   - [ ] Range 241-261 captures during VBlank only

4. **Mode Targeting**
   - [ ] ActiveRender mode captures only 0-239
   - [ ] VBlankPeriod mode captures only 241-261
   - [ ] FullFrame mode captures from all scanlines

5. **Corruption Application**
   - [ ] Targeted captures result in PRG ROM corruption
   - [ ] Stash entries show correct scanline metadata
   - [ ] PC values differ from inter-frame captures
   - [ ] Corruption affects visible output in next frame

6. **Performance**
   - [ ] No slowdown when targeted imagine disabled
   - [ ] Minimal slowdown (~5%) when enabled with single target
   - [ ] Acceptable slowdown (~10%) with full frame capture

7. **Edge Cases**
   - [ ] Handles invalid scanline numbers gracefully
   - [ ] Works with different PPU cores (can switch to/from IMG)
   - [ ] Preserves PPU state when switching cores
   - [ ] Doesn't crash if capture callback fails

---

## User Workflow

### Typical Usage Flow

1. **Load ROM and start emulation**
2. **Navigate to GlitchHarvester Imagine tab**
3. **Enable "Scanline Targeting" checkbox**
4. **Select target mode**:
   - Single scanline: Choose specific line with slider
   - Range: Set start/end scanlines
   - Quick mode: Select Active Render or VBlank Period
5. **Press "Imagine a Bug" button**
   - Next frame will capture PC at target scanline(s)
   - Corruption applies at frame end
   - Visual effect appears in following frame
6. **Adjust target and repeat**
   - Use frame map visualization to understand frame structure
   - Click directly on frame map to target specific scanline
7. **Compare results**
   - Stash shows which scanline each corruption came from
   - Different scanlines reveal different code paths

---

## Advanced Features (Future)

### Phase 7: Enhancements (Optional)

1. **PC Heatmap Visualization**
   - Track PC diversity per scanline over 60 frames
   - Display color-coded map showing "interesting" scanlines
   - Automatically suggest optimal targets

2. **Multi-Frame Capture Mode**
   - Capture same scanline over N frames
   - Build histogram of PC values
   - Apply corruption to most common or most diverse PC

3. **Code Coverage Analysis**
   - Track which PRG addresses are hit per scanline
   - Generate report showing unreachable code
   - Identify "dead zones" in ROM

4. **Temporal Corruption**
   - Corrupt scanline X on frame 1, scanline Y on frame 2
   - Create rotating corruption patterns
   - Animated glitch effects

5. **Frame Phase Auto-Detection**
   - Analyze game to determine actual VBlank handler location
   - Automatically target "most interesting" scanlines
   - Machine learning for optimal target selection

---

## Technical Notes

### Why PPU_IMG Instead of Modifying Existing?

1. **Zero Impact When Unused**: Games not using targeted imagine have zero overhead
2. **Easy A/B Testing**: Can compare IMG vs LOW performance side-by-side
3. **Stability**: Doesn't risk breaking existing PPU cores
4. **Debug Mode**: IMG is clearly marked as a debug/development core

### Performance Considerations

**Overhead Analysis**:
- Hook check: `if (config != null)` - **~1ns per check**
- Scanline change detection: **~2ns per step**
- Capture callback: **~50ns when triggered**
- Buffer management: **~10ns per frame**

**Total Impact**:
- Disabled: **0.01% slowdown** (negligible)
- Single target: **~5% slowdown** (acceptable)
- Full frame (262 captures): **~10% slowdown** (noticeable but usable)

### Memory Usage

- `ImagineCaptureData`: 32 bytes per capture
- Max buffer size: 262 scanlines × 32 bytes = **8.4 KB** per frame
- Negligible compared to framebuffer (256×240×4 = 245 KB)

---

## Dependencies

### Required
- ✅ Existing PPU architecture (IPPU interface)
- ✅ Existing ImagineEngine
- ✅ Existing Corruptor/GlitchHarvester
- ✅ Web API infrastructure

### Optional
- 🔲 Enhanced visualization (canvas drawing)
- 🔲 Statistics/analytics framework
- 🔲 Export/import of target configurations

---

## Risks & Mitigations

### Risk 1: Performance Impact
**Mitigation**: Fast-path check when disabled, limited captures per frame

### Risk 2: Capture Timing Accuracy
**Mitigation**: PPU step granularity is cycle-accurate, captures happen precisely at scanline boundaries

### Risk 3: UI Complexity
**Mitigation**: Progressive disclosure - advanced features hidden by default

### Risk 4: PPU State Switching
**Mitigation**: Save/restore PPU state when switching to/from IMG core

---

## Success Metrics

- [ ] Can target specific scanlines reliably
- [ ] Captures show different PC values than inter-frame
- [ ] UI is intuitive (usability testing with 2-3 users)
- [ ] Performance impact < 10% when enabled
- [ ] Zero bugs in first week of production use
- [ ] Users report finding "new" corruption targets

---

## Timeline

| Phase | Duration | Dependencies | Deliverable |
|-------|----------|--------------|-------------|
| 1. PPU_IMG Core | 8 hours | None | Working PPU_IMG class |
| 2. NES Integration | 6 hours | Phase 1 | Capture buffer & hooks |
| 3. ImagineEngine | 8 hours | Phase 2 | Targeted corruption logic |
| 4. UI Implementation | 10 hours | Phase 3 | Scanline selector UI |
| 5. Web API | 4 hours | Phase 4 | API endpoints |
| 6. Testing | 4 hours | Phase 5 | Validated feature |
| **Total** | **40 hours** | | **Complete feature** |

---

## Future Considerations

- Integration with save states (capture per-scanline across saved moments)
- Export of "interesting scanline maps" for sharing
- Automated scanline selection based on game analysis
- Real-time PC diversity display during emulation
- Integration with achievement system (trigger on specific scanlines)

---

## Questions & Decisions

### Q: Should captures be applied immediately or at frame end?
**A**: Frame end (current design) - safer, prevents mid-frame corruption artifacts

### Q: Should we support sub-scanline targeting (PPU cycles)?
**A**: No (v1) - scanline granularity is sufficient, CPU cycle precision would be overkill

### Q: How to handle games with irregular frame timing?
**A**: Captures are based on scanline count, not time - works for all games

### Q: Should IMG be default PPU?
**A**: No - LOW remains default, IMG is opt-in for targeted imagine

---

**Status**: Ready for implementation  
**Next Action**: Begin Phase 1 (PPU_IMG creation)

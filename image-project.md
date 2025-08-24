## Image Project: Inline SVGs for Core Cards

This document defines the specs, design, and tasks to generate minimal, abstract SVG “chip” images for every core (CPU, PPU, APU, Clock; shaders optional). The images will be embedded directly in code via a SvgFactory to avoid extra asset files.

### Objectives
- Derive exact image slot specs from the existing card generator.
- Define a simple, consistent SVG style that scales well within the card.
- Specify the SvgFactory contract (naming, lookup, fallbacks) the card renderer will use.
- Plan motifs for each core variant to keep visuals meaningful yet minimal.
- Track implementation work with actionable checklists.

## Card image slot specs
Pulled from `Shared/CardSvgRenderer.cs` (base canvas 240×340):

- Base card size: 240×340 (scaled via width/height; internal layout uses base coordinates)
- Outer padding (pad): 14
- Header height: 40
- Image area position: translate(14, 54) because y = pad + headerH = 14 + 40
- Image area size: width = contentW = 240 − 2×14 = 212, height = 130
- Aspect ratio: 212:130 ≈ 1.631 (keep artwork within this aspect)
- Below the image area there is 25px spacing before the description box.

Practical guidance:
- Provide SVG with viewBox="0 0 212 130" so the art fits precisely into the slot.
- Don’t rely on external assets; use inline shapes and fills only.
- No background fill required (card background is black). If using a panel, keep it subtle (#0b0f14 to #111).
- Target a retro-minimal look: 1–3 flat colors + 1 accent; simple geometry; crisp edges; limited strokes (1–2 px in base units).
- Avoid text in the image; names and performance already appear elsewhere in the card.
- Favor symmetry and centered composition so scaling is robust across thumbnails and zoom modal.

## SvgFactory design

We’ll expose inline SVG strings via properties named with the component’s full ID (PREFIX_SUFFIX). Examples:
- CPU and LOW → CPU_LOW
- PPU and FMC → PPU_FMC
- APU and QN → APU_QN
- CLOCK and TRB → CLOCK_TRB

Contract:
- Namespace: `BrokenNes.Shared`
- Class: `SvgFactory` (static)
- Shape: `public static string CPU_LOW => "<svg .../>";` per asset
- Required viewBox: `0 0 212 130` (fits the card’s image slot exactly)
- Style: self-contained; no fonts; avoid external hrefs; use simple fills/strokes
- Accessor: optional helper `Get(string prefix, string id)` → returns matching property or null
- Fallbacks: if no exact match, the card renderer can draw the current dashed placeholder (no change in behavior when missing)

Minimal example property (style template; replace shapes/colors per core):

```csharp
namespace BrokenNes.Shared
{
  public static class SvgFactory
  {
    // Example baseline chip motif (replace with per-core variants)
    public static string CPU_FMC =>
      "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 212 130' role='img' aria-label='CPU_FMC'>" +
      "  <defs>" +
      "    <linearGradient id='g' x1='0' y1='0' x2='0' y2='1'>" +
      "      <stop offset='0' stop-color='#1a1f2a'/>" +
      "      <stop offset='1' stop-color='#0f131b'/>" +
      "    </linearGradient>" +
      "  </defs>" +
      "  <g>" +
      "    <rect x='10' y='18' width='192' height='94' rx='6' fill='url(#g)' stroke='#9ca3af' stroke-width='2'/>" +
      "    <!-- pins -->" +
      "    <g fill='#9ca3af'>" +
      "      " + string.Join("", System.Linq.Enumerable.Range(0, 10).Select(i => $"<rect x='{12 + i*18}' y='14' width='10' height='4' rx='1' />")) +
      "      " + string.Join("", System.Linq.Enumerable.Range(0, 10).Select(i => $"<rect x='{12 + i*18}' y='112' width='10' height='4' rx='1' />")) +
      "    </g>" +
      "    <!-- die -->" +
      "    <rect x='64' y='38' width='84' height='54' rx='3' fill='#111827' stroke='#6b7280' stroke-width='2'/>" +
      "  </g>" +
      "</svg>";

    // ... more properties like public static string CPU_LOW => "<svg...";

    public static string? Get(string prefix, string id)
    {
      var key = (prefix + "_" + id).ToUpperInvariant();
      var prop = typeof(SvgFactory).GetProperty(key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
      return prop?.GetValue(null) as string;
    }
  }
}
```

Card wiring (future change): in `CardSvgRenderer`, replace the dashed placeholder rect with injected raw SVG if available:
- Determine key: from the model’s category domain and Id (e.g., CPU + m.Id)
- Lookup: `SvgFactory.Get("CPU", m.Id)` (or `PPU`, `APU`, `CLOCK`)
- If found: emit the SVG markup inside the image group; else keep the current dashed placeholder

## Visual motifs per core
Keep each as a “chip” with variant cues. Use the same base silhouette (rounded rectangle + pins) for cohesion; vary pins, inner die, accents, and simple overlays.

Legend of cues:
- Performance up (SPD/EIL): chevrons or motion bars; brighter edge
- Low power (LOW/QLOW): fewer or trimmed pins; hollow die; energy icon subdued
- Low quality (LQ/QLQ): slight crack/scanline tears; desaturated accent
- Standard (FMC): neutral; classic chip
- Enhanced (EIL/CUBE/BFR/WF/MNES): inner die glow, extra sub-shapes, waveform or tile cues
- Degraded: muted accent; slight misalignments or notches
- Unstable (TRB/CUBE/QN): jaggy zig-zag or flicker ticks
- CLR: C#-hinted brace-like inner mask or cog motif

### CPU
- [ ] CPU_FMC — baseline chip; 10 pins top/bottom; neutral gray border
- [ ] CPU_LOW — reduced pins; inner die outline only; small battery/leaf-like notch
- [ ] CPU_SPD — speed chevrons on die; brighter border edge; subtle diagonal stripes
- [ ] CPU_EIL — microcode grid overlay (fine squares); emerald accent; slight glow on die
- [ ] CPU_LW2 — experimental notch on one corner; subtle off-center die; amber accent

### PPU
- [ ] PPU_FMC — baseline chip with a tiny 2×2 tile grid etched on die
- [ ] PPU_LOW — fewer pins; tile grid with fewer cells; subdued accent
- [ ] PPU_LQ — scanline tear across die; low-contrast fill
- [ ] PPU_SPD — motion bars left→right across die; bright edge
- [ ] PPU_EIL — fine grid overlay and sharp corner highlights; emerald accent
- [ ] PPU_BFR — subtle bleed bars protruding from die edges (2–3 bars)
- [ ] PPU_CUBE — die filled with isometric cubes or checker mini-squares

### APU
- [ ] APU_FMC — baseline chip; small sine+square wave pair etched on die
- [ ] APU_LOW — thinner waveform; reduced pins; subdued accent
- [ ] APU_LQ — jagged/noisy waveform; crack-like notch
- [ ] APU_LQ2 — doubled noise motif; two small cracks; more muted palette
- [ ] APU_QLOW — QuickNES-like low-power: waveform with reduced amplitude; fewer pins
- [ ] APU_QLQ — QuickNES-like low quality: wobbly waveform; faint misalignment
- [ ] APU_QLQ2 — stronger wobble + two-phase offset
- [ ] APU_SPD — fast waveform with motion blur bars; brighter edge
- [ ] APU_SPD2 — experimental jank: waveform with stepped segments
- [ ] APU_QN — cleaner waveform with slight wobble marks
- [ ] APU_MNES — MIDI jack/din-like 5-dot arc etched on die
- [ ] APU_WF — musical note + small soundbar icon on die

### Clock
- [ ] CLOCK_FMC — analog tick mark ring on die; neutral accent
- [ ] CLOCK_CLR — inner cog/gear ring; brace-like corners; blue accent
- [ ] CLOCK_TRB — turbo bolt/chevron overlay; bright amber/green edge

### Shaders (optional phase)
Shaders aren’t “cores” but are listed in the UI. If we include them later, use a shared chip silhouette with a small symbol (CRT mask, hue wheel, pixel grid) and the shader’s category to pick accent color. We’ll scope these after core images ship.

## Implementation tasks

### SvgFactory skeleton
- [ ] Create `Shared/SvgFactory.cs` with `namespace BrokenNes.Shared;`
- [ ] Add properties for each CPU_* from reflection list (FMC, LOW, SPD, EIL, LW2)
- [ ] Add properties for each PPU_* (FMC, LOW, LQ, SPD, EIL, BFR, CUBE)
- [ ] Add properties for each APU_* (FMC, LOW, LQ, LQ2, QLOW, QLQ, QLQ2, SPD, SPD2, QN, MNES, WF)
- [ ] Add properties for each CLOCK_* (FMC, CLR, TRB)
- [ ] Include `public static string? Get(string prefix, string id)` helper (case-insensitive)
- [ ] Ensure each SVG uses `viewBox="0 0 212 130"` and no external refs

### Card renderer integration
- [ ] In `CardSvgRenderer.Render`, compute domain prefix: CPU/PPU/APU/CLOCK from the model being rendered
- [ ] Lookup SVG via `SvgFactory.Get(prefix, m.Id)`
- [ ] If non-null, inject raw SVG in place of the dashed placeholder group; else keep existing placeholder
- [ ] Keep current accessibility: outer SVG role/aria-label remains; embedded SVGs include a brief aria-label

### Visual production
- [ ] Establish a tiny theme palette (e.g., neutral: #9ca3af, die: #111827, accent per category)
- [ ] Build the base chip silhouette (rect + pins) as first reusable snippet
- [ ] Produce CPU variants (5)
- [ ] Produce PPU variants (7)
- [ ] Produce APU variants (12)
- [ ] Produce Clock variants (3)
- [ ] Review on dark and light backgrounds (safe without background fill)
- [ ] Validate at card thumb (166×235 render) and modal sizes (scales cleanly)

### Quality gates
- [ ] Build passes (`dotnet build -c Release`)
- [ ] No runtime exceptions when rendering cards without/with art
- [ ] Visual QA: no overflow outside 212×130 slot; crisp scaling; consistent style

## Acceptance criteria
- Card maintains layout; embedded SVGs fit exactly in the 212×130 slot with clean scaling
- SvgFactory covers all discovered core IDs; missing entries fall back to current placeholder
- Visual motifs are minimal, abstract, and thematically consistent across cores
- No added static asset files; all art is inline string literals in `SvgFactory`

## Notes
- Fonts: the app preloads fonts for other parts of the UI; embedded SVGs should not use text to avoid font dependencies.
- Accessibility: keep contrast acceptable against dark background; shapes only is fine; aria-labels are helpful but optional inside the nested SVGs.
- Performance: string literals are tiny; reflection in `Get` is amortized and only on render; we can cache lookups later if needed.

# Card Identity Worksheet

## Goal

Bring Backgrounds, Null Providers, Webmodules, and Features up to the same card quality bar as the core cards.

Each in-scope card should end with:

- unique authored metadata
- unique authored SVG identity
- clear thematic intent
- no dependence on generic metadata or generic fallback art as the shipped path

## Locked Rules

- [x] All in-scope non-core cards must become unique in both metadata and design.
- [x] Features are a first-class `FEATURE` card domain.
- [x] Generic builders and fallback art are allowed only for unknown or future out-of-catalog items.

## Open Decisions

- [ ] Decide whether test and internal webmodules need a separate presentation tier.
- [ ] Decide how ratings should be weighted: rarity, spectacle, complexity, or blended intent.
- [ ] Decide whether the authored metadata source lives in code or in a dedicated manifest file.

## Definition Of Done

- [ ] Every Background card has authored metadata and a unique visual mark.
- [ ] Every Null Provider card has authored metadata and a unique visual mark.
- [ ] Every Webmodule card has authored metadata and a unique visual mark.
- [ ] Every Feature card exists as a first-class card and has authored metadata and a unique visual mark.
- [ ] No known in-scope card uses generic description builders as its primary source.
- [ ] No known in-scope card uses `CardSvgRenderer` fallback art as its primary shipped art path.
- [x] The Cores surface displays `FEATURE` alongside the existing non-core domains.

## Shared System Tasks

- [ ] Freeze the final in-scope list for Backgrounds, Null Providers, Webmodules, and Features.
- [ ] Finalize the non-core metadata schema.
- [ ] Define tone and naming rules for non-core descriptions.
- [ ] Build the authored metadata source for all non-core cards.
- [x] Add `FEATURE` to the roster/card domain model.
- [x] Extend `/api/progression/roster` to emit `FEATURE` cards.
- [x] Update the Cores webmodule to group and render `FEATURE` cards.
- [ ] Replace generic metadata builders with authored lookups for known cards.
- [ ] Register bespoke SVG art for every in-scope card.
- [ ] Reserve fallback art for unknown and unregistered items only.
- [ ] Run a final pass for grouping, sorting, modal views, and unlock behavior.

## Per-Card Checklist Template

For each card below, finish these before marking the card complete.

- [ ] Metadata authored
- [ ] SVG emblem authored
- [ ] Manifest or registry wiring complete
- [ ] UI and API QA complete

## Vertical Slice Candidate

- [x] Pick 3 Backgrounds for the first slice.
- [x] Pick 3 Null Providers for the first slice.
- [ ] Pick 3 Webmodules for the first slice.
- [ ] Pick 2 Features for the first slice.
- [ ] Prove schema, art integration, and `FEATURE` roster flow before scaling to the full catalog.

## Background Cards

Count: 29

### Gradient (Default)
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### None (Black)
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### AnimatedWave
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### AnimatedBubble
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### BelousovZhabotinsky
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### BreathingGradients
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### CalmWaterReflection
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### CliffordAttractor
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### ComplexDomainColoring
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### DeJongAttractor
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### DriftingClouds
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### FlowingAurora
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### FractalFlame
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### GentleRipples
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### HenonMap
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### HopfBifurcation
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### IkedaMap
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### JuliaSet
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### LavaLamp
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### LogisticMapBifurcation
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### LorenzAttractor
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### MandelbrotDrift
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### PerlinNoise
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### PlasmaFlow
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### ReactDiffusion
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### RosslerAttractor
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### SpiralGalaxy
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### StarfieldDrift
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### VoronoiDrift
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

## Null Provider Cards

Count: 24

### Static
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Void
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Aurora
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Breath
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Butterfly
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Cells
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Chaos
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Clouds
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Ember
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Fifth Dimension
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Flow
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Fluid
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Growth
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Infinity
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Julia
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Lattice
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Mirrors
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Murmuration
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Oscillations
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Plasma
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Ripples
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Stars
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Swarm
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Waves
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

## Webmodule Cards

Count: 20

### AchievementsRuntime
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### AchievementsTest
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### ApiTest
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### AudioTest
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Continue
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Cores
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### CorruptionSlop
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### DeckBuilder
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### DeckBuilderCrud
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### GlitchHarvester
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### HexEditor
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Home
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### ImagineBug
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Options
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Overlay
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### RomManager
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Story
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### TimeJump
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### VoiceTest
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### XYTest
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

## Feature Cards

Count: 5

Derived behavior to preserve during implementation:

- `Savestates` derives from unlocking `TimeJump`.
- `RTC` derives from unlocking `GlitchHarvester`.
- `GH` derives from unlocking `GlitchHarvester`.
- `Imagine` derives from unlocking `ImagineBug`.
- `Debug` remains a separate flag.

### Savestates
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### RTC
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### GH
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Imagine
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

### Debug
- [ ] Card complete
- [x] Metadata authored
- [x] SVG emblem authored
- [x] Manifest or registry wiring complete
- [ ] UI and API QA complete

## Review Sweep

- [ ] Verify every in-scope card resolves to authored metadata.
- [ ] Verify every in-scope card resolves to a bespoke SVG or registered asset.
- [ ] Verify no placeholder descriptions remain.
- [ ] Verify grouping and sorting across all non-core domains.
- [ ] Verify card modal views remain coherent and domain-specific.
- [ ] Verify progression and unlock surfaces still behave correctly after metadata centralization.
- [ ] Verify future unknown cards still fall back safely without affecting authored cards.
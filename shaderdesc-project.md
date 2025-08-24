# Shader metadata & wrapper rework — Worksheet

Use this worksheet to track the general edit of all GLSL shaders and the rework of the GLSL wrapper so it conforms to other cores. The plan embeds consistent metadata in each shader source and has the C# wrapper parse and expose it automatically.

## Phase 0 — Prep and scaffolding

- [x] Create project worksheet (this file)
- [x] Extend source generator to parse new metadata keys from GLSL headers
- [x] Emit CoreName, Description, Performance, Rating on generated IShader classes
- [x] Preserve Category via `Defines["Category"]`
- [x] Verify Release build succeeds

## Phase 1 — Finalize metadata schema

- [x] Keys to support:
	- [x] DisplayName (string)
	- [x] CoreName (string)
	- [x] Description (string, 1–3 sentences)
	- [x] Performance (int, 0–5 typical)
	- [x] Rating (int, 1–5)
	- [x] Category (string; e.g., Color, CRT, Refraction, Distortion, Stylize)
- [ ] Optional: define a canonical list of Categories for consistency
- [x] Optional: decide UI sorting (by Category then DisplayName)

Reference parsing rules:

- Only the leading comment block at the very top is parsed; parsing stops at the first non-comment line.
- Prefer lines like `// Key: Value`. Leading `/* */` block comments and `*` lines are also handled.
- Keep one key per line.

Example header:

// DisplayName: LAT
// CoreName: Lattice Refraction
// Description: Micro-facet lattice per NES tile generating refracted & sparkling look.
// Performance: 3
// Rating: 4
// Category: Refraction

## Phase 2 — Update all GLSL fragment headers

For each `Shaders/*.frag.glsl`, ensure the header contains all required keys (see Phase 1). Keep DisplayName short (<= 20 chars) and Category concise.

- [x] 16B.frag.glsl
- [x] BLD.frag.glsl
- [x] BUMP.frag.glsl
- [x] CCC.frag.glsl
- [x] CNMA.frag.glsl
- [x] CRY.frag.glsl
- [x] CRZ.frag.glsl
- [x] DOT.frag.glsl
 - [x] EXE.frag.glsl
- [x] HUE.frag.glsl
- [x] LAT.frag.glsl
 - [x] LCD.frag.glsl
 - [x] LSD.frag.glsl
 - [x] MSH.frag.glsl
 - [x] PX.frag.glsl
- [x] RGBX.frag.glsl
- [x] RF.frag.glsl
- [x] SPK.frag.glsl
- [x] TRI.frag.glsl
- [x] TTF.frag.glsl
- [x] TV.frag.glsl
- [x] VHS.frag.glsl
- [x] WARM.frag.glsl
- [x] WTR.frag.glsl

Validation after edits:

- [x] Build project to regenerate classes
- [ ] Spot-check 2–3 shaders (read generated class) for correct metadata values

## Phase 3 — Robustness and defaults

- [x] Core defaults if missing:
	- [x] CoreName: "UNIMPLEMENTED"
	- [x] Description: "UNIMPLEMENTED"
	- [x] Performance: 0
	- [x] Rating: 1
- [ ] Add a lint step (optional script) to check headers for required keys

## Phase 4 — Documentation

- [x] Capture schema and rules in this worksheet
- [ ] Add a short contributor note in `docs/` pointing to this file

## Phase 5 — QA

- [ ] Build (Debug/Release) after bulk header edits
- [ ] Run a smoke test in the app to ensure shaders still render
- [ ] Verify no generator diagnostics (BNES001–003) introduced

---

## Cores.razor integration tasks (shader metadata view)

Goal: show shader metadata on the Cores page similarly to CPU/PPU/APU, leveraging `IShader` fields.

Data plumbing:

- [x] Replace `Shaders = ShaderProvider.All.Select(s => s.Id)` with a metadata list (e.g., `ShaderInfos = ShaderProvider.All.ToList()`)
- [x] Include: Id, DisplayName, Description, Performance, Rating, Category (from `Defines?["Category"]`)
- [x] Sort by Category then DisplayName (or per Phase 1 decision)

UI rendering:

- [x] Update the Shaders section to render rows like other cores:
	- [x] Bold Id or DisplayName
	- [x] Small muted Description
	- [x] Small Perf and Rating badges/labels
	- [x] Show Category (text or chip)
- [ ] Optional: group shaders by Category with subheadings
- [ ] Optional: add basic CSS (align with existing `.opt-row`, `.small` styles)

Resilience and fallbacks:

- [x] If metadata missing (pre-migration), fall back to Id-only list
- [x] Null-safe access to `Defines["Category"]`

Verification:

- [ ] Navigate to /cores and confirm new shader metadata renders
- [ ] Confirm ordering/grouping matches decision


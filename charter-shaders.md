# Charter: GLSL shaders via source generator + reflection

Purpose: Track actionable work to refactor shaders into standalone .glsl files discovered at build time and consumed via a common interface with runtime reflection discovery.

Constraints: Blazor WASM, static hosting (no directory listing), AOT/trimming, potentially many shaders without harming startup or memory.

---

## Decisions
- [x] Author shaders as flat files under `Shaders/`.
- [x] Use a Roslyn Incremental Source Generator to emit one C# class per shader.
- [x] Discover generated shaders at runtime via reflection against a common interface.
- [x] Keep WASM/AOT safe with trimming annotations and/or link config.
- [x] Preserve legacy naming pattern by generating identifiers with a prefix/suffix (e.g., `SHADER_TV`).

## Definitions
- Common interface name: `IShader` (Id, DisplayName, VertexSource?, FragmentSource, optional Defines)
- Marker attribute: `[ShaderDefinition]` (applied to generated classes)
- Registry: `ShaderRegistry` (reflective discovery, lazy instantiation, cached lookup)

---

## Work items

### 1) Repository scaffolding
- [x] Create `Shaders/` folder at repo root (or under `wwwroot/` if preferred for visibility; choose one and standardize)
  - [x] Add `.gitkeep` or a sample shader to ensure folder exists in VCS
- [x] Add `<AdditionalFiles Include="Shaders/**/*.glsl" />` to `BrokenNes.csproj`
- [x] Add an Analyzer reference for the generator (ProjectReference with `OutputItemType=Analyzer`)

### 2) Interface and registry
- [x] Define `IShader`
  - [x] `string Id { get; }`
  - [x] `string DisplayName { get; }`
  - [x] `string? VertexSource { get; }` (null if fragment-only)
  - [x] `string FragmentSource { get; }`
  - [x] `IReadOnlyDictionary<string,string>? Defines { get; }` (optional)
- [x] Implement `ShaderRegistry`
  - [x] Scan the entry assembly for types assignable to `IShader` and/or marked with `[ShaderDefinition]`
  - [x] Use lazy instantiation and cache instances by Id
  - [x] Expose `IReadOnlyList<IShader> All` and `IShader? GetById(string id)`
  - [x] Limit scanning scope to the app assembly to reduce overhead
- [x] Wire registry initialization at app startup
  - [x] Ensure registry is available to the emulator component(s)

### 3) Source generator project (`BrokenNes.Shaders.Generator`)
- [x] Create a new Class Library with an Incremental Generator
- [x] Input discovery
  - [x] Enumerate AdditionalFiles matching `Shaders/**/*.glsl`
  - [x] Group files by base name to pair `{name}.vert.glsl` and `{name}.frag.glsl`
  - [x] Support fragment-only shaders (no vertex file)
- [x] Parse metadata (optional pragmas in comments)
  - [x] `DisplayName: ...`
  - [x] `Category: ...` (stored for future UI use)
- [x] Code generation (per shader)
  - [x] Emit class implementing `IShader` with public parameterless constructor
  - [x] Embed GLSL sources as verbatim string literals
  - [x] Apply `[ShaderDefinition]` attribute to the class
  - [x] Emit `Id` based on filename; keep stable and URL-safe
  - [x] Emit generated identifier(s) honoring legacy prefix/suffix (e.g., `SHADER_{NAME}`)
- [x] Trimming/AOT hints
  - [x] Ensure generator output compiles without requiring runtime reflection on private members
- [x] Error handling & diagnostics
  - [x] Detect missing fragment file and report BNES001
  - [x] Detect duplicate Ids and fail the build with a clear message (BNES002/BNES003)
- [x] Output organization
  - [x] Place generated code under `Shaders.g.cs` for inspection

### 4) Build and trimming configuration
- [x] Add conservative preservation rule in `LinkerConfig.xml` for the marker attribute namespace (only if necessary)
- [x] Annotate the reflection scanner with `[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]`
- [x] Ensure deterministic generator output (stable ordering, normalized newlines)

### 5) Performance and scalability safeguards
- [x] Lazy activation: Do not instantiate all shaders at startup; create on first use
- [x] Restrict reflection: scan once and cache type list; prefer entry assembly scan
- [ ] Optional: generator switch to strip comments/extra whitespace from GLSL to reduce payload size (deferred)
- [ ] Optional: feature flag to emit a generated registry list to bypass reflection when desired (deferred)
- [x] Validate large-shader-count posture by keeping registry/lightweight and sources as shared literals

### 6) Naming conventions and compatibility
- [x] Enforce naming: `{NAME}.frag.glsl` and `{NAME}.vert.glsl`
- [x] Map `{NAME}` to class name and Id (sanitize to valid identifiers; preserve case policy)
- [x] Generate prefixed identifiers/constants compatible with existing usage (e.g., `SHADER_TV`)
- [ ] Provide a compatibility lookup so older identifiers resolve to new Ids (not needed currently)
- [x] Document the convention in dev notes (this charter)

### 7) Migration of existing shaders
- [x] Inventory current inline shader sources
- [x] Extract each into `Shaders/` following naming conventions
- [x] Remove inline shader strings from code once generated counterparts exist
- [x] Update emulator integration to use C#-registered shaders for the JS registry
- [x] Update developer documentation on how to add/modify shaders (captured here)

#### Per-shader migration checklist (from `wwwroot/shaders.js`)
For each shader below, complete these steps:
- [ ] Create `Shaders/{ID}.frag.glsl` with the fragment source (and `Shaders/{ID}.vert.glsl` if a custom vertex shader is ever needed; currently fragment-only).
- [ ] Add metadata header comments (e.g., `// DisplayName: {Label}`; optional `// Category: ...`).
- [ ] Remove this shader’s registration block from `wwwroot/shaders.js`.

- RF (label: RF)
  - [x] `Shaders/RF.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- PX (label: PX)
  - [x] `Shaders/PX.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- LSD (label: LSD)
  - [x] `Shaders/LSD.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- SPK (label: SPK)
  - [x] `Shaders/SPK.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- EXE (label: EXE)
  - [x] `Shaders/EXE.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- WTR (label: WTR)
  - [x] `Shaders/WTR.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- TV (label: TV)
  - [x] `Shaders/TV.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- LCD (label: LCD)
  - [x] `Shaders/LCD.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- TRI (label: TRI)
  - [x] `Shaders/TRI.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- MSH (label: MOSH)
  - [x] `Shaders/MSH.frag.glsl`
  - [x] Metadata header
  - [x] Remove from `wwwroot/shaders.js`

- [x] Remove reference to `wwwroot/shaders.js` from `wwwroot/index.html`
- [x] Remove fallback usage of `window.registerAllShaders` in `wwwroot/nesInterop.js`
- [x] Replace `wwwroot/shaders.js` with a short-lived no-op stub to avoid 404s for cached clients (safe to fully delete later)

### 8) Governance and ownership
- [x] Assign owners for the generator and shader catalog
- [x] Establish review checklist for shader submissions (format, naming, metadata, size)
- [x] Define contribution guidelines for adding new shaders

---

## Notes and assumptions
- Reflection remains the discovery mechanism initially; we may add a generated registry later without changing the public API.
- We avoid any server-side directory listing; shaders are compiled into the app as code.
- We prefer minimal runtime cost; most work happens at build time.

---
Owner: @ircluzar
Repo: BrokenNes
Status: In progress

Updates:
- Generator now parses top-of-file metadata (DisplayName, Category) from fragment shaders and emits them (DisplayName; Category in Defines).
- New diagnostics: BNES002 (duplicate fragment id), BNES003 (duplicate vertex id).
- flatpublish: publishes without trimming by default, with optional --trim using LinkerConfig roots; preserves generated shaders and multi-core types for reflection discovery.

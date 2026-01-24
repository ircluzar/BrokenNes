# Windows WinForms Redundancy Scan Plan

## Scope
- Folder: Windows/**
- Focus: redundant or repeated code that could become helpers or shared utilities.

## Approach
1. Inventory repeated patterns by grouping related files (MainForm partials, Rendering/Backgrounds, NesEmulator providers, tools, helpers).
2. Inspect representative files per group to confirm repeated logic/structure.
3. Record concrete extraction candidates with file links and rationale.
4. Estimate code reduction impact and rank suggestions.

## Findings
### 1) Color conversion helpers repeated across backgrounds and null providers
- Many background renderers duplicate identical `HslToRgb` + `HueToRgb` helpers.
	- Example occurrences:
		- [Windows/Rendering/Backgrounds/AnimatedWaveBackground.cs](Windows/Rendering/Backgrounds/AnimatedWaveBackground.cs#L151)
		- [Windows/Rendering/Backgrounds/BreathingGradientsBackground.cs](Windows/Rendering/Backgrounds/BreathingGradientsBackground.cs#L138)
		- [Windows/Rendering/Backgrounds/BelousovZhabotinskyBackground.cs](Windows/Rendering/Backgrounds/BelousovZhabotinskyBackground.cs#L134)
		- [Windows/Rendering/Backgrounds/StarfieldDriftBackground.cs](Windows/Rendering/Backgrounds/StarfieldDriftBackground.cs#L191)
- Null providers duplicate `HsvToRgb` conversion logic (same math, same signature) across many classes.
	- Example occurrences:
		- [Windows/NesEmulator/nullproviders/GradientWavesNullProvider.cs](Windows/NesEmulator/nullproviders/GradientWavesNullProvider.cs#L46)
		- [Windows/NesEmulator/nullproviders/StarfieldNullProvider.cs](Windows/NesEmulator/nullproviders/StarfieldNullProvider.cs#L84)
		- [Windows/NesEmulator/nullproviders/PerlinNoiseFieldNullProvider.cs](Windows/NesEmulator/nullproviders/PerlinNoiseFieldNullProvider.cs#L102)
		- [Windows/NesEmulator/nullproviders/Penteract5DRotationNullProvider.cs](Windows/NesEmulator/nullproviders/Penteract5DRotationNullProvider.cs#L269)
- Suggestion: extract a shared color helper (e.g., `Rendering/ColorMath.cs`) that exposes `HslToRgb` and `HsvToRgb` and reuse from all backgrounds + null providers.

### 2) Background texture pipeline boilerplate is repeated in most backgrounds
- Many backgrounds share the same structure: `const Width/Height`, `bitmap` field, `time`, `timeSinceLastUpdate`, `Update`, `Render`, `Initialize`, and `RegenerateTexture` with identical Direct2D bitmap creation boilerplate.
	- Example pattern in:
		- [Windows/Rendering/Backgrounds/AnimatedWaveBackground.cs](Windows/Rendering/Backgrounds/AnimatedWaveBackground.cs)
		- [Windows/Rendering/Backgrounds/BreathingGradientsBackground.cs](Windows/Rendering/Backgrounds/BreathingGradientsBackground.cs)
		- [Windows/Rendering/Backgrounds/PlasmaFlowBackground.cs](Windows/Rendering/Backgrounds/PlasmaFlowBackground.cs)
- Suggestion: a base class like `ProceduralBackgroundBase` to centralize bitmap creation, update throttling, and common fields. Each background would only implement a pixel-generation function.

### 3) MainForm toggle handlers repeat the same config-update pattern
- Many `Toggle*` handlers follow the same steps: validate sender, update `config`, `Save()`, apply optional side-effects, then `UpdateConfigMenus()`.
	- Examples:
		- [Windows/MainForm.Display.cs](Windows/MainForm.Display.cs#L28)
		- [Windows/MainForm.UI.cs](Windows/MainForm.UI.cs#L186)
		- [Windows/MainForm.Speed.cs](Windows/MainForm.Speed.cs#L27)
- Suggestion: helper like `ApplyConfigToggle(ToolStripMenuItem menuItem, Action<bool> setValue, Action? apply = null)` to reduce boilerplate and centralize menu refresh logic.

### 4) View mode layout logic is duplicated between `SwitchViewMode` and resize handler
- The layout math for emulator/widget/overlay/web modes appears in both `SwitchViewMode` and `MainForm_Resize`.
	- Switch logic entry: [Windows/MainForm.ViewModes.cs](Windows/MainForm.ViewModes.cs#L71)
	- Resize logic entry: [Windows/MainForm.ViewModes.cs](Windows/MainForm.ViewModes.cs#L217)
- Suggestion: extract a shared `ApplyViewModeLayout(ViewMode mode, bool skipNavigation)` used by both methods to avoid drift and reduce duplicated layout calculations.

### 5) Core selection menu creation and “set core + save + refresh” patterns repeat
- `UpdateCoresMenus()` builds nearly identical single-select menus for CPU/PPU/APU.
	- [Windows/MainForm.Config.cs](Windows/MainForm.Config.cs#L246)
- Core setters are nearly identical (set core, update config, save, refresh):
	- [Windows/MainForm.Cores.cs](Windows/MainForm.Cores.cs#L27)
	- [Windows/MainForm.Cores.cs](Windows/MainForm.Cores.cs#L36)
	- [Windows/MainForm.Cores.cs](Windows/MainForm.Cores.cs#L45)
- Suggestion: a generic method like `SetCore(string coreId, Action<string> apply, Action<string> setConfig)` and a menu factory helper to reduce duplication.

### 6) Apply-saved-core selection has repeated validation branches
- The CPU/PPU/APU apply logic repeats the same “valid -> set” vs “default to FMC” branch.
	- [Windows/MainForm.Config.cs](Windows/MainForm.Config.cs#L347)
- Suggestion: helper method `ApplyCoreOrDefault(string selected, IReadOnlyList<string> validIds, Action<string> apply, Action<string> setConfig, string defaultCore)`.

---

## Suggested helper extractions (ordered by expected code reduction)
1. **Shared color conversion helpers (`HslToRgb`, `HueToRgb`, `HsvToRgb`)** reused across backgrounds and null providers.
2. **Procedural background base class** to centralize bitmap generation and update throttling for background effects.
3. **Unified view mode layout method** to remove duplication between `SwitchViewMode` and `MainForm_Resize`.
4. **Generic menu toggle helper** for MainForm config toggles.
5. **Core menu + core setter helpers** to unify CPU/PPU/APU selection logic and defaulting.

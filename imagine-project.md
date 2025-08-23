# Imagine Project: Model-backed Predictive Corruptor

This doc defines the end-to-end plan to wire the 6502 span predictor into the Imagine panel, run predictions during a controlled emulator freeze, and optionally patch bytes (“Imagine a bug”). It follows the shapes and usage described in `WebUse.md`.

Contents
- Goals and requirements
- Safe runtime choice and assets
- UI/UX wiring (Imagine panel + Debug modal)
- Data flow and contracts (JS <-> C#)
- Prediction modes and sampling params
- Freeze → predict → patch → resume
- Edge cases, testing, and next steps
- Work document: step-by-step tasks and acceptance criteria

## Goals and requirements

- Load a model via the Imagine panel by epoch. Epoch 25 is provided and lives under `wwwroot/models`.
- In the Debug modal, run a prediction test using the captured previous and next bytes.
- Predict and replace bytes around PC using the model.
- Expose a parameter for “number of bytes to generate/replace”.
- “Imagine a bug” button: freeze the game before the CPU consumes the next instruction, predict replacement bytes for the next addresses, write the patch, then resume.
- Keep it universal across devices; use hardware acceleration when available, but choose a safe default.

Status mapping to UI today
- Epoch input and “Load model” button already exist (no handlers wired yet).
- “Debug” opens the modal; `FreezeAndFetchNextInstructionAsync()` collects Prev8 and Next16.
- “Imagine a bug” button is present (no action yet).

## Safe runtime choice and assets

- Runtime: onnxruntime-web in the browser.
  - Default execution provider: WASM (universal and safe).
  - Optional: enable WebGL when available for acceleration (toggle-able; fallback to WASM automatically).
- Model format: ONNX.
  - Source: epoch 25 PyTorch `.pt` exists; export to ONNX once during build/publishing.
  - Place exported model at `wwwroot/models/6502_span_predictor_epoch25.onnx`.
  - Optional manifest (JSON) mapping epoch -> model URL for future epochs.

Note: If ONNX export is pending, the UI can be wired with graceful errors and a disabled “Predict” until the ONNX is available. `WebUse.md` describes token shapes: input [128] ints in [0..256], output [128, 257] logits with MASK=256.

## UI/UX wiring

Imagine panel (in `Pages/Nes.razor`)
- Add handlers for:
  - Load model: loads `epoch` (default 25) via JS, keeps a loaded-flag and model-info label.
  - Imagine a bug: triggers the freeze-predict-patch-resume flow (see below).
- Add small controls for prediction parameters (shared with modal):
  - Bytes to generate: int (1..32), default 4.
  - Temperature: 0.2..0.7, default 0.4.
  - TopK: 0/None or 20..50, default 32.

Imagine Debug modal
- Keep “Freeze and fetch next instruction”.
- Add “Run prediction (test)” button plus the three parameters above.
- Show predicted bytes and confidence/argmax (optional) and do not write to memory unless user clicks “Apply patch here”.

## Data flow and contracts (JS <-> C#)

We’ll add a small JS module (e.g. `wwwroot/lib/imagine.js`) and C# interop wrapper in the Emulator.

JS API (onnxruntime-web)
- imagine.loadModel(epoch: number): Promise<{ ok: boolean, info?: string, error?: string }>
  - Fetches `/models/6502_span_predictor_epoch${epoch}.onnx` and creates a session with EP=["wasm", "webgl?"].
- imagine.predictSpan(req): Promise<{ bytes: number[], logits?: number[] | undefined }>
  - req.window: number[128] tokens (0..256; 256=MASK)
  - req.holeStart: number (0..127)
  - req.holeEnd: number (0..127, exclusive)
  - req.temperature?: number
  - req.topK?: number | null
  - Returns `bytes` for positions holeStart..holeEnd-1 (0..255 each).

C# interop wrapper (sketch of public contracts)
- Task<bool> ImagineLoadModelAsync(int epoch)
- Task<byte[]> ImaginePredictSpanAsync(int[] tokens128, int holeStart, int holeEnd, float temperature, int? topK)

Error modes
- No model loaded → return a friendly error. Keep UI button disabled until loaded.
- Invalid shapes → reject and surface in modal toast.

## Building the 128-token window

Inputs available today
- Prev8: bytes at [PC-8 .. PC-1]
- Next16: bytes at [PC .. PC+15]

Window construction for masked-span (preferred)
- Target masked region: [PC .. PC+L) where L = user-specified “bytes to generate”, clamped to 1..min(16, remaining in page/bank).
- Right context: if L < 16, use Next16[L .. 16) as right context; otherwise right context is empty.
- Left context: Prev8 is a start; to fill up to 128, fetch additional bytes from CPU/PRG ROM around PC while paused. Favor including 64-96 bytes total context when feasible.
- Tokenization:
  - Known bytes → 0..255
  - Masked bytes (the hole) → 256

Fallback (autoregressive) when right-context is weak or L >= 16
- Place the hole at the right end of the 128 window (no right context) and generate one byte at a time, sliding the window forward as each prediction is committed.

Refer to `WebUse.md` for the precise shapes, softmax, and sampling guidelines.

## Prediction modes and sampling params

- Temperature: default 0.4 (0.1..0.7 typical)
- TopK: default 32 (20..50 typical). Set null/0 to disable top-k.
- Mode selection:
  - Modal “Run prediction (test)” uses masked-span if right context exists; otherwise falls back to autoregressive.
  - “Imagine a bug” uses masked-span for L bytes starting at PC (uses Next16 as right context when available).

## Freeze → predict → patch → resume

Contract
1) Freeze
- Call `PauseEmulation` and capture PC and context (`FreezeAndFetchNextInstructionAsync()` already does this). Ensure we’re at an instruction boundary before fetch.

2) Build request
- Compute [holeStart, holeEnd) within a 128-token window centered around PC.
- Fetch additional context bytes while paused to fill the window (bounded to PRG ROM; if not in PRG ROM, abort with warning).

3) Predict
- Call `ImaginePredictSpanAsync(tokens128, holeStart, holeEnd, temperature, topK)` via JS interop.
- Receive `predBytes[L]`.

4) Patch
- Translate CPU addresses [PC .. PC+L) to PRG ROM memory domain addresses.
- Apply writes through the existing Corruptor/RTC write path (so the patch lives in the ROM domain). Record a small “Imagine” stash entry for audit/undo.

5) Resume
- Call `StartAsync()` or unpause to continue emulation.

6) Debug modal test
- Show predicted bytes and allow “Apply patch here” to perform step 4. If not applied, no writes are made.

## Edge cases

- PC not in PRG ROM ($8000..$FFFF): Show warning (already surfaced) and disable prediction.
- Banked PRG mapping: Ensure address translation respects current mapper/bank; use existing corruptor/ROM write helpers.
- Large L (e.g., >16): Prefer autoregressive or multi-pass masked fills. Clamp UI to 1..32.
- Near ROM boundaries: Shift window to keep indices 0..127 valid.
- Model not loaded / fetch error: keep buttons disabled, show toast on failure, allow retry.

## Minimal task checklist (repo changes)

- JS: add `wwwroot/lib/imagine.js` with onnxruntime-web session creation + predictSpan.
- Package: include `onnxruntime-web` via `<script>` import or bundling, and load only when the Imagine panel is opened (lazy load).
- Assets: add `wwwroot/models/6502_span_predictor_epoch25.onnx` and a tiny `models.json` manifest.
- C#: add interop wrapper methods on `Emulator` for LoadModel and PredictSpan, with helpers to build the token window.
- UI: wire buttons in `Nes.razor` (Load model; Debug modal “Run prediction”; “Imagine a bug” end-to-end pipeline) and add parameters (L/temperature/topK).
- Patch writes: reuse Corruptor/RTC write path to PRG ROM domain; tag entries as “Imagine”.
- Telemetry: small status label (Loaded: epoch N; EP: wasm/webgl) and simple errors.

## Testing

- Manual: load a small ROM, pause near a known instruction sequence, run Debug prediction and compare predicted bytes vs original.
- “Imagine a bug”: with L=1..4, verify the instruction stream changes and execution diverges after resume; confirm stash entry is created and replayable.
- Cross-device: verify WASM path works on desktop and mobile Safari/Chrome; optionally enable WebGL acceleration and measure latency.

## Notes and next steps

- If we later ship additional epochs, expand the epoch input to a dropdown bound to the `models.json` manifest and auto-pick the highest.
- Confidence UI: surface per-byte argmax probability for masked positions when helpful.
- Optional: provide a “dry-run” compare view in the modal that overlays predicted vs original bytes.

Refer back to `WebUse.md` for token semantics and sampling tips; this plan mirrors those conventions so we can keep the core logic shared across modes.

## Work document: step-by-step tasks and acceptance criteria

Use this editable checklist to track implementation. Tick subtasks as you complete them.

- [ ] Milestone 1: Runtime + assets (ONNX + onnxruntime-web)
  - [ ] Place model at `wwwroot/models/6502_span_predictor_epoch25.onnx`
  - [ ] Add optional manifest `wwwroot/models/models.json` (e.g., `{ "epochs": [25], "default": 25 }`)
  - [ ] Verify model input/output shapes match `WebUse.md` (in: [128] 0..256; out: [128, 257])
  - [ ] Acceptance: `/models/6502_span_predictor_epoch25.onnx` is fetchable in dev and publish
  - [ ] Acceptance: If model missing, UI shows friendly error and keeps Predict disabled

- [ ] Milestone 2: JS interop module (`wwwroot/lib/imagine.js`)
  - [ ] Create `imagine.js` and expose global `imagine`
  - [ ] Implement `loadModel(epoch)` using onnxruntime-web; EP preference: ["wasm", optional "webgl"]
  - [ ] Implement `predictSpan({ window, holeStart, holeEnd, temperature, topK })`
  - [ ] Lazy-load onnxruntime-web only when needed
  - [ ] Wire include/lazy-load via Blazor/`nesInterop.js` when Imagine panel opens
  - [ ] Acceptance: `imagine.loadModel(25)` => `{ ok:true, info:"wasm"|"webgl" }` or `{ ok:false, error }`
  - [ ] Acceptance: `imagine.predictSpan` returns bytes of length `holeEnd-holeStart` in 0..255

- [ ] Milestone 3: C# interop wrapper + token window builder
  - [ ] Add `Emulator.ImagineInterop.cs`: `ImagineLoadModelAsync`, `ImaginePredictSpanAsync`
  - [ ] Add `Emulator.ImagineWindow.cs`: `BuildTokens128AroundPc(ushort pc, int holeLen, out int holeStart, out int holeEnd)`
  - [ ] Add `Emulator.ImaginePatch.cs`: `ApplyImaginePatchAsync(ushort pc, byte[] bytes)`
  - [ ] Use `IJSRuntime.InvokeAsync<T>` to call `imagine.loadModel`/`imagine.predictSpan`
  - [ ] Build tokens using Prev8/Next16 and expand context up to 128 in PRG ROM; mask [PC..PC+L) with 256
  - [ ] Map CPU addresses to PRG ROM for patching respecting mapper/banks; abort if not in PRG
  - [ ] Acceptance: `ImagineLoadModelAsync(25)` returns true and records EP label
  - [ ] Acceptance: `BuildTokens128AroundPc` returns 128 tokens with correct hole indices

- [ ] Milestone 4: UI wiring in `Pages/Nes.razor`
  - [ ] Wire "Load model" to `ImagineLoadModelAsync(epoch)` and show status: "Loaded epoch N (EP: wasm/webgl)"
  - [ ] Disable Predict/Imagine buttons until model loaded
  - [ ] Add params: Bytes to generate (1..32 default 4), Temperature (0.2..0.7 default 0.4), TopK (None/0 or 20..50 default 32)
  - [ ] Debug modal: add "Run prediction (test)" and show predicted bytes/confidence (optional)
  - [ ] Debug modal: add "Apply patch here" to call `ApplyImaginePatchAsync`
  - [ ] "Imagine a bug": run freeze → predict L bytes at PC → patch → resume
  - [ ] Acceptance: Buttons enable/disable based on model state and PRG ROM checks
  - [ ] Acceptance: Debug modal shows context and predicted bytes; no write until "Apply" is clicked

- [ ] Milestone 5: Predict flow (freeze → build → predict)
  - [ ] Freeze using `PauseEmulation()`; capture via `FreezeAndFetchNextInstructionAsync()`
  - [ ] Compute `[holeStart, holeEnd)`; clamp L to page/bank and 1..32
  - [ ] Build `tokens128` with MASK=256 at target positions
  - [ ] Call `ImaginePredictSpanAsync(tokens128, holeStart, holeEnd, temperature, topK)`; handle errors/timeouts
  - [ ] Acceptance: Predicted bytes array length equals L
  - [ ] Acceptance: Errors toast in UI, emulator not left paused indefinitely

- [ ] Milestone 6: Patch integration via Corruptor (ROM domain)
  - [ ] Translate [PC..PC+L) to PRG domain addresses honoring current mapper/bank
  - [ ] Apply writes via existing Corruptor/RTC ROM write path
  - [ ] Record stash entry tagged "Imagine" with PC, bytes, epoch, params, timestamp
  - [ ] Provide undo using existing stash mechanics
  - [ ] Acceptance: `Next16` reflects patched bytes at PC; stash entry is visible, replayable/exportable

- [ ] Milestone 7: Errors, edge cases, telemetry
  - [ ] Disable actions when model not loaded, PC not in PRG ROM, or mapper blocks writes
  - [ ] Telemetry: status "Imagine: loaded epoch N (EP: wasm/webgl)" and last action string
  - [ ] Acceptance: No action executes when preconditions fail; concise feedback shown

- [ ] Milestone 8: Tests (manual + cross-device)
  - [ ] Manual: freeze near known sequence; Debug → prediction; compare vs original; apply patch; resume; observe divergence
  - [ ] Manual: "Imagine a bug" with L=1..4 works; stash created
  - [ ] Cross-device: WASM path verified on desktop Safari/Chrome; WebGL optional; note latency
  - [ ] Acceptance: No JS interop exceptions; prediction round-trip < ~1s on modern desktop

- [ ] Milestone 9: Publish assets + lazy-load
  - [ ] Ensure `/wwwroot/models/*.onnx` copied in publish output
  - [ ] Lazy-load `imagine.js` and `onnxruntime-web` only when Imagine UI opens
  - [ ] Optionally pre-cache model in `wwwroot/sw.js`
  - [ ] Acceptance: Flat publish contains model; first Imagine use loads runtime; no 404s

- [ ] Contracts and shapes (authoritative)
  - [ ] JS request: `window[128]` 0..256; `holeStart` 0..127; `holeEnd` 0..127; `temperature` float; `topK` number|null
  - [ ] JS response: `{ bytes: number[], logits?: number[] }`; bytes length `holeEnd-holeStart`
  - [ ] C# wrappers: `ImagineLoadModelAsync`, `ImaginePredictSpanAsync`, `ApplyImaginePatchAsync`

- [ ] Edge cases recap
  - [ ] PC not in PRG ROM ($8000..$FFFF): warn, disable prediction
  - [ ] Large L (>16): consider autoregressive fallback; clamp UI to 1..32
  - [ ] Near ROM boundaries: shift window and adjust indices
  - [ ] Model not loaded: disable Predict; show error on invocation

- [ ] Out of scope (v1)
  - [ ] Confidence UI beyond simple argmax probability
  - [ ] Multi-pass/ensemble predictions
  - [ ] Server-backed inference (browser ONNX only for v1)

- [ ] Definition of Done
  - [ ] Load epoch 25, freeze near PC in PRG ROM, run masked-span prediction for L bytes, preview in modal
  - [ ] Apply patch to ROM domain via Corruptor; resume emulation
  - [ ] Stash entry "Imagine" recorded with parameters
  - [ ] WASM path works on desktop Safari/Chrome
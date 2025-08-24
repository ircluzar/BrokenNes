# Fastload Project — Accelerate Savestate Loading

Purpose: Recover and exceed pre-regression performance when loading savestates, while keeping correctness. This doc outlines concrete, staged changes with clear success criteria, code touch points, and a rollback-compatible plan.

Note: This document is in worksheet mode. Check off items as you go. Testing/validation tasks are listed for reference but are intentionally not included in the checkbox plan.

## Checklist (requirements)

- [ ] Define measurable goals and a repeatable benchmark for LoadState().
- [ ] Identify hotspots and quick wins with minimal risk.
- [ ] Reduce JSON parsing overhead for large byte arrays (RAM/PRG/CHR/ROM).
- [ ] Provide a backward-compatible binary fast-path for savestates.
- [ ] Keep SaveState/LoadState coherent across CPU/PPU/APU/Mapper.
- [ ] Maintain compatibility with existing JSON saves and chunked storage.
- [ ] Avoid heavy reflection and AOT-hostile APIs on WebAssembly.
- [ ] Add versioning and diagnostics to verify integrity without large costs.

## Target outcomes

- [ ] Typical state load (MMC3/Mapper4, 8KB RAM, 8–32KB PRG-RAM, 8KB CHR-RAM, 256x240 framebuffer not in state) under 30–50 ms on desktop Chrome; under 80–100 ms on mid mobile; cap p95 under 150 ms on WASM.
- [ ] Zero functional regressions in state fidelity across supported mappers and cores.

## Baseline and measurement

- [ ] Add lightweight instrumentation around each major LoadState step in `NesEmulator/NES.cs` (Release compiled with logs off by default; enable via a flag or DEBUG):
   - [ ] Parse manifest/chunks to string (UI `Pages/Nes.razor::LoadState`).
   - [ ] Measure `NES.LoadState` total and subtotals: parse root, ROM compare/hash, RAM copies, mapper set, CPU/PPU/APU set, controller, audio reset.
   - [ ] Log resulting active core IDs and a tiny CPU register sample under DEBUG only.
- [ ] Add a simple in-app “Perf overlay” toggle to display last load duration and breakdown.

## Hotspots observed in code

- JSON array to byte[] conversion via `JsonElement.EnumerateArray()` in `NES.LoadState` (ram, prgRAM, chrRAM, romData). This is extremely slow on WASM.
- Full JSON DOM (`JsonDocument.Parse`) and string concatenation for chunk assembly in `Pages/Nes.razor::LoadState`.
- ROM hash computation every load path for equality check.
- Mapper/CPU/PPU/APU state parsed via generic `JsonElement`, rather than precompiled serializers or binary.
- Extra logging and string formatting in Release (mostly guarded, but ensure complete elision in Release builds).

## Strategy overview (phased)

- [ ] Phase 1: Low-risk quick wins (keep JSON, cut overhead)
   - Prefer base64 for all large byte arrays to avoid per-byte JSON parsing.
   - Replace DOM parsing with `Utf8JsonReader` (forward-only, zero-alloc) for LoadState root.
   - Short-circuit ROM compare to avoid hashing when possible.
   - Tighten Release logging to be no-op and avoid string interpolation.
   - Use `Array.Copy`/`Buffer.BlockCopy` on pre-sized buffers; avoid per-element loops anywhere.

- [ ] Phase 2: Binary fast-path (optional but recommended)
   - Introduce a compact binary savestate format with versioned header and fixed layout for large regions.
   - Keep JSON path as fallback; detect fast-path by header magic.
   - Support in IndexedDB as `Blob/ArrayBuffer` to avoid base64 overhead entirely.

- [ ] Phase 3: Serializer improvements for component states
   - Provide source-generated JSON (or simple manual writer/reader) for CPU/PPU/APU/Mapper states.
   - Optional: switch to MessagePack (source generator) if JSON still dominates load time.

- [ ] Phase 4: Storage pipeline improvements
   - Store per-region blobs in IDB (ram.bin, prgram.bin, chrram.bin, rom.bin, meta.json) for streaming and partial reads.
   - Maintain manifest for backward compatibility. If only legacy JSON exists, still parse.

## Detailed implementation plan

### Phase 1: Quick wins (JSON-compatible)

1. [ ] Add base64 fields for byte arrays in `SaveState()` and prefer them in `LoadState()`
   - Files: `NesEmulator/NES.cs`
   - [ ] Save: Emit `ramB64`, `prgRAMB64`, `chrRAMB64`, `romB64` (using `Convert.ToBase64String`), and optionally omit raw arrays in Release. Under DEBUG, keep both for diffing.
   - [ ] Load: First check for `...B64` fields; if present, decode directly (`Convert.FromBase64String`), then `Array.Copy` into preallocated buffers. Fall back to existing JSON array parsing only if `...B64` missing.
   - Note: Base64 decode on WASM is implemented in native code and is typically much faster than parsing N integers.

2. [ ] Replace DOM parsing for root with `Utf8JsonReader`
   - Files: `NesEmulator/NES.cs`
   - Convert the input JSON string to `ReadOnlySpan<byte>` (UTF8) and use a hand-rolled reader that pulls just the fields we need by name into a `NesStateLoadDto`.
   - Avoid allocating intermediate strings for large sections.

3. [ ] Implement ROM equality short-circuit
   - If `romHash` exists in state and equals a cached `cartridge.romHash` (compute and cache once at ROM load), skip any recomputation during LoadState.
   - If no hash, compare lengths; if equal and mapper ID + ROM name (if tracked) match, skip hash.
   - Files: `NesEmulator/Cartridge.cs`, `NesEmulator/NES.cs`.

4. [ ] Logging hygiene
   - Wrap verbose `Console.WriteLine` with `#if DEBUG` or a runtime flag `EnableStateDiag`. Ensure zero string interpolation in Release by guarding the entire call.
   - Files: `NesEmulator/NES.cs`, potentially components where SetState logs.

Expected win: 3–10x faster load for large states due to base64 arrays and reader switch; marginal gains from log elision.

### Phase 2: Binary fast-path

Introduce `SaveStateBinary()`/`LoadStateBinary(ReadOnlySpan<byte>)` and surface it via UI as the default. Keep `SaveState()/LoadState(string)` as compatibility.

Design:

- Header: `BNES` + version (u16), flags (u16), lengths (u32 each) for ram/prgram/chrram/rom, core IDs, mapper ID, cycleRemainder (i64), controller fields, and offsets table. Little-endian.
- Sections:
  - RAM/prgRAM/chrRAM/ROM as raw bytes.
  - CPU/PPU/APU/Mapper states as compact binary blocks with their own tiny headers. Each component implements `WriteState(IBinaryWriter w)` and `ReadState(ref BinaryReader r)`. Keep versions per component.
- Integrity: CRC32 or xxHash64 per section (optional) to avoid hashing entire ROM when not needed.

Implementation steps:

1. [ ] Create small binary IO utility (`NesEmulator/State/BinIO.cs`): struct-like `SpanWriter`/`SpanReader` over byte spans for zero-alloc write/read.
2. [ ] Add binary read/write to CPU/PPU/APU/Mapper interfaces:
   - Extend or add optional interface: `IBinaryState { void WriteState(ref StateWriter w); void ReadState(ref StateReader r); }`.
   - Implement for all concrete cores and mappers we ship (Mapper0/1/2/4/5 etc.).
3. [ ] Implement `NES.SaveStateBinary()` and `NES.LoadStateBinary(...)` using the new interfaces and direct `Array.Copy` for regions.
4. [ ] UI integration (`Pages/Nes.razor` and `wwwroot/lib/nesInterop.js`):
   - Add methods to store and retrieve `ArrayBuffer`/`Uint8Array` in IndexedDB (no base64). Keys mirror existing ones, e.g., `state.bin` plus `manifest` for versioning.
   - From .NET, call `IJSRuntime.InvokeAsync<byte[]>(...)` to get the binary payload.
5. [ ] Backward compatibility: If binary not found, fall back to current JSON flow.

Expected win: Another 2–4x over Phase 1; near O(N) memcpy-dominated loads and predictable GC pressure.

### Phase 3: Serializer improvements for components

If we remain on JSON for component internals (CPU/PPU/APU/Mapper) for a while:

- [ ] Create DTOs for each component state with primitive fields/arrays only.
- [ ] Add System.Text.Json source generators for these DTOs to get AOT-friendly, high-perf serializers.
- [ ] Replace passing `JsonElement` into `SetState` with strongly-typed DTOs in the JSON path. Keep `JsonElement` handling only for legacy compatibility.

If we adopt MessagePack:

- [ ] Evaluate MessagePack.CSharp with source generator (no reflection), annotate DTOs, and add a tiny shim to store/retrieve MessagePack blobs in IDB. Measure on WASM first.

### Phase 4: Storage pipeline

- [ ] Use per-region IndexedDB stores: `kv` remains for small strings/meta; `states` for Blobs (or Uint8Array); `roms` already exists.
- [ ] Store per-region entries for incremental updates and parallelizable reads:
   - [ ] `state_meta` (JSON small)
   - [ ] `ram.bin`, `prgram.bin`, `chrram.bin`, `mapper.bin`, `cpu.bin`, `ppu.bin`, `apu.bin`, `rom.ref` (or `rom.bin` only if not in library)
- [ ] At load, read meta, then fetch blobs; minimal JSON parse. For online ROMs, resolve `rom.ref` via existing ROM store.

## Code touch points

- [ ] `NesEmulator/NES.cs`: SaveState/LoadState changes (base64 + Utf8JsonReader + binary fast-path).
- [ ] `Pages/Nes.razor`: Load/Save flow; manifest and chunk handling; add binary path and perf overlay.
- [ ] `wwwroot/lib/nesInterop.js`: IndexedDB helpers to store/get Uint8Array/Blob; keep legacy string path.
- [ ] `NesEmulator/mappers/*`: Add binary state methods; optional JSON DTO switch.
- [ ] `NesEmulator/cpus/*`, `ppus/*`, `apus/*`: Same as above.
- [ ] `NesEmulator/Cartridge.cs`: Cache ROM hash once; expose metadata (mapper id/name, hash, length) to speed compare.

## Versioning and compatibility

- [ ] Add `stateVersion` to both JSON and binary formats.
- [ ] JSON: advertise new base64 fields; keep old numeric arrays until a deprecation window passes. Prefer `...B64` when present.
- [ ] Binary: `BNES` magic + versioned section headers; unknown sections are skippable via length.

## Edge cases to handle

- [ ] Missing fields: fallback paths and defaults.
- [ ] Mismatched array sizes: clamp copies; reject if critical (e.g., PRG-RAM size different and cannot map).
- [ ] Unknown mapper/component versions: reject gracefully with a user-visible error.
- [ ] IndexedDB failures: fallback to legacy JSON.
- [ ] Large ROMs (MMC5): ensure offsets and lengths are 32-bit safe; guard against overflow.

## Validation

Testing and validation are handled separately. The following notes remain for reference only (intentionally not checkboxes):

- Unit tests for: base64 encode/decode paths, Utf8JsonReader parsing, binary header read/write roundtrip, partial-copy safety.
- Golden-file tests: produce a save; load; compare CPU/PPU/APU/Mapper states and RAM regions byte-for-byte.
- Performance tests: timer-based measurements for each phase; store results in `docs/results.txt`.

## Rollout plan

- [ ] Implement Phase 1 (small PRs): base64 fields + reader swap + ROM hash cache. Ship behind a feature flag if needed.
- [ ] Measure; if goals met, optionally stop here. Otherwise proceed.
- [ ] Implement binary fast-path and JS interop for binary IDB; keep JSON fallback.
- [ ] Migrate component serializers (JSON DTOs or MessagePack) if still a bottleneck.

## Small starting PRs (actionable to-dos)

- [ ] NES.cs
   - [ ] Add `ramB64/prgRAMB64/chrRAMB64/romB64` on save; prefer them on load.
   - [ ] Cache `cartridge.romHash` at ROM load; stop recomputing in LoadState when hash matches.
   - [ ] Replace root `JsonDocument.Parse` with `Utf8JsonReader`.
- [ ] Pages/Nes.razor
   - [ ] Add perf timestamps to LoadState similar to SaveState.
   - [ ] Keep current chunk merge; later, branch to binary path if `manifest` indicates `format:"bin"`.
- [ ] wwwroot/lib/nesInterop.js
   - [ ] Implement `saveStateBin(key, Uint8Array)` and `getStateBin(key)` using IDB with `Blob` or `Uint8Array`.

## Notes

- On WebAssembly, reducing managed allocations and JSON DOM traversals is the largest single win.
- Base64 inflation is acceptable; decode time is typically far less than parsing N integers from JSON.
- Binary fast-path brings predictable O(N) memcpy behavior and minimal GC pressure; worthwhile if states are frequent.

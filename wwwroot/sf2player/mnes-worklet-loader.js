// mnes-worklet-loader.js
// Runs INSIDE the AudioWorkletGlobalScope.
// Goal: create an isolated fluidsynth wasm Module (wasmModule) and then load the
// js-synthesizer worklet script which expects AudioWorkletGlobalScope.wasmModule.
// This avoids touching window.Module on the main thread (preventing Blazor collisions).
try {
  // Provide a fresh Module object only visible to the imported libfluidsynth script.
  var Module = {};
  // Load the legacy libfluidsynth build (Emscripten). This populates the local `Module`.
  importScripts('sf2player/libfluidsynth-2.0.2.js');
  // Expose it under the expected name so js-synthesizer.worklet can find it.
  AudioWorkletGlobalScope.wasmModule = Module;
  // Now load the js-synthesizer worklet build which registers the processor.
  importScripts('sf2player/js-synthesizer.worklet.min.js');
  // Optional marker for debugging.
  AudioWorkletGlobalScope._mnesWorkletReady = true;
} catch (e) {
  // Surface an error flag; main thread can probe via audioWorklet.addModule rejection.
  console.error('[MNES] Worklet loader failed', e);
  AudioWorkletGlobalScope._mnesWorkletError = '' + e;
}

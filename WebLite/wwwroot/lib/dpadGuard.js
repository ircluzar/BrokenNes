// dpadGuard.js - d-pad protection for the web builds (Lite and the minimal Web build).
//
// A real d-pad cannot press Left and Right, or Up and Down, at the same moment; a keyboard can (and so can two fingers on a touch pad, or a stick pushed while the
// d-pad is held). Games written for real pads misbehave when they see it, so the HUMAN input sources hold back both directions of a pair that arrives together:
// the pair is neither pressed (neutral). On by default; the setting "Allow opposite d-pad directions" (Lite: Controls > Input settings) turns it off.
//
// This is the web twin of NesEmulator.Systems.DpadGuard (Windows/NesEmulator/systems/DpadGuard.cs, used by the desktop app and the FL plugin). The rule is the
// same: a pure function; the raw button list is never changed (the caller keeps it, because letting go of Right must hand Left back); recorded or programmatic
// input never goes through it. UAT/lite/test-dpad-guard.mjs tests it, with the real nesInterop.js, in a real browser.
//
// Button lists are in NES order: 0 A, 1 B, 2 Select, 3 Start, 4 Up, 5 Down, 6 Left, 7 Right, then 8 X, 9 Y, 10 L, 11 R.
(function (root) {
  'use strict';
  /** A COPY of `state` as the console should see it from a human. With allowOpposite true the copy is the state unchanged. */
  function resolve(state, allowOpposite) {
    const out = Array.prototype.slice.call(state || []);
    if (allowOpposite === true || out.length < 8) return out;
    if (out[4] && out[5]) { out[4] = false; out[5] = false; }
    if (out[6] && out[7]) { out[6] = false; out[7] = false; }
    return out;
  }
  const api = { resolve: resolve };
  root.dpadGuard = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);

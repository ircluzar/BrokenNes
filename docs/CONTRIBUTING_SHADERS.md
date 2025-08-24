# Contributing to BrokenNes Shaders

Thanks for your interest! This guide covers adding and updating GLSL shaders and the source generator.

## Adding a new shader
- Place fragment shader at `Shaders/{ID}.frag.glsl` (vertex optional at `Shaders/{ID}.vert.glsl`).
- Start the file with metadata comments:
  // DisplayName: Nice Label
  // Category: Retro | Fun | Experimental | etc.
- Follow the standard interface expected by WebGL pipeline:
  - varying: `vTex`
  - uniforms: `uTex` (sampler2D), `uTime` (float), `uTexSize` (vec2), `uStrength` (float)
  - Optional: `uPrevTex` for feedback shaders like MSH
- Keep effects efficient; avoid large loops or dynamic branches when possible.

## Build integration
- The generator ingests all `Shaders/**/*.glsl` as AdditionalFiles.
- One C# type is generated per shader and discovered at runtime via reflection.
- Identifiers are emitted as `ShaderIds.SHADER_{ID}`.

## Review checklist
- Filename matches `{ID}.frag.glsl`
- Metadata header includes DisplayName
- Compiles in WebGL (check browser console for shader compile errors)
- Respects standard uniforms/varyings
- Works with current `lib/nesInterop.js` binding logic

## Generator changes
- Keep diagnostics clear. Existing codes:
  - BNES001: Missing fragment
  - BNES002: Duplicate fragment id
  - BNES003: Duplicate vertex id
- Prefer stable ordering and minimal changes to generated output.

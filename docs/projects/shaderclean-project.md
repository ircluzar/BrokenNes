# Shader Clean-Up Workboard

This file is a working checklist to bring all fragment shaders in `Shaders/` up to the `16B.frag.glsl` documentation and formatting standard.

High-level plan: audit every shader, add/standardize header metadata, document uniforms, add stage banners, normalize formatting, then final review.

Requirements (from your request):
- [ ] Turn the original guidance into a work document with checkboxes.
- [ ] Use `16B.frag.glsl` as the canonical example for documentation style.
- [ ] Produce per-shader checklists so progress can be tracked.
- [ ] Do not add tests (you will handle tests separately).

## Project-level Phases
- [ ] Approve header template & category taxonomy
- [ ] Batch 1 — Metadata sweep (DisplayName + Category + short header) for all shaders
- [ ] Batch 2 — Uniform documentation pass (ranges, units, unused marks)
- [ ] Batch 3 — Structural comments & stage banners
- [ ] Batch 4 — Indentation & formatting normalization (non-functional)
- [ ] Batch 5 — Final review & sign-off per shader

## Golden Reference (keep as example)
`16B.frag.glsl` — Reference standard for header, comments, section banners, inline uniform docs, and strength mapping.

## Per-shader Worklists
For each shader, mark sub-tasks below as you complete them. Once all sub-tasks are done, tick the top-level "Complete" box.

### Template for each shader
- [ ] Complete — top-level box to mark shader finished
  - [ ] Metadata: `// DisplayName:` and `// Category:` present and accurate
  - [ ] Header block: short tagline, `Goal:` line, and bulleted list of major stages
  - [ ] Uniforms: all `uniform` lines annotated with ranges/units and `// (unused)` if intentionally reserved
  - [ ] Main structure: `uv/texel` normalization + strength clamping early
  - [ ] Section banners: `// ---` markers for major stages
  - [ ] Indentation & spacing normalized (2 spaces)
  - [ ] Final review: quick visual check; ensure no logic changed

### Shaders (copy this checklist per shader and tick as you go)
- 16B
  - [x] Complete
  - [x] Metadata
  - [x] Header block
  - [x] Uniforms
  - [x] Structure & banners
  - [x] Indentation
  - [x] Final review

- BLD
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- BUMP
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- CCC
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- CNMA
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- CRY
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- CRZ
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- DOT
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- EXE
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- HUE
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- LAT
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- LCD
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- LSD
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- MSH
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- PX
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- RF
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- RGBX
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- SPK
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- TRI
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- TTF
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- TV
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- VHS
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- WARM
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

- WTR
  - [ ] Complete
  - [ ] Metadata
  - [ ] Header block
  - [ ] Uniforms
  - [ ] Structure & banners
  - [ ] Indentation
  - [ ] Final review

## Quick Checklist: header template
- [ ] Add this template to the top of any shader that needs a header:

```glsl
// DisplayName: CODE
// Category: CategoryName
precision mediump float;

// CODE — Short tagline
// Goal: Describe the user‑visible intent in one sentence.
// - Bullet 1 (key processing step or effect)
// - Bullet 2
// - Bullet N
// uStrength: 0..3 — Explain how intensity scales (list what higher values emphasize).
```

## Notes & Decisions
- Categories proposed: Color, Enhance, Refraction, Lighting, Distort, Retro, Stylize
- Keep shaders self-contained; avoid shared includes unless we decide to consolidate helpers later.

## Next actions you can assign to me
- [ ] Run an automated pass to add `Category` to shaders that are missing one (I will not change behavior).  
- [ ] Create a quick script (in `Tools/`) to validate presence of `DisplayName` + `Category` (optional CI).

---
Last updated: 2025-08-15

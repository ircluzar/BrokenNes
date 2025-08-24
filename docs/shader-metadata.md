# Shader metadata headers

This project embeds human-friendly metadata directly in each GLSL shader file and uses the source generator to propagate it into the generated C# `IShader` classes.

Quick rules:

- Put a leading comment block at the very top of each `*.frag.glsl` file.
- Use single-line `// Key: Value` entries (or an initial `/* */` block). One key per line.
- Supported keys:
  - DisplayName: short label (<= 20 chars)
  - CoreName: descriptive name
  - Description: 1–3 sentences
  - Performance: integer, 0–5 (higher = heavier)
  - Rating: integer, 1–5 (user-facing quality/appeal)
  - Category: free-form grouping (e.g., Color, CRT, Refraction, Distortion, Stylize)

Example:

// DisplayName: LAT
// CoreName: Lattice Refraction
// Description: Micro-facet lattice per NES tile generating refracted & sparkling look.
// Performance: 3
// Rating: 4
// Category: Refraction

Notes:

- The generator parses only the leading comment block and stops at the first non-comment line.
- If a key is missing, defaults apply in the generated class:
  - CoreName/Description: "UNIMPLEMENTED"
  - Performance: 0
  - Rating: 1
- Category is surfaced via `IShader.Defines["Category"]`.

See `shaderdesc-project.md` at the repo root for the full worksheet and migration checklist.

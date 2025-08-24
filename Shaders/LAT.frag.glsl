// DisplayName: LAT
// CoreName: Lattice Refraction
// Description: Micro-facet lattice per NES tile generating refracted and sparkling refraction with chromatic dispersion.
// Performance: -30
// Rating: 2
// Category: Refraction
precision mediump float;

// LAT — Lattice facet refraction
// Goal: Micro-facet lattice per NES tile generating refracted & sparkling look.
// - Operate in 8x8 tile space then subdivide into jittered micro-facets
// - Per-facet random normal drives chromatic refraction & sparkle
// - Edge seams darken boundaries; sparkle modulated by facet & time
// - Strength scales facet density & refraction offset range
// uStrength: 0..3 (internally remapped to 0.15..0.60 offset blend)

// Lattice: transforms the image into tiny crystalline facets that catch and scatter light.
// Design goals:
// - Keep NES pixel identity by operating in 8x8 tile space, then sub-dividing into micro-facets.
// - Use a per-facet pseudo normal to refract sample lookups (chromatic dispersion) and add time-based sparkle.
// - Avoid heavy loops and non-portable features; WebGL1-friendly.

varying vec2 vTex;
uniform sampler2D uTex;    // Source frame
uniform float uTime;       // Seconds
uniform vec2 uTexSize;     // Expected 256x240
uniform float uStrength;   // 0..3 strength (remapped internally)

// --- Small utilities ---
float hash12(vec2 p)
{
  // 1D random from 2D coord
  vec3 p3 = fract(vec3(p.xyx) * 0.1031);
  p3 += dot(p3, p3.yzx + 33.33);
  return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p)
{
  // 2D random from 2D coord
  float n = sin(dot(p, vec2(127.1, 311.7)));
  return fract(vec2(262144.0, 32768.0) * n);
}

vec3 hash32(vec2 p)
{
  // 3D random from 2D coord
  float n = sin(dot(p, vec2(12.9898, 78.233)));
  float a = fract(n * 43758.5453);
  float b = fract(n * 28001.8381);
  float c = fract(n * 11942.6740);
  return vec3(a, b, c);
}

float luma(vec3 c){ return dot(c, vec3(0.299, 0.587, 0.114)); }

// Find nearest and second nearest jittered seed in a regular grid cell neighborhood
// Returns: nearest position in q-space, and writes distances via out params
vec2 nearestSeed(vec2 q, out float d1, out float d2, vec2 seedBase)
{
  d1 = 1e9; d2 = 1e9; vec2 best = vec2(0.0);
  // Search 3x3 neighbors around q's integer cell
  vec2 gi = floor(q);
  for (int j = -1; j <= 1; j++)
  {
    for (int i = -1; i <= 1; i++)
    {
      vec2 cell = gi + vec2(float(i), float(j));
      // Jittered seed within this cell
      vec2 rnd = hash22(cell + seedBase) - 0.5;
      vec2 sp = cell + 0.5 + 0.8 * rnd; // keep seeds near cell centers
      vec2 v = sp - q;
      float dsq = dot(v, v);
      if (dsq < d1) { d2 = d1; d1 = dsq; best = sp; }
      else if (dsq < d2) { d2 = dsq; }
    }
  }
  return best;
}

void main()
{
  // Project uses flipped Y
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / max(uTexSize, vec2(1.0));
  vec3 baseCol = texture2D(uTex, clamp(uv, 0.0, 1.0)).rgb;

  // Strength mapping: compress to safe artistic range
  float s = clamp(uStrength, 0.0, 3.0) / 3.0;
  float amt = mix(0.15, 0.60, s);

  // Operate in pixel space to align with NES tiles
  vec2 px = uv * uTexSize;        // pixel coords
  vec2 tilePx = floor(px / 8.0);  // 8x8 NES tile id
  vec2 inTile = fract(px / 8.0);  // local [0,1) within tile

  // Subdivide each tile into N micro-facets on a regular grid (with jittered seeds)
  float facets = mix(2.0, 5.0, smoothstep(0.0, 1.0, s)); // 2..5 facets per axis within a tile
  vec2 q = inTile * facets; // local facet-space

  // Use tile id to decorrelate facet patterns between tiles
  vec2 seedBase = tilePx * 0.173 + 0.37;
  float d1, d2; // nearest and second-nearest squared distances
  vec2 sp = nearestSeed(q, d1, d2, seedBase);

  // Per-facet pseudo normal (constant over facet): from 3D random, biased toward +Z
  vec3 rn = hash32(sp + seedBase);
  vec3 n = normalize(vec3(rn.xy * 2.0 - 1.0, 1.2 + 0.6 * rn.z));

  // Refraction sample offset in UV units. Scale with facet size and strength.
  // Larger facets => larger possible offset in pixels, but clamp overall effect.
  float pxPerFacet = 8.0 / facets; // facet span in pixels
  float refrPx = amt * 0.45 * pxPerFacet; // pixels of offset at max
  vec2 baseOff = n.xy * refrPx * texel; // convert pixel offset to UV

  // Chromatic dispersion: vary offset slightly per channel
  vec2 offR = baseOff * 1.25;
  vec2 offG = baseOff * 1.00;
  vec2 offB = baseOff * 0.80;

  vec3 refrCol;
  refrCol.r = texture2D(uTex, clamp(uv + offR, 0.0, 1.0)).r;
  refrCol.g = texture2D(uTex, clamp(uv + offG, 0.0, 1.0)).g;
  refrCol.b = texture2D(uTex, clamp(uv + offB, 0.0, 1.0)).b;

  // Edge emphasis: thin darker seams where facets meet
  float edge = smoothstep(0.001, 0.02, d2 - d1); // d2-d1 small at edges
  float seam = 1.0 - edge; // 0 at center, ~1 at boundary

  // Sparkle: anisotropic specular that twinkles over time per facet
  vec3 L = normalize(vec3(0.35, 0.55, 1.0));
  float spec = pow(max(dot(n, L), 0.0), mix(40.0, 90.0, rn.z));
  float twk = 0.6 + 0.4 * sin(uTime * (5.0 + 3.0 * rn.x) + 6.2831 * rn.y);
  float spk = spec * twk;
  // Slightly tint sparkle by the base color to feel embedded in the sprite
  vec3 sparkle = spk * mix(vec3(1.0), baseCol, 0.5) * (0.12 + 0.18 * amt);

  // Mix base and refracted; preserve some luma to avoid heavy washout
  float y = luma(baseCol);
  vec3 mixed = mix(baseCol, refrCol, 0.4 + 0.6 * amt);
  float ym = luma(mixed);
  mixed *= mix(1.0, max(0.7, y / max(ym, 1e-3)), 0.35); // gentle luma preservation

  // Apply seams and sparkle
  mixed = mix(mixed, mixed * (0.75 + 0.15 * (1.0 - amt)), clamp(seam * (0.6 * amt), 0.0, 1.0));
  mixed += sparkle;

  // Mild clamp and contrast softening to keep cozy vibes
  mixed = clamp(mixed, 0.0, 1.0);
  vec3 g = vec3(1.0/(1.0+0.03*amt));
  mixed = pow(mixed, g);

  gl_FragColor = vec4(mixed, 1.0);
}

// DisplayName: DOT
// CoreName: Circular Shards Refraction
// Description: Overlapping circular shard field with edge darkening, shear, and chromatic dispersion driven by hashed directions.
// Performance: 2
// Rating: 4
// Category: Refraction
precision mediump float;

// DOT — Overlapping circular refraction shards
// Goal: Circular shard field with directional shear & chromatic dispersion.
// - Square lattice nearest-center selection
// - Overlapping circles (radius > half diagonal) ensure coverage
// - Direction per shard hashed + temporal wobble
// - Shear component adds planar gradient swirl
// - Boundary darkening (crack suggestion) & gentle contrast lift
// uStrength: 0..3 scales shard density, refraction magnitude, crack depth

// DOT — Overlapping circular refraction shards
// Partitions screen by Voronoi over a square lattice (nearest cell center).
// Each cell center defines an overlapping circle (radius > 0.707 cell) so the
// entire plane is covered with slight overlap (no gaps). Per-circle hashed
// refraction direction + subtle temporal wobble + dispersion. Edge darkening
// near the circle boundary suggests cracks.
// Strength (uStrength 0..3) scales shard density and refraction magnitude.

varying vec2 vTex;
uniform sampler2D uTex;     // Source frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source size
uniform float uStrength;    // 0..3 strength

// --- Hash helpers ---
float hash21(vec2 p){
  p = fract(p*vec2(123.34, 345.45));
  p += dot(p, p+34.23);
  return fract(p.x*p.y);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float s = clamp(uStrength, 0.0, 3.0);
  if (s <= 1e-5) { gl_FragColor = vec4(texture2D(uTex, uv).rgb, 1.0); return; }
  float k = s / 3.0; // 0..1

  // --- Square lattice setup ---
  float cellCount = mix(8.0, 42.0, k); // density vs strength
  vec2 gp = uv * cellCount;            // grid space
  vec2 ij = floor(gp);

  // Circle radius (in cell units). > sqrt(2)/2 (~0.7071) guarantees coverage.
  // Previously ~0.68..0.71 which produced smaller shards; increased to ~0.85..0.92
  // for a larger overlapping look. Adjust constants below to taste.
  float radius = 0.92 - 0.07*(1.0 - k); // ~0.85..0.92 (low strength..high strength)

  // Find nearest lattice center among 9 neighbors (unrolled for precision + ES2 friendliness)
  float bestDist = 1e9;
  vec2 bestCenter = vec2(0.0);
  vec2 bestKey = vec2(0.0);

  // Helper macro-like pattern via manual repetition
  {
    // Precompute base center for (0,0)
  }
  for (int dy=-1; dy<=1; ++dy){
    for (int dx=-1; dx<=1; ++dx){
      vec2 offs = vec2(float(dx), float(dy));
      vec2 center = (ij + offs + 0.5) / cellCount; // world UV of that center
      vec2 d = uv - center;
      float dist2 = dot(d,d);
      if (dist2 < bestDist){
        bestDist = dist2;
        bestCenter = center;
        bestKey = ij + offs; // integer-ish id for hashing
      }
    }
  }

  float dist = sqrt(bestDist) * cellCount;   // distance in cell units (0.0 at center)
  vec2 rel = (uv - bestCenter) * cellCount;  // relative coordinates in cell units

  // --- Refraction model ---
  float ang = 6.2831853 * hash21(bestKey);
  float wob = 0.45 * sin(uTime * (0.18 + 0.55*hash21(bestKey+2.7)) + ang*0.40);
  float ang2 = ang + wob;
  vec2 dir = vec2(cos(ang2), sin(ang2));
  vec2 perp = vec2(-dir.y, dir.x);

  // Base magnitude in pixels
  float pixMagR = mix(0.6, 5.8, k);
  float pixMagG = pixMagR * (0.95 + 0.05*hash21(bestKey+11.3));
  float pixMagB = pixMagR * 1.05;

  // Shear based on oriented projection of rel (mild planar gradient)
  float shear = (rel.x*0.7 - rel.y*0.7) * (0.7 + 1.25*k); // approx -?..? scaled
  vec2 offBase = dir * texel * pixMagR;
  vec2 offShear = perp * texel * (shear * 0.75 * pixMagR / radius); // scale by radius for consistency

  vec2 uvR = clamp(uv + offBase + offShear, 0.0, 1.0);
  vec2 uvG = clamp(uv + dir*texel*pixMagG + offShear*0.9, 0.0, 1.0);
  vec2 uvB = clamp(uv + dir*texel*pixMagB + offShear*1.1, 0.0, 1.0);

  vec3 col;
  col.r = texture2D(uTex, uvR).r;
  col.g = texture2D(uTex, uvG).g;
  col.b = texture2D(uTex, uvB).b;

  // --- Edge treatment (circle boundary) ---
  // All pixels lie inside at least one circle (radius > half-diagonal). Darken near boundary.
  float edgeW = mix(0.020, 0.050, 1.0 - k); // thinner at higher density
  float crackAmt = mix(0.14, 0.38, k);
  float d = radius - dist; // distance inward from boundary
  float crack = 1.0 - smoothstep(0.0, edgeW, d);
  col *= 1.0 - crackAmt * crack;

  // Gentle contrast lift
  col = (col - 0.5) * (1.0 + 0.05 + 0.10*k) + 0.5;
  col = clamp(col, 0.0, 1.0);

  gl_FragColor = vec4(col, 1.0);
}

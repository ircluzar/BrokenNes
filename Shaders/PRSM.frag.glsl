// DisplayName: PRSM
// Category: Refraction
precision mediump float;

// PRSM — Broken glass prism
// Divides the screen into triangular shards; each shard refracts the image with a
// small, hashed offset and subtle dispersion. Edge darkening suggests cracks.
// Strength (uStrength 0..3) scales shard density and refraction magnitude.

varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;        // seconds
uniform vec2 uTexSize;      // NES source size
uniform float uStrength;    // 0..3

// --- Hash helpers ---
float hash21(vec2 p){
  p = fract(p*vec2(123.34, 345.45));
  p += dot(p, p+34.23);
  return fract(p.x*p.y);
}
vec2 hash22(vec2 p){
  float n = hash21(p);
  float m = hash21(p + 37.2);
  return vec2(n, m);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float s = clamp(uStrength, 0.0, 3.0);
  if (s <= 1e-5) { gl_FragColor = vec4(texture2D(uTex, uv).rgb, 1.0); return; }
  float k = s / 3.0; // 0..1

  // ===== Triangular lattice mapping =====
  // Basis vectors for equilateral triangular grid
  const vec2 e1 = vec2(1.0, 0.0);
  const vec2 e2 = vec2(0.5, 0.8660254); // sqrt(3)/2
  // Column-major mat2(M) with columns e1,e2
  const mat2 M  = mat2(1.0, 0.5, 0.0, 0.8660254);
  // Inverse of M
  const mat2 Mi = mat2(1.0, -0.5773503, 0.0, 1.1547005);

  // Density: number of triangles across width
  float triCount = mix(10.0, 48.0, k);
  vec2 p = uv * triCount;        // grid-space
  vec2 q = Mi * p;               // lattice coordinates (i,j)
  vec2 ij = floor(q);
  vec2 f  = fract(q);
  // Triangle selector in rhombus (two triangles per rhombus)
  bool upper = (f.x + f.y > 1.0);
  // For edge distance, map to lower-triangle domain
  vec2 fL = upper ? (vec2(1.0) - f) : f;

  // Triangle center in lattice space: avg of 3 vertices
  // lower: (i+1/3, j+1/3), upper: (i+2/3, j+2/3)
  vec2 triCenterL = ij + (upper ? vec2(0.6666667) : vec2(0.3333333));
  vec2 triCenterP = M * triCenterL;         // grid-space center
  vec2 triCenterUv = triCenterP / triCount; // back to uv

  // Shard identity key for hashing (separate the two triangles per cell)
  vec2 triKey = ij + (upper ? vec2(7.0, 13.0) : vec2(3.0, 5.0));

  // ===== Refraction model =====
  // Per-shard base refraction direction with slow temporal wobble
  float ang = 6.2831853 * hash21(triKey);
  float wob = 0.35 * sin(uTime * (0.25 + 0.5*hash21(triKey+2.7)) + ang*0.5);
  float ang2 = ang + wob;
  vec2 dir = vec2(cos(ang2), sin(ang2));

  // Base magnitude in pixels, scaled by strength
  float pixMagR = mix(0.6, 6.0, k);
  float pixMagG = pixMagR * (0.95 + 0.05*hash21(triKey+11.3)); // tiny per-shard variance
  float pixMagB = pixMagR * 1.05; // slight dispersion

  // Subtle intra-triangle shear for a planar-like refraction gradient
  vec2 perp = vec2(-dir.y, dir.x);
  float shear = (fL.x - fL.y) * (0.6 + 1.2*k); // -1..1 scaled

  // Compose UV offsets (constant per shard + tiny shear across shard)
  vec2 offBase = dir * texel * pixMagR;
  vec2 offShear = perp * texel * (shear * 0.75 * pixMagR);

  // Sample positions (clamped) — apply small channel dispersion
  vec2 uvR = clamp(uv + offBase + offShear, 0.0, 1.0);
  vec2 uvG = clamp(uv + dir*texel*pixMagG + offShear*0.9, 0.0, 1.0);
  vec2 uvB = clamp(uv + dir*texel*pixMagB + offShear*1.1, 0.0, 1.0);

  vec3 col;
  col.r = texture2D(uTex, uvR).r;
  col.g = texture2D(uTex, uvG).g;
  col.b = texture2D(uTex, uvB).b;

  // --- Seam fixes: blend across all three triangle edges ---
  // Slightly enlarge/overlap triangles by blending with neighbors within a thin band.
  // 1) Diagonal within the same rhombus (f.x + f.y = 1)
  float diag = abs(f.x + f.y - 1.0);
  float bandDiag = max(0.70 / triCount, 0.0018);
  float blendDiag = 1.0 - smoothstep(0.0, bandDiag, diag);

  if (blendDiag > 0.0) {
    // Opposite triangle in the same rhombus (toggle upper/lower)
    bool upperD = !upper;
    vec2 triKeyD = ij + (upperD ? vec2(7.0, 13.0) : vec2(3.0, 5.0));

    float angD = 6.2831853 * hash21(triKeyD);
    float wobD = 0.35 * sin(uTime * (0.25 + 0.5*hash21(triKeyD+2.7)) + angD*0.5);
    float ang2D = angD + wobD;
    vec2 dirD = vec2(cos(ang2D), sin(ang2D));

    float pixMagG_D = pixMagR * (0.95 + 0.05*hash21(triKeyD+11.3));
    float pixMagB_D = pixMagR * 1.05;

    // Shear flips sign across the shared edge
    float shearD = -(fL.x - fL.y) * (0.6 + 1.2*k);
    vec2 perpD = vec2(-dirD.y, dirD.x);
    vec2 offBaseD  = dirD * texel * pixMagR;
    vec2 offShearD = perpD * texel * (shearD * 0.75 * pixMagR);

    vec2 uvRD = clamp(uv + offBaseD + offShearD, 0.0, 1.0);
    vec2 uvGD = clamp(uv + dirD*texel*pixMagG_D + offShearD*0.9, 0.0, 1.0);
    vec2 uvBD = clamp(uv + dirD*texel*pixMagB_D + offShearD*1.1, 0.0, 1.0);

    vec3 colD;
    colD.r = texture2D(uTex, uvRD).r;
    colD.g = texture2D(uTex, uvGD).g;
    colD.b = texture2D(uTex, uvBD).b;

    vec3 avgD = 0.5 * (col + colD);
    col = mix(col, avgD, clamp(blendDiag, 0.0, 1.0));
  }

  // 2) Edges aligned with lattice axes (fL.x = 0 and fL.y = 0) — neighbor cells
  float bandEdge = max(0.70 / triCount, 0.0018);
  float blendX = 1.0 - smoothstep(0.0, bandEdge, fL.x); // near x-edge
  float blendY = 1.0 - smoothstep(0.0, bandEdge, fL.y); // near y-edge

  // X-edge neighbor: cell offset depends on orientation
  if (blendX > 0.0) {
    bool upperX = !upper; // across any shared edge, neighbor triangle flips orientation
    vec2 ijX = ij + (upper ? vec2(1.0, 0.0) : vec2(-1.0, 0.0));
    vec2 triKeyX = ijX + (upperX ? vec2(7.0, 13.0) : vec2(3.0, 5.0));

    float angX = 6.2831853 * hash21(triKeyX);
    float wobX = 0.35 * sin(uTime * (0.25 + 0.5*hash21(triKeyX+2.7)) + angX*0.5);
    float ang2X = angX + wobX;
    vec2 dirX = vec2(cos(ang2X), sin(ang2X));

    float pixMagG_X = pixMagR * (0.95 + 0.05*hash21(triKeyX+11.3));
    float pixMagB_X = pixMagR * 1.05;

    float shearX = -(fL.x - fL.y) * (0.6 + 1.2*k);
    vec2 perpX = vec2(-dirX.y, dirX.x);
    vec2 offBaseX  = dirX * texel * pixMagR;
    vec2 offShearX = perpX * texel * (shearX * 0.75 * pixMagR);

    vec2 uvRX = clamp(uv + offBaseX + offShearX, 0.0, 1.0);
    vec2 uvGX = clamp(uv + dirX*texel*pixMagG_X + offShearX*0.9, 0.0, 1.0);
    vec2 uvBX = clamp(uv + dirX*texel*pixMagB_X + offShearX*1.1, 0.0, 1.0);

    vec3 colXv;
    colXv.r = texture2D(uTex, uvRX).r;
    colXv.g = texture2D(uTex, uvGX).g;
    colXv.b = texture2D(uTex, uvBX).b;

    vec3 avgX = 0.5 * (col + colXv);
    col = mix(col, avgX, clamp(blendX, 0.0, 1.0));
  }

  // Y-edge neighbor: cell offset depends on orientation
  if (blendY > 0.0) {
    bool upperY = !upper;
    vec2 ijY = ij + (upper ? vec2(0.0, 1.0) : vec2(0.0, -1.0));
    vec2 triKeyY = ijY + (upperY ? vec2(7.0, 13.0) : vec2(3.0, 5.0));

    float angY = 6.2831853 * hash21(triKeyY);
    float wobY = 0.35 * sin(uTime * (0.25 + 0.5*hash21(triKeyY+2.7)) + angY*0.5);
    float ang2Y = angY + wobY;
    vec2 dirY = vec2(cos(ang2Y), sin(ang2Y));

    float pixMagG_Y = pixMagR * (0.95 + 0.05*hash21(triKeyY+11.3));
    float pixMagB_Y = pixMagR * 1.05;

    float shearY = -(fL.x - fL.y) * (0.6 + 1.2*k);
    vec2 perpY = vec2(-dirY.y, dirY.x);
    vec2 offBaseY  = dirY * texel * pixMagR;
    vec2 offShearY = perpY * texel * (shearY * 0.75 * pixMagR);

    vec2 uvRY = clamp(uv + offBaseY + offShearY, 0.0, 1.0);
    vec2 uvGY = clamp(uv + dirY*texel*pixMagG_Y + offShearY*0.9, 0.0, 1.0);
    vec2 uvBY = clamp(uv + dirY*texel*pixMagB_Y + offShearY*1.1, 0.0, 1.0);

    vec3 colYv;
    colYv.r = texture2D(uTex, uvRY).r;
    colYv.g = texture2D(uTex, uvGY).g;
    colYv.b = texture2D(uTex, uvBY).b;

    vec3 avgY = 0.5 * (col + colYv);
    col = mix(col, avgY, clamp(blendY, 0.0, 1.0));
  }

  // Edge darkening to suggest cracks — distance to nearest triangle edge in lower domain
  float dEdge = min(fL.x, min(fL.y, 1.0 - fL.x - fL.y));
  float edgeW = mix(0.020, 0.045, 1.0 - k); // thinner edges at higher strength (more shards)
  float crack = 1.0 - smoothstep(0.0, edgeW, dEdge);
  float crackAmt = mix(0.18, 0.45, k);
  // Reduce crack darkening where we blend across any edge to avoid double-darkened seams
  float totalBlend = clamp(blendDiag + blendX + blendY, 0.0, 1.0);
  float crackBlendReduce = 1.0 - 0.5 * totalBlend;
  col *= 1.0 - (crackAmt * crackBlendReduce) * crack;

  // Gentle contrast lift for clarity
  col = (col - 0.5) * (1.0 + 0.06 + 0.10*k) + 0.5;
  col = clamp(col, 0.0, 1.0);

  gl_FragColor = vec4(col, 1.0);
}

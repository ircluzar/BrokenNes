// DisplayName: CRY
// CoreName: Crystalline Refraction
// Description: Faceted Voronoi-driven refraction with edge-weighted displacement, subtle dispersion, and gentle inter-shard bleed.
// Performance: -25
// Rating: 1
// Category: Refraction
precision mediump float;

// CRY — Crystalline refraction
// Goal: Faceted refraction whose strength follows scene edges & shard structure.
// - Voronoi-like facet field supplies quantized normals
// - Edge-driven displacement magnitude + per-cell jitter
// - Subtle chromatic dispersion & cyan tint (no rainbow wash)
// - Gentle inter-shard bleed & light sparkle near centers
// uStrength: 0..3 increases cell resolution, displacement, tint & sparkle

varying vec2 vTex;
uniform sampler2D uTex;     // Source NES frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source pixel dimensions
uniform float uStrength;    // 0..3 strength

// Hash helpers
float hash21(vec2 p){
  p = fract(p * vec2(123.34, 456.21));
  p += dot(p, p + 78.233);
  return fract(p.x * p.y);
}
vec2 hash22(vec2 p){
  return fract(sin(vec2(
    dot(p,vec2(127.1,311.7)),
    dot(p,vec2(269.5,183.3))
  ))*43758.5453);
}

// Cell/Voronoi: returns (nearest distance, edge sharpness proxy)
vec2 cellInfo(vec2 p){
  vec2 ip = floor(p);
  vec2 fp = fract(p);
  float minD = 1e9;
  float secondD = 1e9;
  for(int y=-1;y<=1;y++){
    for(int x=-1;x<=1;x++){
      vec2 b = vec2(float(x), float(y));
      vec2 off = hash22(ip + b);
      off = off * 0.9 + 0.05; // bias away from corners slightly
      vec2 pt = b + off;
      float d = distance(fp, pt);
      if(d < minD){ secondD = minD; minD = d; }
      else if(d < secondD){ secondD = d; }
    }
  }
  float edge = clamp(secondD - minD, 0.0, 1.0);
  return vec2(minD, edge);
}

// Gradient of the cell distance field (approximate normal in grid space)
vec2 fieldNormal(vec2 p){
  float eps = 0.65;
  float d = cellInfo(p).x;
  float dx = cellInfo(p + vec2(eps, 0.0)).x - d;
  float dy = cellInfo(p + vec2(0.0, eps)).x - d;
  return normalize(vec2(dx, dy) + 1e-6);
}

float luma(vec3 c){ return dot(c, vec3(0.299, 0.587, 0.114)); }

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float strength = clamp(uStrength, 0.0, 3.0);
  vec2 texel = 1.0 / uTexSize;

  // --- Base sample & edge gradient ---
  vec3 center = texture2D(uTex, uv).rgb;
  float Lc = luma(center);
  float Lx1 = luma(texture2D(uTex, clamp(uv + vec2(texel.x, 0.0), 0.0, 1.0)).rgb);
  float Lx0 = luma(texture2D(uTex, clamp(uv - vec2(texel.x, 0.0), 0.0, 1.0)).rgb);
  float Ly1 = luma(texture2D(uTex, clamp(uv + vec2(0.0, texel.y), 0.0, 1.0)).rgb);
  float Ly0 = luma(texture2D(uTex, clamp(uv - vec2(0.0, texel.y), 0.0, 1.0)).rgb);
  float gx = (Lx1 - Lx0);
  float gy = (Ly1 - Ly0);
  float edgeMag = clamp(length(vec2(gx,gy)) * 4.0, 0.0, 1.0); // normalize-ish

  // --- Facet field setup ---
  float minCells = 20.0;
  float maxCells = 64.0;
  float cells = mix(minCells, maxCells, clamp(strength*0.5, 0.0, 1.0));
  vec2 gp = uv * cells;

  // Slow global wobble
  vec2 jiggle = vec2(sin(uTime*0.45), cos(uTime*0.33)) * 0.02 * strength;
  gp += jiggle * cells;

  vec2 info = cellInfo(gp);
  float minD = info.x;     // distance to shard center
  float edgeF = info.y;    // shard edge sharpness proxy

  // --- Facet normal quantization ---
  vec2 n = fieldNormal(gp);
  float facets = mix(4.0, 9.0, clamp(strength*0.5, 0.0, 1.0));
  float a = atan(n.y, n.x);
  float q = floor((a / 6.2831853) * facets);
  float aQ = (q + 0.5) * (6.2831853 / facets);
  n = vec2(cos(aQ), sin(aQ));

  // --- Content-driven refraction magnitude ---
  float centerInfluence = 1.0 - smoothstep(0.0, 0.6, minD*2.0);
  float facetSharp = pow(edgeF, 1.6);
  float dispBase = (0.0022 + 0.0018*strength);
  float dispAmount = dispBase * (0.45 + 0.55*edgeMag) * (0.6 + 1.4*centerInfluence) * (0.5 + 1.2*facetSharp);

  // Per-cell jitter
  vec2 jitter = (hash22(floor(gp)) - 0.5) * dispBase * 1.8;

  // Final displacement direction
  vec2 disp = n * dispAmount + jitter;

  // --- Chromatic dispersion ---
  vec2 uvR = clamp(uv + disp * 1.0, 0.0, 1.0);
  vec2 uvG = clamp(uv + disp * 0.85, 0.0, 1.0);
  vec2 uvB = clamp(uv + disp * 0.70, 0.0, 1.0);

  // Snap to texel centers for a crisp, pixel-perfect feel
  uvR = (floor(uvR * uTexSize) + 0.5) / uTexSize;
  uvG = (floor(uvG * uTexSize) + 0.5) / uTexSize;
  uvB = (floor(uvB * uTexSize) + 0.5) / uTexSize;

  vec3 col;
  col.r = texture2D(uTex, uvR).r;
  col.g = texture2D(uTex, uvG).g;
  col.b = texture2D(uTex, uvB).b;

  // --- Inter-shard bleed (energy-preserving) ---
  float bleed = smoothstep(0.0, 0.5, minD) * 0.22 * strength;
  if(bleed > 0.0){
    vec3 acc = vec3(0.0); float wsum=0.0;
    for(int y=-1;y<=1;y++){
      for(int x=-1;x<=1;x++){
        vec2 o = vec2(float(x), float(y)) * texel;
        float w = 1.0 - 0.08 * float(x*x + y*y);
        acc += texture2D(uTex, clamp(uv + o + disp*0.25, 0.0, 1.0)).rgb * w;
        wsum += w;
      }
    }
    acc /= max(wsum, 1e-4);
    col = mix(col, acc, bleed);
  }

  // --- Subtle crystalline tint ---
  vec3 tint = vec3(0.95, 1.02, 1.06);
  float tintAmt = mix(0.08, 0.20, clamp(strength*0.5, 0.0, 1.0));
  col = mix(col, col * tint, tintAmt);

  // --- Edge sparkle ---
  float sparkle = pow(max(0.0, 1.0 - minD*5.0), 3.0) * (0.3 + 0.7*edgeF);
  float twinkle = hash21(floor(gp) + vec2(uTime*1.7));
  float glint = sparkle * (0.55 + 0.45 * twinkle) * 0.25 * strength;
  col += glint;

  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col, 1.0);
}

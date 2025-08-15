// DisplayName: BLD
// Category: Stylize
precision mediump float;

// BLD — 4‑direction color bleed (edge‑aware directional diffusion)
// Goal: Let saturated pixels softly “poison” neighbors while keeping edges legible.
// - Gather chains of samples in +/‑X and +/‑Y (4 rays) with exponential falloff
// - Weight by saturation; gate across large luminance edges to avoid heavy smears
// - Merge directional contributions and restore a touch of contrast & saturation
// - Strength scales ray length, bleed weight, and contrast recovery
// uStrength: 0..3 overall effect intensity

varying vec2 vTex;
uniform sampler2D uTex;     // Source NES frame
uniform float uTime;        // Seconds (unused; reserved)
uniform vec2 uTexSize;      // Source size in pixels
uniform float uStrength;    // 0..3 strength

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

float luma(vec3 c){ return dot(c, LUMA); }
float satVal(vec3 c){ float y = luma(c); return length(c - vec3(y)); }

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float s = clamp(uStrength, 0.0, 3.0);
  if (s <= 1e-5) { gl_FragColor = vec4(texture2D(uTex, uv).rgb, 1.0); return; }
  float k = s / 3.0; // 0..1

  vec3 base = texture2D(uTex, uv).rgb;
  float baseLum = luma(base);

  // --- Ray configuration ---
  int maxSteps = int(2.0 + floor(k * 4.0)); // 2..6 samples each direction
  float falloff = mix(1.35, 0.55, k);       // faster falloff at low strength
  float satBoost = mix(1.2, 1.8, k);        // higher saturation influence
  float edgeProtect = mix(0.55, 0.30, k);   // reduce edge protection with strength

  vec3 accum = base * 1.0; // include self
  float wSum = 1.0;

  // --- Directional diffusion accumulate (4 cardinal rays) ---
  for(int dirIdx=0; dirIdx<4; ++dirIdx){
    vec2 dir = (dirIdx==0)? vec2( 1.0, 0.0) :
               (dirIdx==1)? vec2(-1.0, 0.0) :
               (dirIdx==2)? vec2( 0.0, 1.0) : vec2(0.0,-1.0);
    vec3 lastColor = base;
    float lastLum = baseLum;
    for(int step=1; step<=6; ++step){ // upper bound for static loop; early continue if beyond maxSteps
      if(step > maxSteps) break;
      float fstep = float(step);
      vec2 o = uv + dir * texel * fstep;
      o = clamp(o, 0.0, 1.0);
      vec3 c = texture2D(uTex, o).rgb;
      float lumC = luma(c);
      float ed = abs(lumC - lastLum); // local edge delta between chain samples
      float satC = satVal(c);
      // Weight: exponential distance falloff * saturation factor * edge gating
      float w = exp(-fstep * falloff);
      float satW = 1.0 + (satC * satBoost);
      float edgeW = 1.0 / (1.0 + ed * (8.0 * edgeProtect));
      float totalW = w * satW * edgeW;
      accum += c * totalW;
      wSum += totalW;
      // Allow color to continue leaking even across moderate edges by partially
      // relaxing lastLum interpolation toward new sample (prevents harsh stops)
      lastLum = mix(lastLum, lumC, 0.35 + 0.25*k);
      lastColor = c;
    }
  }

  vec3 bleedCol = accum / max(wSum, 1e-5);

  // --- Contrast & saturation restoration ---
  float Lb = luma(bleedCol);
  vec3 L3 = vec3(Lb);
  float satAmt = mix(1.05, 1.25, k); // gentle
  vec3 satCol = L3 + (bleedCol - L3) * satAmt;
  float contrast = mix(1.00, 1.08 + 0.10*k, k); // increasing micro-contrast
  vec3 restored = (satCol - 0.5) * contrast + 0.5;

  // --- Blend & finalize ---
  float blend = mix(0.30, 0.85, k);
  vec3 outCol = mix(base, restored, blend);

  // Mild clamp & gamma nuance to avoid dulling
  outCol = clamp(outCol, 0.0, 1.0);
  float gamma = mix(1.0, 0.97, k);
  outCol = pow(outCol, vec3(gamma));

  gl_FragColor = vec4(outCol, 1.0);
}

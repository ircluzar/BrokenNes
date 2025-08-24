// DisplayName: RGBX
// CoreName: Chromatic Vector
// Description: Separates RGB channels into distinct animated motion fields with soft bleed for a readable psychedelic effect.
// Performance: -17
// Rating: 5
// Category: Color
precision mediump float;

// RGBX — Animated chromatic vector split
// Goal: Psychedelic, readable chromatic aberration with channel-specific motion.
// - Radial/tangential/inverse radial channel directions with wobble
// - Edge-weighted magnitude & temporal modulation
// - Directional gathers per channel for soft bleed
// - Mild hue rotation + contrast pop
// uStrength: 0..3 scales separation magnitude & bleed

// RGB channel splitter with animated chromatic aberration and psychedelic bleed.
// - Separates R, G, B into different motion fields (radial, tangential, inverse radial)
// - Time-varying offsets create lively color fringing
// - Directional gathers per channel produce soft, trippy color bleeding while keeping detail readable

varying vec2 vTex;
uniform sampler2D uTex;     // Source NES frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source pixel dimensions
uniform float uStrength;    // 0..3 strength

float luma(vec3 c){ return dot(c, vec3(0.299, 0.587, 0.114)); }

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float s = clamp(uStrength, 0.0, 3.0);

  // --- Center-relative geometry ---
  vec2 toC = uv - 0.5;
  float r = length(toC) + 1e-6;
  vec2 nrm = toC / r;                 // outward radial
  vec2 tanv = vec2(-nrm.y, nrm.x);    // tangential (CCW)
  float ang = atan(toC.y, toC.x);

  // --- Time-varying channel directions ---
  // R: mostly radial outward with swirling wobble
  // G: mostly tangential sweep
  // B: inward radial with different wobble phase
  float wigR = 0.35 * sin(t*1.3 + ang*5.0);
  float wigG = 0.35 * sin(t*1.1 + ang*7.0 + 2.1);
  float wigB = 0.35 * sin(t*1.5 + ang*6.0 + 4.2);
  vec2 dirR = normalize(nrm + wigR * vec2(cos(ang*3.0 + t*0.9), sin(ang*3.0 + t*0.9)));
  vec2 dirG = normalize(tanv + wigG * vec2(cos(ang*4.0 - t*1.1), sin(ang*4.0 - t*1.1)));
  vec2 dirB = normalize(-nrm + wigB * vec2(cos(ang*5.0 + t*1.2), sin(ang*5.0 + t*1.2)));

  // --- Base magnitude in texels ---
  float edgeBoost = smoothstep(0.0, 0.6, r*1.6);
  float mag = (0.45 + 1.35*s) * (0.5 + 0.7*edgeBoost);

  // --- Temporal modulation ---
  float mR = (1.0 + 0.45*sin(t*1.7 + r*22.0 + ang*3.0));
  float mG = (1.0 + 0.45*sin(t*1.4 + r*19.0 - ang*4.0));
  float mB = (1.0 + 0.45*sin(t*1.9 + r*25.0 + ang*2.0));

  vec2 offR = dirR * texel * mag * mR;
  vec2 offG = dirG * texel * (mag * 0.85) * mG;
  vec2 offB = dirB * texel * (mag * 1.10) * mB;

  // --- Directional bleeding gathers ---
  // Keep loops tiny for performance. Bleed grows with strength but stays readable.
  float bleed = mix(0.18, 0.70, clamp(s/3.0, 0.0, 1.0));
  int taps = 2; // [-2..2]

  // Red gather
  float rAcc = 0.0; float rW = 0.0;
  for(int i=-2;i<=2;i++){
    float fi = float(i);
    float w = exp(-fi*fi*0.42);
    vec2 p = uv + offR + dirR * fi * texel * (1.4 * bleed);
    rAcc += texture2D(uTex, clamp(p, 0.0, 1.0)).r * w;
    rW += w;
  }
  float R = rAcc / max(rW, 1e-4);

  // Green gather
  float gAcc = 0.0; float gW = 0.0;
  for(int i=-2;i<=2;i++){
    float fi = float(i);
    float w = exp(-fi*fi*0.42);
    vec2 p = uv + offG + dirG * fi * texel * (1.1 * bleed);
    gAcc += texture2D(uTex, clamp(p, 0.0, 1.0)).g * w;
    gW += w;
  }
  float G = gAcc / max(gW, 1e-4);

  // Blue gather
  float bAcc = 0.0; float bW = 0.0;
  for(int i=-2;i<=2;i++){
    float fi = float(i);
    float w = exp(-fi*fi*0.42);
    vec2 p = uv + offB + dirB * fi * texel * (1.6 * bleed);
    bAcc += texture2D(uTex, clamp(p, 0.0, 1.0)).b * w;
    bW += w;
  }
  float B = bAcc / max(bW, 1e-4);

  vec3 col = vec3(R, G, B);

  // --- Mild hue rotation ---
  float rot = 0.18 * clamp(s*0.6, 0.0, 1.0) * sin(t*0.9);
  mat3 hue = mat3(
    0.95+0.05*cos(rot+0.0), 0.05*sin(rot+1.7),     0.05*sin(rot+3.1),
    0.05*sin(rot+0.8),      0.95+0.05*cos(rot+2.1), 0.05*sin(rot+0.3),
    0.05*sin(rot+2.2),      0.05*sin(rot+1.2),     0.95+0.05*cos(rot+4.0)
  );
  col = clamp(hue * col, 0.0, 1.0);

  // --- Contrast pop ---
  float L = luma(col);
  col = mix(vec3(L), col, 1.06);
  col = (col - 0.5) * 1.06 + 0.5;
  col = clamp(col, 0.0, 1.0);

  gl_FragColor = vec4(col, 1.0);
}

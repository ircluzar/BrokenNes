// DisplayName: CNMA
// Category: Color
precision mediump float;

// CNMA — Cinematic content‑aware grading
// Goal: Filmic midtone/exposure shaping with subtle teal & orange grade.
// - Adaptive local exposure targeting dynamic midgray
// - Midtone contrast curve + shoulder highlight rolloff
// - Local contrast (unsharp) with highlight protection
// - Teal shadows & warm highlights; adaptive saturation
// - Optional halation & vignette modulated by brightness
// uStrength: 0..3 scales exposure adaptivity, contrast, grade, and halo/vignette

varying vec2 vTex;
uniform sampler2D uTex;     // Source frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source size
uniform float uStrength;    // 0..3 strength

float luma(vec3 c){ return dot(c, vec3(0.299, 0.587, 0.114)); }
vec3 saturate(vec3 c){ return clamp(c, 0.0, 1.0); }
vec3 satAdjust(vec3 c, float s){ float L = luma(c); return mix(vec3(L), c, s); }

// 9-tap box-ish blur (center + 8 neighbors)
vec3 blur9(vec2 uv, vec2 texel, float radius){
  vec3 acc = vec3(0.0); float wsum=0.0;
  for(int y=-1;y<=1;y++){
    for(int x=-1;x<=1;x++){
      vec2 o = vec2(float(x), float(y)) * texel * radius;
      float w = (x==0 && y==0) ? 1.0 : 0.75; // mild center bias
      acc += texture2D(uTex, clamp(uv + o, 0.0, 1.0)).rgb * w;
      wsum += w;
    }
  }
  return acc / max(wsum, 1e-4);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float s = clamp(uStrength, 0.0, 3.0);

  // --- Base sample ---
  vec3 col0 = texture2D(uTex, uv).rgb;
  float L0 = luma(col0);

  // --- Local luminance averages (two radii) ---
  vec3 blurSmall = blur9(uv, texel, 1.0);
  vec3 blurLarge = blur9(uv, texel, 3.0);
  float Ls = luma(blurSmall);
  float Ll = luma(blurLarge);
  float Lavg = mix(Ls, Ll, 0.5);

  // --- Content-adaptive exposure ---
  float targetMid = mix(0.42, 0.48, 0.5 + 0.5*sin(uTime*0.05)); // tiny drift to avoid static feel
  float Exposure = clamp(targetMid / (Lavg + 1e-3), 0.6, 1.8);
  float expAmt = mix(0.0, 1.0, clamp(s/3.0, 0.0, 1.0));
  vec3 col1 = col0 * mix(1.0, Exposure, 0.65*expAmt);

  // --- Midtone contrast curve & shoulder ---
  float pivot = clamp(mix(0.45, Lavg, 0.6), 0.25, 0.65);
  float contrast = mix(1.02, 1.35, clamp(s/3.0, 0.0, 1.0));
  col1 = (col1 - vec3(pivot)) * contrast + vec3(pivot);

  // Highlight rolloff (filmic shoulder)
  vec3 shoulder = col1 / (col1 + vec3(0.7)); // soft rolloff
  col1 = mix(col1, shoulder, 0.45 * expAmt);

  // --- Local contrast (unsharp mask on luma) ---
  vec3 blurC = blur9(uv, texel, 1.5);
  vec3 detail = col1 - blurC;
  float detailAmt = mix(0.10, 0.35, clamp(s/3.0,0.0,1.0));
  // Protect highlights from ringing
  float hiMask = 1.0 - smoothstep(0.6, 0.95, luma(col1));
  vec3 col2 = col1 + detail * (detailAmt * hiMask);

  // --- Teal & orange grade ---
  float L2 = luma(col2);
  float coolW = smoothstep(0.55, 0.15, L2);
  float warmW = smoothstep(0.35, 0.85, L2);
  vec3 coolTint = vec3(0.96, 1.02, 1.08);
  vec3 warmTint = vec3(1.06, 1.02, 0.97);
  float gradeAmt = 0.35 * clamp(s/3.0, 0.0, 1.0);
  vec3 col3 = col2 * mix(vec3(1.0), coolTint, gradeAmt * coolW);
  col3 = col3 * mix(vec3(1.0), warmTint, gradeAmt * warmW);

  // --- Adaptive saturation ---
  float sat = mix(1.0, 1.20, clamp(s/3.0,0.0,1.0));
  float midMask = smoothstep(0.15, 0.50, L2) * (1.0 - smoothstep(0.65, 0.95, L2));
  col3 = satAdjust(col3, mix(1.0, sat, midMask));

  // --- Soft halation ---
  float bright = smoothstep(0.7, 1.0, L2);
  if(bright > 0.0){
    vec3 halo = vec3(0.0);
    float wsum = 0.0;
    for(int a=0;a<6;a++){
      float fa = float(a);
      float ang = fa/6.0 * 6.2831853;
      vec2 dir = vec2(cos(ang), sin(ang));
      float r = 1.5 + 0.8*fa; // small expanding ring
      vec2 o = dir * texel * r;
      float w = 1.0 / (1.0 + fa);
      halo += texture2D(uTex, clamp(uv + o, 0.0, 1.0)).rgb * w;
      wsum += w;
    }
    halo /= max(wsum, 1e-4);
    vec3 halation = mix(col3, halo, 0.5) * vec3(1.03, 0.99, 0.96); // slightly reddish
    col3 = mix(col3, halation, bright * 0.12 * clamp(s/3.0,0.0,1.0));
  }

  // --- Vignette ---
  float r = length((uv - 0.5) * vec2(1.1, 1.0));
  float vign = mix(1.0, smoothstep(0.9, 0.3, r), 0.45 * clamp(s/3.0,0.0,1.0));
  vec3 col = col3 * vign;

  col = saturate(col);
  gl_FragColor = vec4(col, 1.0);
}

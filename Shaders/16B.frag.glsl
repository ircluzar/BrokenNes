// DisplayName: 16B
// Category: Enhance
precision mediump float;

// 16B — SNES-like upgrade
// Goal: make NES output feel more like a 16-bit console (SNES) by
// - Smoothing blocky pixels with a gentle edge-aware blur
// - Performing light chroma-only blur (cleaner color transitions)
// - Boosting saturation and a slight gamma lift for richer color
// - Optional subtle scanline shading (very light)
// uStrength: 0..3 scales overall intensity.

varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;
uniform vec2 uTexSize;   // 256x240 for NES
uniform float uStrength; // 0..3

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

vec3 rgb2yuv(vec3 c){
  float y = dot(c, LUMA);
  float u = (c.b - y) * 0.565; // BT.601 approx
  float v = (c.r - y) * 0.713;
  return vec3(y,u,v);
}

vec3 yuv2rgb(vec3 yuv){
  float y = yuv.x, u = yuv.y, v = yuv.z;
  float r = y + 1.403 * v;
  float g = y - 0.344 * u - 0.714 * v;
  float b = y + 1.770 * u;
  return vec3(r,g,b);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float k = clamp(uStrength, 0.0, 3.0) / 3.0; // 0..1 intensity

  // Base color
  vec3 c0 = texture2D(uTex, uv).rgb;

  // --- Edge-aware 3x3 smoothing (simple bilateral-ish) ---
  // Gaussian kernel 1 2 1; 2 4 2; 1 2 1 (sum 16)
  float w1 = 1.0, w2 = 2.0, w4 = 4.0;
  float sigma = mix(0.020, 0.060, k); // edge preservation (luma domain)
  float inv2s2 = 0.5 / (sigma*sigma);

  float l0 = dot(c0, LUMA);

  vec3 acc = vec3(0.0);
  float wsum = 0.0;

  // Center
  {
    float w = w4;
    acc += c0 * w;
    wsum += w;
  }
  // Cardinal neighbors
  for(int i=0;i<2;i++){
    // i==0 -> left/right, i==1 -> up/down
    vec2 o = (i==0) ? vec2(texel.x,0.0) : vec2(0.0,texel.y);
    vec3 cL = texture2D(uTex, clamp(uv - o, 0.0, 1.0)).rgb;
    vec3 cR = texture2D(uTex, clamp(uv + o, 0.0, 1.0)).rgb;
    float lL = dot(cL, LUMA);
    float lR = dot(cR, LUMA);
    float gL = exp(-(lL-l0)*(lL-l0)*inv2s2);
    float gR = exp(-(lR-l0)*(lR-l0)*inv2s2);
    float wL = w2 * mix(1.0, gL, 0.8);
    float wR = w2 * mix(1.0, gR, 0.8);
    acc += cL*wL + cR*wR;
    wsum += wL + wR;
  }
  // Diagonals
  {
    vec2 o = texel;
    vec3 c1 = texture2D(uTex, clamp(uv + vec2(-o.x,-o.y), 0.0, 1.0)).rgb;
    vec3 c2 = texture2D(uTex, clamp(uv + vec2( o.x,-o.y), 0.0, 1.0)).rgb;
    vec3 c3 = texture2D(uTex, clamp(uv + vec2(-o.x, o.y), 0.0, 1.0)).rgb;
    vec3 c4 = texture2D(uTex, clamp(uv + vec2( o.x, o.y), 0.0, 1.0)).rgb;
    float l1 = dot(c1, LUMA), l2 = dot(c2, LUMA), l3 = dot(c3, LUMA), l4 = dot(c4, LUMA);
    float g1 = exp(-(l1-l0)*(l1-l0)*inv2s2);
    float g2 = exp(-(l2-l0)*(l2-l0)*inv2s2);
    float g3 = exp(-(l3-l0)*(l3-l0)*inv2s2);
    float g4 = exp(-(l4-l0)*(l4-l0)*inv2s2);
    float w = w1;
    float w1b = w * mix(1.0, g1, 0.8);
    float w2b = w * mix(1.0, g2, 0.8);
    float w3b = w * mix(1.0, g3, 0.8);
    float w4b = w * mix(1.0, g4, 0.8);
    acc += c1*w1b + c2*w2b + c3*w3b + c4*w4b;
    wsum += w1b + w2b + w3b + w4b;
  }

  vec3 smooth9 = acc / max(wsum, 1e-5);

  // Blend original toward smoothed based on strength
  vec3 smoothCol = mix(c0, smooth9, mix(0.28, 0.85, k));

  // --- Chroma-only horizontal blur (cleaner color transitions) ---
  // Sample neighbors and blur U/V slightly; keep Y mostly from smoothCol
  vec3 yuvC = rgb2yuv(smoothCol);
  vec3 yuvL = rgb2yuv(texture2D(uTex, clamp(uv - vec2(texel.x,0.0), 0.0, 1.0)).rgb);
  vec3 yuvR = rgb2yuv(texture2D(uTex, clamp(uv + vec2(texel.x,0.0), 0.0, 1.0)).rgb);
  float chromaMix = mix(0.20, 0.60, k); // how much we blur U/V
  float yKeep = mix(0.85, 0.95, k);     // keep most of smoothed luma
  float U = mix(yuvC.y, (yuvL.y*0.25 + yuvC.y*0.5 + yuvR.y*0.25), chromaMix);
  float V = mix(yuvC.z, (yuvL.z*0.25 + yuvC.z*0.5 + yuvR.z*0.25), chromaMix);
  float Y = mix(dot(smoothCol, LUMA), yuvC.x, yKeep);
  vec3 chromaSmoothed = yuv2rgb(vec3(Y,U,V));

  // --- Saturation boost and gentle gamma lift ---
  float L = dot(chromaSmoothed, LUMA);
  vec3 L3 = vec3(L);
  float sat = mix(1.05, 1.55, k); // 5%..55% boost
  vec3 satCol = L3 + (chromaSmoothed - L3) * sat;
  float gamma = mix(1.00, 0.92, k); // <1 brightens slightly
  vec3 tone = pow(satCol, vec3(gamma));
  // Mild contrast curve to soften harsh steps
  tone = (tone - 0.5) * (1.0 - 0.12 * k) + 0.5;

  // --- Very subtle scanline shading (optional) ---
  float line = fract(uv.y * uTexSize.y);
  float scan = mix(0.00, 0.06, k); // up to 6%
  float shade = 1.0 - scan * smoothstep(0.0, 1.0, line);

  vec3 outCol = clamp(tone * shade, 0.0, 1.0);
  gl_FragColor = vec4(outCol, 1.0);
}

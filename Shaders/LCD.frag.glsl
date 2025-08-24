// DisplayName: LCD
// CoreName: Aging LCD Artifact
// Description: Simulates old LCD traits: horizontal smear, subpixel ghosting, frost diffusion, banding, and grain.
// Performance: 2
// Rating: 4
// Category: Retro
precision mediump float;

// LCD — Smear/ghost/frost LCD artifact
// Goal: Simulate aging LCD with horizontal smear, subpixel ghost, frost diffusion & banding.
// - Frost diffusion radial multi-sample
// - Horizontal smear trail
// - Ghost offset with vertical modulation
// - Column banding & grain; optional desaturation by strength
// uStrength: 0..3 scales smear, ghost, frost, banding & grain
varying vec2 vTex;
uniform sampler2D uTex;    // Source frame
uniform float uTime;       // Seconds
uniform vec2 uTexSize;     // Source size
uniform float uStrength;   // 0..3 strength
float hash(vec2 p){ p=fract(p*vec2(127.1,311.7)); p+=dot(p,p+19.19); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }
void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);
  float smear = mix(0.18, 0.48, strength);
  float ghost = mix(0.10, 0.32, strength);
  float frost = mix(0.10, 0.32, strength);
  // --- Frost diffusion ---
  vec3 frostAccum = vec3(0.0); float wsum=0.0;
  for(int i=0;i<7;i++){
    float a = float(i)/7.0 * 6.28318 + t*0.13;
    float r = (0.5 + 0.5*noise(uv*vec2(80.0,60.0)+a+t*0.7)) * frost * 7.0;
    vec2 offs = vec2(cos(a),sin(a)) * texel * r;
    float w = 1.0 - 0.12*float(i);
    frostAccum += texture2D(uTex, clamp(uv + offs,0.0,1.0)).rgb * w;
    wsum += w;
  }
  frostAccum /= wsum;
  // --- Horizontal smear ---
  vec3 smearAccum = vec3(0.0); wsum=0.0;
  for(int j=-4;j<=4;j++){
    float fj = float(j);
    float w = exp(-fj*fj*0.18);
    vec2 offs = vec2(fj*texel.x*smear*2.5, 0.0);
    smearAccum += texture2D(uTex, clamp(uv - offs,0.0,1.0)).rgb * w;
    wsum += w;
  }
  smearAccum /= wsum;
  float ghostPhase = sin(t*0.7+uv.y*8.0)*0.5+0.5;
  vec2 ghostOff = vec2(-ghost*ghostPhase*2.0*texel.x, ghost*ghostPhase*1.2*texel.y);
  // --- Ghost sample ---
  vec3 ghostCol = texture2D(uTex, clamp(uv + ghostOff,0.0,1.0)).rgb;
  vec3 base = texture2D(uTex, uv).rgb;
  // --- Composite layering ---
  vec3 col = mix(base, frostAccum, frost*0.7);
  col = mix(col, smearAccum, smear*0.7);
  col = mix(col, ghostCol, ghost*0.6);
  float colBand = sin(uv.x*uTexSize.x*3.14159*0.5 + t*0.2);
  col *= 0.97 + 0.03*colBand;
  // --- Grain ---
  float grain = noise(uv*vec2(320.0,240.0)+t*1.3)-0.5;
  col += grain * 0.025 * (0.7+0.7*strength);
  float L = luma(col);
  col = mix(vec3(L), col, 0.82 - 0.18*strength);
  col = clamp(col,0.0,1.0);
  gl_FragColor = vec4(col,1.0);
}

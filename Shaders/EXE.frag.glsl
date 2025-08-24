// DisplayName: EXE
// CoreName: Energetic Beam Exfiltration
// Description: Animated vertical beam attracts pixels with swirl, particles, glitch slices, and chromatic shear.
// Performance: -22
// Rating: 1
// Category: Distort
precision mediump float;

// EXE — Energetic beam / data exfiltration effect
// Goal: Animated vertical beam attracts pixels, adds chromatic shear, particles & glitches.
// - Moving beam center w/ exponential attraction field
// - Swirl + noise driven displacement & chromatic shear
// - Vertical particle streak accumulation along beam
// - Intermittent horizontal glitch slices & scanline/stripe overlays
// - Grading pass for cohesive palette
// uStrength: 0..3 scales displacement, particles, stripes & glitch amplitude
varying vec2 vTex;
uniform sampler2D uTex;     // Source frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source size
uniform float uStrength;    // 0..3 strength

float hash(vec2 p){ p=fract(p*vec2(125.34, 417.13)); p+=dot(p,p+23.17); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }

vec3 grade(vec3 c, float strength){
  float L = luma(c);
  float sat = 0.45 + 0.15*sin(uTime*0.13);
  vec3 grey = vec3(L);
  c = mix(grey, c, sat);
  c *= mat3( 1.05, 0.05, 0.00,
             0.02, 1.08, 0.02,
             0.00, 0.04, 0.90);
  c = pow(c + 0.035, vec3(0.92));
  c = mix(c, vec3(L), 0.12*strength);
  return clamp(c,0.0,1.0);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);

  // --- Beam center & base field ---
  float beamSpeed = 0.07 + 0.05*sin(t*0.31);
  float beamX = fract(t*beamSpeed + 0.25 + 0.1*sin(t*0.17));
  float dBeam = abs(uv.x - beamX);
  float attract = exp(-pow(dBeam* uTexSize.x * (0.8 + 0.6*strength), 1.15));
  float ang = noise(vec2(uv.y*40.0 + t*1.2, uv.x*18.0 - t*0.8))*6.28318; // swirl angle
  vec2 swirl = vec2(cos(ang), sin(ang));

  float jitter = (hash(floor(uv*vec2(uTexSize.x, uTexSize.y)) + t*2.37) - 0.5);
  // Pull direction combines attraction + vertical sinus + swirl
  vec2 pullDir = normalize(vec2(beamX - uv.x, 0.0005 + 0.12*sin(t*0.9 + uv.y*6.0)) + swirl*0.2);
  float pullMag = attract * (0.55 + 0.45*sin(t*3.0 + uv.y*25.0)) * (0.25 + 0.75*strength);
  pullMag += jitter * 0.04 * strength;
  vec2 disp = pullDir * pullMag * texel * (4.0 + 10.0*strength);

  // Chromatic shear proportional to attraction & strength
  float shear = (0.20 + 0.35*strength) * attract;
  vec2 rOff = disp + vec2(+shear, 0.0)*texel;
  vec2 gOff = disp;
  vec2 bOff = disp + vec2(-shear, 0.0)*texel;

  vec3 col;
  col.r = texture2D(uTex, clamp(uv + rOff, 0.0, 1.0)).r;
  col.g = texture2D(uTex, clamp(uv + gOff, 0.0, 1.0)).g;
  col.b = texture2D(uTex, clamp(uv + bOff, 0.0, 1.0)).b;

  float L = luma(col);
  // --- Particle streak accumulation ---
  float partSeed = hash(vec2(floor(uv* uTexSize)) + t);
  float spawn = smoothstep(0.55, 0.9, L) * step(0.35, partSeed);
  vec3 partAccum = vec3(0.0);
  float wsum=0.0;
  for(int i=-3;i<=3;i++){
    float fi = float(i);
    float w = exp(-fi*fi*0.25);
    float drift = (noise(vec2(uv.x*60.0 + fi*0.7, t*1.5 + uv.y*25.0)) - 0.5);
    vec2 pUv = uv + disp + vec2(0.0, fi * texel.y * (0.9 + 0.4*strength)) + vec2(0.0, drift*0.8*texel.y);
    partAccum += texture2D(uTex, clamp(pUv,0.0,1.0)).rgb * w;
    wsum += w;
  }
  partAccum /= max(wsum, 0.0001);
  vec3 particles = mix(col, partAccum, 0.65) * spawn * (0.4 + 0.8*strength);
  col += particles;

  // --- Vertical stripe modulation ---
  float colId = uv.x * uTexSize.x;
  float vPhase = fract(colId*0.5);
  float stripe = smoothstep(0.15,0.0, min(vPhase, 1.0 - vPhase));
  float vStrength = mix(0.18, 0.42, clamp(strength*0.6,0.0,1.0));
  float vMask = 1.0 - vStrength * (1.0 - stripe);
  col *= vMask;

  // --- Horizontal scanline modulation ---
  float hPhase = fract(uv.y * uTexSize.y);
  float hScan = 0.85 + 0.15 * pow(sin(3.14159 * hPhase), 2.0);
  col *= (0.94 + 0.06*hScan);

  // --- Intermittent glitch slice ---
  float seg = floor(t*1.2);
  float segT = fract(t*1.2);
  float gChance = hash(vec2(seg, 91.2));
  float glitchEnv = step(0.83, gChance) * smoothstep(0.05,0.2,segT) * (1.0 - smoothstep(0.55,0.95,segT));
  if(glitchEnv > 0.0){
    vec2 gDisp = vec2(0.0);
    gDisp.x = (noise(vec2(uv.y*300.0 + t*90.0, seg*7.0)) - 0.5) * 4.0 * texel.x * strength;
    col = mix(col, texture2D(uTex, clamp(uv + gDisp,0.0,1.0)).rgb, 0.65);
    col += (hash(vec2(uv.y*400.0, t*120.0)) - 0.5) * 0.25 * glitchEnv;
  }

  // --- Grain & vignette ---
  float grain = noise(uv * vec2(360.0, 240.0) + t*2.7) - 0.5;
  col += grain * (0.04 + 0.08*strength);
  float r = length((uv - 0.5) * vec2(1.15,1.05));
  float vign = smoothstep(0.85, 0.25, r);
  col *= vign;

  col = grade(col, strength);
  col = clamp(col,0.0,1.0);
  gl_FragColor = vec4(col,1.0);
}

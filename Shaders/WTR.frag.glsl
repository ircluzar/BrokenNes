// DisplayName: WTR
// CoreName: Water Energy Distortion
// Description: Compound vector-field warping with vertical/horizontal beams, sine wobbles and radial lenses, plus chromatic shear.
// Performance: -50
// Rating: 1
// Category: Distort
precision mediump float;

// WTR — Multi-field vector displacement (water energy)
// Goal: Complex compound vector field warping with chromatic shear.
// - Vertical & horizontal beam fields (VCOUNT/HCOUNT lines)
// - Sine wiggle lines & moving radial lens fields
// - Displacement clamped then converted to chromatic shear offsets
// - Sparkle noise, pulsing brightness & grading
// uStrength: 0..3 scales field magnitude, shear & sparkle
varying vec2 vTex;
uniform sampler2D uTex;    // Source frame
uniform float uTime;       // Seconds
uniform vec2 uTexSize;     // Source size
uniform float uStrength;   // 0..3 strength
const int COUNT = 69;
const int VCOUNT = 34;
const int HCOUNT = 34;
float hash(vec2 p){ p=fract(p*vec2(131.17, 415.97)); p+=dot(p,p+19.31); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }
vec2 vBeamField(vec2 uv, float xPos, float t, float strength, float seed){
  float tilt = (hash(vec2(seed,37.2))*2.0 - 1.0) * 0.55 + sin(t*0.2 + seed*50.0)*0.08;
  vec2 dirLine = normalize(vec2(sin(tilt), cos(tilt)));
  vec2 anchor = vec2(xPos, 0.5);
  vec2 rel = uv - anchor;
  vec2 perp = vec2(-dirLine.y, dirLine.x);
  float d = abs(dot(perp, rel));
  float fall = exp(-pow(d * uTexSize.x * (0.8 + 0.6*strength), 1.15));
  float jitter = (noise(vec2(seed*17.0, uv.y*40.0 + t*3.0)) - 0.5);
  float ang = noise(vec2(uv.y*60.0 + seed*11.0, t*0.7 + seed*3.0))*6.28318;
  vec2 swirl = vec2(cos(ang), sin(ang));
  float proj = dot(rel, dirLine);
  vec2 nearest = anchor + dirLine * proj;
  vec2 toLine = nearest - uv;
  vec2 dir = normalize(toLine + swirl*0.25 + vec2(0.0, 0.001 + 0.05*sin(t*0.9 + uv.y*7.0 + seed)));
  float mag = fall * (0.15 + 0.85*strength) * (0.6 + 0.4*sin(t*2.2 + seed*10.0));
  mag += jitter * 0.06 * strength;
  return dir * mag;
}
vec2 hBeamField(vec2 uv, float yPos, float t, float strength, float seed){
  float tilt = (hash(vec2(seed,73.9))*2.0 - 1.0) * 0.55 + sin(t*0.22 + seed*47.0)*0.08;
  vec2 dirLine = normalize(vec2(cos(tilt), sin(tilt)));
  vec2 anchor = vec2(0.5, yPos);
  vec2 rel = uv - anchor;
  vec2 perp = vec2(-dirLine.y, dirLine.x);
  float d = abs(dot(perp, rel));
  float fall = exp(-pow(d * uTexSize.y * (0.8 + 0.6*strength), 1.15));
  float jitter = (noise(vec2(seed*23.0, uv.x*40.0 + t*2.7)) - 0.5);
  float ang = noise(vec2(uv.x*55.0 + seed*9.0, t*0.65 + seed*4.0))*6.28318;
  vec2 swirl = vec2(cos(ang), sin(ang));
  float proj = dot(rel, dirLine);
  vec2 nearest = anchor + dirLine * proj;
  vec2 toLine = nearest - uv;
  vec2 dir = normalize(toLine + swirl*0.25 + vec2(0.001 + 0.05*sin(t*0.85 + uv.x*6.5 + seed), 0.0));
  float mag = fall * (0.15 + 0.85*strength) * (0.6 + 0.4*sin(t*2.0 + seed*12.0));
  mag += jitter * 0.06 * strength;
  return dir * mag;
}
vec2 wiggleField(vec2 uv, float baseY, float t, float strength, float seed){
  float phase = t* (0.4 + 0.3*fract(seed));
  float curve = sin(uv.x* (6.0 + 4.0*fract(seed*13.0)) + phase + seed*20.0) * 0.03 * (0.6 + 0.4*strength);
  float yLine = baseY + curve;
  float d = abs(uv.y - yLine);
  float fall = exp(-pow(d * uTexSize.y * (1.0 + 0.5*strength), 1.1));
  float shift = sin(uv.y*50.0 + phase*6.0 + seed*30.0) * 0.5 + 0.5;
  vec2 dir = normalize(vec2((shift-0.5)*0.3, yLine - uv.y));
  float mag = fall * (0.1 + 0.9*strength) * (0.5 + 0.5*sin(t*3.0 + seed*40.0));
  return dir * mag;
}
vec2 lensField(vec2 uv, vec2 c, float t, float strength, float seed){
  vec2 toC = uv - c;
  float r = length(toC);
  float radius = 0.08 + 0.12*fract(seed*7.0);
  float edge = r / radius;
  if(edge > 1.8) return vec2(0.0);
  float core = exp(-edge*edge * (1.5 + 0.8*strength));
  float mode = (fract(seed*5.0) > 0.5) ? 1.0 : -1.0;
  float swirlA = noise(vec2(seed*100.0 + r*120.0, t*1.3))*6.28318;
  vec2 swirl = vec2(cos(swirlA), sin(swirlA));
  vec2 dir = normalize(toC + swirl * 0.3);
  float mag = mode * core * (0.2 + 0.8*strength) * (0.6 + 0.4*sin(t*1.7 + seed*60.0));
  return dir * mag;
}
vec3 grade(vec3 c, float strength){
  float L = luma(c);
  c = mix(vec3(L), c, 0.55 - 0.25*clamp(strength*0.4,0.0,1.0));
  c *= vec3(0.95,1.03,0.92);
  c = pow(c + 0.02, vec3(0.95));
  return clamp(c,0.0,1.0);
}
void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);
  vec2 disp = vec2(0.0);
  for(int i=0;i<VCOUNT;i++){
    float fi = float(i);
    float seed = fi/float(VCOUNT);
    float speed = 0.03 + 0.07*fract(hash(vec2(seed, 12.3)));
    float xPos = fract(seed + t*speed + 0.15*sin(t*0.11 + seed*10.0));
    disp += vBeamField(uv, xPos, t, strength, seed);
  }
  for(int i=0;i<HCOUNT;i++){
    float fi = float(i);
    float seed = fi/float(HCOUNT);
    float speed = 0.025 + 0.065*fract(hash(vec2(seed, 41.7)));
    float yPos = fract(seed*0.73 + t*speed + 0.12*sin(t*0.13 + seed*14.0));
    disp += hBeamField(uv, yPos, t, strength, seed);
  }
  for(int i=0;i<COUNT;i++){
    float fi = float(i);
    float seed = fi/float(COUNT);
    float baseY = fract(seed + sin(t*0.07 + seed*9.0)*0.05 + t* (0.01 + 0.02*fract(seed*17.0)));
    disp += wiggleField(uv, baseY, t, strength, seed);
  }
  for(int i=0;i<COUNT;i++){
    float fi = float(i);
    float seed = fi/float(COUNT);
    float ang = seed*6.28318 + t*(0.05 + 0.1*fract(seed*29.0));
    float rad = 0.18 + 0.32*fract(seed*37.0);
    vec2 center = vec2(0.5) + vec2(cos(ang), sin(ang))*rad;
    center += vec2(noise(vec2(seed*81.0, t*0.5)) - 0.5, noise(vec2(seed*91.0, t*0.5 + 10.0)) - 0.5) * 0.08;
    disp += lensField(uv, center, t, strength, seed);
  }
  float maxLen = 5.0 + 30.0*strength;
  float len = length(disp);
  if(len > 1e-5){
    float clampLen = min(len, maxLen);
    disp = disp / len * clampLen;
  }
  vec2 texel = 1.0 / uTexSize;
  vec2 dUV = disp * texel * (1.5 + 3.5*strength);
  vec2 dir = (length(dUV) > 0.0) ? normalize(dUV) : vec2(1.0,0.0);
  float shear = (0.4 + 0.8*strength) * length(dUV);
  vec2 rOff = dUV + dir * shear * 0.40;
  vec2 gOff = dUV;
  vec2 bOff = dUV - dir * shear * 0.40;
  vec3 col;
  col.r = texture2D(uTex, clamp(uv + rOff,0.0,1.0)).r;
  col.g = texture2D(uTex, clamp(uv + gOff,0.0,1.0)).g;
  col.b = texture2D(uTex, clamp(uv + bOff,0.0,1.0)).b;
  float sparkle = noise(uv*vec2(300.0,260.0) + t*3.0) - 0.5;
  col += sparkle * 0.05 * strength;
  float pulse = 0.5 + 0.5*sin(t*2.0);
  col *= 0.9 + 0.1*pulse;
  col = grade(col, strength);
  col = clamp(col,0.0,1.0);
  gl_FragColor = vec4(col,1.0);
}

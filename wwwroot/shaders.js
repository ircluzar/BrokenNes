// Legacy shaders.js removed. This is a tiny no-op stub to avoid 404s for cached clients.
(function(){ /* no-op */ })();
// Registers shaders with nesInterop when both this script and nesInterop.js are loaded.
(function(){
  function ensure(){ return window.nesInterop && typeof window.nesInterop.registerShader === 'function'; }
  function registerAll(){
    if(!ensure()) return; // nesInterop not ready yet
    // Avoid duplicate registration
    if(window.nesInterop._shaderRegistry && Object.keys(window.nesInterop._shaderRegistry).length>0) return;
    // RF composite shader
    window.nesInterop.registerShader('RF', 'RF', `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;uniform float uTime;uniform vec2 uTexSize;uniform float uStrength;float hash(vec2 p){p=fract(p*vec2(123.34,456.21));p+=dot(p,p+45.32);return fract(p.x*p.y);}float randLine(float y){return hash(vec2(floor(y),0.0));}void main(){vec2 uv = vec2(vTex.x, 1.0 - vTex.y);float s = uStrength;float px = 1.0 / uTexSize.x;float chroma = (0.7 + 1.1 * (s - 1.0)) * px;float blurAmt = mix(0.42, 0.78, clamp(s - 1.0, 0.0, 1.0));float r = texture2D(uTex, uv + vec2(chroma,0.0)).r;float g = texture2D(uTex, uv).g;float b = texture2D(uTex, uv - vec2(chroma,0.0)).b;vec3 base = vec3(r,g,b);vec3 c1 = texture2D(uTex, uv + vec2(-2.0*px,0.0)).rgb;vec3 c2 = texture2D(uTex, uv + vec2(-px,0.0)).rgb;vec3 c3 = texture2D(uTex, uv + vec2(px,0.0)).rgb;vec3 c4 = texture2D(uTex, uv + vec2(2.0*px,0.0)).rgb;vec3 blur = (c1*0.05 + c2*0.20 + base*0.50 + c3*0.20 + c4*0.05);vec3 col = mix(base, blur, blurAmt);float jitter = (hash(vec2(floor(uTime*90.0))) - 0.5) * (1.0 / uTexSize.y) * (0.6 + 0.6 * s);col = mix(col, texture2D(uTex, uv + vec2(0.0, jitter)).rgb, 0.18);float line = uv.y * uTexSize.y;float seg = floor(uTime / 4.0);float tt = fract(uTime / 4.0);float r1h = hash(vec2(seg,17.0));float r2h = hash(vec2(seg+1.0,17.0));float a1 = pow(r1h, 2.2);float a2 = pow(r2h, 2.2);float rippleScale = mix(a1, a2, smoothstep(0.0,1.0,tt));float rippleAmp = 0.4 * s * rippleScale;float ripple = sin(line * 0.18 + uTime * 9.5) * px * rippleAmp;col = mix(col, texture2D(uTex, uv + vec2(ripple,0.0)).rgb, 0.22);float shimmer = (randLine(line + floor(uTime*85.0)) - 0.5) * px * 1.5 * s;col = mix(col, texture2D(uTex, uv + vec2(shimmer,0.0)).rgb, 0.35);float n = hash(floor(uv * uTexSize) + uTime * 2.2);col += (n - 0.5) * 0.018 * (0.7 + 1.6 * (s - 1.0));float l = dot(col, vec3(0.299, 0.587, 0.114));col = mix(vec3(l), col, 0.95);col = (col - 0.5) * 1.10 + 0.5;col = clamp(col, 0.0, 1.0);gl_FragColor = vec4(col, 1.0);}`);
    // Pixel passthrough
    window.nesInterop.registerShader('PX', 'PX', `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;void main(){vec2 uv=vec2(vTex.x,1.0-vTex.y);gl_FragColor=texture2D(uTex,uv);}`);
    // LSD shader (slower graceful version)
    window.nesInterop.registerShader('LSD', 'LSD', `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;uniform float uTime;uniform vec2 uTexSize;float hash(vec2 p){p=fract(p*vec2(34.123,71.77));p+=dot(p,p+23.19);return fract(p.x*p.y);}float noise(vec2 p){vec2 i=floor(p);vec2 f=fract(p);f=f*f*(3.0-2.0*f);float a=hash(i);float b=hash(i+vec2(1.0,0.0));float c=hash(i+vec2(0.0,1.0));float d=hash(i+vec2(1.0,1.0));return mix(mix(a,b,f.x),mix(c,d,f.x),f.y);}void main(){vec2 uv = vec2(vTex.x,1.0 - vTex.y);vec2 texel = 1.0 / uTexSize;float t=uTime;float drift = sin(t*0.20)*0.5+0.5;float flowPhase = t*0.35;vec2 warp1 = vec2(noise(uv*2.5 + flowPhase), noise(uv*2.5 - flowPhase));vec2 warp2 = vec2(noise(uv*5.0 - flowPhase*0.55), noise(uv*5.0 + flowPhase*0.42));vec2 warp = (warp1*0.6 + warp2*0.4 - 0.5) * 0.0095;float seg = floor(t*0.18);float segT = fract(t*0.18);float chance = hash(vec2(seg, 99.7));float event = step(0.82, chance);float envelope = smoothstep(0.07,0.28,segT)* (1.0 - smoothstep(0.55,0.92,segT));float spazz = event * envelope;float bigWave = sin(uv.y*(12.0 + sin(t*1.2)*6.0) + t*2.5) * 0.045 * spazz * (0.6 + 0.4*sin(t*0.8));float smallWave = sin(uv.y*8.0 + t*1.2)*0.003 + sin(uv.x*6.0 - t*0.9)*0.0025;float wave = bigWave + smallWave;float bleedScale = 0.45 + 0.55*drift;vec2 dir1 = normalize(vec2(0.6,0.8));vec2 dir2 = normalize(vec2(-0.7,0.4));vec2 dir3 = normalize(vec2(0.2,-0.9));vec2 center = vec2(0.5) + (vec2(sin(t*0.18), cos(t*0.14))*0.05);vec2 toC = uv - center;float r = length(toC);float rot = sin(t*0.25) * 0.4;float angle = atan(toC.y,toC.x) + rot * smoothstep(0.0,0.6,r) * (1.0 - smoothstep(0.6,0.9,r));vec2 swirlUv = center + vec2(cos(angle), sin(angle))*r;vec2 baseUv = swirlUv + warp + vec2(wave*0.25,0.0);vec2 offR = baseUv + dir1 * texel * (1.9*bleedScale) + dir2 * texel * (sin(t*1.3+uv.y*5.0)*0.9);vec2 offG = baseUv + dir2 * texel * (1.3*bleedScale) + dir3 * texel * (sin(t*1.1+uv.x*4.5)*0.8);vec2 offB = baseUv + dir3 * texel * (1.6*bleedScale) + dir1 * texel * (sin(t*1.6+uv.y*3.5)*0.75);if(spazz>0.0){float k = spazz;offR += vec2(sin(t*12.0 + uv.y*36.0), cos(t*10.0 + uv.x*30.0))*texel*11.0*k;offG += vec2(sin(t*11.0 + uv.y*28.0), sin(t*9.0 + uv.x*22.0))*texel*9.0*k;offB += vec2(cos(t*12.5 + uv.y*42.0), sin(t*14.0 + uv.x*40.0))*texel*13.0*k;}vec3 acc = vec3(0.0); float wsum = 0.0; for(int i=-3;i<=3;i++){float fi=float(i);float w=exp(-fi*fi*0.18);vec2 offs=vec2(fi*texel.x*1.15, fi*texel.y*0.55);acc.r+=texture2D(uTex, clamp(offR+offs,0.0,1.0)).r*w;acc.g+=texture2D(uTex, clamp(offG+offs*0.9,0.0,1.0)).g*w;acc.b+=texture2D(uTex, clamp(offB+offs*1.05,0.0,1.0)).b*w;wsum+=w;}vec3 col=acc/wsum;vec3 vdiff=vec3(0.0);for(int j=-2;j<=2;j++){float fj=float(j);float w=exp(-fj*fj*0.33);vec2 vOff=vec2(0.0, fj*texel.y*(0.9 + 0.5*drift));vdiff+=texture2D(uTex, clamp(baseUv+vOff,0.0,1.0)).rgb*w;}vdiff/=2.5066;col=mix(col, vdiff, 0.32 + 0.22*drift);float crA = sin(t*0.55)*0.5 + 0.5;mat3 rotM = mat3(0.65+0.35*cos(t*0.5), 0.16*sin(t*0.7), 0.20*sin(t*1.0),0.20*sin(t*0.8), 0.65+0.35*cos(t*0.45+2.0), 0.16*sin(t*0.3),0.16*sin(t*0.4), 0.20*sin(t*0.6+1.5), 0.65+0.35*cos(t*0.5+3.14));col=mix(col, clamp(rotM*col,0.0,1.2), 0.48 + 0.30*crA);float l=dot(col, vec3(0.299,0.587,0.114));col=mix(vec3(l), col, 0.92 + 0.05*sin(t*1.1));col=pow(col, vec3(0.92));col+=vec3(0.045*sin(t*2.4+uv.y*6.5),0.036*sin(t*2.0+uv.x*7.5),0.05*sin(t*2.2+uv.y*6.0))*0.35;vec2 finalUv=clamp(baseUv + vec2(wave*0.33,0.0),0.0,1.0);vec3 detail=texture2D(uTex, finalUv).rgb;col=mix(col, detail, 0.22);col=clamp(col,0.0,1.0);gl_FragColor=vec4(col,1.0);}`);
  // SPK shader (cascading sparkles / prismatic reflections)
  window.nesInterop.registerShader('SPK','SPK', `precision mediump float;varying vec2 vTex;uniform sampler2D uTex;uniform float uTime;uniform vec2 uTexSize;uniform float uStrength;float hash(vec2 p){p=fract(p*vec2(123.34,456.21));p+=dot(p,p+45.32);return fract(p.x*p.y);}float noise(vec2 p){vec2 i=floor(p);vec2 f=fract(p);f=f*f*(3.0-2.0*f);float a=hash(i);float b=hash(i+vec2(1,0));float c=hash(i+vec2(0,1));float d=hash(i+vec2(1,1));return mix(mix(a,b,f.x),mix(c,d,f.x),f.y);}float luma(vec3 c){return dot(c,vec3(0.299,0.587,0.114));}void main(){vec2 uv=vec2(vTex.x,1.0-vTex.y);vec2 texel=1.0/uTexSize;float t=uTime;float inten=clamp(uStrength,0.3,3.0);vec3 base=texture2D(uTex,uv).rgb;vec3 cL=texture2D(uTex,uv-vec2(texel.x,0.0)).rgb;vec3 cR=texture2D(uTex,uv+vec2(texel.x,0.0)).rgb;vec3 cU=texture2D(uTex,uv-vec2(0.0,texel.y)).rgb;vec3 cD=texture2D(uTex,uv+vec2(0.0,texel.y)).rgb;float dx=luma(cR)-luma(cL);float dy=luma(cD)-luma(cU);float edge=clamp(length(vec2(dx,dy))*2.2,0.0,1.0);float lum=luma(base);float burst=smoothstep(0.55,0.9,lum)*(0.5+0.5*edge);vec2 gdir=normalize(vec2(dx,dy)+1e-5);vec2 ortho=vec2(-gdir.y,gdir.x);float specAmp=(0.25+0.75*edge)*(0.4+0.6*inten);vec3 prism;{float shift=1.5*specAmp;vec2 offsR=uv+(gdir*shift+ortho*0.7*specAmp)*texel;vec2 offsG=uv-(ortho*shift*0.6)*texel;vec2 offsB=uv-(gdir*shift-ortho*0.4*specAmp)*texel;prism.r=texture2D(uTex,clamp(offsR,0.0,1.0)).r;prism.g=texture2D(uTex,clamp(offsG,0.0,1.0)).g;prism.b=texture2D(uTex,clamp(offsB,0.0,1.0)).b;}vec3 sparkAccum=vec3(0.0);float wsum=0.0;for(int layer=0;layer<3;layer++){float lf=float(layer);float scale=mix(120.0,480.0,lf/2.0);float speed=mix(0.25,0.9,lf/2.0);vec2 suv=uv*scale+vec2(noise(vec2(lf*13.7,t*0.11))*40.0,t*speed*scale);float jitter=(noise(vec2(uv.y*scale*0.37+lf*9.13,t*0.9))-0.5)*2.0;suv.x+=jitter*0.65;vec2 cell=floor(suv);vec2 f=fract(suv);float rnd=hash(cell+lf*17.31);float spawn=step(0.93-0.4*edge-0.35*lum,rnd);vec2 starP=f-0.5;float d=length(starP);float star=pow(clamp(1.0-d*2.2,0.0,1.0),3.0);float tw=0.5+0.5*sin(t*12.0+rnd*40.0+lf*3.0);float sparkle=spawn*star*tw;vec3 scol=vec3(0.6+0.4*sin(rnd*20.0+lf*0.7+t*3.0),0.6+0.4*sin(rnd*25.0+lf*1.1+t*2.6+2.1),0.6+0.4*sin(rnd*30.0+lf*1.7+t*2.9+4.2));float weight=mix(0.55,1.2,lf/2.0);sparkAccum+=scol*sparkle*weight;wsum+=weight;}sparkAccum/=max(wsum,0.001);float radial=length(uv-0.5);float depthFactor=smoothstep(0.9,0.15,radial);sparkAccum*=(0.6+0.8*depthFactor);float wave=sin(t*5.0+lum*15.0+radial*40.0)*0.5+0.5;float burstEnv=burst*(0.35+0.65*wave);vec3 burstCol=prism*(0.4+0.6*burstEnv);vec3 col=base;col=mix(col,prism,0.35+0.25*edge);col+=sparkAccum*(0.55+0.35*edge)*inten;col+=burstCol*0.35*inten;float shimmer=noise(uv*vec2(280.0,140.0)+vec2(t*1.8,t*2.1));col*=0.85+0.15*shimmer;col=pow(col,vec3(0.95));float lfin=luma(col);col=mix(vec3(lfin),col,0.82+0.12*edge);float baseLum=lum;float effLum=luma(col);float over=max(effLum-baseLum,0.0);float darkenFactor=1.0-0.35*clamp(over*1.5,0.0,1.0);col*=darkenFactor;col=clamp(col,0.0,1.0);vec3 finalCol=mix(col,base,0.420);gl_FragColor=vec4(finalCol,1.0);}`);

    window.nesInterop.registerShader('EXE','EXE', `precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;        // seconds
uniform vec2 uTexSize;      // NES source size
uniform float uStrength;    // effect intensity 0..3

float hash(vec2 p){ p=fract(p*vec2(125.34, 417.13)); p+=dot(p,p+23.17); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }

vec3 grade(vec3 c, float strength){
  // Analog horror: desaturate, push greens & yellows, slight crush but lifted blacks
  float L = luma(c);
  float sat = 0.45 + 0.15*sin(uTime*0.13); // subtle breathing
  vec3 grey = vec3(L);
  c = mix(grey, c, sat);
  // Tint matrix (green / amber bias)
  c *= mat3( 1.05, 0.05, 0.00,
             0.02, 1.08, 0.02,
             0.00, 0.04, 0.90);
  // Lift blacks a little, compress highs
  c = pow(c + 0.035, vec3(0.92));
  c = mix(c, vec3(L), 0.12*strength); // extra bleakness with strength
  return clamp(c,0.0,1.0);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);

  // Dynamic haunted beam path moves horizontally & subtly warps vertically
  float beamSpeed = 0.07 + 0.05*sin(t*0.31);
  float beamX = fract(t*beamSpeed + 0.25 + 0.1*sin(t*0.17));
  // Distance to beam line
  float dBeam = abs(uv.x - beamX);
  // Attraction profile (Gaussian-ish)
  float attract = exp(-pow(dBeam* uTexSize.x * (0.8 + 0.6*strength), 1.15));
  // Swirl angle field using noise
  float ang = noise(vec2(uv.y*40.0 + t*1.2, uv.x*18.0 - t*0.8))*6.28318;
  vec2 swirl = vec2(cos(ang), sin(ang));

  // Base displacement: pull toward beam, adding swirl & temporal jitter
  float jitter = (hash(floor(uv*vec2(uTexSize.x, uTexSize.y)) + t*2.37) - 0.5);
  vec2 pullDir = normalize(vec2(beamX - uv.x, 0.0005 + 0.12*sin(t*0.9 + uv.y*6.0)) + swirl*0.2);
  float pullMag = attract * (0.55 + 0.45*sin(t*3.0 + uv.y*25.0)) * (0.25 + 0.75*strength);
  pullMag += jitter * 0.04 * strength;
  vec2 disp = pullDir * pullMag * texel * (4.0 + 10.0*strength);

  // Chromatic shear (aberration) relative to beam distance
  float shear = (0.20 + 0.35*strength) * attract;
  vec2 rOff = disp + vec2(+shear, 0.0)*texel;
  vec2 gOff = disp;
  vec2 bOff = disp + vec2(-shear, 0.0)*texel;

  // Sample base with displaced coordinates (clamped inside frame)
  vec3 col;
  col.r = texture2D(uTex, clamp(uv + rOff, 0.0, 1.0)).r;
  col.g = texture2D(uTex, clamp(uv + gOff, 0.0, 1.0)).g;
  col.b = texture2D(uTex, clamp(uv + bOff, 0.0, 1.0)).b;

  // Particle-like extrusion: gather along vertical around displaced uv for bright source pixels
  float L = luma(col);
  float partSeed = hash(vec2(floor(uv* uTexSize)) + t);
  float spawn = smoothstep(0.55, 0.9, L) * step(0.35, partSeed);
  vec3 partAccum = vec3(0.0);
  float wsum=0.0;
  for(int i=-3;i<=3;i++){
    float fi = float(i);
    float w = exp(-fi*fi*0.25);
    // Drift upward / downward with noise forming wispy trails
    float drift = (noise(vec2(uv.x*60.0 + fi*0.7, t*1.5 + uv.y*25.0)) - 0.5);
    vec2 pUv = uv + disp + vec2(0.0, fi * texel.y * (0.9 + 0.4*strength)) + vec2(0.0, drift*0.8*texel.y);
    partAccum += texture2D(uTex, clamp(pUv,0.0,1.0)).rgb * w;
    wsum += w;
  }
  partAccum /= max(wsum, 0.0001);
  vec3 particles = mix(col, partAccum, 0.65) * spawn * (0.4 + 0.8*strength);
  col += particles;

  // Vertical scanlines (column attenuation)
  // Use pixel column index + subpixel for soft stripes
  float colId = uv.x * uTexSize.x; // NES pixel columns
  float vPhase = fract(colId*0.5); // half-resolution stripe pattern
  float stripe = smoothstep(0.15,0.0, min(vPhase, 1.0 - vPhase));
  float vStrength = mix(0.18, 0.42, clamp(strength*0.6,0.0,1.0));
  float vMask = 1.0 - vStrength * (1.0 - stripe);
  col *= vMask;

  // Add faint traditional horizontal residual beam shimmer (very subtle)
  float hPhase = fract(uv.y * uTexSize.y);
  float hScan = 0.85 + 0.15 * pow(sin(3.14159 * hPhase), 2.0);
  col *= (0.94 + 0.06*hScan);

  // Glitch bursts: periodic frame segments distort brightness
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

  // Film / sensor noise & mild vignette
  float grain = noise(uv * vec2(360.0, 240.0) + t*2.7) - 0.5;
  col += grain * (0.04 + 0.08*strength);
  float r = length((uv - 0.5) * vec2(1.15,1.05));
  float vign = smoothstep(0.85, 0.25, r);
  col *= vign;

  // Color grading
  col = grade(col, strength);

  // Clamp & output
  col = clamp(col,0.0,1.0);
  gl_FragColor = vec4(col,1.0);
}`);

    window.nesInterop.registerShader('WTR','WTR', `precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;
uniform vec2 uTexSize;
uniform float uStrength;

// Constants
const int COUNT = 69; // wiggles & lens clouds remain 69
const int VCOUNT = 34; // half (floor) of 69 vertical beams
const int HCOUNT = 34; // half (floor) of 69 horizontal beams

float hash(vec2 p){ p=fract(p*vec2(131.17, 415.97)); p+=dot(p,p+19.31); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }

// Vertical beam contribution (now tilted)
vec2 vBeamField(vec2 uv, float xPos, float t, float strength, float seed){
  // Random stable tilt around vertical (0 means vertical). Range approx +/- ~31 deg + small temporal wobble.
  float tilt = (hash(vec2(seed,37.2))*2.0 - 1.0) * 0.55 + sin(t*0.2 + seed*50.0)*0.08;
  // Vertical base direction (0,1) rotated by tilt around origin -> dir = (sin(tilt), cos(tilt))
  vec2 dirLine = normalize(vec2(sin(tilt), cos(tilt)));
  vec2 anchor = vec2(xPos, 0.5);
  // Distance from point to infinite tilted line
  vec2 rel = uv - anchor;
  vec2 perp = vec2(-dirLine.y, dirLine.x);
  float d = abs(dot(perp, rel));
  float fall = exp(-pow(d * uTexSize.x * (0.8 + 0.6*strength), 1.15));
  float jitter = (noise(vec2(seed*17.0, uv.y*40.0 + t*3.0)) - 0.5);
  float ang = noise(vec2(uv.y*60.0 + seed*11.0, t*0.7 + seed*3.0))*6.28318;
  vec2 swirl = vec2(cos(ang), sin(ang));
  // Vector toward nearest point on line
  float proj = dot(rel, dirLine);
  vec2 nearest = anchor + dirLine * proj;
  vec2 toLine = nearest - uv;
  vec2 dir = normalize(toLine + swirl*0.25 + vec2(0.0, 0.001 + 0.05*sin(t*0.9 + uv.y*7.0 + seed)));
  float mag = fall * (0.15 + 0.85*strength) * (0.6 + 0.4*sin(t*2.2 + seed*10.0));
  mag += jitter * 0.06 * strength;
  return dir * mag;
}

// Horizontal beam contribution (now tilted)
vec2 hBeamField(vec2 uv, float yPos, float t, float strength, float seed){
  // Random stable tilt around horizontal (0 means horizontal). Range approx +/- ~31 deg + wobble.
  float tilt = (hash(vec2(seed,73.9))*2.0 - 1.0) * 0.55 + sin(t*0.22 + seed*47.0)*0.08;
  // Horizontal base direction (1,0) rotated by tilt -> dir = (cos(tilt), sin(tilt))
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

// Wiggle line field (curvy dynamic serpentine lens)
vec2 wiggleField(vec2 uv, float baseY, float t, float strength, float seed){
  // Animated y center with horizontal sinusoidal modulation
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

// Lens cloud (radial) - can pull inward or push outward
vec2 lensField(vec2 uv, vec2 c, float t, float strength, float seed){
  vec2 toC = uv - c;
  float r = length(toC);
  float radius = 0.08 + 0.12*fract(seed*7.0);
  float edge = r / radius;
  if(edge > 1.8) return vec2(0.0);
  float core = exp(-edge*edge * (1.5 + 0.8*strength));
  // Inward or outward toggle
  float mode = (fract(seed*5.0) > 0.5) ? 1.0 : -1.0;
  float swirlA = noise(vec2(seed*100.0 + r*120.0, t*1.3))*6.28318;
  vec2 swirl = vec2(cos(swirlA), sin(swirlA));
  vec2 dir = normalize(toC + swirl * 0.3);
  float mag = mode * core * (0.2 + 0.8*strength) * (0.6 + 0.4*sin(t*1.7 + seed*60.0));
  return dir * mag;
}

vec3 grade(vec3 c, float strength){
  // Simple desat & cold-to-sickly shift
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

  // Vertical beams (tilted, half count)
  for(int i=0;i<VCOUNT;i++){
    float fi = float(i);
    float seed = fi/float(VCOUNT);
    float speed = 0.03 + 0.07*fract(hash(vec2(seed, 12.3)));
    float xPos = fract(seed + t*speed + 0.15*sin(t*0.11 + seed*10.0));
    disp += vBeamField(uv, xPos, t, strength, seed);
  }

  // Horizontal beams (tilted, half count)
  for(int i=0;i<HCOUNT;i++){
    float fi = float(i);
    float seed = fi/float(HCOUNT);
    float speed = 0.025 + 0.065*fract(hash(vec2(seed, 41.7)));
    float yPos = fract(seed*0.73 + t*speed + 0.12*sin(t*0.13 + seed*14.0));
    disp += hBeamField(uv, yPos, t, strength, seed);
  }

  // Wiggle lines
  for(int i=0;i<COUNT;i++){
    float fi = float(i);
    float seed = fi/float(COUNT);
    float baseY = fract(seed + sin(t*0.07 + seed*9.0)*0.05 + t* (0.01 + 0.02*fract(seed*17.0)));
    disp += wiggleField(uv, baseY, t, strength, seed);
  }

  // Lens clouds (position drift)
  for(int i=0;i<COUNT;i++){
    float fi = float(i);
    float seed = fi/float(COUNT);
    float ang = seed*6.28318 + t*(0.05 + 0.1*fract(seed*29.0));
    float rad = 0.18 + 0.32*fract(seed*37.0);
    vec2 center = vec2(0.5) + vec2(cos(ang), sin(ang))*rad;
    // Additional jitter using noise ring
    center += vec2(noise(vec2(seed*81.0, t*0.5)) - 0.5, noise(vec2(seed*91.0, t*0.5 + 10.0)) - 0.5) * 0.08;
    disp += lensField(uv, center, t, strength, seed);
  }

  // Normalize displacement to avoid runaway; scale by texel size so large fields still coherent
  float maxLen = 5.0 + 30.0*strength; // theoretical cap
  float len = length(disp);
  if(len > 1e-5){
    float clampLen = min(len, maxLen);
    disp = disp / len * clampLen;
  }
  vec2 texel = 1.0 / uTexSize;
  vec2 dUV = disp * texel * (1.5 + 3.5*strength);

  // Chromatic shear based on local displacement orientation
  vec2 dir = (length(dUV) > 0.0) ? normalize(dUV) : vec2(1.0,0.0);
  float shear = (0.4 + 0.8*strength) * length(dUV);
  vec2 rOff = dUV + dir * shear * 0.40;
  vec2 gOff = dUV;
  vec2 bOff = dUV - dir * shear * 0.40;

  // Base sample (single gather w/ aberration)
  vec3 col;
  col.r = texture2D(uTex, clamp(uv + rOff,0.0,1.0)).r;
  col.g = texture2D(uTex, clamp(uv + gOff,0.0,1.0)).g;
  col.b = texture2D(uTex, clamp(uv + bOff,0.0,1.0)).b;

  // Add beam sparkle noise
  float sparkle = noise(uv*vec2(300.0,260.0) + t*3.0) - 0.5;
  col += sparkle * 0.05 * strength;

  // Subtle pulsing darkness to emphasize chaos
  float pulse = 0.5 + 0.5*sin(t*2.0);
  col *= 0.9 + 0.1*pulse;

  // Grading
  col = grade(col, strength);
  col = clamp(col,0.0,1.0);
  gl_FragColor = vec4(col,1.0);
}`);

    window.nesInterop.registerShader('TV','TV', `precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex; // NES frame (already nearest / pixel perfect)
uniform float uTime;    // seconds
uniform vec2 uTexSize;  // source pixel dimensions
uniform float uStrength; // effect intensity (1.0 base)

// Hash & noise helpers
float hash(vec2 p){ p = fract(p*vec2(123.34, 415.21)); p += dot(p,p+19.19); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }

// Convert linear-ish to gamma space tweak
vec3 softGamma(vec3 c){ return pow(clamp(c,0.0,1.0), vec3(0.85)); }

// Barrel distortion for consumer CRT (late 90s fairly mild)
vec2 barrel(vec2 uv, float amt){
  // remap to -1..1
  vec2 cc = uv*2.0 - 1.0;
  // elliptical scale (consumer sets flatter vertical)
  float r2 = dot(cc, cc);
  // Horizontal stronger curvature (~1.25x)
  float kx = amt * 0.60; // horizontal coefficient
  float ky = amt * 0.40; // vertical coefficient
  cc.x *= 1.0 + kx * r2;
  cc.y *= 1.0 + ky * r2;
  // slight pin cushion adjust
  float r4 = r2*r2;
  cc *= 1.0 + 0.04*amt*r4;
  return (cc*0.5 + 0.5);
}

// Shadow mask pattern (slot/triad hybrid)
vec3 shadowMask(vec2 uv, float scanMix){
  // Derived from subpixel repeat of 3 (RGB) columns, add vertical slots.
  // Scale pattern relative to output resolution assumption (~4x NES)
  vec2 scale = vec2(3.0, 2.0);
  vec2 p = fract(uv * uTexSize / scale);
  float stripe = step(p.x, 1.0/3.0)*1.0 + step(1.0/3.0, p.x)*step(p.x,2.0/3.0)*2.0 + step(2.0/3.0, p.x)*3.0;
  vec3 triad = (stripe==1.0)?vec3(1.05,0.35,0.35):(stripe==2.0?vec3(0.35,1.05,0.35):vec3(0.35,0.35,1.05));
  // Vertical slot aperture: dark line every other row region
  float slot = smoothstep(0.15,0.0, abs(p.y-0.5));
  triad *= mix(0.55,1.0, slot);
  // Blend with neutral based on scanline weight to avoid over-coloring dark lines
  return mix(vec3(0.9), triad, 0.65 * scanMix);
}

void main(){
  float strength = clamp(uStrength, 0.0, 3.0);
  // Base uv (flip Y like others)
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);

  // Apply barrel distortion; amount increases with strength but stays modest
  float barrelAmt = mix(0.07, 0.18, clamp(strength-0.5, 0.0, 1.0));
  vec2 cuv = barrel(uv, barrelAmt);

  // Outside edges vignette & black clamp
  if(any(lessThan(cuv, vec2(0.0))) || any(greaterThan(cuv, vec2(1.0)))){
    discard; // emulate tube border cut-off
  }

  // Under-sample NES texture with slight subpixel offsets for convergence error
  vec2 texel = 1.0 / uTexSize;
  float conv = 0.25 * strength; // convergence error scale (in texels)
  vec2 offR = vec2(+conv*texel.x, 0.0);
  vec2 offB = vec2(-conv*0.7*texel.x, 0.0);
  float t = uTime;
  // Add low frequency temporal sway
  offR += vec2(sin(t*0.7)*0.25, cos(t*0.9)*0.15)*texel*strength;
  offB += vec2(cos(t*0.65)*0.20, sin(t*0.75)*0.18)*texel*strength;
  vec3 base;
  base.r = texture2D(uTex, clamp(cuv + offR,0.0,1.0)).r;
  base.g = texture2D(uTex, cuv).g;
  base.b = texture2D(uTex, clamp(cuv + offB,0.0,1.0)).b;

  // Beam scan simulation & phosphor persistence approximation
  // Assume 60Hz; beam y position cycles once per 1/60 sec
  float frameHz = 60.0;
  float beamY = fract(t * frameHz);
  // Convert uv y to beam domain
  float dy = cuv.y - beamY;
  // Wrap (treat difference on circular domain)
  if(dy < -0.5) dy += 1.0; else if(dy > 0.5) dy -= 1.0;
  float timeSinceBeam = dy; // in [ -0.5, 0.5 ] proportion of frame
  // Convert to positive forward time since last pass
  float ts = (timeSinceBeam < 0.0) ? (-timeSinceBeam) : (1.0 - timeSinceBeam);
  // Persistence curve: brighter near beam, decays exponentially
  float persistence = exp(-ts * mix(5.0, 2.4, clamp(strength/2.0,0.0,1.0)));
  // Beam flash envelope
  float beamCore = exp(-pow(abs(dy)*uTexSize.y, 1.1) * 0.02 * (1.0+strength));
  float beamGlow = exp(-pow(abs(dy)*uTexSize.y, 1.1) * 0.0025) * 0.6;
  float scanMix = clamp(beamCore*1.2 + beamGlow, 0.0, 1.0);

  // Refined scanlines: softer, less contrast & minimized temporal flicker.
  float linePhase = fract(cuv.y * uTexSize.y);
  float sraw = sin(3.14159265 * linePhase);
  // Smooth shaping: raise then apply smooth cubic to soften edges
  float shaped = sraw * sraw;             // widen central bright region
  shaped = shaped * (3.0 - 2.0 * shaped); // smoothstep-like
  // Base darkening amount (reduced)
  float scanStrength = mix(0.06, 0.14, clamp(strength*0.5,0.0,1.0));
  // Static scanline mask (no direct beam tie-in to avoid flicker): brighter center, gentle falloff
  float scanMask = 1.0 - scanStrength * (1.0 - shaped);
  // Subtle coupling to beam (very small to hint movement but avoid visible flicker)
  scanMask *= (0.995 + 0.005 * scanMix);

  // Shadow mask modulation
  vec3 mask = shadowMask(cuv, scanMix);
  vec3 col = base * mask * scanMask;

  // Phosphor persistence applied as a temporal blend with a faux previous color (blurred local history approximation)
  // Approximate 'previous' by a small spatial blur + desaturation
  vec3 blurAccum = vec3(0.0); float wsum=0.0;
  for(int x=-1; x<=1; x++){
    for(int y=-1; y<=1; y++){
      vec2 o = vec2(float(x), float(y))*texel;
      float w = (x==0 && y==0)?2.0:1.0;
      blurAccum += texture2D(uTex, clamp(cuv + o,0.0,1.0)).rgb * w;
      wsum += w;
    }
  }
  vec3 prevApprox = blurAccum / wsum;
  float lPrev = dot(prevApprox, vec3(0.299,0.587,0.114));
  prevApprox = mix(vec3(lPrev), prevApprox, 0.6);
  // Blend current with prev via persistence weight
  // Persistence blend softened to reduce visible brightness pulsing
  col = mix(prevApprox, col, 0.5 + 0.5*persistence);

  // Minor bloom / halo (sample a few offsets)
  vec3 halo = vec3(0.0);
  for(int i=0;i<4;i++){
    float a = float(i)/4.0 * 6.28318;
    vec2 offs = vec2(cos(a), sin(a)) * texel * 2.5;
    halo += texture2D(uTex, clamp(cuv + offs,0.0,1.0)).rgb;
  }
  halo /= 4.0;
  col += halo * 0.12 * strength;

  // Subtle global curvature vignette
  float r = length((cuv - 0.5) * vec2(1.1,1.25));
  float vign = smoothstep(0.85, 0.35, r);
  col *= vign;

  // Slight horizontal color bleed (phosphor spread)
  vec3 bleed = vec3(0.0);
  bleed.r = texture2D(uTex, clamp(cuv + vec2(texel.x*1.0,0.0),0.0,1.0)).r;
  bleed.g = texture2D(uTex, clamp(cuv + vec2(-texel.x*1.0,0.0),0.0,1.0)).g;
  bleed.b = texture2D(uTex, clamp(cuv + vec2(texel.x*0.5,0.0),0.0,1.0)).b;
  col = mix(col, bleed, 0.04 + 0.08*strength);

  // Mild temporal noise / dither to break banding
  float n = noise(vec2(gl_FragCoord.xy*0.25) + t*1.3);
  col += (n-0.5) * 0.02;

  // Tone & gamma
  col = softGamma(col);
  // Slight warm tint common in late 90s tubes
  col *= vec3(1.04, 1.02, 0.97);
  col *= 1.12; // Slightly brighter output
  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col,1.0);
}`);
    
    // LAG shader - frosted LCD with pixel lag, smearing, and ghosting
    window.nesInterop.registerShader('LCD','LCD', `precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;
uniform vec2 uTexSize;
uniform float uStrength;

// Frosted glass diffusion
float hash(vec2 p){ p=fract(p*vec2(127.1,311.7)); p+=dot(p,p+19.19); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);

  // Simulate LCD pixel response lag: blend with previous frame (approximate by spatial blur)
  float smear = mix(0.18, 0.48, strength); // how much to smear
  float ghost = mix(0.10, 0.32, strength); // how much to ghost
  float frost = mix(0.10, 0.32, strength); // how much to diffuse

  // Frosted glass: random offset blur
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

  // Smearing: horizontal/vertical trailing
  vec3 smearAccum = vec3(0.0); wsum=0.0;
  for(int j=-4;j<=4;j++){
    float fj = float(j);
    float w = exp(-fj*fj*0.18);
    vec2 offs = vec2(fj*texel.x*smear*2.5, 0.0);
    smearAccum += texture2D(uTex, clamp(uv - offs,0.0,1.0)).rgb * w;
    wsum += w;
  }
  smearAccum /= wsum;

  // Ghosting: faded afterimage offset by time
  float ghostPhase = sin(t*0.7+uv.y*8.0)*0.5+0.5;
  vec2 ghostOff = vec2(-ghost*ghostPhase*2.0*texel.x, ghost*ghostPhase*1.2*texel.y);
  vec3 ghostCol = texture2D(uTex, clamp(uv + ghostOff,0.0,1.0)).rgb;

  // Base color
  vec3 base = texture2D(uTex, uv).rgb;

  // Mix all effects
  vec3 col = mix(base, frostAccum, frost*0.7);
  col = mix(col, smearAccum, smear*0.7);
  col = mix(col, ghostCol, ghost*0.6);

  // Subtle vertical banding (LCD column crosstalk)
  float colBand = sin(uv.x*uTexSize.x*3.14159*0.5 + t*0.2);
  col *= 0.97 + 0.03*colBand;

  // Subtle noise for LCD pixel grain
  float grain = noise(uv*vec2(320.0,240.0)+t*1.3)-0.5;
  col += grain * 0.025 * (0.7+0.7*strength);

  // Mild desaturation for frosted look
  float L = luma(col);
  col = mix(vec3(L), col, 0.82 - 0.18*strength);

  // Clamp
  col = clamp(col,0.0,1.0);
  gl_FragColor = vec4(col,1.0);
}`);

    // TRI shader - pseudo 3D voxel extrusion / parallax lighting


  window.nesInterop.registerShader('TRI','TRI', `precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;      // seconds
uniform vec2 uTexSize;    // NES pixel dimensions
uniform float uStrength;  // 0..3

// Helpers
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }
float hash(vec2 p){ p=fract(p*vec2(137.13,317.77)); p+=dot(p,p+23.7); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);
  vec2 texel = 1.0 / uTexSize;

  // Base color & height (height derived from luma with slight color weighting to favor brighter warm tones)
  vec3 base = texture2D(uTex, uv).rgb;
  float hRaw = luma(base * vec3(1.05,1.0,0.95));
  float height = pow(hRaw, 0.85) * (0.55 + 1.45*strength); // 0..~2 range with strength

  // Neighbor heights for normal approximation (sobel-esque light slope)
  float hL = luma(texture2D(uTex, clamp(uv - vec2(texel.x,0.0),0.0,1.0)).rgb);
  float hR = luma(texture2D(uTex, clamp(uv + vec2(texel.x,0.0),0.0,1.0)).rgb);
  float hU = luma(texture2D(uTex, clamp(uv - vec2(0.0,texel.y),0.0,1.0)).rgb);
  float hD = luma(texture2D(uTex, clamp(uv + vec2(0.0,texel.y),0.0,1.0)).rgb);
  float scale = (0.9 + 1.2*strength);
  float dx = (hR - hL) * scale;
  float dy = (hD - hU) * scale;
  vec3 normal = normalize(vec3(-dx, -dy, 0.75 + 0.35*strength));

  // Camera / parallax direction slowly orbiting
  float orbit = t * (0.15 + 0.01*strength);
  vec2 camDir = normalize(vec2(sin(orbit), 0.3 + 0.7*cos(orbit*0.73)));
  // Light direction rotates independently for dynamic highlights
  vec3 lightDir = normalize(vec3(sin(t*0.6)*0.6, 0.55 + 0.25*sin(t*0.37+1.7), 1.2));
  float diff = clamp(dot(normal, lightDir), 0.0, 1.0);
  float rim = pow(1.0 - clamp(dot(normal, normalize(vec3(camDir,0.8))),0.0,1.0), 3.0);
  float ambient = 0.30 + 0.10*strength;

  // Parallax shift for voxel top face (shift opposite camDir so it appears extruded toward viewer)
  float extrude = (0.7 + 1.6*strength);
  vec2 parallax = -camDir * height * extrude * texel * (1.0 + 0.25*sin(t*0.9 + hRaw*6.0));
  // Slight vertical bobbing to accent depth
  parallax.y += sin(t*0.8 + uv.x*10.0)*texel.y * 0.15 * strength * height;

  // Sample top color at parallax-shifted coords (clamped within frame)
  vec3 topCol = texture2D(uTex, clamp(uv + parallax, 0.0, 1.0)).rgb;

  // Side visibility: if forward neighbor has lower projected height show a shaded side face
  float hForward = luma(texture2D(uTex, clamp(uv + camDir*texel,0.0,1.0)).rgb) * (0.55 + 1.45*strength);
  float sideVis = clamp((height - hForward) * 4.0, 0.0, 1.0);
  // Side shading darkens & tints slightly blue-purple to imply shadowed depth
  vec3 sideShade = base * (ambient*0.45 + diff*0.25) * vec3(0.85,0.90,1.05);

  // Top face lighting (brighter & includes rim accent)
  vec3 litTop = topCol * (ambient + diff*0.95) + rim*0.15*vec3(1.2,1.1,1.05);

  // Mix side & top by side visibility
  vec3 col = mix(litTop, sideShade, sideVis);

  // Grid lines to emphasize voxel separation
  vec2 cell = uv * uTexSize;
  vec2 g = fract(cell);
  float lineW = mix(0.11, 0.20, clamp(strength/3.0,0.0,1.0));
  float border = step(g.x, lineW) + step(g.y, lineW) + step(1.0-lineW, g.x) + step(1.0-lineW, g.y);
  border = clamp(border, 0.0, 1.0);
  col = mix(col, col*0.35, border * (0.55 + 0.35*strength));

  // Subtle ambient occlusion from height differences
  float nhAvg = (hL + hR + hU + hD)*0.25;
  float ao = clamp(1.0 - (height - nhAvg)*1.4, 0.3, 1.0);
  col *= ao;

  // Mild color grading & saturation boost for toy-like blocks
  float l = luma(col);
  float satBoost = 0.35 + 0.25*strength;
  col = mix(vec3(l), col, 1.0 + satBoost);
  col *= vec3(1.04,1.02,1.06);

  // Tiny noise to break banding on flat faces
  float gn = noise(uv * uTexSize * 0.75 + t*1.7) - 0.5;
  col += gn * 0.03 * (0.5 + 0.5*strength);

  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col,1.0);
}`);

    // MSH shader - fake MPEG datamosh mosaic using previous frame sampling
    window.nesInterop.registerShader('MSH','MOSH', `precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;       // current frame
uniform sampler2D uPrevTex;   // previous frame (bound only for MSH)
uniform float uTime;
uniform vec2 uTexSize;
uniform float uStrength;      // 0..3

float hash(vec2 p){ p=fract(p*vec2(123.34,456.21)); p+=dot(p,p+45.32); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }

void main(){
  // Keep Y orientation consistent with the rest of the engine
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  // Previous-frame texture was rendered using a Y-flipping passthrough, so sample it with inverted Y
  vec2 prevUv = vec2(uv.x, 1.0 - uv.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float s = clamp(uStrength, 0.0, 3.0);

  // Base color from current frame
  vec3 cur = texture2D(uTex, uv).rgb;
  float Lc = luma(cur);

  // Coarse gradient/edge for targeting where to "mosh"
  float Lx1 = luma(texture2D(uTex, clamp(uv - vec2(texel.x,0.0),0.0,1.0)).rgb);
  float Lx2 = luma(texture2D(uTex, clamp(uv + vec2(texel.x,0.0),0.0,1.0)).rgb);
  float Ly1 = luma(texture2D(uTex, clamp(uv - vec2(0.0,texel.y),0.0,1.0)).rgb);
  float Ly2 = luma(texture2D(uTex, clamp(uv + vec2(0.0,texel.y),0.0,1.0)).rgb);
  float edge = clamp(length(vec2(Lx2 - Lx1, Ly2 - Ly1)) * 2.2, 0.0, 1.0);

  // Time-segmented glitch envelope (short bursts)
  float seg = floor(t * 0.9);
  float segT = fract(t * 0.9);
  float chance = hash(vec2(seg, 77.31));
  float env = step(0.58, chance) * smoothstep(0.05, 0.22, segT) * (1.0 - smoothstep(0.55, 0.95, segT));
  float glitch = env * clamp(s/3.0, 0.0, 1.0);

  // Macroblock grid selection (simulate MPEG 8x8/16x16 style)
  float baseBlock = mix(12.0, 6.0, clamp(s*0.5, 0.0, 1.0)); // higher s -> smaller blocks
  // Extremely slow, tiny global scale wobble; centered scaling
  float wob = 1.0 + 0.02 * sin(t * 0.01);
  float bSize = baseBlock * wob;
  vec2 grid = uTexSize / bSize;         // blocks per axis
  // Center-anchored grid so any scaling expands/contracts from the image center
  vec2 uvc = uv - 0.5;
  vec2 scaled = uvc * grid;
  vec2 bCoord = floor(scaled);          // integer macroblock id (centered)
  vec2 bCenter = (bCoord + 0.5) / grid + 0.5; // macroblock center in uv (center-anchored)
  float bSeed = hash(bCoord + vec2(seg, 19.7));

  // Previous frame at current location (for motion/mismatch estimate)
  vec3 prevHere = texture2D(uPrevTex, prevUv).rgb;
  float Lp = luma(prevHere);
  float motion = clamp(abs(Lc - Lp) * 4.0, 0.0, 1.0);

  // Tiny motion-vector search in prev frame around uv to find best match to current
  float span = 2.0; // search radius in texels
  float bestD = 1e9;
  vec2 bestOff = vec2(0.0);
  for(int j=-1;j<=1;j++){
    for(int i=-1;i<=1;i++){
      vec2 off = vec2(float(i), float(j)) * texel * span; // NES-space offset
      // Map NES-space offset to prev texture coords (invert Y)
      vec2 prevOff = vec2(off.x, -off.y);
      vec3 p = texture2D(uPrevTex, clamp(prevUv + prevOff, 0.0, 1.0)).rgb;
      float d = abs(luma(p) - Lc);
      if (d < bestD){ bestD = d; bestOff = off; }
    }
  }

  // Introduce sticky block drift driven by seed & time (imitates broken motion vectors)
  // Very subtle, slow per-block wiggle in a stable random direction (hardly perceptible)
  float ang = hash(bCoord + vec2(31.7, 12.1)) * 6.28318;
  vec2 dir = vec2(cos(ang), sin(ang));
  float phase = hash(bCoord + vec2(9.1, 4.7)) * 6.28318;
  float wig = sin(t * 0.01 + phase); // extremely slow wiggle
  // Amplitude stays well below 0.3 texel even at high strength
  float amp = (0.08 + 0.12 * clamp(s * 0.5, 0.0, 1.0));
  vec2 drift = dir * texel * amp * wig;

  // Mosaic sampling position: block center + estimated best motion + sticky drift + small jitter
  // Tiny static per-block jitter to avoid perfect lock; keep very small
  vec2 jitter = (vec2(hash(bCoord+5.1), hash(bCoord+9.3)) - 0.5) * texel * 0.25;
  vec2 moshuv = bCenter + bestOff + drift + jitter; // NES-space target

  // Color pulled from previous frame at mosaic position
  // Convert to prev texture space (invert Y component of delta)
  vec2 delta = moshuv - uv;
  vec3 moshCol = texture2D(uPrevTex, clamp(prevUv + vec2(delta.x, -delta.y), 0.0, 1.0)).rgb;

  // Pixelation of the current frame to enhance blockiness
  vec2 pixUv = (floor(scaled) + 0.5) / grid + 0.5;
  vec3 pixCol = texture2D(uTex, clamp(pixUv, 0.0, 1.0)).rgb;

  // Per-block gate: prefer blocks with low edge (flat areas) and random chance; stronger with glitch
  float gate = step(0.33, bSeed + glitch*0.75 - edge*0.25);
  // Extra freezing for low motion regions
  float freeze = smoothstep(0.25, 0.05, motion);

  // Blend weights
  float pixW = mix(0.10, 0.35, clamp(s,0.0,1.0));
  float moshW = gate * (0.35 + 0.65*glitch) * (0.35 + 0.65*s);
  moshW = clamp(moshW + freeze*0.35*s, 0.0, 1.0);

  // Subtle channel shear to mimic compression artifacts
  vec2 ch = vec2(1.0, -1.0) * texel * (0.5 + 1.5*s) * (0.2 + 0.8*glitch);
  vec3 shearPrev;
  shearPrev.r = texture2D(uPrevTex, clamp(prevUv + vec2(delta.x + ch.x, -(delta.y)), 0.0, 1.0)).r;
  shearPrev.g = moshCol.g;
  shearPrev.b = texture2D(uPrevTex, clamp(prevUv + vec2(delta.x + ch.y, -(delta.y)), 0.0, 1.0)).b;
  moshCol = mix(moshCol, shearPrev, 0.5);

  // Compose final color
  vec3 col = cur;
  col = mix(col, pixCol, pixW);
  col = mix(col, moshCol, moshW);

  // Mild block-edge ringing (emphasize macroblock borders)
  vec2 cell = fract(scaled);
  float border = min(min(cell.x, 1.0-cell.x), min(cell.y, 1.0-cell.y));
  float ring = smoothstep(0.0, 0.08, border);
  col *= 0.92 + 0.08*ring;

  // Tiny temporal noise to hide banding
  float gn = noise(uv * uTexSize * 0.75 + t*1.7) - 0.5;
  col += gn * 0.018 * (0.5 + 0.5*s);

  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col, 1.0);
}`);
  }
  // Try now; if nesInterop not yet loaded, retry after load event
  if(!registerAll()){
    window.addEventListener('load', registerAll, { once:true });
    // Fallback retry shortly (for cases where nesInterop loads after but before load fires)
    setTimeout(registerAll, 300);
  }
})();

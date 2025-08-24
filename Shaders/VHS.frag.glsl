// DisplayName: VHS
// CoreName: Broken VCR
// Description: Emulates timebase errors, chroma/luma separation, bleed, hum bands, speckle/dropouts, scanlines, and halation.
// Performance: -35
// Rating: 1
// Category: Retro
precision mediump float;

// VHS — Analog videotape degradation
// Goal: Emulate VHS timebase errors, chroma/luma separation & noise artifacts.
// - Line wobble, top flagging, bottom head-switch jitter
// - Chroma low-pass & phase wander + crawl on edges
// - Horizontal bleed, AC hum bands, speckle & dropout lines
// - Scanlines, halation, vignette & analog grain
// uStrength: 0..3 scales displacement, chroma blur, bleed & artifact intensity

// Analog VHS look:
// - Timebase wobble, flagging at top, head-switch noise at bottom
// - Chroma/luma separation with limited chroma bandwidth and phase wander
// - Color bleed, chroma crawl on edges, horizontal smear
// - Analog RF noise, white speckle dropouts, AC hum bands
// - Soft scanlines and halation around brights

varying vec2 vTex;
uniform sampler2D uTex;     // Source frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source pixel dimensions
uniform float uStrength;    // 0..3 strength

float hash(vec2 p){ p=fract(p*vec2(123.34,456.21)); p+=dot(p,p+45.32); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
float luma(vec3 c){ return dot(c, vec3(0.299, 0.587, 0.114)); }

// RGB <-> YUV helpers
vec3 rgb2yuv(vec3 c){
  float Y = dot(c, vec3(0.299, 0.587, 0.114));
  float U = (c.b - Y) * 0.565;
  float V = (c.r - Y) * 0.713;
  return vec3(Y,U,V);
}
vec3 yuv2rgb(vec3 yuv){
  float Y=yuv.x, U=yuv.y, V=yuv.z;
  float R = Y + 1.403*V;
  float G = Y - 0.344*U - 0.714*V;
  float B = Y + 1.770*U;
  return vec3(R,G,B);
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float s = clamp(uStrength, 0.0, 3.0);

  // Per-scanline domain
  float yLine = uv.y * uTexSize.y;

  // Timebase wobble: sinusoid + per-line jitter
  float wobble = sin(uv.y*16.0 + t*5.2) * (0.4 + 0.6*sin(t*0.9)) * texel.x * (1.2 + 2.6*s);
  float lineJit = (hash(vec2(floor(yLine), floor(t*80.0))) - 0.5) * texel.x * (0.8 + 2.5*s);

  // Top flagging (tape curl): stronger displacement near top edge
  float topMask = smoothstep(0.25, 0.0, uv.y);
  float flagSaw = (fract(uv.y*220.0 + t*28.0) - 0.5) * 2.0;
  float flag = (0.5*flagSaw + 0.5*sin(t*6.0 + uv.y*40.0)) * texel.x * (8.0 + 20.0*s) * topMask;

  // Head switching noise near bottom (line timing jumps)
  float bottomMask = smoothstep(0.80, 0.98, uv.y);
  float headJit = (noise(vec2(t*26.0, yLine*0.33)) - 0.5) * texel.x * (5.0 + 14.0*s) * bottomMask;

  // Compose total horizontal displacement per line
  float xDisp = wobble + lineJit + flag + headJit;

  // Minor vertical jitter (tracking instability)
  float yJit = (noise(vec2(uv.x*60.0 + t*5.0, t*3.3)) - 0.5) * texel.y * (0.4 + 2.2*s);
  vec2 baseUv = clamp(uv + vec2(0.0, yJit), 0.0, 1.0);

  // Sample base color for luma (sharper)
  vec3 baseCol = texture2D(uTex, clamp(baseUv + vec2(xDisp,0.0), 0.0, 1.0)).rgb;
  float Y = luma(baseCol);

  // Chroma sampling: low-pass horizontally + light vertical softness
  float chromaWidth = (1.5 + 3.5*s); // in texels
  float chromaVy = (0.5 + 0.8*s);
  vec2 cuv = baseUv + vec2(xDisp, 0.0);
  float U=0.0, V=0.0, wsum=0.0;
  for(int i=-3;i<=3;i++){
    float fi = float(i);
    float w = exp(-fi*fi / 6.0);
    vec2 offs = vec2(fi * texel.x * chromaWidth, fi==0.0 ? 0.0 : sign(fi)*texel.y*chromaVy*0.15);
    vec3 c = texture2D(uTex, clamp(cuv + offs, 0.0, 1.0)).rgb;
    vec3 yuv = rgb2yuv(c);
    U += yuv.y * w; V += yuv.z * w; wsum += w;
  }
  U /= max(wsum, 1e-4); V /= max(wsum, 1e-4);

  // Chroma phase wander and misregistration (horizontal offset only on chroma)
  float phaseNoise = (noise(vec2(yLine*0.07, t*0.7))*2.0 - 1.0);
  float phase = phaseNoise * (0.6 + 1.6*s) + sin(t*1.5 + yLine*0.03) * (0.3 + 0.6*s);
  float cp = cos(phase), sp = sin(phase);
  float U2 = U*cp - V*sp;
  float V2 = U*sp + V*cp;

  // Chroma crawl on edges: modulate chroma with high-freq along x where luma edges exist
  // Approximate luma edge strength with finite diff
  float Yl = luma(texture2D(uTex, clamp(cuv - vec2(texel.x,0.0), 0.0,1.0)).rgb);
  float Yr = luma(texture2D(uTex, clamp(cuv + vec2(texel.x,0.0), 0.0,1.0)).rgb);
  float edge = clamp(abs(Yr - Yl)*3.0, 0.0, 1.0);
  float crawl = sin((uv.x*uTexSize.x)*3.14159265 + t*9.0 + uv.y*5.0);
  U2 += crawl * edge * (0.03 + 0.08*s);
  V2 += sin((uv.x*uTexSize.x)*2.2 + t*7.0) * edge * (0.02 + 0.06*s);

  // Recombine Y + altered chroma
  vec3 yuv = vec3(Y, U2, V2);
  vec3 col = yuv2rgb(yuv);

  // Horizontal bleed / smear (analog filter)
  float bleed = mix(0.15, 0.65, clamp(s/3.0, 0.0, 1.0));
  vec3 smear = vec3(0.0);
  float wsumS = 0.0;
  for(int j=-2;j<=2;j++){
    float fj = float(j);
    float w = exp(-fj*fj*0.35);
    vec3 c = texture2D(uTex, clamp(baseUv + vec2(xDisp + fj*texel.x*(1.0+2.0*bleed), 0.0), 0.0, 1.0)).rgb;
    smear += c*w; wsumS += w;
  }
  smear /= max(wsumS, 1e-4);
  col = mix(col, smear, 0.35 + 0.35*bleed);

  // AC hum bands (slowly moving brightness ripples)
  float hum = 0.035 + 0.065*s;
  float humBands = 1.0 + (sin(uv.y*3.0 + t*6.28318*0.5) * 0.07 + sin(uv.y*7.0 - t*3.14*0.28) * 0.04) * hum;
  col *= humBands;

  // White speckle dropouts (analog RF dropouts), elongated along x
  float speckSeed = hash(vec2(floor(gl_FragCoord.y + t*60.0), floor(gl_FragCoord.x*0.5)));
  float speck = step(0.9985 - 0.25*s, speckSeed);
  if(speck > 0.0){
    float spark = 0.6 + 0.4*hash(gl_FragCoord.xy + vec2(t*200.0));
    col = mix(col, vec3(1.1)*spark, 0.65 + 0.30*s);
  }

  // Horizontal dropout lines (brief bright/black streaks)
  float lineSeed = hash(vec2(floor(yLine), floor(t*90.0)+17.0));
  float lineGate = step(0.985 - 0.35*s, lineSeed);
  if(lineGate > 0.0){
    float mode = hash(vec2(floor(yLine*0.5), 9.1));
    float w = smoothstep(0.0, 0.2, abs(fract(uv.x*5.0 + t*2.0) - 0.5));
    vec3 band = mix(vec3(0.0), vec3(1.2), step(0.5, mode));
    col = mix(col, band, (0.25 + 0.55*s) * (0.7 + 0.3*w));
  }

  // Head-switching noise band at bottom: noisy, desaturated, chroma phase chaos
  if(bottomMask > 0.0){
    float n = noise(vec2(uv.x*400.0 + t*120.0, uv.y*200.0 + t*30.0));
    vec3 bw = vec3(n*1.5);
    float sw = smoothstep(0.80, 0.90, uv.y) * (0.6 + 0.4*sin(t*20.0));
    col = mix(col, bw, sw * (0.35 + 0.45*s));
  }

  // Scanline attenuation (horizontal); keep subtle
  float phaseSL = fract(yLine);
  float scan = 0.84 + 0.16 * pow(sin(3.14159265 * phaseSL), 2.0);
  col *= (0.88 + 0.12*scan);

  // Halation around brights (single-pass cheap bloom)
  float Yt = clamp(Y*1.2, 0.0, 1.0);
  float haloAmt = smoothstep(0.65, 1.0, Yt) * (0.12 + 0.25*s);
  vec3 halo = vec3(0.0);
  float wsumH=0.0;
  for(int k=-2;k<=2;k++){
    for(int m=-1;m<=1;m++){
      vec2 o = vec2(float(k), float(m));
      float w = exp(-(o.x*o.x*0.25 + o.y*o.y*0.8));
      halo += texture2D(uTex, clamp(baseUv + vec2(xDisp,0.0) + o*texel*vec2(2.0,1.5), 0.0, 1.0)).rgb * w;
      wsumH += w;
    }
  }
  halo /= max(wsumH, 1e-4);
  col = mix(col, halo, haloAmt);

  // Add analog static/grain (band-limited)
  float grain = (noise(uv*vec2(280.0,240.0) + t*2.8) - 0.5);
  float grain2 = (noise(uv*vec2(90.0,70.0) - t*0.9) - 0.5);
  col += (grain*0.06 + grain2*0.03) * (0.7 + 0.9*s);

  // Slight vignette to frame
  float r = length((uv - 0.5) * vec2(1.06,1.0));
  float vign = smoothstep(0.95, 0.40, r);
  col *= vign;

  // Clamp & output
  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col, 1.0);
}

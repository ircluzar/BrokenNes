// DisplayName: MOSH
precision mediump float;
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
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 prevUv = vec2(uv.x, 1.0 - uv.y);
  vec2 texel = 1.0 / uTexSize;
  float t = uTime;
  float s = clamp(uStrength, 0.0, 3.0);
  vec3 cur = texture2D(uTex, uv).rgb;
  float Lc = luma(cur);
  float Lx1 = luma(texture2D(uTex, clamp(uv - vec2(texel.x,0.0),0.0,1.0)).rgb);
  float Lx2 = luma(texture2D(uTex, clamp(uv + vec2(texel.x,0.0),0.0,1.0)).rgb);
  float Ly1 = luma(texture2D(uTex, clamp(uv - vec2(0.0,texel.y),0.0,1.0)).rgb);
  float Ly2 = luma(texture2D(uTex, clamp(uv + vec2(0.0,texel.y),0.0,1.0)).rgb);
  float edge = clamp(length(vec2(Lx2 - Lx1, Ly2 - Ly1)) * 2.2, 0.0, 1.0);
  float seg = floor(t * 0.9);
  float segT = fract(t * 0.9);
  float chance = hash(vec2(seg, 77.31));
  float env = step(0.58, chance) * smoothstep(0.05, 0.22, segT) * (1.0 - smoothstep(0.55, 0.95, segT));
  float glitch = env * clamp(s/3.0, 0.0, 1.0);
  float baseBlock = mix(12.0, 6.0, clamp(s*0.5, 0.0, 1.0));
  float wob = 1.0 + 0.02 * sin(t * 0.01);
  float bSize = baseBlock * wob;
  vec2 grid = uTexSize / bSize;
  vec2 uvc = uv - 0.5;
  vec2 scaled = uvc * grid;
  vec2 bCoord = floor(scaled);
  vec2 bCenter = (bCoord + 0.5) / grid + 0.5;
  float bSeed = hash(bCoord + vec2(seg, 19.7));
  vec3 prevHere = texture2D(uPrevTex, prevUv).rgb;
  float Lp = luma(prevHere);
  float motion = clamp(abs(Lc - Lp) * 4.0, 0.0, 1.0);
  float span = 2.0;
  float bestD = 1e9;
  vec2 bestOff = vec2(0.0);
  for(int j=-1;j<=1;j++){
    for(int i=-1;i<=1;i++){
      vec2 off = vec2(float(i), float(j)) * texel * span;
      vec2 prevOff = vec2(off.x, -off.y);
      vec3 p = texture2D(uPrevTex, clamp(prevUv + prevOff, 0.0, 1.0)).rgb;
      float d = abs(luma(p) - Lc);
      if (d < bestD){ bestD = d; bestOff = off; }
    }
  }
  float ang = hash(bCoord + vec2(31.7, 12.1)) * 6.28318;
  vec2 dir = vec2(cos(ang), sin(ang));
  float phase = hash(bCoord + vec2(9.1, 4.7)) * 6.28318;
  float wig = sin(t * 0.01 + phase);
  float amp = (0.08 + 0.12 * clamp(s * 0.5, 0.0, 1.0));
  vec2 drift = dir * texel * amp * wig;
  vec2 jitter = (vec2(hash(bCoord+5.1), hash(bCoord+9.3)) - 0.5) * texel * 0.25;
  vec2 moshuv = bCenter + bestOff + drift + jitter;
  vec2 delta = moshuv - uv;
  vec3 moshCol = texture2D(uPrevTex, clamp(prevUv + vec2(delta.x, -delta.y), 0.0, 1.0)).rgb;
  vec2 pixUv = (floor(scaled) + 0.5) / grid + 0.5;
  vec3 pixCol = texture2D(uTex, clamp(pixUv, 0.0, 1.0)).rgb;
  float gate = step(0.33, bSeed + glitch*0.75 - edge*0.25);
  float freeze = smoothstep(0.25, 0.05, motion);
  float pixW = mix(0.10, 0.35, clamp(s,0.0,1.0));
  float moshW = gate * (0.35 + 0.65*glitch) * (0.35 + 0.65*s);
  moshW = clamp(moshW + freeze*0.35*s, 0.0, 1.0);
  vec2 ch = vec2(1.0, -1.0) * texel * (0.5 + 1.5*s) * (0.2 + 0.8*glitch);
  vec3 shearPrev;
  shearPrev.r = texture2D(uPrevTex, clamp(prevUv + vec2(delta.x + ch.x, -(delta.y)), 0.0, 1.0)).r;
  shearPrev.g = moshCol.g;
  shearPrev.b = texture2D(uPrevTex, clamp(prevUv + vec2(delta.x + ch.y, -(delta.y)), 0.0, 1.0)).b;
  moshCol = mix(moshCol, shearPrev, 0.5);
  vec3 col = cur;
  col = mix(col, pixCol, pixW);
  col = mix(col, moshCol, moshW);
  vec2 cell = fract(scaled);
  float border = min(min(cell.x, 1.0-cell.x), min(cell.y, 1.0-cell.y));
  float ring = smoothstep(0.0, 0.08, border);
  col *= 0.92 + 0.08*ring;
  float gn = noise(uv * uTexSize * 0.75 + t*1.7) - 0.5;
  col += gn * 0.018 * (0.5 + 0.5*s);
  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col, 1.0);
}

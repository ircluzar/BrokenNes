// DisplayName: TV
// CoreName: CRT Tube (Lightweight)
// Description: Barrel distortion, shadow mask triads, beam persistence, convergence wobble, halo, vignette, and mild bleed.
// Performance: 2
// Rating: 5
// Category: Retro
precision mediump float;

// TV — CRT tube simulation (lightweight)
// Goal: Barrel distortion, shadow mask triads, beam persistence & subtle bleed.
// - Barrel warp + chroma convergence wobble
// - Beam simulation (persistence + scanline envelope)
// - Shadow mask triads modulated by beam intensity
// - Halo, vignette, mild bleed & noise
// uStrength: 0..3 scales convergence, persistence shaping, bleed & halo
varying vec2 vTex;
uniform sampler2D uTex;     // Source frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source pixel dimensions
uniform float uStrength;    // 0..3 strength

float hash(vec2 p){ p = fract(p*vec2(123.34, 415.21)); p += dot(p,p+19.19); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
vec3 softGamma(vec3 c){ return pow(clamp(c,0.0,1.0), vec3(0.85)); }
vec2 barrel(vec2 uv, float amt){ vec2 cc = uv*2.0 - 1.0; float r2 = dot(cc, cc); float kx = amt * 0.60; float ky = amt * 0.40; cc.x *= 1.0 + kx * r2; cc.y *= 1.0 + ky * r2; float r4 = r2*r2; cc *= 1.0 + 0.04*amt*r4; return (cc*0.5 + 0.5); }
vec3 shadowMask(vec2 uv, float scanMix){ vec2 scale = vec2(3.0, 2.0); vec2 p = fract(uv * uTexSize / scale); float stripe = step(p.x, 1.0/3.0)*1.0 + step(1.0/3.0, p.x)*step(p.x,2.0/3.0)*2.0 + step(2.0/3.0, p.x)*3.0; vec3 triad = (stripe==1.0)?vec3(1.05,0.35,0.35):(stripe==2.0?vec3(0.35,1.05,0.35):vec3(0.35,0.35,1.05)); float slot = smoothstep(0.15,0.0, abs(p.y-0.5)); triad *= mix(0.55,1.0, slot); return mix(vec3(0.9), triad, 0.65 * scanMix); }

void main(){
    float strength = clamp(uStrength, 0.0, 3.0);
    vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
    float barrelAmt = mix(0.07, 0.18, clamp(strength-0.5, 0.0, 1.0));
    vec2 cuv = barrel(uv, barrelAmt);
    if(any(lessThan(cuv, vec2(0.0))) || any(greaterThan(cuv, vec2(1.0)))){ discard; }

    vec2 texel = 1.0 / uTexSize;
    float conv = 0.25 * strength;
    vec2 offR = vec2(+conv*texel.x, 0.0);
    vec2 offB = vec2(-conv*0.7*texel.x, 0.0);
    float t = uTime;
    offR += vec2(sin(t*0.7)*0.25, cos(t*0.9)*0.15)*texel*strength;
    offB += vec2(cos(t*0.65)*0.20, sin(t*0.75)*0.18)*texel*strength;

    vec3 base;
    base.r = texture2D(uTex, clamp(cuv + offR,0.0,1.0)).r;
    base.g = texture2D(uTex, cuv).g;
    base.b = texture2D(uTex, clamp(cuv + offB,0.0,1.0)).b;

    float frameHz = 60.0;
    float beamY = fract(t * frameHz);
    float dy = cuv.y - beamY;
    if(dy < -0.5) dy += 1.0; else if(dy > 0.5) dy -= 1.0;
    float timeSinceBeam = dy;
    float ts = (timeSinceBeam < 0.0) ? (-timeSinceBeam) : (1.0 - timeSinceBeam);
    float persistence = exp(-ts * mix(5.0, 2.4, clamp(strength/2.0,0.0,1.0)));
    float beamCore = exp(-pow(abs(dy)*uTexSize.y, 1.1) * 0.02 * (1.0+strength));
    float beamGlow = exp(-pow(abs(dy)*uTexSize.y, 1.1) * 0.0025) * 0.6;
    float scanMix = clamp(beamCore*1.2 + beamGlow, 0.0, 1.0);

    float linePhase = fract(cuv.y * uTexSize.y);
    float sraw = sin(3.14159265 * linePhase);
    float shaped = sraw * sraw;
    shaped = shaped * (3.0 - 2.0 * shaped);
    float scanStrength = mix(0.06, 0.14, clamp(strength*0.5,0.0,1.0));
    float scanMask = 1.0 - scanStrength * (1.0 - shaped);
    scanMask *= (0.995 + 0.005 * scanMix);

    vec3 mask = shadowMask(cuv, scanMix);
    vec3 col = base * mask * scanMask;

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
    col = mix(prevApprox, col, 0.5 + 0.5*persistence);

    vec3 halo = vec3(0.0);
    for(int i=0;i<4;i++){
        float a = float(i)/4.0 * 6.28318;
        vec2 offs = vec2(cos(a), sin(a)) * texel * 2.5;
        halo += texture2D(uTex, clamp(cuv + offs,0.0,1.0)).rgb;
    }
    halo /= 4.0;
    col += halo * 0.12 * strength;

    float r = length((cuv - 0.5) * vec2(1.1,1.25));
    float vign = smoothstep(0.85, 0.35, r);
    col *= vign;

    vec3 bleed = vec3(0.0);
    bleed.r = texture2D(uTex, clamp(cuv + vec2(texel.x*1.0,0.0),0.0,1.0)).r;
    bleed.g = texture2D(uTex, clamp(cuv + vec2(-texel.x*1.0,0.0),0.0,1.0)).g;
    bleed.b = texture2D(uTex, clamp(cuv + vec2(texel.x*0.5,0.0),0.0,1.0)).b;
    col = mix(col, bleed, 0.04 + 0.08*strength);

    float n = noise(vec2(gl_FragCoord.xy*0.25) + t*1.3);
    col += (n-0.5) * 0.02;
    col = softGamma(col);
    col *= vec3(1.04, 1.02, 0.97);
    col *= 1.12;
    col = clamp(col, 0.0, 1.0);
    gl_FragColor = vec4(col,1.0);
}

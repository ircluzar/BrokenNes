// DisplayName: TRI
precision mediump float;
varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;      // seconds
uniform vec2 uTexSize;    // NES pixel dimensions
uniform float uStrength;  // 0..3
float luma(vec3 c){ return dot(c, vec3(0.299,0.587,0.114)); }
float hash(vec2 p){ p=fract(p*vec2(137.13,317.77)); p+=dot(p,p+23.7); return fract(p.x*p.y); }
float noise(vec2 p){ vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f); float a=hash(i); float b=hash(i+vec2(1,0)); float c=hash(i+vec2(0,1)); float d=hash(i+vec2(1,1)); return mix(mix(a,b,f.x), mix(c,d,f.x), f.y); }
void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float t = uTime;
  float strength = clamp(uStrength, 0.0, 3.0);
  vec2 texel = 1.0 / uTexSize;
  vec3 base = texture2D(uTex, uv).rgb;
  float hRaw = luma(base * vec3(1.05,1.0,0.95));
  float height = pow(hRaw, 0.85) * (0.55 + 1.45*strength);
  float hL = luma(texture2D(uTex, clamp(uv - vec2(texel.x,0.0),0.0,1.0)).rgb);
  float hR = luma(texture2D(uTex, clamp(uv + vec2(texel.x,0.0),0.0,1.0)).rgb);
  float hU = luma(texture2D(uTex, clamp(uv - vec2(0.0,texel.y),0.0,1.0)).rgb);
  float hD = luma(texture2D(uTex, clamp(uv + vec2(0.0,texel.y),0.0,1.0)).rgb);
  float scale = (0.9 + 1.2*strength);
  float dx = (hR - hL) * scale;
  float dy = (hD - hU) * scale;
  vec3 normal = normalize(vec3(-dx, -dy, 0.75 + 0.35*strength));
  float orbit = t * (0.15 + 0.01*strength);
  vec2 camDir = normalize(vec2(sin(orbit), 0.3 + 0.7*cos(orbit*0.73)));
  vec3 lightDir = normalize(vec3(sin(t*0.6)*0.6, 0.55 + 0.25*sin(t*0.37+1.7), 1.2));
  float diff = clamp(dot(normal, lightDir), 0.0, 1.0);
  float rim = pow(1.0 - clamp(dot(normal, normalize(vec3(camDir,0.8))),0.0,1.0), 3.0);
  float ambient = 0.30 + 0.10*strength;
  float extrude = (0.7 + 1.6*strength);
  vec2 parallax = -camDir * height * extrude * texel * (1.0 + 0.25*sin(t*0.9 + hRaw*6.0));
  parallax.y += sin(t*0.8 + uv.x*10.0)*texel.y * 0.15 * strength * height;
  vec3 topCol = texture2D(uTex, clamp(uv + parallax, 0.0, 1.0)).rgb;
  float hForward = luma(texture2D(uTex, clamp(uv + camDir*texel,0.0,1.0)).rgb) * (0.55 + 1.45*strength);
  float sideVis = clamp((height - hForward) * 4.0, 0.0, 1.0);
  vec3 sideShade = base * (ambient*0.45 + diff*0.25) * vec3(0.85,0.90,1.05);
  vec3 litTop = topCol * (ambient + diff*0.95) + rim*0.15*vec3(1.2,1.1,1.05);
  vec3 col = mix(litTop, sideShade, sideVis);
  vec2 cell = uv * uTexSize;
  vec2 g = fract(cell);
  float lineW = mix(0.11, 0.20, clamp(strength/3.0,0.0,1.0));
  float border = step(g.x, lineW) + step(g.y, lineW) + step(1.0-lineW, g.x) + step(1.0-lineW, g.y);
  border = clamp(border, 0.0, 1.0);
  col = mix(col, col*0.35, border * (0.55 + 0.35*strength));
  float nhAvg = (hL + hR + hU + hD)*0.25;
  float ao = clamp(1.0 - (height - nhAvg)*1.4, 0.3, 1.0);
  col *= ao;
  float l = luma(col);
  float satBoost = 0.35 + 0.25*strength;
  col = mix(vec3(l), col, 1.0 + satBoost);
  col *= vec3(1.04,1.02,1.06);
  float gn = noise(uv * uTexSize * 0.75 + t*1.7) - 0.5;
  col += gn * 0.03 * (0.5 + 0.5*strength);
  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col,1.0);
}

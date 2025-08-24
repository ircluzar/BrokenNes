// DisplayName: RF
// CoreName: Analog RF Channel
// Description: Mild analog RF simulation with chroma misalignment, horizontal blur, ripple jitter and shimmer noise.
// Performance: -7
// Rating: 5
// Category: Retro
precision mediump float;

// RF — Analog RF channel simulation (mild)
// Goal: Horizontal chroma misalignment + soft blur & ripple noise shimmer.
// - Chroma offset & horizontal blur kernel
// - Temporal jitter & ripple modulation
// - Random shimmer lines & noise grain
// - Mild luma mix & contrast lift
// uStrength: 0..3 scales chroma spread, blur, ripple & shimmer noise
varying vec2 vTex;
uniform sampler2D uTex;    // Source frame
uniform float uTime;       // Seconds
uniform vec2 uTexSize;     // Source size
uniform float uStrength;   // 0..3 strength
float hash(vec2 p){p=fract(p*vec2(123.34,456.21));p+=dot(p,p+45.32);return fract(p.x*p.y);}float randLine(float y){return hash(vec2(floor(y),0.0));}
void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float s = uStrength;
  float px = 1.0 / uTexSize.x;
  float chroma = (0.7 + 1.1 * (s - 1.0)) * px;
  float blurAmt = mix(0.42, 0.78, clamp(s - 1.0, 0.0, 1.0));
  // --- Chroma split ---
  float r = texture2D(uTex, uv + vec2(chroma,0.0)).r;
  float g = texture2D(uTex, uv).g;
  float b = texture2D(uTex, uv - vec2(chroma,0.0)).b;
  vec3 base = vec3(r,g,b);
  vec3 c1 = texture2D(uTex, uv + vec2(-2.0*px,0.0)).rgb;
  vec3 c2 = texture2D(uTex, uv + vec2(-px,0.0)).rgb;
  vec3 c3 = texture2D(uTex, uv + vec2(px,0.0)).rgb;
  vec3 c4 = texture2D(uTex, uv + vec2(2.0*px,0.0)).rgb;
  // --- Horizontal blur ---
  vec3 blur = (c1*0.05 + c2*0.20 + base*0.50 + c3*0.20 + c4*0.05);
  vec3 col = mix(base, blur, blurAmt);
  // --- Vertical jitter ---
  float jitter = (hash(vec2(floor(uTime*90.0))) - 0.5) * (1.0 / uTexSize.y) * (0.6 + 0.6 * s);
  col = mix(col, texture2D(uTex, uv + vec2(0.0, jitter)).rgb, 0.18);
  float line = uv.y * uTexSize.y;
  float seg = floor(uTime / 4.0);
  float tt = fract(uTime / 4.0);
  float r1h = hash(vec2(seg,17.0));
  float r2h = hash(vec2(seg+1.0,17.0));
  float a1 = pow(r1h, 2.2);
  float a2 = pow(r2h, 2.2);
  float rippleScale = mix(a1, a2, smoothstep(0.0,1.0,tt));
  float rippleAmp = 0.4 * s * rippleScale;
  // --- Ripple displacement ---
  float ripple = sin(line * 0.18 + uTime * 9.5) * px * rippleAmp;
  col = mix(col, texture2D(uTex, uv + vec2(ripple,0.0)).rgb, 0.22);
  // --- Shimmer line jitter ---
  float shimmer = (randLine(line + floor(uTime*85.0)) - 0.5) * px * 1.5 * s;
  col = mix(col, texture2D(uTex, uv + vec2(shimmer,0.0)).rgb, 0.35);
  float n = hash(floor(uv * uTexSize) + uTime * 2.2);
  col += (n - 0.5) * 0.018 * (0.7 + 1.6 * (s - 1.0));
  float l = dot(col, vec3(0.299, 0.587, 0.114));
  col = mix(vec3(l), col, 0.95);
  col = (col - 0.5) * 1.10 + 0.5;
  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col, 1.0);
}

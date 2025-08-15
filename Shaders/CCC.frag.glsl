// DisplayName: CCC
// Category: Color
precision mediump float;

// Color Cycle Carousel (CCC)
// Continuously cycles hues via HSV, occasionally blending to an inverted palette.
// uStrength controls speed and depth; 0 => passthrough.

varying vec2 vTex;
uniform sampler2D uTex;
uniform float uTime;        // seconds
uniform vec2 uTexSize;      // source dimensions (unused but provided for consistency)
uniform float uStrength;    // 0..3

// RGB <-> HSV helpers (all components in 0..1)
vec3 rgb2hsv(vec3 c){
  float cMax = max(c.r, max(c.g, c.b));
  float cMin = min(c.r, min(c.g, c.b));
  float delta = cMax - cMin;
  float h = 0.0;
  if (delta > 1e-6) {
    if (cMax == c.r)      h = (c.g - c.b) / delta;
    else if (cMax == c.g) h = 2.0 + (c.b - c.r) / delta;
    else                  h = 4.0 + (c.r - c.g) / delta;
    h = fract(h / 6.0);
  }
  float s = cMax <= 0.0 ? 0.0 : (delta / cMax);
  float v = cMax;
  return vec3(h, s, v);
}

vec3 hsv2rgb(vec3 hsv){
  float h = hsv.x * 6.0; // 0..6
  float s = clamp(hsv.y, 0.0, 1.0);
  float v = clamp(hsv.z, 0.0, 1.0);
  float i = floor(h);
  float f = h - i;
  float p = v * (1.0 - s);
  float q = v * (1.0 - s * f);
  float t = v * (1.0 - s * (1.0 - f));
  vec3 col;
  if (i < 1.0)      col = vec3(v, t, p);
  else if (i < 2.0) col = vec3(q, v, p);
  else if (i < 3.0) col = vec3(p, v, t);
  else if (i < 4.0) col = vec3(p, q, v);
  else if (i < 5.0) col = vec3(t, p, v);
  else              col = vec3(v, p, q);
  return col;
}

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float s = clamp(uStrength, 0.0, 3.0);
  vec3 src = texture2D(uTex, uv).rgb;

  // Early passthrough for zero strength
  if (s <= 1e-5) { gl_FragColor = vec4(src, 1.0); return; }

  float k = s / 3.0; // normalize strength to 0..1

  // Base hue rotation speed (cycles per second). Scales with strength.
  // ~0.10 cps at k=0.2, up to ~0.45 cps at k=1.0
  float cps = mix(0.10, 0.45, k);
  float hueShift = fract(uTime * cps);

  // Slight extra hue wobble to avoid perfectly uniform sweep
  float wob = 0.03 * sin(uTime * 1.7) + 0.02 * sin(uTime * 0.9);

  // Normal path: rotate hue, push saturation/brightness a bit with strength
  vec3 hsvN = rgb2hsv(src);
  hsvN.x = fract(hsvN.x + hueShift + wob);
  hsvN.y = clamp(hsvN.y * (1.0 + 0.35*k), 0.0, 1.0);
  hsvN.z = clamp(hsvN.z * (0.95 + 0.25*k), 0.0, 1.0);
  vec3 colN = hsv2rgb(hsvN);

  // Invert path: invert source, then rotate hue with a slightly offset rate
  vec3 inv = 1.0 - src;
  vec3 hsvI = rgb2hsv(inv);
  hsvI.x = fract(hsvI.x + hueShift * (1.15 + 0.25*k) + 0.17);
  hsvI.y = clamp(hsvI.y * (0.80 + 0.50*k), 0.0, 1.0);
  hsvI.z = clamp(hsvI.z * (0.90 + 0.35*k), 0.0, 1.0);
  vec3 colI = hsv2rgb(hsvI);

  // Time envelope for blending toward inverted palette (slow breathe)
  // At k=1, blend peaks near 0.85; at lower strengths, stays subtle.
  float lfo = 0.5 + 0.5 * sin(uTime * 0.5);
  float invertMix = pow(lfo, 2.0) * (0.85 * k);

  // Combine and apply a gentle contrast curve for pop
  vec3 col = mix(colN, colI, invertMix);
  col = (col - 0.5) * (1.0 + 0.10*k) + 0.5;
  col = clamp(col, 0.0, 1.0);

  gl_FragColor = vec4(col, 1.0);
}

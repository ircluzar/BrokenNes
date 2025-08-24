// DisplayName: BUMP
// CoreName: Pseudo Bump
// Description: Derives a height field from color and shades with an animated light for a relief-lit look.
// Performance: -8
// Rating: 4
// Category: Lighting
precision mediump float;

// BUMP — Pseudo bump/relief lighting from native NES color data
// Goal: Derive a height field from brightness + hue ripple and shade with a moving light.
// - Convert RGB to HSV; build height from value plus sinusoidal hue modulation
// - Sample neighbor heights to compute gradients (central differences)
// - Form a perturbed normal and evaluate simple diffuse + specular shading
// - Animate light direction slowly using uTime; saturate specular by colorfulness
// uStrength: 0..3 (maps to bumpScale 1.2..7.0) — controls relief intensity

varying vec2 vTex;
uniform sampler2D uTex;    // Source frame
uniform vec2 uTexSize;     // (width,height) pixels
uniform float uTime;       // Seconds
uniform float uStrength;   // 0..3 strength (relief intensity)

// Convert RGB to HSV (h in [0,1], s in [0,1], v in [0,1])
vec3 rgb2hsv(vec3 c){
  float cMax = max(c.r, max(c.g, c.b));
  float cMin = min(c.r, min(c.g, c.b));
  float d = cMax - cMin;
  float h = 0.0;
  if (d > 1e-6) {
    if (cMax == c.r)      h = mod((c.g - c.b) / d, 6.0);
    else if (cMax == c.g) h = (c.b - c.r) / d + 2.0;
    else                  h = (c.r - c.g) / d + 4.0;
    h /= 6.0; if (h < 0.0) h += 1.0;
  }
  float s = cMax <= 1e-6 ? 0.0 : (d / cMax);
  return vec3(h, s, cMax);
}

// Height contribution from hue (cyclical) scaled by saturation
float hueBump(vec3 hsv){
  float h = hsv.x; // [0,1]
  float s = hsv.y;
  // sinusoidal ripple over hue wheel; center to ~[0,1]
  return (sin(h * 6.2831853) * 0.5 + 0.5) * s;
}

// Derive a height value from color using brightness (V) and hue ripple
float heightFromColor(vec3 rgb){
  vec3 hsv = rgb2hsv(rgb);
  // Brightness dominant, hue ripple adds fine structure. Tuned weights.
  return hsv.z * 0.82 + hueBump(hsv) * 0.18;
}

void main(){
  // Flip Y (NES buffer uploaded upside-down)
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);

  // Base color
  vec3 col = texture2D(uTex, uv).rgb;

  // Neighbor sampling step in UV space (1 pixel)
  vec2 texel = vec2(1.0 / uTexSize.x, 1.0 / uTexSize.y);

  // Sample neighbor heights
  float hC = heightFromColor(col);
  float hL = heightFromColor(texture2D(uTex, uv - vec2(texel.x, 0.0)).rgb);
  float hR = heightFromColor(texture2D(uTex, uv + vec2(texel.x, 0.0)).rgb);
  float hD = heightFromColor(texture2D(uTex, uv + vec2(0.0, texel.y)).rgb); // note flipped Y in uv
  float hU = heightFromColor(texture2D(uTex, uv - vec2(0.0, texel.y)).rgb);

  // Gradient (central differences)
  float dx = (hR - hL);
  float dy = (hU - hD);

  // --- Strength mapping to bump scale ---
  float s = uStrength; if (!(s > 0.0)) s = 1.0; // if uniform missing or zero, default 1
  float bumpScale = mix(1.2, 7.0, clamp(s, 0.2, 3.0) / 3.0);

  // Build perturbed normal; Z kept positive
  vec3 n = normalize(vec3(-dx * bumpScale, -dy * bumpScale, 1.0));

  // --- Animated light direction ---
  float a = uTime * 0.35;
  vec3 L = normalize(vec3(0.6 * cos(a) - 0.3, 0.6 * sin(a) + 0.5, 0.8));
  vec3 V = vec3(0.0, 0.0, 1.0); // view direction
  vec3 H = normalize(L + V);

  // --- Shading terms ---
  float diff = max(dot(n, L), 0.0);
  float spec = pow(max(dot(n, H), 0.0), 24.0);

  // Make specular depend a little on saturation so colorful areas pop more
  float sat = rgb2hsv(col).y;
  float ambient = 0.35;
  float kd = 0.9;
  float ks = mix(0.05, 0.25, sat);

  // --- Compose lit color ---
  vec3 lit = col * (ambient + kd * diff) + vec3(1.0) * (ks * spec);
  lit = clamp(lit, 0.0, 1.0);
  gl_FragColor = vec4(lit, 1.0);
}

// DisplayName: HUE
// CoreName: Hue Inversion + Slow Rotation
// Description: Invert hue with protection for near-gray and near-luma-extreme regions, then apply an ultra-slow rotation over time.
// Performance: -6
// Rating: 1
// Category: Color
precision mediump float;

// HUE — Slow hue inversion & rotation
// Goal: Invert hue but preserve near-gray & near-luma-extreme regions; then apply ultra-slow rotation.
// - Convert to HSL; build protection masks for low saturation & near-black/white
// - Invert hue (add 180°) then add period-based rotation (default 1 hour)
// - Mix only where mask permits; preserve saturation & lightness
// - Parameters allow tuning saturation & luminance protection thresholds
// uHuePeriod: seconds per 360° (<=0 uses 3600s) | uProtectSat 0..1 | uProtectLum 0..0.5

// HUE — invert hue but avoid affecting near-black/near-white (no touchout to B/W)
// Then apply an extremely slow hue rotation over time so the change is
// imperceptible short-term but noticeable across many minutes.
//
// Uniforms:
//  - uTex: sampler2D source texture
//  - uTime: seconds since start (used to slowly rotate hue)
//  - uHuePeriod: seconds for a full 360° rotation (default ~3600s if <=0)
//  - uProtectSat: saturation threshold to protect low-saturation pixels (0..1)
//  - uProtectLum: luminance edge threshold to protect near-black/white (0..0.5)

varying vec2 vTex;
uniform sampler2D uTex;      // Source frame
uniform float uTime;         // Seconds
uniform float uHuePeriod;    // Period in seconds (<=0 defaults)
uniform float uProtectSat;   // 0..1 saturation protect threshold
uniform float uProtectLum;   // 0..0.5 luminance protect threshold

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

// Convert RGB to HSL (all components in 0..1)
vec3 rgb2hsl(vec3 c){
  float maxc = max(max(c.r, c.g), c.b);
  float minc = min(min(c.r, c.g), c.b);
  float l = (maxc + minc) * 0.5;
  float h = 0.0;
  float s = 0.0;
  if(maxc != minc){
    float d = maxc - minc;
    s = d / (1.0 - abs(2.0*l - 1.0));
    if(maxc == c.r){
      h = (c.g - c.b) / d + (c.g < c.b ? 6.0 : 0.0);
    } else if(maxc == c.g){
      h = (c.b - c.r) / d + 2.0;
    } else {
      h = (c.r - c.g) / d + 4.0;
    }
    h /= 6.0;
  }
  return vec3(h, s, l);
}

// helper: convert hue to rgb channel (top-level; GLSL does not allow nested functions)
float hue2rgb(float p, float q, float t){
  if(t < 0.0) t += 1.0;
  if(t > 1.0) t -= 1.0;
  if(t < 1.0/6.0) return p + (q - p) * 6.0 * t;
  if(t < 1.0/2.0) return q;
  if(t < 2.0/3.0) return p + (q - p) * (2.0/3.0 - t) * 6.0;
  return p;
}

// Convert HSL back to RGB
vec3 hsl2rgb(vec3 hsl){
  float h = hsl.x;
  float s = hsl.y;
  float l = hsl.z;
  if(s == 0.0) return vec3(l);
  float q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
  float p = 2.0 * l - q;
  float r = hue2rgb(p, q, h + 1.0/3.0);
  float g = hue2rgb(p, q, h);
  float b = hue2rgb(p, q, h - 1.0/3.0);
  return vec3(r, g, b);
}

void main(){
  // match other shaders' UV orientation
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec3 c = texture2D(uTex, uv).rgb;

  // read HSL
  vec3 hsl = rgb2hsl(c);

  // determine protection thresholds with safe defaults
  float protectSat = (uProtectSat > 0.0) ? uProtectSat : 0.06; // protect near-grays
  float protectLum = (uProtectLum > 0.0) ? uProtectLum : 0.04; // protect near black/white

  // compute mask that is 0 for low-sat or near-black/white, 1 for fully color regions
  float satMask = smoothstep(protectSat * 0.5, protectSat * 1.5, hsl.y);
  float lumEdge = min(hsl.z, 1.0 - hsl.z); // distance from midline toward black/white
  float lumMask = smoothstep(protectLum * 0.5, protectLum * 2.0, lumEdge);
  float mask = satMask * lumMask;

  // base hue inversion (add 0.5 -> 180°)
  float invHue = fract(hsl.x + 0.5);

  // extremely slow extra rotation: full rotation takes uHuePeriod seconds
  float period = (uHuePeriod > 0.0) ? uHuePeriod : 3600.0; // default 1 hour
  // delta as fraction of hue-space (0..1). uTime/period wraps automatically with mod.
  float delta = fract(uTime / period);

  // final hue is inverted hue plus a tiny slow rotation
  float finalHue = fract(invHue + delta);

  // mix only where mask permits; preserve original saturation and lightness
  float outHue = mix(hsl.x, finalHue, mask);

  vec3 outHSL = vec3(outHue, hsl.y, hsl.z);
  vec3 outRGB = hsl2rgb(outHSL);

  gl_FragColor = vec4(clamp(outRGB, 0.0, 1.0), 1.0);
}

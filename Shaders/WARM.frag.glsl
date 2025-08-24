// DisplayName: WARM
// CoreName: Warmth & Phosphor Tilt
// Description: Subtle warm color shift with soft contrast tilt and gentle green cross-talk while preserving luminance.
// Performance: -3
// Rating: 3
// Category: Color
precision mediump float;

// WARM — Subtle warmth & phosphor tilt
// Goal: Gentle red bias & contrast softening while preserving luminance.
// - Convert to pseudo YUV; shift chroma toward warmth
// - Channel-specific gamma tilt + green cross-talk
// - Strength remapped to limited 0.15..0.50 warmth range
// uStrength: 0..3 (remapped)

// A subtle warm color filter reminiscent of slightly aged CRT phosphors.
// Goals:
// - Gentle shift toward warmer temperature (more red, less blue)
// - Preserve luminance and avoid crushing blacks or clipping highlights
// - Minimal haloing or blurring (pure color transform with soft tone curve)
//
// Uniforms (optional ones are tolerated by the runtime):
//   varying vec2 vTex;                // Provided by shared vertex shader
//   uniform sampler2D uTex;           // Source NES frame
//   uniform float uTime;              // Unused here, reserved
//   uniform vec2 uTexSize;            // Source size (256x240)
//   uniform float uStrength;          // 0..3 (mapped by host). We remap to a small 0..~0.5 range.

varying vec2 vTex;
uniform sampler2D uTex;    // Source frame
uniform float uTime;       // Unused (reserved)
uniform vec2 uTexSize;     // Source size
uniform float uStrength;   // 0..3 strength

const vec3 LUMA = vec3(0.299, 0.587, 0.114);

// BT.601 YUV helpers (full range, approximate)
float luma(vec3 c){ return dot(c, LUMA); }
vec2 toUV(vec3 c){
  float U = -0.169*c.r - 0.331*c.g + 0.500*c.b;
  float V =  0.500*c.r - 0.419*c.g - 0.081*c.b;
  return vec2(U,V);
}
vec3 fromYUV(float Y, vec2 UV){
  float U = UV.x; float V = UV.y;
  float R = Y + 1.402    * V;
  float G = Y - 0.344136 * U - 0.714136 * V;
  float B = Y + 1.772    * U;
  return vec3(R,G,B);
}

vec3 warmify(vec3 c, float amt){
  // Preserve luminance while nudging chroma toward warmth.
  float Y = luma(c);
  vec2 UV = toUV(c);
  // Reduce U (blue-difference), increase V (red-difference).
  // Scale with a soft curve so mid values get most warmth, extremes less.
  float mid = smoothstep(0.08, 0.92, Y);
  float k = amt * (0.7 + 0.3 * (1.0 - abs(mid - 0.5)*2.0));
  UV.x -= 0.06 * k;  // less blue
  UV.y += 0.06 * k;  // more red

  vec3 rgb = fromYUV(Y, UV);

  // Subtle shoulder/toe adjustment for a cozy feel (slightly softer contrast)
  // Apply a gentle per-channel gamma tilt favoring R, then clamp.
  vec3 g = vec3(1.0/(1.0 + 0.06*amt), 1.0/(1.0 + 0.04*amt), 1.0/(1.0 + 0.02*amt));
  rgb = pow(clamp(rgb, 0.0, 1.0), g);

  // Very light cross-talk to simulate phosphor warmth spill into greens
  rgb.g = mix(rgb.g, (rgb.g*0.92 + rgb.r*0.08), 0.25*amt);

  return clamp(rgb, 0.0, 1.0);
}

void main(){
  // Flip Y to match project convention
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec3 col = texture2D(uTex, clamp(uv, 0.0, 1.0)).rgb;

  // Map host strength (0..3) into a conservative warmth range [~0.15 .. ~0.5]
  float s = clamp(uStrength, 0.0, 3.0) / 3.0;
  float amt = mix(0.15, 0.50, s);

  vec3 outCol = warmify(col, amt);
  gl_FragColor = vec4(outCol, 1.0);
}

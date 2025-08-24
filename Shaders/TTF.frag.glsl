// DisplayName: TTF
// CoreName: TrueType Subpixel
// Description: Subpixel sampling for sharper vertical features using RGB offsets, edge gating, and small gathers.
// Performance: -4
// Rating: 3
// Category: Utility
precision mediump float;

// TTF — TrueType-style subpixel enhancement
// Goal: Subpixel sampling for sharper vertical features without heavy color fringing.
// - Per-channel offsets based on derivatives / texel fallback
// - Edge gating along X to avoid color bleed in flat areas
// - Small Gaussian gather per channel
// - Mild contrast pop
// uStrength: 0..3 scales blend toward subpixel result & contrast
// Default derivatives availability to off; JS will define HAS_DERIVATIVES=1 when supported
#ifndef HAS_DERIVATIVES
#define HAS_DERIVATIVES 0
#endif

// TrueType-style subpixel rendering for pixel art.
// Assumes an RGB horizontal stripe layout and offsets sampling per channel
// using screen-space derivatives to approximate per-pixel subpixel offsets.
//
// Goals
// - Add apparent horizontal detail (like thin vertical strokes/diagonals)
// - Reduce color fringing with a tiny 1D gather and edge gating
// - Keep pixel-art crisp away from strong vertical edges
//
// Inputs
// - vTex: varying texcoord (0..1)
// - uTex: NES frame (nearest filtering)
// - uTexSize: source pixel dimensions (256x240)
// - uStrength: 0..3 typical. Scales subpixel effect intensity

varying vec2 vTex;
uniform sampler2D uTex;
uniform vec2 uTexSize;   // 256x240
uniform float uStrength; // 0..3

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main(){
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  vec2 texel = 1.0 / uTexSize;
  float s = clamp(uStrength, 0.0, 3.0);

  // Derive horizontal uv step per screen pixel (fallback to one texel if derivatives unavailable)
#if HAS_DERIVATIVES
        float dudx = abs(dFdx(uv.x));
        float stepX = dudx > 1e-5 ? dudx : texel.x;
#else
        float stepX = texel.x;
#endif

  // Subpixel offsets for RGB stripe (R left, G center, B right)
  // Scale by strength and clamp so we don't hop over more than ~half a texel.
  float sub = clamp(stepX * (0.33 * (0.5 + 0.5 * min(s, 1.5))), 0.0, texel.x * 0.5 + 1e-6);
  vec2 offR = vec2(-sub, 0.0);
  vec2 offG = vec2( 0.0, 0.0);
  vec2 offB = vec2( sub, 0.0);

  // Edge detection along X to gate effect (only apply where vertical edges likely)
  vec3 cL = texture2D(uTex, clamp(uv - vec2(stepX, 0.0), 0.0, 1.0)).rgb;
  vec3 cC = texture2D(uTex, clamp(uv, 0.0, 1.0)).rgb;
  vec3 cR = texture2D(uTex, clamp(uv + vec2(stepX, 0.0), 0.0, 1.0)).rgb;
  float gx = luma(cR) - luma(cL);
  float edge = smoothstep(0.05, 0.35, abs(gx));

  // Slight 1D Gaussian gather per channel to suppress chroma speckle
  float w0 = 0.5;
  float w1 = 0.25;
  vec2 small = vec2(stepX * 0.5, 0.0);

  float R = w0 * texture2D(uTex, clamp(uv + offR, 0.0, 1.0)).r
          + w1 * texture2D(uTex, clamp(uv + offR - small, 0.0, 1.0)).r
          + w1 * texture2D(uTex, clamp(uv + offR + small, 0.0, 1.0)).r;

  float G = w0 * texture2D(uTex, clamp(uv + offG, 0.0, 1.0)).g
          + w1 * texture2D(uTex, clamp(uv + offG - small, 0.0, 1.0)).g
          + w1 * texture2D(uTex, clamp(uv + offG + small, 0.0, 1.0)).g;

  float B = w0 * texture2D(uTex, clamp(uv + offB, 0.0, 1.0)).b
          + w1 * texture2D(uTex, clamp(uv + offB - small, 0.0, 1.0)).b
          + w1 * texture2D(uTex, clamp(uv + offB + small, 0.0, 1.0)).b;

  vec3 subpix = vec3(R, G, B);
  vec3 orig = cC;

  // Blend toward subpixel result based on edge strength and global strength
  float k = clamp(edge * (0.55 + 0.45 * min(s, 1.0)), 0.0, 1.0);
  vec3 col = mix(orig, subpix, k);

  // Mild contrast pop for perceived sharpness
  col = (col - 0.5) * (1.03 + 0.06 * min(s, 1.0)) + 0.5;
  gl_FragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}

// DisplayName: MUSK
// CoreName: Mars Horizon
// Description: Adds a starry space background with red Martian tint and subtle atmospheric distortion.
// Performance: -10
// Rating: 4
// Category: Color
precision mediump float;

// MUSK — Mars Horizon
// - Twinkling stars overlay
// - Reddish tint for Martian atmosphere
// - Subtle distortion for atmospheric effect

varying vec2 vTex;
uniform sampler2D uTex;     // Source NES frame
uniform float uTime;        // Seconds
uniform vec2 uTexSize;      // Source pixel dimensions
uniform float uStrength;    // 0..3 strength

// Simple hash/noise for star placement
float noise(vec2 p) {
  return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void main() {
  vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
  float s = clamp(uStrength, 0.0, 3.0);

  // Base color from source
  vec3 col = texture2D(uTex, uv).rgb;

  // Subtle atmospheric distortion
  vec2 distort = vec2(
    sin(uv.y * 10.0 + uTime * 0.5) * 0.001 * s,
    cos(uv.x * 8.0 + uTime * 0.3) * 0.001 * s
  );
  vec2 distortedUV = clamp(uv + distort, 0.0, 1.0);
  col = texture2D(uTex, distortedUV).rgb;

  // Stars: grid-based pseudo-random points
  vec2 starUV = uv * 50.0;
  vec2 starPos = floor(starUV);

  float star = 0.0;
  for (int yi = -1; yi <= 1; yi++) {
    for (int xi = -1; xi <= 1; xi++) {
      vec2 cell = starPos + vec2(float(xi), float(yi));
      float n = noise(cell);
      vec2 starCenter = cell + vec2(0.5);
      float dist = distance(starUV, starCenter);
      float twinkle = 0.5 + 0.5 * sin(uTime * 2.0 + n * 100.0);
      float starContrib = (1.0 - smoothstep(0.0, 0.1, dist)) * twinkle * 0.3 * s;
      star += starContrib * step(0.98, n);
    }
  }

  // Martian tint
  vec3 tint = vec3(1.1, 0.9, 0.8);
  float tintAmount = clamp(0.2 + 0.3 * s, 0.0, 1.0);
  col = mix(col, col * tint, tintAmount);

  // Add star glow
  col += vec3(star);

  col = clamp(col, 0.0, 1.0);
  gl_FragColor = vec4(col, 1.0);
}


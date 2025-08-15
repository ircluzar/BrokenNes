// DisplayName: CRZ
// Category: Refraction
precision mediump float;

// CRZ — Crystalline glass refraction
// Goal: Sharp irregular glass facets refract & disperse pixels with edge glints.
// - Voronoi-like shard field with quantized normals (facets)
// - Edge & center driven displacement + per-cell jitter
// - Mild chromatic dispersion and inter-shard bleed
// - Sparkle/glint and vignette + faint micro-scratch noise
// uStrength: 0..3 scales cell density, displacement, glints & bleed

varying vec2 vTex;
uniform sampler2D uTex;    // Source NES frame
uniform float uTime;       // Seconds
uniform vec2 uTexSize;     // Source pixel dimensions
uniform float uStrength;   // 0..3 strength

// Simple hashing / random
float hash21(vec2 p){ p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 78.233); return fract(p.x * p.y); }
vec2 hash22(vec2 p){ return fract(sin(vec2(dot(p,vec2(127.1,311.7)), dot(p,vec2(269.5,183.3))))*43758.5453); }

// cell distance field (Voronoi-like) producing nearest distance and jittered site pos
vec2 cellInfo(vec2 p, float cells){
    // p in local grid space (p = uv * cells)
    vec2 ip = floor(p);
    vec2 fp = fract(p);
    float minD = 1e9;
    float secondD = 1e9;
    vec2 bestPt = vec2(0.0);

    for(int y=-1; y<=1; y++){
        for(int x=-1; x<=1; x++){
            vec2 b = vec2(float(x), float(y));
            vec2 off = hash22(ip + b);
            // make points biased to create shards: push distribution towards cell edges
            off = off * 0.9 + 0.05;
            vec2 pt = b + off;
            float d = distance(fp, pt);
            if(d < minD){ secondD = minD; minD = d; bestPt = pt; }
            else if(d < secondD){ secondD = d; }
        }
    }
    // return (minDist, edgeFactor) where edgeFactor ~ difference to second nearest (sharpness)
    float edge = clamp(secondD - minD, 0.0, 1.0);
    return vec2(minD, edge);
}

// approximate gradient of distance field at p (grid space) by finite differences
vec2 fieldNormal(vec2 p, float cells){
    float eps = 0.7; // sample offset in grid space
    float d = cellInfo(p, cells).x;
    float dx = cellInfo(p + vec2(eps, 0.0), cells).x - d;
    float dy = cellInfo(p + vec2(0.0, eps), cells).x - d;
    vec2 n = normalize(vec2(dx, dy) + 1e-6);
    return n;
}

vec3 softGamma(vec3 c){ return pow(clamp(c,0.0,1.0), vec3(0.90)); }

void main(){
    vec2 uv = vec2(vTex.x, 1.0 - vTex.y);
    float strength = clamp(uStrength, 0.0, 3.0);

    // --- Shard density ---
    float minCells = 18.0;
    float maxCells = 56.0;
    float cells = mix(minCells, maxCells, clamp(strength/2.0, 0.0, 1.0));

    // Grid-space coordinate
    vec2 gp = uv * cells;

    // Slow global wobble
    vec2 globalJiggle = (vec2(sin(uTime*0.6), cos(uTime*0.45)) * 0.02) * strength;
    gp += globalJiggle * cells;

    // Distance to nearest site & edge factor
    vec2 info = cellInfo(gp, cells);
    float minD = info.x;        // [0..~1]
    float edgeF = info.y;       // [0..1]

    // Distance field normal
    vec2 n = fieldNormal(gp, cells);

    // Quantize normals into angular facets
    float facets = mix(3.0, 7.0, clamp(strength/2.0, 0.0, 1.0));
    float ang = atan(n.y, n.x);
    float q = floor((ang / 6.2831853) * facets);
    float angQ = (q + 0.5) * (6.2831853 / facets);
    n = vec2(cos(angQ), sin(angQ));

    // Refraction-like displacement magnitude
    // displacement strength scales with how close to a shard center (minD small -> center) and edgeF (sharper edges)
    float centerInfluence = 1.0 - smoothstep(0.0, 0.6, minD*2.0);
    float edgeInfluence = pow(edgeF, 1.8);
    float dispAmount = 0.0025 * mix(0.6, 2.2, strength) * (0.6 + centerInfluence*1.2) * (0.4 + edgeInfluence*1.6);

    // Directional displacement
    vec2 disp = n * dispAmount;

    // Per-cell jitter
    vec2 jitter = (hash22(floor(gp)).xy - 0.5) * 0.5 * dispAmount * 4.0;
    disp += jitter;

    // Chromatic dispersion
    vec2 uvR = clamp(uv + disp * 1.10, vec2(0.0), vec2(1.0));
    vec2 uvG = clamp(uv + disp * 0.00, vec2(0.0), vec2(1.0));
    vec2 uvB = clamp(uv + disp * -0.80, vec2(0.0), vec2(1.0));

    // Pixel-perfect sampling
    vec2 texel = 1.0 / uTexSize;
    uvR = floor(uvR * uTexSize) * texel + texel*0.5;
    uvG = floor(uvG * uTexSize) * texel + texel*0.5;
    uvB = floor(uvB * uTexSize) * texel + texel*0.5;

    vec3 col;
    col.r = texture2D(uTex, uvR).r;
    col.g = texture2D(uTex, uvG).g;
    col.b = texture2D(uTex, uvB).b;

    // Edge glints & sparkle
    float edgeHighlight = smoothstep(0.04, 0.0, minD) * pow(edgeF, 0.8);
    // moving sparkle along edges
    float sparkle = pow(max(0.0, 1.0 - minD*6.0), 3.0) * (0.25 + 0.75 * edgeF);
    float shimmer = hash21(floor(gp) + vec2(uTime*2.0));
    float glint = edgeHighlight * sparkle * (0.6 + 0.4 * shimmer);
    col += vec3(1.0, 0.95, 0.85) * glint * 0.9 * strength;

    // Inter-shard bleed (local blur)
    vec3 blurAccum = vec3(0.0); float w=0.0;
    for(int x=-1;x<=1;x++){
        for(int y=-1;y<=1;y++){
            vec2 o = vec2(float(x), float(y)) * texel;
            blurAccum += texture2D(uTex, clamp(uv + o + disp*0.25, 0.0, 1.0)).rgb;
            w += 1.0;
        }
    }
    vec3 localAvg = blurAccum / w;
    float bleedMix = smoothstep(0.0, 0.5, minD) * 0.25 * strength;
    col = mix(col, localAvg, bleedMix);

    // Vignette & contrast
    float r = length((uv - 0.5) * vec2(1.0, 1.0));
    float vign = smoothstep(0.85, 0.25, r);
    col *= vign;

    // Micro-scratch noise
    float noiseVal = hash21(gl_FragCoord.xy * 0.5 + vec2(uTime*12.0));
    col += vec3((noiseVal - 0.5) * 0.01 * strength);

    col = softGamma(col);
    col = clamp(col, 0.0, 1.0);
    gl_FragColor = vec4(col, 1.0);
}

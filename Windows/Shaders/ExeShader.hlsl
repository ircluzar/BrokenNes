// DisplayName: EXE
// CoreName: Creepy Look
// Description: Animated vertical beam attracts pixels with swirl, particles, glitch slices, and chromatic shear.
// Performance: -22
// Rating: 1
// Category: Distort

cbuffer Constants : register(b0)
{
    float2 uTexSize;
    float uTime;
    float uStrength;
};

Texture2D<float4> uTex : register(t0);
SamplerState uSampler : register(s0);

struct PS_INPUT
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

float hash(float2 p)
{
    p = frac(p * float2(125.34, 417.13));
    p += dot(p, p + 23.17);
    return frac(p.x * p.y);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + float2(1.0, 0.0));
    float c = hash(i + float2(0.0, 1.0));
    float d = hash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float luma(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

float3 grade(float3 c, float strength, float t)
{
    float L = luma(c);
    float sat = 0.45 + 0.15 * sin(t * 0.13);
    float3 grey = float3(L, L, L);
    c = lerp(grey, c, sat);
    
    // Color matrix transformation
    c = float3(
        c.r * 1.05 + c.g * 0.05 + c.b * 0.00,
        c.r * 0.02 + c.g * 1.08 + c.b * 0.02,
        c.r * 0.00 + c.g * 0.04 + c.b * 0.90
    );
    
    c = pow(c + 0.035, float3(0.92, 0.92, 0.92));
    c = lerp(c, float3(L, L, L), 0.12 * strength);
    return clamp(c, 0.0, 1.0);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0 / uTexSize;
    float t = uTime;
    float strength = clamp(uStrength, 0.0, 3.0);

    // Beam center & base field
    float beamSpeed = 0.07 + 0.05 * sin(t * 0.31);
    float beamX = frac(t * beamSpeed + 0.25 + 0.1 * sin(t * 0.17));
    float dBeam = abs(uv.x - beamX);
    float attract = exp(-pow(dBeam * uTexSize.x * (0.8 + 0.6 * strength), 1.15));
    float ang = noise(float2(uv.y * 40.0 + t * 1.2, uv.x * 18.0 - t * 0.8)) * 6.28318;
    float2 swirl = float2(cos(ang), sin(ang));

    float jitter = (hash(floor(uv * float2(uTexSize.x, uTexSize.y)) + t * 2.37) - 0.5);
    
    // Pull direction
    float2 pullDir = normalize(float2(beamX - uv.x, 0.0005 + 0.12 * sin(t * 0.9 + uv.y * 6.0)) + swirl * 0.2);
    float pullMag = attract * (0.55 + 0.45 * sin(t * 3.0 + uv.y * 25.0)) * (0.25 + 0.75 * strength);
    pullMag += jitter * 0.04 * strength;
    float2 disp = pullDir * pullMag * texel * (4.0 + 10.0 * strength);

    // Chromatic shear
    float shear = (0.20 + 0.35 * strength) * attract;
    float2 rOff = disp + float2(+shear, 0.0) * texel;
    float2 gOff = disp;
    float2 bOff = disp + float2(-shear, 0.0) * texel;

    float3 col;
    col.r = uTex.Sample(uSampler, clamp(uv + rOff, 0.0, 1.0)).r;
    col.g = uTex.Sample(uSampler, clamp(uv + gOff, 0.0, 1.0)).g;
    col.b = uTex.Sample(uSampler, clamp(uv + bOff, 0.0, 1.0)).b;

    float L = luma(col);
    
    // Particle streak accumulation
    float partSeed = hash(floor(uv * uTexSize) + t);
    float spawn = smoothstep(0.55, 0.9, L) * step(0.35, partSeed);
    float3 partAccum = float3(0.0, 0.0, 0.0);
    float wsum = 0.0;
    
    [unroll]
    for (int i = -3; i <= 3; i++)
    {
        float fi = float(i);
        float w = exp(-fi * fi * 0.25);
        float drift = (noise(float2(uv.x * 60.0 + fi * 0.7, t * 1.5 + uv.y * 25.0)) - 0.5);
        float2 pUv = uv + disp + float2(0.0, fi * texel.y * (0.9 + 0.4 * strength)) + float2(0.0, drift * 0.8 * texel.y);
        partAccum += uTex.Sample(uSampler, clamp(pUv, 0.0, 1.0)).rgb * w;
        wsum += w;
    }
    partAccum /= max(wsum, 0.0001);
    float3 particles = lerp(col, partAccum, 0.65) * spawn * (0.4 + 0.8 * strength);
    col += particles;

    // Vertical stripe modulation
    float colId = uv.x * uTexSize.x;
    float vPhase = frac(colId * 0.5);
    float stripe = smoothstep(0.15, 0.0, min(vPhase, 1.0 - vPhase));
    float vStrength = lerp(0.18, 0.42, clamp(strength * 0.6, 0.0, 1.0));
    float vMask = 1.0 - vStrength * (1.0 - stripe);
    col *= vMask;

    // Horizontal scanline modulation
    float hPhase = frac(uv.y * uTexSize.y);
    float hScan = 0.85 + 0.15 * pow(sin(3.14159 * hPhase), 2.0);
    col *= (0.94 + 0.06 * hScan);

    // Intermittent glitch slice
    float seg = floor(t * 1.2);
    float segT = frac(t * 1.2);
    float gChance = hash(float2(seg, 91.2));
    float glitchEnv = step(0.83, gChance) * smoothstep(0.05, 0.2, segT) * (1.0 - smoothstep(0.55, 0.95, segT));
    
    if (glitchEnv > 0.0)
    {
        float2 gDisp = float2(0.0, 0.0);
        gDisp.x = (noise(float2(uv.y * 300.0 + t * 90.0, seg * 7.0)) - 0.5) * 4.0 * texel.x * strength;
        col = lerp(col, uTex.Sample(uSampler, clamp(uv + gDisp, 0.0, 1.0)).rgb, 0.65);
        col += (hash(float2(uv.y * 400.0, t * 120.0)) - 0.5) * 0.25 * glitchEnv;
    }

    // Grain & vignette
    float grain = noise(uv * float2(360.0, 240.0) + t * 2.7) - 0.5;
    col += grain * (0.04 + 0.08 * strength);
    float r = length((uv - 0.5) * float2(1.15, 1.05));
    float vign = smoothstep(0.85, 0.25, r);
    col *= vign;

    col = grade(col, strength, t);
    col = clamp(col, 0.0, 1.0);
    return float4(col, 1.0);
}

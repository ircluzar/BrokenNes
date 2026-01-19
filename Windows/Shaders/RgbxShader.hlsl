// DisplayName: RGBX
// CoreName: Chromatic Vector
// Description: Separates RGB channels into distinct animated motion fields with soft bleed for a readable psychedelic effect.
// Performance: -17
// Rating: 5
// Category: Color

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

float luma(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0 / uTexSize;
    float t = uTime;
    float s = clamp(uStrength, 0.0, 3.0);

    // Center-relative geometry
    float2 toC = uv - 0.5;
    float r = length(toC) + 1e-6;
    float2 nrm = toC / r;                           // outward radial
    float2 tanv = float2(-nrm.y, nrm.x);            // tangential (CCW)
    float ang = atan2(toC.y, toC.x);

    // Time-varying channel directions
    float wigR = 0.35 * sin(t * 1.3 + ang * 5.0);
    float wigG = 0.35 * sin(t * 1.1 + ang * 7.0 + 2.1);
    float wigB = 0.35 * sin(t * 1.5 + ang * 6.0 + 4.2);
    float2 dirR = normalize(nrm + wigR * float2(cos(ang * 3.0 + t * 0.9), sin(ang * 3.0 + t * 0.9)));
    float2 dirG = normalize(tanv + wigG * float2(cos(ang * 4.0 - t * 1.1), sin(ang * 4.0 - t * 1.1)));
    float2 dirB = normalize(-nrm + wigB * float2(cos(ang * 5.0 + t * 1.2), sin(ang * 5.0 + t * 1.2)));

    // Base magnitude in texels
    float edgeBoost = smoothstep(0.0, 0.6, r * 1.6);
    float mag = (0.45 + 1.35 * s) * (0.5 + 0.7 * edgeBoost);

    // Temporal modulation
    float mR = (1.0 + 0.45 * sin(t * 1.7 + r * 22.0 + ang * 3.0));
    float mG = (1.0 + 0.45 * sin(t * 1.4 + r * 19.0 - ang * 4.0));
    float mB = (1.0 + 0.45 * sin(t * 1.9 + r * 25.0 + ang * 2.0));

    float2 offR = dirR * texel * mag * mR;
    float2 offG = dirG * texel * (mag * 0.85) * mG;
    float2 offB = dirB * texel * (mag * 1.10) * mB;

    // Directional bleeding gathers
    float bleed = lerp(0.18, 0.70, clamp(s / 3.0, 0.0, 1.0));

    // Red gather
    float rAcc = 0.0;
    float rW = 0.0;
    [unroll]
    for (int i = -2; i <= 2; i++)
    {
        float fi = float(i);
        float w = exp(-fi * fi * 0.42);
        float2 p = uv + offR + dirR * fi * texel * (1.4 * bleed);
        rAcc += uTex.Sample(uSampler, clamp(p, 0.0, 1.0)).r * w;
        rW += w;
    }
    float R = rAcc / max(rW, 1e-4);

    // Green gather
    float gAcc = 0.0;
    float gW = 0.0;
    [unroll]
    for (int j = -2; j <= 2; j++)
    {
        float fj = float(j);
        float w = exp(-fj * fj * 0.42);
        float2 p = uv + offG + dirG * fj * texel * (1.1 * bleed);
        gAcc += uTex.Sample(uSampler, clamp(p, 0.0, 1.0)).g * w;
        gW += w;
    }
    float G = gAcc / max(gW, 1e-4);

    // Blue gather
    float bAcc = 0.0;
    float bW = 0.0;
    [unroll]
    for (int k = -2; k <= 2; k++)
    {
        float fk = float(k);
        float w = exp(-fk * fk * 0.42);
        float2 p = uv + offB + dirB * fk * texel * (1.6 * bleed);
        bAcc += uTex.Sample(uSampler, clamp(p, 0.0, 1.0)).b * w;
        bW += w;
    }
    float B = bAcc / max(bW, 1e-4);

    float3 col = float3(R, G, B);

    // Mild hue rotation
    float rot = 0.18 * clamp(s * 0.6, 0.0, 1.0) * sin(t * 0.9);
    float3x3 hue = float3x3(
        0.95 + 0.05 * cos(rot + 0.0), 0.05 * sin(rot + 1.7), 0.05 * sin(rot + 3.1),
        0.05 * sin(rot + 0.8), 0.95 + 0.05 * cos(rot + 2.1), 0.05 * sin(rot + 0.3),
        0.05 * sin(rot + 2.2), 0.05 * sin(rot + 1.2), 0.95 + 0.05 * cos(rot + 4.0)
    );
    col = clamp(mul(hue, col), 0.0, 1.0);

    // Contrast pop
    float L = luma(col);
    col = lerp(float3(L, L, L), col, 1.06);
    col = (col - 0.5) * 1.06 + 0.5;
    col = clamp(col, 0.0, 1.0);

    return float4(col, 1.0);
}

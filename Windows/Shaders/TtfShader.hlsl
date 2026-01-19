// DisplayName: TTF
// CoreName: Subpixel Clean
// Description: Subpixel sampling for sharper vertical features using RGB offsets, edge gating, and small gathers.
// Performance: -4
// Rating: 3
// Category: Utility

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
    float s = clamp(uStrength, 0.0, 3.0);

    // Derive horizontal uv step per screen pixel using derivatives
    float dudx = abs(ddx(uv.x));
    float stepX = dudx > 1e-5 ? dudx : texel.x;

    // Subpixel offsets for RGB stripe (R left, G center, B right)
    // Scale by strength and clamp so we don't hop over more than ~half a texel.
    float sub = clamp(stepX * (0.33 * (0.5 + 0.5 * min(s, 1.5))), 0.0, texel.x * 0.5 + 1e-6);
    float2 offR = float2(-sub, 0.0);
    float2 offG = float2(0.0, 0.0);
    float2 offB = float2(sub, 0.0);

    // Edge detection along X to gate effect (only apply where vertical edges likely)
    float3 cL = uTex.Sample(uSampler, clamp(uv - float2(stepX, 0.0), 0.0, 1.0)).rgb;
    float3 cC = uTex.Sample(uSampler, clamp(uv, 0.0, 1.0)).rgb;
    float3 cR = uTex.Sample(uSampler, clamp(uv + float2(stepX, 0.0), 0.0, 1.0)).rgb;
    float gx = luma(cR) - luma(cL);
    float edge = smoothstep(0.05, 0.35, abs(gx));

    // Slight 1D Gaussian gather per channel to suppress chroma speckle
    float w0 = 0.5;
    float w1 = 0.25;
    float2 small = float2(stepX * 0.5, 0.0);

    float R = w0 * uTex.Sample(uSampler, clamp(uv + offR, 0.0, 1.0)).r
            + w1 * uTex.Sample(uSampler, clamp(uv + offR - small, 0.0, 1.0)).r
            + w1 * uTex.Sample(uSampler, clamp(uv + offR + small, 0.0, 1.0)).r;

    float G = w0 * uTex.Sample(uSampler, clamp(uv + offG, 0.0, 1.0)).g
            + w1 * uTex.Sample(uSampler, clamp(uv + offG - small, 0.0, 1.0)).g
            + w1 * uTex.Sample(uSampler, clamp(uv + offG + small, 0.0, 1.0)).g;

    float B = w0 * uTex.Sample(uSampler, clamp(uv + offB, 0.0, 1.0)).b
            + w1 * uTex.Sample(uSampler, clamp(uv + offB - small, 0.0, 1.0)).b
            + w1 * uTex.Sample(uSampler, clamp(uv + offB + small, 0.0, 1.0)).b;

    float3 subpix = float3(R, G, B);
    float3 orig = cC;

    // Blend toward subpixel result based on edge strength and global strength
    float k = clamp(edge * (0.55 + 0.45 * min(s, 1.0)), 0.0, 1.0);
    float3 col = lerp(orig, subpix, k);

    // Mild contrast pop for perceived sharpness
    col = (col - 0.5) * (1.03 + 0.06 * min(s, 1.0)) + 0.5;
    return float4(clamp(col, 0.0, 1.0), 1.0);
}

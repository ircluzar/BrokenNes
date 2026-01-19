// DisplayName: 16B
// CoreName: 16-bit Upgrade
// Description: Gentle edge-aware smoothing with light chroma blur and subtle grading to evoke a SNES-like 16-bit feel.
// Performance: -15
// Rating: 4
// Category: Enhance

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

static const float3 LUMA = float3(0.299f, 0.587f, 0.114f);

float3 rgb2yuv(float3 c)
{
    float y = dot(c, LUMA);
    float u = (c.b - y) * 0.565f;
    float v = (c.r - y) * 0.713f;
    return float3(y, u, v);
}

float3 yuv2rgb(float3 yuv)
{
    float y = yuv.x;
    float u = yuv.y;
    float v = yuv.z;
    float r = y + 1.403f * v;
    float g = y - 0.344f * u - 0.714f * v;
    float b = y + 1.770f * u;
    return float3(r, g, b);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0f / uTexSize;
    float k = saturate(uStrength / 3.0f);

    // Base color
    float3 c0 = uTex.Sample(uSampler, uv).rgb;

    // Edge-aware 3x3 smoothing (simple bilateral-ish)
    float w1 = 1.0f, w2 = 2.0f, w4 = 4.0f;
    float sigma = lerp(0.020f, 0.060f, k);
    float inv2s2 = 0.5f / (sigma * sigma);

    float l0 = dot(c0, LUMA);

    float3 acc = float3(0.0f, 0.0f, 0.0f);
    float wsum = 0.0f;

    // Center
    acc += c0 * w4;
    wsum += w4;

    // Cardinal neighbors
    [unroll]
    for (int i = 0; i < 2; i++)
    {
        float2 o = (i == 0) ? float2(texel.x, 0.0f) : float2(0.0f, texel.y);
        float3 cL = uTex.Sample(uSampler, saturate(uv - o)).rgb;
        float3 cR = uTex.Sample(uSampler, saturate(uv + o)).rgb;
        float lL = dot(cL, LUMA);
        float lR = dot(cR, LUMA);
        float gL = exp(-pow(lL - l0, 2.0f) * inv2s2);
        float gR = exp(-pow(lR - l0, 2.0f) * inv2s2);
        float wL = w2 * lerp(1.0f, gL, 0.8f);
        float wR = w2 * lerp(1.0f, gR, 0.8f);
        acc += cL * wL + cR * wR;
        wsum += wL + wR;
    }

    // Diagonals
    {
        float2 o = texel;
        float3 c1 = uTex.Sample(uSampler, saturate(uv + float2(-o.x, -o.y))).rgb;
        float3 c2 = uTex.Sample(uSampler, saturate(uv + float2(o.x, -o.y))).rgb;
        float3 c3 = uTex.Sample(uSampler, saturate(uv + float2(-o.x, o.y))).rgb;
        float3 c4 = uTex.Sample(uSampler, saturate(uv + float2(o.x, o.y))).rgb;
        float l1 = dot(c1, LUMA);
        float l2 = dot(c2, LUMA);
        float l3 = dot(c3, LUMA);
        float l4 = dot(c4, LUMA);
        float g1 = exp(-pow(l1 - l0, 2.0f) * inv2s2);
        float g2 = exp(-pow(l2 - l0, 2.0f) * inv2s2);
        float g3 = exp(-pow(l3 - l0, 2.0f) * inv2s2);
        float g4 = exp(-pow(l4 - l0, 2.0f) * inv2s2);
        float w1b = w1 * lerp(1.0f, g1, 0.8f);
        float w2b = w1 * lerp(1.0f, g2, 0.8f);
        float w3b = w1 * lerp(1.0f, g3, 0.8f);
        float w4b = w1 * lerp(1.0f, g4, 0.8f);
        acc += c1 * w1b + c2 * w2b + c3 * w3b + c4 * w4b;
        wsum += w1b + w2b + w3b + w4b;
    }

    float3 smooth9 = acc / max(wsum, 1e-5f);

    // Blend original toward smoothed based on strength
    float3 smoothCol = lerp(c0, smooth9, lerp(0.28f, 0.85f, k));

    // Chroma-only horizontal blur
    float3 yuvC = rgb2yuv(smoothCol);
    float3 yuvL = rgb2yuv(uTex.Sample(uSampler, saturate(uv - float2(texel.x, 0.0f))).rgb);
    float3 yuvR = rgb2yuv(uTex.Sample(uSampler, saturate(uv + float2(texel.x, 0.0f))).rgb);
    float chromaMix = lerp(0.20f, 0.60f, k);
    float yKeep = lerp(0.85f, 0.95f, k);
    float U = lerp(yuvC.y, (yuvL.y * 0.25f + yuvC.y * 0.5f + yuvR.y * 0.25f), chromaMix);
    float V = lerp(yuvC.z, (yuvL.z * 0.25f + yuvC.z * 0.5f + yuvR.z * 0.25f), chromaMix);
    float Y = lerp(dot(smoothCol, LUMA), yuvC.x, yKeep);
    float3 chromaSmoothed = yuv2rgb(float3(Y, U, V));

    // Saturation boost and gentle gamma lift
    float L = dot(chromaSmoothed, LUMA);
    float3 L3 = float3(L, L, L);
    float sat = lerp(1.05f, 1.55f, k);
    float3 satCol = L3 + (chromaSmoothed - L3) * sat;
    float gamma = lerp(1.00f, 0.92f, k);
    float3 tone = pow(satCol, float3(gamma, gamma, gamma));
    tone = (tone - 0.5f) * (1.0f - 0.12f * k) + 0.5f;

    // Subtle scanline shading
    float line = frac(uv.y * uTexSize.y);
    float scan = lerp(0.00f, 0.06f, k);
    float shade = 1.0f - scan * smoothstep(0.0f, 1.0f, line);

    float3 outCol = saturate(tone * shade);
    return float4(outCol, 1.0f);
}

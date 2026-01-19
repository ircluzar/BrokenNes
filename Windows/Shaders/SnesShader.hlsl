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

static const float3 LUMA = float3(0.299, 0.587, 0.114);

float3 rgb2yuv(float3 c)
{
    float y = dot(c, LUMA);
    float u = (c.b - y) * 0.565; // BT.601 approx
    float v = (c.r - y) * 0.713;
    return float3(y, u, v);
}

float3 yuv2rgb(float3 yuv)
{
    float y = yuv.x, u = yuv.y, v = yuv.z;
    float r = y + 1.403 * v;
    float g = y - 0.344 * u - 0.714 * v;
    float b = y + 1.770 * u;
    return float3(r, g, b);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0 / uTexSize;
    float k = clamp(uStrength, 0.0, 3.0) / 3.0; // 0..1 intensity

    // Base color
    float3 c0 = uTex.Sample(uSampler, uv).rgb;

    // --- Edge-aware 3x3 smoothing (simple bilateral-ish) ---
    // Gaussian kernel 1 2 1; 2 4 2; 1 2 1 (sum 16)
    float w1 = 1.0, w2 = 2.0, w4 = 4.0;
    float sigma = lerp(0.020, 0.060, k); // edge preservation (luma domain)
    float inv2s2 = 0.5 / (sigma * sigma);

    float l0 = dot(c0, LUMA);

    float3 acc = float3(0.0, 0.0, 0.0);
    float wsum = 0.0;

    // Center
    {
        float w = w4;
        acc += c0 * w;
        wsum += w;
    }
    
    // Cardinal neighbors
    [unroll]
    for (int i = 0; i < 2; i++)
    {
        // i==0 -> left/right, i==1 -> up/down
        float2 o = (i == 0) ? float2(texel.x, 0.0) : float2(0.0, texel.y);
        float3 cL = uTex.Sample(uSampler, clamp(uv - o, 0.0, 1.0)).rgb;
        float3 cR = uTex.Sample(uSampler, clamp(uv + o, 0.0, 1.0)).rgb;
        float lL = dot(cL, LUMA);
        float lR = dot(cR, LUMA);
        float gL = exp(-(lL - l0) * (lL - l0) * inv2s2);
        float gR = exp(-(lR - l0) * (lR - l0) * inv2s2);
        float wL = w2 * lerp(1.0, gL, 0.8);
        float wR = w2 * lerp(1.0, gR, 0.8);
        acc += cL * wL + cR * wR;
        wsum += wL + wR;
    }
    
    // Diagonals
    {
        float2 o = texel;
        float3 c1 = uTex.Sample(uSampler, clamp(uv + float2(-o.x, -o.y), 0.0, 1.0)).rgb;
        float3 c2 = uTex.Sample(uSampler, clamp(uv + float2(o.x, -o.y), 0.0, 1.0)).rgb;
        float3 c3 = uTex.Sample(uSampler, clamp(uv + float2(-o.x, o.y), 0.0, 1.0)).rgb;
        float3 c4 = uTex.Sample(uSampler, clamp(uv + float2(o.x, o.y), 0.0, 1.0)).rgb;
        float l1 = dot(c1, LUMA), l2 = dot(c2, LUMA), l3 = dot(c3, LUMA), l4 = dot(c4, LUMA);
        float g1 = exp(-(l1 - l0) * (l1 - l0) * inv2s2);
        float g2 = exp(-(l2 - l0) * (l2 - l0) * inv2s2);
        float g3 = exp(-(l3 - l0) * (l3 - l0) * inv2s2);
        float g4 = exp(-(l4 - l0) * (l4 - l0) * inv2s2);
        float w = w1;
        float w1b = w * lerp(1.0, g1, 0.8);
        float w2b = w * lerp(1.0, g2, 0.8);
        float w3b = w * lerp(1.0, g3, 0.8);
        float w4b = w * lerp(1.0, g4, 0.8);
        acc += c1 * w1b + c2 * w2b + c3 * w3b + c4 * w4b;
        wsum += w1b + w2b + w3b + w4b;
    }

    float3 smooth9 = acc / max(wsum, 1e-5);

    // Blend original toward smoothed based on strength
    float3 smoothCol = lerp(c0, smooth9, lerp(0.28, 0.85, k));

    // --- Chroma-only horizontal blur (cleaner color transitions) ---
    // Sample neighbors and blur U/V slightly; keep Y mostly from smoothCol
    float3 yuvC = rgb2yuv(smoothCol);
    float3 yuvL = rgb2yuv(uTex.Sample(uSampler, clamp(uv - float2(texel.x, 0.0), 0.0, 1.0)).rgb);
    float3 yuvR = rgb2yuv(uTex.Sample(uSampler, clamp(uv + float2(texel.x, 0.0), 0.0, 1.0)).rgb);
    float chromaMix = lerp(0.20, 0.60, k); // how much we blur U/V
    float yKeep = lerp(0.85, 0.95, k);     // keep most of smoothed luma
    float U = lerp(yuvC.y, (yuvL.y * 0.25 + yuvC.y * 0.5 + yuvR.y * 0.25), chromaMix);
    float V = lerp(yuvC.z, (yuvL.z * 0.25 + yuvC.z * 0.5 + yuvR.z * 0.25), chromaMix);
    float Y = lerp(dot(smoothCol, LUMA), yuvC.x, yKeep);
    float3 chromaSmoothed = yuv2rgb(float3(Y, U, V));

    // --- Saturation boost and gentle gamma lift ---
    float L = dot(chromaSmoothed, LUMA);
    float3 L3 = float3(L, L, L);
    float sat = lerp(1.05, 1.55, k); // 5%..55% boost
    float3 satCol = L3 + (chromaSmoothed - L3) * sat;
    float gamma = lerp(1.00, 0.92, k); // <1 brightens slightly
    float3 tone = pow(satCol, float3(gamma, gamma, gamma));
    // Mild contrast curve to soften harsh steps
    tone = (tone - 0.5) * (1.0 - 0.12 * k) + 0.5;

    // --- Very subtle scanline shading (optional) ---
    float linePos = frac(uv.y * uTexSize.y);
    float scan = lerp(0.00, 0.06, k); // up to 6%
    float shade = 1.0 - scan * smoothstep(0.0, 1.0, linePos);

    float3 outCol = clamp(tone * shade, 0.0, 1.0);
    return float4(outCol, 1.0);
}

// DisplayName: BLD
// CoreName: 4-Way Color Bleed
// Description: Edge-aware directional diffusion that lets saturated pixels softly bleed along X/Y while protecting strong edges.
// Performance: -12
// Rating: 3
// Category: Stylize

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

float luma(float3 c)
{
    return dot(c, LUMA);
}

float satVal(float3 c)
{
    float y = luma(c);
    return length(c - float3(y, y, y));
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0 / uTexSize;
    float s = clamp(uStrength, 0.0, 3.0);
    
    if (s <= 1e-5)
    {
        return float4(uTex.Sample(uSampler, uv).rgb, 1.0);
    }
    
    float k = s / 3.0;

    float3 base = uTex.Sample(uSampler, uv).rgb;
    float baseLum = luma(base);

    // Ray configuration
    int maxSteps = int(2.0 + floor(k * 4.0)); // 2..6 samples each direction
    float falloff = lerp(1.35, 0.55, k);      // faster falloff at low strength
    float satBoost = lerp(1.2, 1.8, k);       // higher saturation influence
    float edgeProtect = lerp(0.55, 0.30, k);  // reduce edge protection with strength

    float3 accum = base * 1.0;
    float wSum = 1.0;

    // Directional diffusion accumulate (4 cardinal rays)
    [unroll]
    for (int dirIdx = 0; dirIdx < 4; ++dirIdx)
    {
        float2 dir = (dirIdx == 0) ? float2(1.0, 0.0) :
                     (dirIdx == 1) ? float2(-1.0, 0.0) :
                     (dirIdx == 2) ? float2(0.0, 1.0) : float2(0.0, -1.0);
        float3 lastColor = base;
        float lastLum = baseLum;
        
        for (int step = 1; step <= 6; ++step)
        {
            if (step > maxSteps) break;
            float fstep = float(step);
            float2 o = uv + dir * texel * fstep;
            o = clamp(o, 0.0, 1.0);
            float3 c = uTex.Sample(uSampler, o).rgb;
            float lumC = luma(c);
            float ed = abs(lumC - lastLum);
            float satC = satVal(c);
            
            // Weight: exponential distance falloff * saturation factor * edge gating
            float w = exp(-fstep * falloff);
            float satW = 1.0 + (satC * satBoost);
            float edgeW = 1.0 / (1.0 + ed * (8.0 * edgeProtect));
            float totalW = w * satW * edgeW;
            accum += c * totalW;
            wSum += totalW;
            
            // Allow color to continue leaking even across moderate edges
            lastLum = lerp(lastLum, lumC, 0.35 + 0.25 * k);
            lastColor = c;
        }
    }

    float3 bleedCol = accum / max(wSum, 1e-5);

    // Contrast & saturation restoration
    float Lb = luma(bleedCol);
    float3 L3 = float3(Lb, Lb, Lb);
    float satAmt = lerp(1.05, 1.25, k);
    float3 satCol = L3 + (bleedCol - L3) * satAmt;
    float contrast = lerp(1.00, 1.08 + 0.10 * k, k);
    float3 restored = (satCol - 0.5) * contrast + 0.5;

    // Blend & finalize
    float blend = lerp(0.30, 0.85, k);
    float3 outCol = lerp(base, restored, blend);

    // Mild clamp & gamma nuance to avoid dulling
    outCol = clamp(outCol, 0.0, 1.0);
    float gamma = lerp(1.0, 0.97, k);
    outCol = pow(outCol, float3(gamma, gamma, gamma));

    return float4(outCol, 1.0);
}

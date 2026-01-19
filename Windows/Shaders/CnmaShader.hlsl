// DisplayName: CNMA
// CoreName: Cinematic
// Description: Content-aware exposure shaping with filmic contrast, teal/orange grade, adaptive saturation, and halation/vignette.
// Performance: -18
// Rating: 1
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

float luma(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }
float3 saturate3(float3 c) { return saturate(c); }
float3 satAdjust(float3 c, float s)
{
    float L = luma(c);
    return lerp(float3(L, L, L), c, s);
}

float3 blur9(Texture2D<float4> tex, SamplerState samp, float2 uv, float2 texel, float radius)
{
    float3 acc = float3(0.0f, 0.0f, 0.0f);
    float wsum = 0.0f;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 o = float2((float)x, (float)y) * texel * radius;
            float w = (x == 0 && y == 0) ? 1.0f : 0.75f;
            acc += tex.Sample(samp, saturate(uv + o)).rgb * w;
            wsum += w;
        }
    }
    return acc / max(wsum, 1e-4f);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0f / uTexSize;
    float s = saturate(uStrength);

    float3 col0 = uTex.Sample(uSampler, uv).rgb;
    float L0 = luma(col0);

    float3 blurSmall = blur9(uTex, uSampler, uv, texel, 1.0f);
    float3 blurLarge = blur9(uTex, uSampler, uv, texel, 3.0f);
    float Ls = luma(blurSmall);
    float Ll = luma(blurLarge);
    float Lavg = lerp(Ls, Ll, 0.5f);

    float targetMid = lerp(0.42f, 0.48f, 0.5f + 0.5f * sin(uTime * 0.05f));
    float Exposure = clamp(targetMid / (Lavg + 1e-3f), 0.6f, 1.8f);
    float expAmt = saturate(s / 3.0f);
    float3 col1 = col0 * lerp(1.0f, Exposure, 0.65f * expAmt);

    float pivot = clamp(lerp(0.45f, Lavg, 0.6f), 0.25f, 0.65f);
    float contrast = lerp(1.02f, 1.35f, saturate(s / 3.0f));
    col1 = (col1 - float3(pivot, pivot, pivot)) * contrast + float3(pivot, pivot, pivot);

    float3 shoulder = col1 / (col1 + float3(0.7f, 0.7f, 0.7f));
    col1 = lerp(col1, shoulder, 0.45f * expAmt);

    float3 blurC = blur9(uTex, uSampler, uv, texel, 1.5f);
    float3 detail = col1 - blurC;
    float detailAmt = lerp(0.10f, 0.35f, saturate(s / 3.0f));
    float hiMask = 1.0f - smoothstep(0.6f, 0.95f, luma(col1));
    float3 col2 = col1 + detail * (detailAmt * hiMask);

    float L2 = luma(col2);
    float coolW = smoothstep(0.55f, 0.15f, L2);
    float warmW = smoothstep(0.35f, 0.85f, L2);
    float3 coolTint = float3(0.96f, 1.02f, 1.08f);
    float3 warmTint = float3(1.06f, 1.02f, 0.97f);
    float gradeAmt = 0.35f * saturate(s / 3.0f);
    float3 col3 = col2 * lerp(float3(1.0f, 1.0f, 1.0f), coolTint, gradeAmt * coolW);
    col3 = col3 * lerp(float3(1.0f, 1.0f, 1.0f), warmTint, gradeAmt * warmW);

    float sat = lerp(1.0f, 1.20f, saturate(s / 3.0f));
    float midMask = smoothstep(0.15f, 0.50f, L2) * (1.0f - smoothstep(0.65f, 0.95f, L2));
    col3 = satAdjust(col3, lerp(1.0f, sat, midMask));

    float bright = smoothstep(0.7f, 1.0f, L2);
    if (bright > 0.0f)
    {
        float3 halo = float3(0.0f, 0.0f, 0.0f);
        float wsumH = 0.0f;
        [unroll]
        for (int a = 0; a < 6; a++)
        {
            float fa = (float)a;
            float ang = fa / 6.0f * 6.2831853f;
            float2 dir = float2(cos(ang), sin(ang));
            float r = 1.5f + 0.8f * fa;
            float2 o = dir * texel * r;
            float w = 1.0f / (1.0f + fa);
            halo += uTex.Sample(uSampler, saturate(uv + o)).rgb * w;
            wsumH += w;
        }
        halo /= max(wsumH, 1e-4f);
        float3 halation = lerp(col3, halo, 0.5f) * float3(1.03f, 0.99f, 0.96f);
        col3 = lerp(col3, halation, bright * 0.12f * saturate(s / 3.0f));
    }

    float r = length((uv - 0.5f) * float2(1.1f, 1.0f));
    float vign = lerp(1.0f, smoothstep(0.9f, 0.3f, r), 0.45f * saturate(s / 3.0f));
    float3 col = col3 * vign;

    col = saturate3(col);
    return float4(col, 1.0f);
}

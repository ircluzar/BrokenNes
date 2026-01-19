// DisplayName: CCC
// CoreName: Color Cycle
// Description: Rotates palette hues over time with a breathing inverted mix and mild contrast/saturation pop.
// Performance: -5
// Rating: 2
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

float3 rgb2hsv(float3 c)
{
    float cMax = max(c.r, max(c.g, c.b));
    float cMin = min(c.r, min(c.g, c.b));
    float delta = cMax - cMin;
    float h = 0.0f;
    if (delta > 1e-6f)
    {
        if (cMax == c.r) h = (c.g - c.b) / delta;
        else if (cMax == c.g) h = 2.0f + (c.b - c.r) / delta;
        else h = 4.0f + (c.r - c.g) / delta;
        h = frac(h / 6.0f);
    }
    float s = (cMax <= 0.0f) ? 0.0f : (delta / cMax);
    float v = cMax;
    return float3(h, s, v);
}

float3 hsv2rgb(float3 hsv)
{
    float h = hsv.x * 6.0f;
    float s = saturate(hsv.y);
    float v = saturate(hsv.z);
    float i = floor(h);
    float f = h - i;
    float p = v * (1.0f - s);
    float q = v * (1.0f - s * f);
    float t = v * (1.0f - s * (1.0f - f));
    float3 col;
    if (i < 1.0f) col = float3(v, t, p);
    else if (i < 2.0f) col = float3(q, v, p);
    else if (i < 3.0f) col = float3(p, v, t);
    else if (i < 4.0f) col = float3(p, q, v);
    else if (i < 5.0f) col = float3(t, p, v);
    else col = float3(v, p, q);
    return col;
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float s = saturate(uStrength);
    float3 src = uTex.Sample(uSampler, uv).rgb;

    if (s <= 1e-5f)
    {
        return float4(src, 1.0f);
    }

    float k = s / 3.0f;

    float cps = lerp(0.10f, 0.45f, k);
    float hueShift = frac(uTime * cps);
    float wob = 0.03f * sin(uTime * 1.7f) + 0.02f * sin(uTime * 0.9f);

    float3 hsvN = rgb2hsv(src);
    hsvN.x = frac(hsvN.x + hueShift + wob);
    hsvN.y = saturate(hsvN.y * (1.0f + 0.35f * k));
    hsvN.z = saturate(hsvN.z * (0.95f + 0.25f * k));
    float3 colN = hsv2rgb(hsvN);

    float3 inv = 1.0f - src;
    float3 hsvI = rgb2hsv(inv);
    hsvI.x = frac(hsvI.x + hueShift * (1.15f + 0.25f * k) + 0.17f);
    hsvI.y = saturate(hsvI.y * (0.80f + 0.50f * k));
    hsvI.z = saturate(hsvI.z * (0.90f + 0.35f * k));
    float3 colI = hsv2rgb(hsvI);

    float lfo = 0.5f + 0.5f * sin(uTime * 0.5f);
    float invertMix = pow(lfo, 2.0f) * (0.85f * k);

    float3 col = lerp(colN, colI, invertMix);
    col = (col - 0.5f) * (1.0f + 0.10f * k) + 0.5f;
    col = saturate(col);

    return float4(col, 1.0f);
}

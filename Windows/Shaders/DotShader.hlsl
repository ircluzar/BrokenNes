// DisplayName: DOT
// CoreName: Circular Shards
// Description: Overlapping circular shard field with edge darkening, shear, and chromatic dispersion driven by hashed directions.
// Performance: -14
// Rating: 2
// Category: Refraction

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

float hash21(float2 p)
{
    p = frac(p * float2(123.34f, 345.45f));
    p += dot(p, p + 34.23f);
    return frac(p.x * p.y);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0f / uTexSize;
    float s = saturate(uStrength);
    if (s <= 1e-5f)
    {
        return uTex.Sample(uSampler, uv);
    }
    float k = s / 3.0f;

    float cellCount = lerp(8.0f, 42.0f, k);
    float2 gp = uv * cellCount;
    float2 ij = floor(gp);

    float radius = 0.92f - 0.07f * (1.0f - k);

    float bestDist = 1e9f;
    float2 bestCenter = float2(0.0f, 0.0f);
    float2 bestKey = float2(0.0f, 0.0f);

    [unroll]
    for (int dy = -1; dy <= 1; ++dy)
    {
        for (int dx = -1; dx <= 1; ++dx)
        {
            float2 offs = float2((float)dx, (float)dy);
            float2 center = (ij + offs + 0.5f) / cellCount;
            float2 d = uv - center;
            float dist2 = dot(d, d);
            if (dist2 < bestDist)
            {
                bestDist = dist2;
                bestCenter = center;
                bestKey = ij + offs;
            }
        }
    }

    float dist = sqrt(bestDist) * cellCount;
    float2 rel = (uv - bestCenter) * cellCount;

    float ang = 6.2831853f * hash21(bestKey);
    float wob = 0.45f * sin(uTime * (0.18f + 0.55f * hash21(bestKey + 2.7f)) + ang * 0.40f);
    float ang2 = ang + wob;
    float2 dir = float2(cos(ang2), sin(ang2));
    float2 perp = float2(-dir.y, dir.x);

    float pixMagR = lerp(0.6f, 5.8f, k);
    float pixMagG = pixMagR * (0.95f + 0.05f * hash21(bestKey + 11.3f));
    float pixMagB = pixMagR * 1.05f;

    float shear = (rel.x * 0.7f - rel.y * 0.7f) * (0.7f + 1.25f * k);
    float2 offBase = dir * texel * pixMagR;
    float2 offShear = perp * texel * (shear * 0.75f * pixMagR / radius);

    float2 uvR = saturate(uv + offBase + offShear);
    float2 uvG = saturate(uv + dir * texel * pixMagG + offShear * 0.9f);
    float2 uvB = saturate(uv + dir * texel * pixMagB + offShear * 1.1f);

    float3 col;
    col.r = uTex.Sample(uSampler, uvR).r;
    col.g = uTex.Sample(uSampler, uvG).g;
    col.b = uTex.Sample(uSampler, uvB).b;

    float edgeW = lerp(0.020f, 0.050f, 1.0f - k);
    float crackAmt = lerp(0.14f, 0.38f, k);
    float d = radius - dist;
    float crack = 1.0f - smoothstep(0.0f, edgeW, d);
    col *= 1.0f - crackAmt * crack;

    col = (col - 0.5f) * (1.0f + 0.05f + 0.10f * k) + 0.5f;
    col = saturate(col);

    return float4(col, 1.0f);
}

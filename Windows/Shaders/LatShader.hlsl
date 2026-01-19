// DisplayName: LAT
// CoreName: Lattice
// Description: Micro-facet lattice per NES tile generating refracted and sparkling refraction with chromatic dispersion.
// Performance: -30
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

float hash12(float2 p)
{
    float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031f);
    p3 += dot(p3, p3.yzx + 33.33f);
    return frac((p3.x + p3.y) * p3.z);
}

float2 hash22(float2 p)
{
    float n = sin(dot(p, float2(127.1f, 311.7f)));
    return frac(float2(262144.0f, 32768.0f) * n);
}

float3 hash32(float2 p)
{
    float n = sin(dot(p, float2(12.9898f, 78.233f)));
    float a = frac(n * 43758.5453f);
    float b = frac(n * 28001.8381f);
    float c = frac(n * 11942.6740f);
    return float3(a, b, c);
}

float luma(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }

float2 nearestSeed(float2 q, out float d1, out float d2, float2 seedBase)
{
    d1 = 1e9f;
    d2 = 1e9f;
    float2 best = float2(0.0f, 0.0f);
    float2 gi = floor(q);
    [unroll]
    for (int j = -1; j <= 1; j++)
    {
        for (int i = -1; i <= 1; i++)
        {
            float2 cell = gi + float2((float)i, (float)j);
            float2 rnd = hash22(cell + seedBase) - 0.5f;
            float2 sp = cell + 0.5f + 0.8f * rnd;
            float2 v = sp - q;
            float dsq = dot(v, v);
            if (dsq < d1)
            {
                d2 = d1;
                d1 = dsq;
                best = sp;
            }
            else if (dsq < d2)
            {
                d2 = dsq;
            }
        }
    }
    return best;
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0f / max(uTexSize, float2(1.0f, 1.0f));
    float3 baseCol = uTex.Sample(uSampler, saturate(uv)).rgb;

    float s = saturate(uStrength) / 3.0f;
    float amt = lerp(0.15f, 0.60f, s);

    float2 px = uv * uTexSize;
    float2 tilePx = floor(px / 8.0f);
    float2 inTile = frac(px / 8.0f);

    float facets = lerp(2.0f, 5.0f, saturate(s));
    float2 q = inTile * facets;

    float2 seedBase = tilePx * 0.173f + 0.37f;
    float d1, d2;
    float2 sp = nearestSeed(q, d1, d2, seedBase);

    float3 rn = hash32(sp + seedBase);
    float3 n = normalize(float3(rn.xy * 2.0f - 1.0f, 1.2f + 0.6f * rn.z));

    float pxPerFacet = 8.0f / facets;
    float refrPx = amt * 0.45f * pxPerFacet;
    float2 baseOff = n.xy * refrPx * texel;

    float2 offR = baseOff * 1.25f;
    float2 offG = baseOff * 1.00f;
    float2 offB = baseOff * 0.80f;

    float3 refrCol;
    refrCol.r = uTex.Sample(uSampler, saturate(uv + offR)).r;
    refrCol.g = uTex.Sample(uSampler, saturate(uv + offG)).g;
    refrCol.b = uTex.Sample(uSampler, saturate(uv + offB)).b;

    float edge = smoothstep(0.001f, 0.02f, d2 - d1);
    float seam = 1.0f - edge;

    float3 L = normalize(float3(0.35f, 0.55f, 1.0f));
    float spec = pow(max(dot(n, L), 0.0f), lerp(40.0f, 90.0f, rn.z));
    float twk = 0.6f + 0.4f * sin(uTime * (5.0f + 3.0f * rn.x) + 6.2831f * rn.y);
    float spk = spec * twk;
    float3 sparkle = spk * lerp(float3(1.0f, 1.0f, 1.0f), baseCol, 0.5f) * (0.12f + 0.18f * amt);

    float y = luma(baseCol);
    float3 mixed = lerp(baseCol, refrCol, 0.4f + 0.6f * amt);
    float ym = luma(mixed);
    mixed *= lerp(1.0f, max(0.7f, y / max(ym, 1e-3f)), 0.35f);

    mixed = lerp(mixed, mixed * (0.75f + 0.15f * (1.0f - amt)), clamp(seam * (0.6f * amt), 0.0f, 1.0f));
    mixed += sparkle;

    mixed = saturate(mixed);
    float3 g = float3(1.0f / (1.0f + 0.03f * amt), 1.0f / (1.0f + 0.03f * amt), 1.0f / (1.0f + 0.03f * amt));
    mixed = pow(mixed, g);

    return float4(mixed, 1.0f);
}

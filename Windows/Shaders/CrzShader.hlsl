// DisplayName: CRZ
// CoreName: Crystal Glass
// Description: Sharp irregular glass facets producing refraction and dispersion with edge glints and micro-scratch sparkle.
// Performance: -28
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
    p = frac(p * float2(123.34f, 456.21f));
    p += dot(p, p + 78.233f);
    return frac(p.x * p.y);
}

float2 hash22(float2 p)
{
    return frac(sin(float2(dot(p, float2(127.1f, 311.7f)), dot(p, float2(269.5f, 183.3f)))) * 43758.5453f);
}

float2 cellInfo(float2 p, float cells)
{
    float2 ip = floor(p);
    float2 fp = frac(p);
    float minD = 1e9f;
    float secondD = 1e9f;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 b = float2((float)x, (float)y);
            float2 off = hash22(ip + b);
            off = off * 0.9f + 0.05f;
            float2 pt = b + off;
            float d = distance(fp, pt);
            if (d < minD)
            {
                secondD = minD;
                minD = d;
            }
            else if (d < secondD)
            {
                secondD = d;
            }
        }
    }
    float edge = saturate(secondD - minD);
    return float2(minD, edge);
}

float2 fieldNormal(float2 p, float cells)
{
    float eps = 0.7f;
    float d = cellInfo(p, cells).x;
    float dx = cellInfo(p + float2(eps, 0.0f), cells).x - d;
    float dy = cellInfo(p + float2(0.0f, eps), cells).x - d;
    return normalize(float2(dx, dy) + 1e-6f);
}

float3 softGamma(float3 c) { return pow(saturate(c), float3(0.90f, 0.90f, 0.90f)); }

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float strength = saturate(uStrength);

    float minCells = 18.0f;
    float maxCells = 56.0f;
    float cells = lerp(minCells, maxCells, saturate(strength * 0.5f));

    float2 gp = uv * cells;

    float2 globalJiggle = float2(sin(uTime * 0.6f), cos(uTime * 0.45f)) * 0.02f * strength;
    gp += globalJiggle * cells;

    float2 info = cellInfo(gp, cells);
    float minD = info.x;
    float edgeF = info.y;

    float2 n = fieldNormal(gp, cells);

    float facets = lerp(3.0f, 7.0f, saturate(strength * 0.5f));
    float ang = atan2(n.y, n.x);
    float q = floor((ang / 6.2831853f) * facets);
    float angQ = (q + 0.5f) * (6.2831853f / facets);
    n = float2(cos(angQ), sin(angQ));

    float centerInfluence = 1.0f - smoothstep(0.0f, 0.6f, minD * 2.0f);
    float edgeInfluence = pow(edgeF, 1.8f);
    float dispAmount = 0.0025f * lerp(0.6f, 2.2f, strength) * (0.6f + centerInfluence * 1.2f) * (0.4f + edgeInfluence * 1.6f);

    float2 disp = n * dispAmount;

    float2 jitter = (hash22(floor(gp)) - 0.5f) * 0.5f * dispAmount * 4.0f;
    disp += jitter;

    float2 uvR = saturate(uv + disp * 1.10f);
    float2 uvG = saturate(uv + disp * 0.00f);
    float2 uvB = saturate(uv + disp * -0.80f);

    float2 texel = 1.0f / uTexSize;
    uvR = floor(uvR * uTexSize) * texel + texel * 0.5f;
    uvG = floor(uvG * uTexSize) * texel + texel * 0.5f;
    uvB = floor(uvB * uTexSize) * texel + texel * 0.5f;

    float3 col;
    col.r = uTex.Sample(uSampler, uvR).r;
    col.g = uTex.Sample(uSampler, uvG).g;
    col.b = uTex.Sample(uSampler, uvB).b;

    float edgeHighlight = smoothstep(0.04f, 0.0f, minD) * pow(edgeF, 0.8f);
    float sparkle = pow(max(0.0f, 1.0f - minD * 6.0f), 3.0f) * (0.25f + 0.75f * edgeF);
    float shimmer = hash21(floor(gp) + float2(uTime * 2.0f, uTime * 2.0f));
    float glint = edgeHighlight * sparkle * (0.6f + 0.4f * shimmer);
    col += float3(1.0f, 0.95f, 0.85f) * glint * 0.9f * strength;

    float3 blurAccum = float3(0.0f, 0.0f, 0.0f);
    float w = 0.0f;
    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            float2 o = float2((float)x, (float)y) * texel;
            blurAccum += uTex.Sample(uSampler, saturate(uv + o + disp * 0.25f)).rgb;
            w += 1.0f;
        }
    }
    float3 localAvg = blurAccum / max(w, 1e-4f);
    float bleedMix = smoothstep(0.0f, 0.5f, minD) * 0.25f * strength;
    col = lerp(col, localAvg, bleedMix);

    float r = length((uv - 0.5f) * float2(1.0f, 1.0f));
    float vign = smoothstep(0.85f, 0.25f, r);
    col *= vign;

    float noiseVal = hash21(input.position.xy * 0.5f + float2(uTime * 12.0f, uTime * 12.0f));
    col += (noiseVal - 0.5f) * 0.01f * strength;

    col = softGamma(col);
    col = saturate(col);
    return float4(col, 1.0f);
}

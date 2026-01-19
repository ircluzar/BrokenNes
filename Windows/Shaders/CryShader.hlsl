// DisplayName: CRY
// CoreName: Crystalline
// Description: Faceted Voronoi-driven refraction with edge-weighted displacement, subtle dispersion, and gentle inter-shard bleed.
// Performance: -25
// Rating: 1
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

// Returns (nearest distance, edge sharpness proxy)
float2 cellInfo(float2 p)
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

float2 fieldNormal(float2 p)
{
    float eps = 0.65f;
    float d = cellInfo(p).x;
    float dx = cellInfo(p + float2(eps, 0.0f)).x - d;
    float dy = cellInfo(p + float2(0.0f, eps)).x - d;
    return normalize(float2(dx, dy) + 1e-6f);
}

float luma(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float strength = saturate(uStrength);
    float2 texel = 1.0f / uTexSize;

    float3 center = uTex.Sample(uSampler, uv).rgb;
    float Lc = luma(center);
    float Lx1 = luma(uTex.Sample(uSampler, saturate(uv + float2(texel.x, 0.0f))).rgb);
    float Lx0 = luma(uTex.Sample(uSampler, saturate(uv - float2(texel.x, 0.0f))).rgb);
    float Ly1 = luma(uTex.Sample(uSampler, saturate(uv + float2(0.0f, texel.y))).rgb);
    float Ly0 = luma(uTex.Sample(uSampler, saturate(uv - float2(0.0f, texel.y))).rgb);
    float gx = Lx1 - Lx0;
    float gy = Ly1 - Ly0;
    float edgeMag = saturate(length(float2(gx, gy)) * 4.0f);

    float minCells = 20.0f;
    float maxCells = 64.0f;
    float cells = lerp(minCells, maxCells, saturate(strength * 0.5f));
    float2 gp = uv * cells;

    float2 jiggle = float2(sin(uTime * 0.45f), cos(uTime * 0.33f)) * 0.02f * strength;
    gp += jiggle * cells;

    float2 info = cellInfo(gp);
    float minD = info.x;
    float edgeF = info.y;

    float2 n = fieldNormal(gp);
    float facets = lerp(4.0f, 9.0f, saturate(strength * 0.5f));
    float a = atan2(n.y, n.x);
    float q = floor((a / 6.2831853f) * facets);
    float aQ = (q + 0.5f) * (6.2831853f / facets);
    n = float2(cos(aQ), sin(aQ));

    float centerInfluence = 1.0f - smoothstep(0.0f, 0.6f, minD * 2.0f);
    float facetSharp = pow(edgeF, 1.6f);
    float dispBase = (0.0022f + 0.0018f * strength);
    float dispAmount = dispBase * (0.45f + 0.55f * edgeMag) * (0.6f + 1.4f * centerInfluence) * (0.5f + 1.2f * facetSharp);

    float2 jitter = (hash22(floor(gp)) - 0.5f) * dispBase * 1.8f;
    float2 disp = n * dispAmount + jitter;

    float2 uvR = saturate(uv + disp * 1.0f);
    float2 uvG = saturate(uv + disp * 0.85f);
    float2 uvB = saturate(uv + disp * 0.70f);

    uvR = (floor(uvR * uTexSize) + 0.5f) / uTexSize;
    uvG = (floor(uvG * uTexSize) + 0.5f) / uTexSize;
    uvB = (floor(uvB * uTexSize) + 0.5f) / uTexSize;

    float3 col;
    col.r = uTex.Sample(uSampler, uvR).r;
    col.g = uTex.Sample(uSampler, uvG).g;
    col.b = uTex.Sample(uSampler, uvB).b;

    float bleed = smoothstep(0.0f, 0.5f, minD) * 0.22f * strength;
    if (bleed > 0.0f)
    {
        float3 accB = float3(0.0f, 0.0f, 0.0f);
        float wsum = 0.0f;
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                float2 o = float2((float)x, (float)y) * texel;
                float w = 1.0f - 0.08f * (float)(x * x + y * y);
                accB += uTex.Sample(uSampler, saturate(uv + o + disp * 0.25f)).rgb * w;
                wsum += w;
            }
        }
        accB /= max(wsum, 1e-4f);
        col = lerp(col, accB, bleed);
    }

    float3 tint = float3(0.95f, 1.02f, 1.06f);
    float tintAmt = lerp(0.08f, 0.20f, saturate(strength * 0.5f));
    col = lerp(col, col * tint, tintAmt);

    float sparkle = pow(max(0.0f, 1.0f - minD * 5.0f), 3.0f) * (0.3f + 0.7f * edgeF);
    float twinkle = hash21(floor(gp) + float2(uTime * 1.7f, uTime * 1.7f));
    float glint = sparkle * (0.55f + 0.45f * twinkle) * 0.25f * strength;
    col += glint;

    col = saturate(col);
    return float4(col, 1.0f);
}

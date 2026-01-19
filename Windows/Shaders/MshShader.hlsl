// DisplayName: MSH
// CoreName: Pixel Mush 
// Description: Temporal block mosh using previous frame with motion/edge gating, chroma shear, and glitch envelopes.
// Performance: -19
// Rating: 4
// Category: Distort

cbuffer Constants : register(b0)
{
    float2 uTexSize;
    float uTime;
    float uStrength;
};

Texture2D<float4> uTex : register(t0);
Texture2D<float4> uPrevTex : register(t1);
SamplerState uSampler : register(s0);

struct PS_INPUT
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

float hash(float2 p)
{
    p = frac(p * float2(123.34f, 456.21f));
    p += dot(p, p + 45.32f);
    return frac(p.x * p.y);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0f - 2.0f * f);
    float a = hash(i);
    float b = hash(i + float2(1, 0));
    float c = hash(i + float2(0, 1));
    float d = hash(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float luma(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 prevUv = uv;
    float2 texel = 1.0f / uTexSize;
    float t = uTime;
    float s = saturate(uStrength);

    float3 cur = uTex.Sample(uSampler, uv).rgb;
    float Lc = luma(cur);
    float Lx1 = luma(uTex.Sample(uSampler, saturate(uv - float2(texel.x, 0.0f))).rgb);
    float Lx2 = luma(uTex.Sample(uSampler, saturate(uv + float2(texel.x, 0.0f))).rgb);
    float Ly1 = luma(uTex.Sample(uSampler, saturate(uv - float2(0.0f, texel.y))).rgb);
    float Ly2 = luma(uTex.Sample(uSampler, saturate(uv + float2(0.0f, texel.y))).rgb);
    float edge = saturate(length(float2(Lx2 - Lx1, Ly2 - Ly1)) * 2.2f);

    float seg = floor(t * 0.9f);
    float segT = frac(t * 0.9f);
    float chance = hash(float2(seg, 77.31f));
    float env = step(0.58f, chance) * smoothstep(0.05f, 0.22f, segT) * (1.0f - smoothstep(0.55f, 0.95f, segT));
    float glitch = env * (s / 3.0f);

    float baseBlock = lerp(12.0f, 6.0f, saturate(s * 0.5f));
    float wob = 1.0f + 0.02f * sin(t * 0.01f);
    float bSize = baseBlock * wob;
    float2 grid = uTexSize / bSize;
    float2 uvc = uv - 0.5f;
    float2 scaled = uvc * grid;
    float2 bCoord = floor(scaled);
    float2 bCenter = (bCoord + 0.5f) / grid + 0.5f;

    float bSeed = hash(bCoord + float2(seg, 19.7f));
    float3 prevHere = uPrevTex.Sample(uSampler, prevUv).rgb;
    float Lp = luma(prevHere);
    float motion = saturate(abs(Lc - Lp) * 4.0f);

    float span = 2.0f;
    float bestD = 1e9f;
    float2 bestOff = float2(0.0f, 0.0f);
    [unroll]
    for (int j = -1; j <= 1; j++)
    {
        for (int i = -1; i <= 1; i++)
        {
            float2 off = float2((float)i, (float)j) * texel * span;
            float2 prevOff = float2(off.x, -off.y);
            float3 p = uPrevTex.Sample(uSampler, saturate(prevUv + prevOff)).rgb;
            float d = abs(luma(p) - Lc);
            if (d < bestD)
            {
                bestD = d;
                bestOff = off;
            }
        }
    }

    float ang = hash(bCoord + float2(31.7f, 12.1f)) * 6.28318f;
    float2 dir = float2(cos(ang), sin(ang));
    float phase = hash(bCoord + float2(9.1f, 4.7f)) * 6.28318f;
    float wig = sin(t * 0.01f + phase);
    float amp = (0.08f + 0.12f * saturate(s * 0.5f));
    float2 drift = dir * texel * amp * wig;
    float2 jitter = (float2(hash(bCoord + 5.1f), hash(bCoord + 9.3f)) - 0.5f) * texel * 0.25f;
    float2 moshuv = bCenter + bestOff + drift + jitter;
    float2 delta = moshuv - uv;
    float3 moshCol = uPrevTex.Sample(uSampler, saturate(prevUv + float2(delta.x, -delta.y))).rgb;

    float2 pixUv = (floor(scaled) + 0.5f) / grid + 0.5f;
    float3 pixCol = uTex.Sample(uSampler, saturate(pixUv)).rgb;

    float gate = step(0.33f, bSeed + glitch * 0.75f - edge * 0.25f);
    float freeze = smoothstep(0.25f, 0.05f, motion);
    float pixW = lerp(0.10f, 0.35f, saturate(s));
    float moshW = gate * (0.35f + 0.65f * glitch) * (0.35f + 0.65f * s);
    moshW = saturate(moshW + freeze * 0.35f * s);

    float2 ch = float2(1.0f, -1.0f) * texel * (0.5f + 1.5f * s) * (0.2f + 0.8f * glitch);
    float3 shearPrev;
    shearPrev.r = uPrevTex.Sample(uSampler, saturate(prevUv + float2(delta.x + ch.x, -delta.y))).r;
    shearPrev.g = moshCol.g;
    shearPrev.b = uPrevTex.Sample(uSampler, saturate(prevUv + float2(delta.x + ch.y, -delta.y))).b;
    moshCol = lerp(moshCol, shearPrev, 0.5f);

    float3 col = cur;
    col = lerp(col, pixCol, pixW);
    col = lerp(col, moshCol, moshW);

    float2 cell = frac(scaled);
    float border = min(min(cell.x, 1.0f - cell.x), min(cell.y, 1.0f - cell.y));
    float ring = smoothstep(0.0f, 0.08f, border);
    col *= 0.92f + 0.08f * ring;

    float gn = noise(uv * uTexSize * 0.75f + t * 1.7f) - 0.5f;
    col += gn * 0.018f * (0.5f + 0.5f * s);
    col = saturate(col);

    return float4(col, 1.0f);
}

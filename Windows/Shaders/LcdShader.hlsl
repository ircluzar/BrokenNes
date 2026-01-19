// DisplayName: LCD
// CoreName: Aging LCD
// Description: Simulates old LCD traits: horizontal smear, subpixel ghosting, frost diffusion, banding, and grain.
// Performance: -16
// Rating: 4
// Category: Retro

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

float hash(float2 p)
{
    p = frac(p * float2(127.1f, 311.7f));
    p += dot(p, p + 19.19f);
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
    float2 texel = 1.0f / uTexSize;
    float t = uTime;
    float strength = saturate(uStrength);
    float smear = lerp(0.18f, 0.48f, strength);
    float ghost = lerp(0.10f, 0.32f, strength);
    float frost = lerp(0.10f, 0.32f, strength);

    float3 frostAccum = float3(0.0f, 0.0f, 0.0f);
    float wsum = 0.0f;
    [unroll]
    for (int i = 0; i < 7; i++)
    {
        float a = (float)i / 7.0f * 6.28318f + t * 0.13f;
        float r = (0.5f + 0.5f * noise(uv * float2(80.0f, 60.0f) + a + t * 0.7f)) * frost * 7.0f;
        float2 offs = float2(cos(a), sin(a)) * texel * r;
        float w = 1.0f - 0.12f * (float)i;
        frostAccum += uTex.Sample(uSampler, saturate(uv + offs)).rgb * w;
        wsum += w;
    }
    frostAccum /= max(wsum, 1e-4f);

    float3 smearAccum = float3(0.0f, 0.0f, 0.0f);
    wsum = 0.0f;
    [unroll]
    for (int j = -4; j <= 4; j++)
    {
        float fj = (float)j;
        float w = exp(-fj * fj * 0.18f);
        float2 offs = float2(fj * texel.x * smear * 2.5f, 0.0f);
        smearAccum += uTex.Sample(uSampler, saturate(uv - offs)).rgb * w;
        wsum += w;
    }
    smearAccum /= max(wsum, 1e-4f);

    float ghostPhase = sin(t * 0.7f + uv.y * 8.0f) * 0.5f + 0.5f;
    float2 ghostOff = float2(-ghost * ghostPhase * 2.0f * texel.x, ghost * ghostPhase * 1.2f * texel.y);
    float3 ghostCol = uTex.Sample(uSampler, saturate(uv + ghostOff)).rgb;
    float3 base = uTex.Sample(uSampler, uv).rgb;

    float3 col = lerp(base, frostAccum, frost * 0.7f);
    col = lerp(col, smearAccum, smear * 0.7f);
    col = lerp(col, ghostCol, ghost * 0.6f);

    float colBand = sin(uv.x * uTexSize.x * 3.14159f * 0.5f + t * 0.2f);
    col *= 0.97f + 0.03f * colBand;

    float grain = noise(uv * float2(320.0f, 240.0f) + t * 1.3f) - 0.5f;
    col += grain * 0.025f * (0.7f + 0.7f * strength);

    float L = luma(col);
    col = lerp(float3(L, L, L), col, 0.82f - 0.18f * strength);
    col = saturate(col);

    return float4(col, 1.0f);
}

// DisplayName: LSD
// CoreName: Psychedelic
// Description: Layered noise warps, waves, temporal swirl, and directional chromatic splits with episodic burst events.
// Performance: -20
// Rating: 5
// Category: Distort

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
    p = frac(p * float2(34.123f, 71.77f));
    p += dot(p, p + 23.19f);
    return frac(p.x * p.y);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0f - 2.0f * f);
    float a = hash(i);
    float b = hash(i + float2(1.0f, 0.0f));
    float c = hash(i + float2(0.0f, 1.0f));
    float d = hash(i + float2(1.0f, 1.0f));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0f / uTexSize;
    float t = uTime;
    float k = saturate(uStrength / 3.0f);

    float3 baseSample = uTex.Sample(uSampler, uv).rgb;

    float drift = sin(t * 0.20f) * 0.5f + 0.5f;
    float flowPhase = t * 0.35f;
    float2 warp1 = float2(noise(uv * 2.5f + flowPhase), noise(uv * 2.5f - flowPhase));
    float2 warp2 = float2(noise(uv * 5.0f - flowPhase * 0.55f), noise(uv * 5.0f + flowPhase * 0.42f));
    float2 warp = (warp1 * 0.6f + warp2 * 0.4f - 0.5f) * 0.0095f;

    float seg = floor(t * 0.18f);
    float segT = frac(t * 0.18f);
    float chance = hash(float2(seg, 99.7f));
    float eventFlag = step(0.82f, chance);
    float envelope = smoothstep(0.07f, 0.28f, segT) * (1.0f - smoothstep(0.55f, 0.92f, segT));
    float spazz = eventFlag * envelope;

    float bigWave = sin(uv.y * (12.0f + sin(t * 1.2f) * 6.0f) + t * 2.5f) * 0.045f * spazz * (0.6f + 0.4f * sin(t * 0.8f));
    float smallWave = sin(uv.y * 8.0f + t * 1.2f) * 0.003f + sin(uv.x * 6.0f - t * 0.9f) * 0.0025f;
    float wave = bigWave + smallWave;

    float bleedScale = 0.45f + 0.55f * drift;
    float2 dir1 = normalize(float2(0.6f, 0.8f));
    float2 dir2 = normalize(float2(-0.7f, 0.4f));
    float2 dir3 = normalize(float2(0.2f, -0.9f));

    float2 center = float2(0.5f, 0.5f) + (float2(sin(t * 0.18f), cos(t * 0.14f)) * 0.05f);
    float2 toC = uv - center;
    float r = length(toC);
    float rot = sin(t * 0.25f) * 0.4f;
    float angle = atan2(toC.y, toC.x) + rot * smoothstep(0.0f, 0.6f, r) * (1.0f - smoothstep(0.6f, 0.9f, r));
    float2 swirlUv = center + float2(cos(angle), sin(angle)) * r;

    float2 baseUv = swirlUv + warp + float2(wave * 0.25f, 0.0f);

    float2 offR = baseUv + dir1 * texel * (1.9f * bleedScale) + dir2 * texel * (sin(t * 1.3f + uv.y * 5.0f) * 0.9f);
    float2 offG = baseUv + dir2 * texel * (1.3f * bleedScale) + dir3 * texel * (sin(t * 1.1f + uv.x * 4.5f) * 0.8f);
    float2 offB = baseUv + dir3 * texel * (1.6f * bleedScale) + dir1 * texel * (sin(t * 1.6f + uv.y * 3.5f) * 0.75f);

    if (spazz > 0.0f)
    {
        float kk = spazz;
        offR += float2(sin(t * 12.0f + uv.y * 36.0f), cos(t * 10.0f + uv.x * 30.0f)) * texel * 11.0f * kk;
        offG += float2(sin(t * 11.0f + uv.y * 28.0f), sin(t * 9.0f + uv.x * 22.0f)) * texel * 9.0f * kk;
        offB += float2(cos(t * 12.5f + uv.y * 42.0f), sin(t * 14.0f + uv.x * 40.0f)) * texel * 13.0f * kk;
    }

    float3 acc = float3(0.0f, 0.0f, 0.0f);
    float wsum = 0.0f;
    [unroll]
    for (int i = -3; i <= 3; i++)
    {
        float fi = (float)i;
        float w = exp(-fi * fi * 0.18f);
        float2 offs = float2(fi * texel.x * 1.15f, fi * texel.y * 0.55f);
        acc.r += uTex.Sample(uSampler, saturate(offR + offs)).r * w;
        acc.g += uTex.Sample(uSampler, saturate(offG + offs * 0.9f)).g * w;
        acc.b += uTex.Sample(uSampler, saturate(offB + offs * 1.05f)).b * w;
        wsum += w;
    }
    float3 col = acc / max(wsum, 1e-4f);

    float3 vdiff = float3(0.0f, 0.0f, 0.0f);
    [unroll]
    for (int j = -2; j <= 2; j++)
    {
        float fj = (float)j;
        float w = exp(-fj * fj * 0.33f);
        float2 vOff = float2(0.0f, fj * texel.y * (0.9f + 0.5f * drift));
        vdiff += uTex.Sample(uSampler, saturate(baseUv + vOff)).rgb * w;
    }
    vdiff /= 2.5066f;
    col = lerp(col, vdiff, 0.32f + 0.22f * drift);

    float crA = sin(t * 0.55f) * 0.5f + 0.5f;
    float3x3 rotM = float3x3(
        0.65f + 0.35f * cos(t * 0.5f), 0.16f * sin(t * 0.7f), 0.20f * sin(t * 1.0f),
        0.20f * sin(t * 0.8f), 0.65f + 0.35f * cos(t * 0.45f + 2.0f), 0.16f * sin(t * 0.3f),
        0.16f * sin(t * 0.4f), 0.20f * sin(t * 0.6f + 1.5f), 0.65f + 0.35f * cos(t * 0.5f + 3.14f));
    col = lerp(col, saturate(mul(rotM, col)), 0.48f + 0.30f * crA);

    float l = dot(col, float3(0.299f, 0.587f, 0.114f));
    col = lerp(float3(l, l, l), col, 0.92f + 0.05f * sin(t * 1.1f));
    col = pow(col, float3(0.92f, 0.92f, 0.92f));
    col += float3(0.045f * sin(t * 2.4f + uv.y * 6.5f), 0.036f * sin(t * 2.0f + uv.x * 7.5f), 0.05f * sin(t * 2.2f + uv.y * 6.0f)) * 0.35f;

    float2 finalUv = saturate(baseUv + float2(wave * 0.33f, 0.0f));
    float3 detail = uTex.Sample(uSampler, finalUv).rgb;
    col = lerp(col, detail, 0.22f);

    col = saturate(col);

    // Strength scaling: allow dialing effect down toward base
    float mixAmt = lerp(0.35f, 1.0f, k);
    col = lerp(baseSample, col, mixAmt);

    return float4(col, 1.0f);
}

// DisplayName: SPK
// CoreName: Prism Sparkle
// Description: Edge-oriented prism split with layered starfield sparkles, radial depth modulation and shimmer.
// Performance: -21
// Rating: 4
// Category: Lighting

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
    float2 texel = 1.0f / uTexSize;
    float t = uTime;
    float inten = clamp(uStrength, 0.3f, 3.0f);

    float3 base = uTex.Sample(uSampler, uv).rgb;
    float3 cL = uTex.Sample(uSampler, saturate(uv - float2(texel.x, 0.0f))).rgb;
    float3 cR = uTex.Sample(uSampler, saturate(uv + float2(texel.x, 0.0f))).rgb;
    float3 cU = uTex.Sample(uSampler, saturate(uv - float2(0.0f, texel.y))).rgb;
    float3 cD = uTex.Sample(uSampler, saturate(uv + float2(0.0f, texel.y))).rgb;
    float dx = luma(cR) - luma(cL);
    float dy = luma(cD) - luma(cU);
    float edge = saturate(length(float2(dx, dy)) * 2.2f);
    float lum = luma(base);
    float burst = smoothstep(0.55f, 0.9f, lum) * (0.5f + 0.5f * edge);
    float2 gdir = normalize(float2(dx, dy) + 1e-5f);
    float2 ortho = float2(-gdir.y, gdir.x);

    float specAmp = (0.25f + 0.75f * edge) * (0.4f + 0.6f * inten);

    float3 prism;
    {
        float shift = 1.5f * specAmp;
        float2 offsR = uv + (gdir * shift + ortho * 0.7f * specAmp) * texel;
        float2 offsG = uv - (ortho * shift * 0.6f) * texel;
        float2 offsB = uv - (gdir * shift - ortho * 0.4f * specAmp) * texel;
        prism.r = uTex.Sample(uSampler, saturate(offsR)).r;
        prism.g = uTex.Sample(uSampler, saturate(offsG)).g;
        prism.b = uTex.Sample(uSampler, saturate(offsB)).b;
    }

    float3 sparkAccum = float3(0.0f, 0.0f, 0.0f);
    float wsum = 0.0f;
    [unroll]
    for (int layer = 0; layer < 3; layer++)
    {
        float lf = (float)layer;
        float scale = lerp(120.0f, 480.0f, lf / 2.0f);
        float speed = lerp(0.25f, 0.9f, lf / 2.0f);
        float2 suv = uv * scale + float2(noise(float2(lf * 13.7f, t * 0.11f)) * 40.0f, t * speed * scale);
        float jitter = (noise(float2(uv.y * scale * 0.37f + lf * 9.13f, t * 0.9f)) - 0.5f) * 2.0f;
        suv.x += jitter * 0.65f;
        float2 cell = floor(suv);
        float2 f = frac(suv);
        float rnd = hash(cell + lf * 17.31f);
        float spawn = step(0.93f - 0.4f * edge - 0.35f * lum, rnd);
        float2 starP = f - 0.5f;
        float d = length(starP);
        float star = pow(saturate(1.0f - d * 2.2f), 3.0f);
        float tw = 0.5f + 0.5f * sin(t * 12.0f + rnd * 40.0f + lf * 3.0f);
        float sparkle = spawn * star * tw;
        float3 scol = float3(
            0.6f + 0.4f * sin(rnd * 20.0f + lf * 0.7f + t * 3.0f),
            0.6f + 0.4f * sin(rnd * 25.0f + lf * 1.1f + t * 2.6f + 2.1f),
            0.6f + 0.4f * sin(rnd * 30.0f + lf * 1.7f + t * 2.9f + 4.2f));
        float weight = lerp(0.55f, 1.2f, lf / 2.0f);
        sparkAccum += scol * sparkle * weight;
        wsum += weight;
    }
    sparkAccum /= max(wsum, 0.001f);

    float radial = length(uv - 0.5f);
    float depthFactor = smoothstep(0.9f, 0.15f, radial);
    sparkAccum *= (0.6f + 0.8f * depthFactor);

    float wave = sin(t * 5.0f + lum * 15.0f + radial * 40.0f) * 0.5f + 0.5f;
    float burstEnv = burst * (0.35f + 0.65f * wave);
    float3 burstCol = prism * (0.4f + 0.6f * burstEnv);

    float3 col = base;
    col = lerp(col, prism, 0.35f + 0.25f * edge);
    col += sparkAccum * (0.55f + 0.35f * edge) * inten;
    col += burstCol * 0.35f * inten;

    float shimmer = noise(uv * float2(280.0f, 140.0f) + float2(t * 1.8f, t * 2.1f));
    col *= 0.85f + 0.15f * shimmer;
    col = pow(col, float3(0.95f, 0.95f, 0.95f));

    float lfin = luma(col);
    col = lerp(float3(lfin, lfin, lfin), col, 0.82f + 0.12f * edge);
    float baseLum = lum;
    float effLum = luma(col);
    float over = max(effLum - baseLum, 0.0f);
    float darkenFactor = 1.0f - 0.35f * saturate(over * 1.5f);
    col *= darkenFactor;
    col = saturate(col);

    float3 finalCol = lerp(col, base, 0.420f);
    return float4(finalCol, 1.0f);
}

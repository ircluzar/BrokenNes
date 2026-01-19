// DisplayName: TV
// CoreName: CRT Tube
// Description: Barrel distortion, shadow mask triads, beam persistence, convergence wobble, halo, vignette, and mild bleed.
// Performance: -23
// Rating: 5
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
    p = frac(p * float2(123.34, 415.21));
    p += dot(p, p + 19.19);
    return frac(p.x * p.y);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + float2(1.0, 0.0));
    float c = hash(i + float2(0.0, 1.0));
    float d = hash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float3 softGamma(float3 c)
{
    return pow(clamp(c, 0.0, 1.0), float3(0.85, 0.85, 0.85));
}

float2 barrel(float2 uv, float amt)
{
    float2 cc = uv * 2.0 - 1.0;
    float r2 = dot(cc, cc);
    float kx = amt * 0.60;
    float ky = amt * 0.40;
    cc.x *= 1.0 + kx * r2;
    cc.y *= 1.0 + ky * r2;
    float r4 = r2 * r2;
    cc *= 1.0 + 0.04 * amt * r4;
    return (cc * 0.5 + 0.5);
}

float3 shadowMask(float2 uv, float scanMix)
{
    float2 scale = float2(3.0, 2.0);
    float2 p = frac(uv * uTexSize / scale);
    float stripe = step(p.x, 1.0 / 3.0) * 1.0 + step(1.0 / 3.0, p.x) * step(p.x, 2.0 / 3.0) * 2.0 + step(2.0 / 3.0, p.x) * 3.0;
    float3 triad = (stripe == 1.0) ? float3(1.05, 0.35, 0.35) : (stripe == 2.0 ? float3(0.35, 1.05, 0.35) : float3(0.35, 0.35, 1.05));
    float slot = smoothstep(0.15, 0.0, abs(p.y - 0.5));
    triad *= lerp(0.55, 1.0, slot);
    return lerp(float3(0.9, 0.9, 0.9), triad, 0.65 * scanMix);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float strength = clamp(uStrength, 0.0, 3.0);
    float2 uv = input.texCoord;
    float barrelAmt = lerp(0.07, 0.18, clamp(strength - 0.5, 0.0, 1.0));
    float2 cuv = barrel(uv, barrelAmt);
    
    if (any(cuv < 0.0) || any(cuv > 1.0))
    {
        discard;
    }

    float2 texel = 1.0 / uTexSize;
    float conv = 0.25 * strength;
    float2 offR = float2(+conv * texel.x, 0.0);
    float2 offB = float2(-conv * 0.7 * texel.x, 0.0);
    float t = uTime;
    offR += float2(sin(t * 0.7) * 0.25, cos(t * 0.9) * 0.15) * texel * strength;
    offB += float2(cos(t * 0.65) * 0.20, sin(t * 0.75) * 0.18) * texel * strength;

    float3 base;
    base.r = uTex.Sample(uSampler, clamp(cuv + offR, 0.0, 1.0)).r;
    base.g = uTex.Sample(uSampler, cuv).g;
    base.b = uTex.Sample(uSampler, clamp(cuv + offB, 0.0, 1.0)).b;

    float frameHz = 60.0;
    float beamY = frac(t * frameHz);
    float dy = cuv.y - beamY;
    if (dy < -0.5) dy += 1.0;
    else if (dy > 0.5) dy -= 1.0;
    float timeSinceBeam = dy;
    float ts = (timeSinceBeam < 0.0) ? (-timeSinceBeam) : (1.0 - timeSinceBeam);
    float persistence = exp(-ts * lerp(5.0, 2.4, clamp(strength / 2.0, 0.0, 1.0)));
    float beamCore = exp(-pow(abs(dy) * uTexSize.y, 1.1) * 0.02 * (1.0 + strength));
    float beamGlow = exp(-pow(abs(dy) * uTexSize.y, 1.1) * 0.0025) * 0.6;
    float scanMix = clamp(beamCore * 1.2 + beamGlow, 0.0, 1.0);

    float linePhase = frac(cuv.y * uTexSize.y);
    float sraw = sin(3.14159265 * linePhase);
    float shaped = sraw * sraw;
    shaped = shaped * (3.0 - 2.0 * shaped);
    float scanStrength = lerp(0.06, 0.14, clamp(strength * 0.5, 0.0, 1.0));
    float scanMask = 1.0 - scanStrength * (1.0 - shaped);
    scanMask *= (0.995 + 0.005 * scanMix);

    float3 mask = shadowMask(cuv, scanMix);
    float3 col = base * mask * scanMask;

    float3 blurAccum = float3(0.0, 0.0, 0.0);
    float wsum = 0.0;
    [unroll]
    for (int x = -1; x <= 1; x++)
    {
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            float2 o = float2(float(x), float(y)) * texel;
            float w = (x == 0 && y == 0) ? 2.0 : 1.0;
            blurAccum += uTex.Sample(uSampler, clamp(cuv + o, 0.0, 1.0)).rgb * w;
            wsum += w;
        }
    }
    float3 prevApprox = blurAccum / wsum;
    float lPrev = dot(prevApprox, float3(0.299, 0.587, 0.114));
    prevApprox = lerp(float3(lPrev, lPrev, lPrev), prevApprox, 0.6);
    col = lerp(prevApprox, col, 0.5 + 0.5 * persistence);

    float3 halo = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float a = float(i) / 4.0 * 6.28318;
        float2 offs = float2(cos(a), sin(a)) * texel * 2.5;
        halo += uTex.Sample(uSampler, clamp(cuv + offs, 0.0, 1.0)).rgb;
    }
    halo /= 4.0;
    col += halo * 0.12 * strength;

    float r = length((cuv - 0.5) * float2(1.1, 1.25));
    float vign = smoothstep(0.85, 0.35, r);
    col *= vign;

    float3 bleed = float3(0.0, 0.0, 0.0);
    bleed.r = uTex.Sample(uSampler, clamp(cuv + float2(texel.x * 1.0, 0.0), 0.0, 1.0)).r;
    bleed.g = uTex.Sample(uSampler, clamp(cuv + float2(-texel.x * 1.0, 0.0), 0.0, 1.0)).g;
    bleed.b = uTex.Sample(uSampler, clamp(cuv + float2(texel.x * 0.5, 0.0), 0.0, 1.0)).b;
    col = lerp(col, bleed, 0.04 + 0.08 * strength);

    float n = noise(float2(input.position.xy * 0.25) + t * 1.3);
    col += (n - 0.5) * 0.02;
    col = softGamma(col);
    col *= float3(1.04, 1.02, 0.97);
    col *= 1.12;
    col = clamp(col, 0.0, 1.0);
    
    return float4(col, 1.0);
}

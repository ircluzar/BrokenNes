// DisplayName: VHS
// CoreName: Broken VCR
// Description: Emulates timebase errors, chroma/luma separation, bleed, hum bands, speckle/dropouts, scanlines, and halation.
// Performance: -35
// Rating: 1
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
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
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

float luma(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

float3 rgb2yuv(float3 c)
{
    float Y = dot(c, float3(0.299, 0.587, 0.114));
    float U = (c.b - Y) * 0.565;
    float V = (c.r - Y) * 0.713;
    return float3(Y, U, V);
}

float3 yuv2rgb(float3 yuv)
{
    float Y = yuv.x, U = yuv.y, V = yuv.z;
    float R = Y + 1.403 * V;
    float G = Y - 0.344 * U - 0.714 * V;
    float B = Y + 1.770 * U;
    return float3(R, G, B);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float2 texel = 1.0 / uTexSize;
    float t = uTime;
    float s = clamp(uStrength, 0.0, 3.0);

    float yLine = uv.y * uTexSize.y;

    // Timebase wobble: sinusoid + per-line jitter
    float wobble = sin(uv.y * 16.0 + t * 5.2) * (0.4 + 0.6 * sin(t * 0.9)) * texel.x * (1.2 + 2.6 * s);
    float lineJit = (hash(float2(floor(yLine), floor(t * 80.0))) - 0.5) * texel.x * (0.8 + 2.5 * s);

    // Top flagging (tape curl)
    float topMask = smoothstep(0.25, 0.0, uv.y);
    float flagSaw = (frac(uv.y * 220.0 + t * 28.0) - 0.5) * 2.0;
    float flag = (0.5 * flagSaw + 0.5 * sin(t * 6.0 + uv.y * 40.0)) * texel.x * (8.0 + 20.0 * s) * topMask;

    // Head switching noise near bottom
    float bottomMask = smoothstep(0.80, 0.98, uv.y);
    float headJit = (noise(float2(t * 26.0, yLine * 0.33)) - 0.5) * texel.x * (5.0 + 14.0 * s) * bottomMask;

    // Compose total horizontal displacement per line
    float xDisp = wobble + lineJit + flag + headJit;

    // Minor vertical jitter
    float yJit = (noise(float2(uv.x * 60.0 + t * 5.0, t * 3.3)) - 0.5) * texel.y * (0.4 + 2.2 * s);
    float2 baseUv = clamp(uv + float2(0.0, yJit), 0.0, 1.0);

    // Sample base color for luma (sharper)
    float3 baseCol = uTex.Sample(uSampler, clamp(baseUv + float2(xDisp, 0.0), 0.0, 1.0)).rgb;
    float Y = luma(baseCol);

    // Chroma sampling: low-pass horizontally + light vertical softness
    float chromaWidth = (1.5 + 3.5 * s);
    float chromaVy = (0.5 + 0.8 * s);
    float2 cuv = baseUv + float2(xDisp, 0.0);
    float U = 0.0, V = 0.0, wsum = 0.0;
    
    [unroll]
    for (int i = -3; i <= 3; i++)
    {
        float fi = float(i);
        float w = exp(-fi * fi / 6.0);
        float2 offs = float2(fi * texel.x * chromaWidth, fi == 0.0 ? 0.0 : sign(fi) * texel.y * chromaVy * 0.15);
        float3 c = uTex.Sample(uSampler, clamp(cuv + offs, 0.0, 1.0)).rgb;
        float3 yuv = rgb2yuv(c);
        U += yuv.y * w;
        V += yuv.z * w;
        wsum += w;
    }
    U /= max(wsum, 1e-4);
    V /= max(wsum, 1e-4);

    // Chroma phase wander and misregistration
    float phaseNoise = (noise(float2(yLine * 0.07, t * 0.7)) * 2.0 - 1.0);
    float phase = phaseNoise * (0.6 + 1.6 * s) + sin(t * 1.5 + yLine * 0.03) * (0.3 + 0.6 * s);
    float cp = cos(phase), sp = sin(phase);
    float U2 = U * cp - V * sp;
    float V2 = U * sp + V * cp;

    // Chroma crawl on edges
    float Yl = luma(uTex.Sample(uSampler, clamp(cuv - float2(texel.x, 0.0), 0.0, 1.0)).rgb);
    float Yr = luma(uTex.Sample(uSampler, clamp(cuv + float2(texel.x, 0.0), 0.0, 1.0)).rgb);
    float edge = clamp(abs(Yr - Yl) * 3.0, 0.0, 1.0);
    float crawl = sin((uv.x * uTexSize.x) * 3.14159265 + t * 9.0 + uv.y * 5.0);
    U2 += crawl * edge * (0.03 + 0.08 * s);
    V2 += sin((uv.x * uTexSize.x) * 2.2 + t * 7.0) * edge * (0.02 + 0.06 * s);

    // Recombine Y + altered chroma
    float3 yuv = float3(Y, U2, V2);
    float3 col = yuv2rgb(yuv);

    // Horizontal bleed / smear
    float bleed = lerp(0.15, 0.65, clamp(s / 3.0, 0.0, 1.0));
    float3 smear = float3(0.0, 0.0, 0.0);
    float wsumS = 0.0;
    
    [unroll]
    for (int j = -2; j <= 2; j++)
    {
        float fj = float(j);
        float w = exp(-fj * fj * 0.35);
        float3 c = uTex.Sample(uSampler, clamp(baseUv + float2(xDisp + fj * texel.x * (1.0 + 2.0 * bleed), 0.0), 0.0, 1.0)).rgb;
        smear += c * w;
        wsumS += w;
    }
    smear /= max(wsumS, 1e-4);
    col = lerp(col, smear, 0.35 + 0.35 * bleed);

    // AC hum bands
    float hum = 0.035 + 0.065 * s;
    float humBands = 1.0 + (sin(uv.y * 3.0 + t * 6.28318 * 0.5) * 0.07 + sin(uv.y * 7.0 - t * 3.14 * 0.28) * 0.04) * hum;
    col *= humBands;

    // White speckle dropouts
    float speckSeed = hash(float2(floor(input.position.y + t * 60.0), floor(input.position.x * 0.5)));
    float speck = step(0.9985 - 0.25 * s, speckSeed);
    if (speck > 0.0)
    {
        float spark = 0.6 + 0.4 * hash(input.position.xy + float2(t * 200.0, t * 200.0));
        col = lerp(col, float3(1.1, 1.1, 1.1) * spark, 0.65 + 0.30 * s);
    }

    // Horizontal dropout lines
    float lineSeed = hash(float2(floor(yLine), floor(t * 90.0) + 17.0));
    float lineGate = step(0.985 - 0.35 * s, lineSeed);
    if (lineGate > 0.0)
    {
        float mode = hash(float2(floor(yLine * 0.5), 9.1));
        float w = smoothstep(0.0, 0.2, abs(frac(uv.x * 5.0 + t * 2.0) - 0.5));
        float3 band = lerp(float3(0.0, 0.0, 0.0), float3(1.2, 1.2, 1.2), step(0.5, mode));
        col = lerp(col, band, (0.25 + 0.55 * s) * (0.7 + 0.3 * w));
    }

    // Head-switching noise band at bottom
    if (bottomMask > 0.0)
    {
        float n = noise(float2(uv.x * 400.0 + t * 120.0, uv.y * 200.0 + t * 30.0));
        float3 bw = float3(n * 1.5, n * 1.5, n * 1.5);
        float sw = smoothstep(0.80, 0.90, uv.y) * (0.6 + 0.4 * sin(t * 20.0));
        col = lerp(col, bw, sw * (0.35 + 0.45 * s));
    }

    // Scanline attenuation
    float phaseSL = frac(yLine);
    float scan = 0.84 + 0.16 * pow(sin(3.14159265 * phaseSL), 2.0);
    col *= (0.88 + 0.12 * scan);

    // Halation around brights
    float Yt = clamp(Y * 1.2, 0.0, 1.0);
    float haloAmt = smoothstep(0.65, 1.0, Yt) * (0.12 + 0.25 * s);
    float3 halo = float3(0.0, 0.0, 0.0);
    float wsumH = 0.0;
    
    [unroll]
    for (int k = -2; k <= 2; k++)
    {
        [unroll]
        for (int m = -1; m <= 1; m++)
        {
            float2 o = float2(float(k), float(m));
            float w = exp(-(o.x * o.x * 0.25 + o.y * o.y * 0.8));
            halo += uTex.Sample(uSampler, clamp(baseUv + float2(xDisp, 0.0) + o * texel * float2(2.0, 1.5), 0.0, 1.0)).rgb * w;
            wsumH += w;
        }
    }
    halo /= max(wsumH, 1e-4);
    col = lerp(col, halo, haloAmt);

    // Analog grain
    float grain = (noise(uv * float2(280.0, 240.0) + t * 2.8) - 0.5);
    float grain2 = (noise(uv * float2(90.0, 70.0) - t * 0.9) - 0.5);
    col += (grain * 0.06 + grain2 * 0.03) * (0.7 + 0.9 * s);

    // Vignette
    float r = length((uv - 0.5) * float2(1.06, 1.0));
    float vign = smoothstep(0.95, 0.40, r);
    col *= vign;

    col = clamp(col, 0.0, 1.0);
    return float4(col, 1.0);
}

// DisplayName: HUE
// CoreName: Hue Rotation
// Description: Invert hue with protection for near-gray and near-luma-extreme regions, then apply an ultra-slow rotation over time.
// Performance: -6
// Rating: 1
// Category: Color

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

static const float3 LUMA = float3(0.299f, 0.587f, 0.114f);

float3 rgb2hsl(float3 c)
{
    float maxc = max(max(c.r, c.g), c.b);
    float minc = min(min(c.r, c.g), c.b);
    float l = (maxc + minc) * 0.5f;
    float h = 0.0f;
    float s = 0.0f;
    if (maxc != minc)
    {
        float d = maxc - minc;
        s = d / (1.0f - abs(2.0f * l - 1.0f));
        if (maxc == c.r)
        {
            h = (c.g - c.b) / d + ((c.g < c.b) ? 6.0f : 0.0f);
        }
        else if (maxc == c.g)
        {
            h = (c.b - c.r) / d + 2.0f;
        }
        else
        {
            h = (c.r - c.g) / d + 4.0f;
        }
        h /= 6.0f;
    }
    return float3(h, s, l);
}

float hue2rgb(float p, float q, float t)
{
    if (t < 0.0f) t += 1.0f;
    if (t > 1.0f) t -= 1.0f;
    if (t < 1.0f / 6.0f) return p + (q - p) * 6.0f * t;
    if (t < 1.0f / 2.0f) return q;
    if (t < 2.0f / 3.0f) return p + (q - p) * (2.0f / 3.0f - t) * 6.0f;
    return p;
}

float3 hsl2rgb(float3 hsl)
{
    float h = hsl.x;
    float s = hsl.y;
    float l = hsl.z;
    if (s == 0.0f) return float3(l, l, l);
    float q = (l < 0.5f) ? l * (1.0f + s) : l + s - l * s;
    float p = 2.0f * l - q;
    float r = hue2rgb(p, q, h + 1.0f / 3.0f);
    float g = hue2rgb(p, q, h);
    float b = hue2rgb(p, q, h - 1.0f / 3.0f);
    return float3(r, g, b);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float3 c = uTex.Sample(uSampler, uv).rgb;

    float k = saturate(uStrength / 3.0f);
    if (k <= 0.0f)
    {
        return float4(c, 1.0f);
    }

    float3 hsl = rgb2hsl(c);

    float protectSat = 0.06f;
    float protectLum = 0.04f;

    float satMask = smoothstep(protectSat * 0.5f, protectSat * 1.5f, hsl.y);
    float lumEdge = min(hsl.z, 1.0f - hsl.z);
    float lumMask = smoothstep(protectLum * 0.5f, protectLum * 2.0f, lumEdge);
    float mask = satMask * lumMask * k;

    float invHue = frac(hsl.x + 0.5f);

    float period = 3600.0f;
    float delta = frac(uTime / period) * k;

    float finalHue = frac(invHue + delta);
    float outHue = lerp(hsl.x, finalHue, mask);

    float3 outHSL = float3(outHue, hsl.y, hsl.z);
    float3 outRGB = hsl2rgb(outHSL);

    return float4(saturate(outRGB), 1.0f);
}

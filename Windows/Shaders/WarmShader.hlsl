// DisplayName: WARM
// CoreName: Extra Warmth
// Description: Subtle warm color shift with soft contrast tilt and gentle green cross-talk while preserving luminance.
// Performance: -3
// Rating: 3
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

float luma(float3 c) { return dot(c, LUMA); }
float2 toUV(float3 c)
{
    float U = -0.169f * c.r - 0.331f * c.g + 0.500f * c.b;
    float V = 0.500f * c.r - 0.419f * c.g - 0.081f * c.b;
    return float2(U, V);
}
float3 fromYUV(float Y, float2 UV)
{
    float U = UV.x; float V = UV.y;
    float R = Y + 1.402f * V;
    float G = Y - 0.344136f * U - 0.714136f * V;
    float B = Y + 1.772f * U;
    return float3(R, G, B);
}

float3 warmify(float3 c, float amt)
{
    float Y = luma(c);
    float2 UV = toUV(c);
    float mid = smoothstep(0.08f, 0.92f, Y);
    float k = amt * (0.7f + 0.3f * (1.0f - abs(mid - 0.5f) * 2.0f));
    UV.x -= 0.06f * k;
    UV.y += 0.06f * k;

    float3 rgb = fromYUV(Y, UV);

    float3 g = float3(1.0f / (1.0f + 0.06f * amt), 1.0f / (1.0f + 0.04f * amt), 1.0f / (1.0f + 0.02f * amt));
    rgb = pow(saturate(rgb), g);

    rgb.g = lerp(rgb.g, (rgb.g * 0.92f + rgb.r * 0.08f), 0.25f * amt);

    return saturate(rgb);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float3 col = uTex.Sample(uSampler, saturate(uv)).rgb;

    float s = saturate(uStrength) / 3.0f;
    float amt = lerp(0.15f, 0.50f, s);

    float3 outCol = warmify(col, amt);
    return float4(outCol, 1.0f);
}

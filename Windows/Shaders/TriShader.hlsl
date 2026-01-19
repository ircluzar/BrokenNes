// DisplayName: TRI
// CoreName: Faux Extrusion
// Description: Height-from-luma pseudo 3D with animated parallax, rim lighting, borders, AO and subtle noise.
// Performance: -11
// Rating: 2
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

float luma(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }
float hash(float2 p) { p = frac(p * float2(137.13f, 317.77f)); p += dot(p, p + 23.7f); return frac(p.x * p.y); }
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

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float t = uTime;
    float strength = saturate(uStrength);
    float2 texel = 1.0f / uTexSize;
    float3 base = uTex.Sample(uSampler, uv).rgb;

    float hRaw = luma(base * float3(1.05f, 1.0f, 0.95f));
    float height = pow(hRaw, 0.85f) * (0.55f + 1.45f * strength);
    float hL = luma(uTex.Sample(uSampler, saturate(uv - float2(texel.x, 0.0f))).rgb);
    float hR = luma(uTex.Sample(uSampler, saturate(uv + float2(texel.x, 0.0f))).rgb);
    float hU = luma(uTex.Sample(uSampler, saturate(uv - float2(0.0f, texel.y))).rgb);
    float hD = luma(uTex.Sample(uSampler, saturate(uv + float2(0.0f, texel.y))).rgb);
    float scale = (0.9f + 1.2f * strength);
    float dx = (hR - hL) * scale;
    float dy = (hD - hU) * scale;
    float3 normal = normalize(float3(-dx, -dy, 0.75f + 0.35f * strength));

    float orbit = t * (0.15f + 0.01f * strength);
    float2 camDir = normalize(float2(sin(orbit), 0.3f + 0.7f * cos(orbit * 0.73f)));
    float3 lightDir = normalize(float3(sin(t * 0.6f) * 0.6f, 0.55f + 0.25f * sin(t * 0.37f + 1.7f), 1.2f));
    float diff = saturate(dot(normal, lightDir));
    float rim = pow(1.0f - saturate(dot(normal, normalize(float3(camDir, 0.8f)))), 3.0f);
    float ambient = 0.30f + 0.10f * strength;
    float extrude = (0.7f + 1.6f * strength);
    float2 parallax = -camDir * height * extrude * texel * (1.0f + 0.25f * sin(t * 0.9f + hRaw * 6.0f));
    parallax.y += sin(t * 0.8f + uv.x * 10.0f) * texel.y * 0.15f * strength * height;
    float3 topCol = uTex.Sample(uSampler, saturate(uv + parallax)).rgb;
    float hForward = luma(uTex.Sample(uSampler, saturate(uv + camDir * texel)).rgb) * (0.55f + 1.45f * strength);
    float sideVis = saturate((height - hForward) * 4.0f);
    float3 sideShade = base * (ambient * 0.45f + diff * 0.25f) * float3(0.85f, 0.90f, 1.05f);
    float3 litTop = topCol * (ambient + diff * 0.95f) + rim * 0.15f * float3(1.2f, 1.1f, 1.05f);
    float3 col = lerp(litTop, sideShade, sideVis);

    float2 cell = uv * uTexSize;
    float2 g = frac(cell);
    float lineW = lerp(0.11f, 0.20f, saturate(strength / 3.0f));
    float border = step(g.x, lineW) + step(g.y, lineW) + step(1.0f - lineW, g.x) + step(1.0f - lineW, g.y);
    border = saturate(border);
    col = lerp(col, col * 0.35f, border * (0.55f + 0.35f * strength));

    float nhAvg = (hL + hR + hU + hD) * 0.25f;
    float ao = clamp(1.0f - (height - nhAvg) * 1.4f, 0.3f, 1.0f);
    col *= ao;

    float l = luma(col);
    float satBoost = 0.35f + 0.25f * strength;
    col = lerp(float3(l, l, l), col, 1.0f + satBoost);
    col *= float3(1.04f, 1.02f, 1.06f);
    float gn = noise(uv * uTexSize * 0.75f + t * 1.7f) - 0.5f;
    col += gn * 0.03f * (0.5f + 0.5f * strength);
    col = saturate(col);

    return float4(col, 1.0f);
}

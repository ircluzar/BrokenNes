// DisplayName: BUMP
// CoreName: Pseudo Bump
// Description: Derives a height field from color and shades with an animated light for a relief-lit look.
// Performance: -8
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

float3 rgb2hsv(float3 c)
{
    float cMax = max(c.r, max(c.g, c.b));
    float cMin = min(c.r, min(c.g, c.b));
    float d = cMax - cMin;
    float h = 0.0;
    
    if (d > 1e-6)
    {
        if (cMax == c.r)
            h = fmod((c.g - c.b) / d, 6.0);
        else if (cMax == c.g)
            h = (c.b - c.r) / d + 2.0;
        else
            h = (c.r - c.g) / d + 4.0;
        h /= 6.0;
        if (h < 0.0) h += 1.0;
    }
    
    float s = cMax <= 1e-6 ? 0.0 : (d / cMax);
    return float3(h, s, cMax);
}

float hueBump(float3 hsv)
{
    float h = hsv.x;
    float s = hsv.y;
    return (sin(h * 6.2831853) * 0.5 + 0.5) * s;
}

float heightFromColor(float3 rgb)
{
    float3 hsv = rgb2hsv(rgb);
    return hsv.z * 0.82 + hueBump(hsv) * 0.18;
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    
    // Base color
    float3 col = uTex.Sample(uSampler, uv).rgb;
    
    // Neighbor sampling step
    float2 texel = float2(1.0 / uTexSize.x, 1.0 / uTexSize.y);
    
    // Sample neighbor heights
    float hC = heightFromColor(col);
    float hL = heightFromColor(uTex.Sample(uSampler, uv - float2(texel.x, 0.0)).rgb);
    float hR = heightFromColor(uTex.Sample(uSampler, uv + float2(texel.x, 0.0)).rgb);
    float hD = heightFromColor(uTex.Sample(uSampler, uv + float2(0.0, texel.y)).rgb);
    float hU = heightFromColor(uTex.Sample(uSampler, uv - float2(0.0, texel.y)).rgb);
    
    // Gradient (central differences)
    float dx = (hR - hL);
    float dy = (hU - hD);
    
    // Strength mapping to bump scale
    float s = uStrength;
    if (!(s > 0.0)) s = 1.0;
    float bumpScale = lerp(1.2, 7.0, clamp(s, 0.2, 3.0) / 3.0);
    
    // Build perturbed normal
    float3 n = normalize(float3(-dx * bumpScale, -dy * bumpScale, 1.0));
    
    // Animated light direction
    float a = uTime * 0.35;
    float3 L = normalize(float3(0.6 * cos(a) - 0.3, 0.6 * sin(a) + 0.5, 0.8));
    float3 V = float3(0.0, 0.0, 1.0);
    float3 H = normalize(L + V);
    
    // Shading terms
    float diff = max(dot(n, L), 0.0);
    float spec = pow(max(dot(n, H), 0.0), 24.0);
    
    // Make specular depend on saturation
    float sat = rgb2hsv(col).y;
    float ambient = 0.35;
    float kd = 0.9;
    float ks = lerp(0.05, 0.25, sat);
    
    // Compose lit color
    float3 lit = col * (ambient + kd * diff) + float3(1.0, 1.0, 1.0) * (ks * spec);
    lit = clamp(lit, 0.0, 1.0);
    return float4(lit, 1.0);
}

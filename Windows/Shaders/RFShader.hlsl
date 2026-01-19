
// DisplayName: RF
// CoreName: Analog RF
// Description: Mild analog RF simulation with chroma misalignment, horizontal blur, ripple jitter and shimmer noise.
// Performance: -7
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
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float randLine(float y)
{
    return hash(float2(floor(y), 0.0));
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float s = uStrength;
    float px = 1.0 / uTexSize.x;
    float chroma = (0.7 + 1.1 * (s - 1.0)) * px;
    float blurAmt = lerp(0.42, 0.78, clamp(s - 1.0, 0.0, 1.0));
    
    // Chroma split
    float r = uTex.Sample(uSampler, uv + float2(chroma, 0.0)).r;
    float g = uTex.Sample(uSampler, uv).g;
    float b = uTex.Sample(uSampler, uv - float2(chroma, 0.0)).b;
    float3 base = float3(r, g, b);
    
    float3 c1 = uTex.Sample(uSampler, uv + float2(-2.0 * px, 0.0)).rgb;
    float3 c2 = uTex.Sample(uSampler, uv + float2(-px, 0.0)).rgb;
    float3 c3 = uTex.Sample(uSampler, uv + float2(px, 0.0)).rgb;
    float3 c4 = uTex.Sample(uSampler, uv + float2(2.0 * px, 0.0)).rgb;
    
    // Horizontal blur
    float3 blur = (c1 * 0.05 + c2 * 0.20 + base * 0.50 + c3 * 0.20 + c4 * 0.05);
    float3 col = lerp(base, blur, blurAmt);
    
    // Vertical jitter
    float jitter = (hash(float2(floor(uTime * 90.0), 0.0)) - 0.5) * (1.0 / uTexSize.y) * (0.6 + 0.6 * s);
    col = lerp(col, uTex.Sample(uSampler, uv + float2(0.0, jitter)).rgb, 0.18);
    
    float linePos = uv.y * uTexSize.y;
    float seg = floor(uTime / 4.0);
    float tt = frac(uTime / 4.0);
    float r1h = hash(float2(seg, 17.0));
    float r2h = hash(float2(seg + 1.0, 17.0));
    float a1 = pow(r1h, 2.2);
    float a2 = pow(r2h, 2.2);
    float rippleScale = lerp(a1, a2, smoothstep(0.0, 1.0, tt));
    float rippleAmp = 0.4 * s * rippleScale;
    
    // Ripple displacement
    float ripple = sin(linePos * 0.18 + uTime * 9.5) * px * rippleAmp;
    col = lerp(col, uTex.Sample(uSampler, uv + float2(ripple, 0.0)).rgb, 0.22);
    
    // Shimmer line jitter
    float shimmer = (randLine(linePos + floor(uTime * 85.0)) - 0.5) * px * 1.5 * s;
    col = lerp(col, uTex.Sample(uSampler, uv + float2(shimmer, 0.0)).rgb, 0.35);
    
    float n = hash(floor(uv * uTexSize) + uTime * 2.2);
    col += (n - 0.5) * 0.018 * (0.7 + 1.6 * (s - 1.0));
    
    float l = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(float3(l, l, l), col, 0.95);
    col = (col - 0.5) * 1.10 + 0.5;
    col = clamp(col, 0.0, 1.0);
    
    return float4(col, 1.0);
}

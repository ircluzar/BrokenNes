// DisplayName: PX
// CoreName: Passthrough
// Description: Identity shader for testing and baseline comparison; flips Y to match NES texture orientation.
// Performance: 0
// Rating: 3
// Category: Utility

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

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    return uTex.Sample(uSampler, uv);
}

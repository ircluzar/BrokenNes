// DisplayName: MUSK
// CoreName: Mars Horizon
// Description: Adds a starry space background with red Martian tint and subtle atmospheric distortion.
// Performance: -10
// Rating: 4
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

float noise(float2 p)
{
    return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float s = clamp(uStrength, 0.0, 3.0);

    // Subtle atmospheric distortion
    float2 distort = float2(
        sin(uv.y * 10.0 + uTime * 0.5) * 0.001 * s,
        cos(uv.x * 8.0 + uTime * 0.3) * 0.001 * s
    );
    float2 distortedUV = clamp(uv + distort, 0.0, 1.0);
    float3 col = uTex.Sample(uSampler, distortedUV).rgb;

    // Stars: grid-based pseudo-random points
    float2 starUV = uv * 50.0;
    float2 starPos = floor(starUV);

    float star = 0.0;
    [unroll]
    for (int yi = -1; yi <= 1; yi++)
    {
        [unroll]
        for (int xi = -1; xi <= 1; xi++)
        {
            float2 cell = starPos + float2(float(xi), float(yi));
            float n = noise(cell);
            float2 starCenter = cell + float2(0.5, 0.5);
            float dist = distance(starUV, starCenter);
            float twinkle = 0.5 + 0.5 * sin(uTime * 2.0 + n * 100.0);
            float starContrib = (1.0 - smoothstep(0.0, 0.1, dist)) * twinkle * 0.3 * s;
            star += starContrib * step(0.98, n);
        }
    }

    // Martian tint
    float3 tint = float3(1.1, 0.9, 0.8);
    float tintAmount = clamp(0.2 + 0.3 * s, 0.0, 1.0);
    col = lerp(col, col * tint, tintAmount);

    // Add star glow
    col += float3(star, star, star);

    col = clamp(col, 0.0, 1.0);
    return float4(col, 1.0);
}

// DisplayName: WTR
// CoreName: Water Ripples
// Description: Compound vector-field warping with vertical/horizontal beams, sine wobbles and radial lenses, plus chromatic shear.
// Performance: -50
// Rating: 1
// Category: Distort

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

static const int COUNT = 69;
static const int VCOUNT = 34;
static const int HCOUNT = 34;

float hash(float2 p)
{
    p = frac(p * float2(131.17f, 415.97f));
    p += dot(p, p + 19.31f);
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

float2 vBeamField(float2 uv, float xPos, float t, float strength, float seed)
{
    float tilt = (hash(float2(seed, 37.2f)) * 2.0f - 1.0f) * 0.55f + sin(t * 0.2f + seed * 50.0f) * 0.08f;
    float2 dirLine = normalize(float2(sin(tilt), cos(tilt)));
    float2 anchor = float2(xPos, 0.5f);
    float2 rel = uv - anchor;
    float2 perp = float2(-dirLine.y, dirLine.x);
    float d = abs(dot(perp, rel));
    float fall = exp(-pow(d * uTexSize.x * (0.8f + 0.6f * strength), 1.15f));
    float jitter = (noise(float2(seed * 17.0f, uv.y * 40.0f + t * 3.0f)) - 0.5f);
    float ang = noise(float2(uv.y * 60.0f + seed * 11.0f, t * 0.7f + seed * 3.0f)) * 6.28318f;
    float2 swirl = float2(cos(ang), sin(ang));
    float proj = dot(rel, dirLine);
    float2 nearest = anchor + dirLine * proj;
    float2 toLine = nearest - uv;
    float2 dir = normalize(toLine + swirl * 0.25f + float2(0.0f, 0.001f + 0.05f * sin(t * 0.9f + uv.y * 7.0f + seed)));
    float mag = fall * (0.15f + 0.85f * strength) * (0.6f + 0.4f * sin(t * 2.2f + seed * 10.0f));
    mag += jitter * 0.06f * strength;
    return dir * mag;
}

float2 hBeamField(float2 uv, float yPos, float t, float strength, float seed)
{
    float tilt = (hash(float2(seed, 73.9f)) * 2.0f - 1.0f) * 0.55f + sin(t * 0.22f + seed * 47.0f) * 0.08f;
    float2 dirLine = normalize(float2(cos(tilt), sin(tilt)));
    float2 anchor = float2(0.5f, yPos);
    float2 rel = uv - anchor;
    float2 perp = float2(-dirLine.y, dirLine.x);
    float d = abs(dot(perp, rel));
    float fall = exp(-pow(d * uTexSize.y * (0.8f + 0.6f * strength), 1.15f));
    float jitter = (noise(float2(seed * 23.0f, uv.x * 40.0f + t * 2.7f)) - 0.5f);
    float ang = noise(float2(uv.x * 55.0f + seed * 9.0f, t * 0.65f + seed * 4.0f)) * 6.28318f;
    float2 swirl = float2(cos(ang), sin(ang));
    float proj = dot(rel, dirLine);
    float2 nearest = anchor + dirLine * proj;
    float2 toLine = nearest - uv;
    float2 dir = normalize(toLine + swirl * 0.25f + float2(0.001f + 0.05f * sin(t * 0.85f + uv.x * 6.5f + seed), 0.0f));
    float mag = fall * (0.15f + 0.85f * strength) * (0.6f + 0.4f * sin(t * 2.0f + seed * 12.0f));
    mag += jitter * 0.06f * strength;
    return dir * mag;
}

float2 wiggleField(float2 uv, float baseY, float t, float strength, float seed)
{
    float phase = t * (0.4f + 0.3f * frac(seed));
    float curve = sin(uv.x * (6.0f + 4.0f * frac(seed * 13.0f)) + phase + seed * 20.0f) * 0.03f * (0.6f + 0.4f * strength);
    float yLine = baseY + curve;
    float d = abs(uv.y - yLine);
    float fall = exp(-pow(d * uTexSize.y * (1.0f + 0.5f * strength), 1.1f));
    float shift = sin(uv.y * 50.0f + phase * 6.0f + seed * 30.0f) * 0.5f + 0.5f;
    float2 dir = normalize(float2((shift - 0.5f) * 0.3f, yLine - uv.y));
    float mag = fall * (0.1f + 0.9f * strength) * (0.5f + 0.5f * sin(t * 3.0f + seed * 40.0f));
    return dir * mag;
}

float2 lensField(float2 uv, float2 c, float t, float strength, float seed)
{
    float2 toC = uv - c;
    float r = length(toC);
    float radius = 0.08f + 0.12f * frac(seed * 7.0f);
    float edge = r / radius;
    if (edge > 1.8f) return float2(0.0f, 0.0f);
    float core = exp(-edge * edge * (1.5f + 0.8f * strength));
    float mode = (frac(seed * 5.0f) > 0.5f) ? 1.0f : -1.0f;
    float swirlA = noise(float2(seed * 100.0f + r * 120.0f, t * 1.3f)) * 6.28318f;
    float2 swirl = float2(cos(swirlA), sin(swirlA));
    float2 dir = normalize(toC + swirl * 0.3f);
    float mag = mode * core * (0.2f + 0.8f * strength) * (0.6f + 0.4f * sin(t * 1.7f + seed * 60.0f));
    return dir * mag;
}

float3 grade(float3 c, float strength)
{
    float L = luma(c);
    c = lerp(float3(L, L, L), c, 0.55f - 0.25f * saturate(strength * 0.4f));
    c *= float3(0.95f, 1.03f, 0.92f);
    c = pow(c + 0.02f, float3(0.95f, 0.95f, 0.95f));
    return saturate(c);
}

float4 main(PS_INPUT input) : SV_TARGET
{
    float2 uv = input.texCoord;
    float t = uTime;
    float strength = saturate(uStrength);
    float2 disp = float2(0.0f, 0.0f);

    [loop]
    for (int i = 0; i < VCOUNT; i++)
    {
        float fi = (float)i;
        float seed = fi / (float)VCOUNT;
        float speed = 0.03f + 0.07f * frac(hash(float2(seed, 12.3f)));
        float xPos = frac(seed + t * speed + 0.15f * sin(t * 0.11f + seed * 10.0f));
        disp += vBeamField(uv, xPos, t, strength, seed);
    }

    [loop]
    for (int i = 0; i < HCOUNT; i++)
    {
        float fi = (float)i;
        float seed = fi / (float)HCOUNT;
        float speed = 0.025f + 0.065f * frac(hash(float2(seed, 41.7f)));
        float yPos = frac(seed * 0.73f + t * speed + 0.12f * sin(t * 0.13f + seed * 14.0f));
        disp += hBeamField(uv, yPos, t, strength, seed);
    }

    [loop]
    for (int i = 0; i < COUNT; i++)
    {
        float fi = (float)i;
        float seed = fi / (float)COUNT;
        float baseY = frac(seed + sin(t * 0.07f + seed * 9.0f) * 0.05f + t * (0.01f + 0.02f * frac(seed * 17.0f)));
        disp += wiggleField(uv, baseY, t, strength, seed);
    }

    [loop]
    for (int i = 0; i < COUNT; i++)
    {
        float fi = (float)i;
        float seed = fi / (float)COUNT;
        float ang = seed * 6.28318f + t * (0.05f + 0.1f * frac(seed * 29.0f));
        float rad = 0.18f + 0.32f * frac(seed * 37.0f);
        float2 center = float2(0.5f, 0.5f) + float2(cos(ang), sin(ang)) * rad;
        center += float2(noise(float2(seed * 81.0f, t * 0.5f)) - 0.5f, noise(float2(seed * 91.0f, t * 0.5f + 10.0f)) - 0.5f) * 0.08f;
        disp += lensField(uv, center, t, strength, seed);
    }

    float maxLen = 5.0f + 30.0f * strength;
    float len = length(disp);
    if (len > 1e-5f)
    {
        float clampLen = min(len, maxLen);
        disp = disp / len * clampLen;
    }

    float2 texel = 1.0f / uTexSize;
    float2 dUV = disp * texel * (1.5f + 3.5f * strength);
    float2 dir = (length(dUV) > 0.0f) ? normalize(dUV) : float2(1.0f, 0.0f);
    float shear = (0.4f + 0.8f * strength) * length(dUV);
    float2 rOff = dUV + dir * shear * 0.40f;
    float2 gOff = dUV;
    float2 bOff = dUV - dir * shear * 0.40f;

    float3 col;
    col.r = uTex.Sample(uSampler, saturate(uv + rOff)).r;
    col.g = uTex.Sample(uSampler, saturate(uv + gOff)).g;
    col.b = uTex.Sample(uSampler, saturate(uv + bOff)).b;

    float sparkle = noise(uv * float2(300.0f, 260.0f) + t * 3.0f) - 0.5f;
    col += sparkle * 0.05f * strength;
    float pulse = 0.5f + 0.5f * sin(t * 2.0f);
    col *= 0.9f + 0.1f * pulse;
    col = grade(col, strength);
    col = saturate(col);

    return float4(col, 1.0f);
}

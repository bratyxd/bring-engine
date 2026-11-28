#pragma pack_matrix(column_major)

// Bring Engine — GPU-Instanced SoA Billboard Particle Shader (with Atlas Flipbook Cross-Frame Blending)
struct ParticleGpuInstance
{
    float4 PosSize;    // xyz = world position, w = quad size
    float4 Color;      // rgba
    float4 UvRect;     // (u0, v0, uW, vH) current frame
    float4 RotExtra;   // x = rotation rad, y = stretchX, z = frameBlend [0..1], w = unused
    float4 UvRectNext; // (u0, v0, uW, vH) next frame for cross-frame blend
};

StructuredBuffer<ParticleGpuInstance> Particles : register(t0, space0);

cbuffer CameraUniformsVS : register(b0, space1)
{
    float4x4 ViewProj;
    float4 CameraRightTime; // xyz = camera right, w = time
    float4 CameraUpPad;     // xyz = camera up, w = pad
};

cbuffer BatchUniformsVS : register(b1, space1)
{
    uint BaseInstance;
    uint3 _BatchPad;
};

struct PSInput
{
    float4 ClipPos    : SV_Position;
    float2 Uv         : TEXCOORD0;
    float4 Color      : TEXCOORD1;
    float2 UvNext     : TEXCOORD2;
    float  FrameBlend : TEXCOORD3;
};

static const float2 QuadOffsets[4] = {
    float2(-0.5, -0.5),
    float2( 0.5, -0.5),
    float2( 0.5,  0.5),
    float2(-0.5,  0.5)
};

static const float2 QuadUvs[4] = {
    float2(0.0, 1.0),
    float2(1.0, 1.0),
    float2(1.0, 0.0),
    float2(0.0, 0.0)
};

PSInput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    ParticleGpuInstance p = Particles[BaseInstance + instanceId];
    float2 o = QuadOffsets[vertexId & 3u];
    float2 qUv = QuadUvs[vertexId & 3u];

    float c = cos(p.RotExtra.x);
    float s = sin(p.RotExtra.x);
    float2 scaledO = o;
    if (p.RotExtra.y > 0.5)
    {
        float stretch = p.RotExtra.y > 1.1 ? p.RotExtra.y : 2.5;
        scaledO.x *= stretch;
        scaledO.y *= 0.5;
    }
    float2 rotO = float2(scaledO.x * c - scaledO.y * s, scaledO.x * s + scaledO.y * c) * p.PosSize.w;

    float3 worldPos = p.PosSize.xyz + CameraRightTime.xyz * rotO.x + CameraUpPad.xyz * rotO.y;

    PSInput outV;
    outV.ClipPos = mul(ViewProj, float4(worldPos, 1.0));
    outV.Uv = p.UvRect.xy + qUv * p.UvRect.zw;
    outV.Color = p.Color;
    outV.UvNext = p.UvRectNext.xy + qUv * p.UvRectNext.zw;
    outV.FrameBlend = saturate(p.RotExtra.z);
    return outV;
}

SamplerState PartSamp : register(s0, space2);
Texture2D PartTex : register(t0, space2);

float4 PSMain(PSInput input) : SV_Target0
{
    float4 tex = PartTex.Sample(PartSamp, input.Uv);
    if (input.FrameBlend > 0.001)
    {
        float4 texNext = PartTex.Sample(PartSamp, input.UvNext);
        tex = lerp(tex, texNext, input.FrameBlend);
    }
    float4 col = tex * input.Color;
    if (col.a <= 0.004)
        discard;
    return col;
}

#pragma pack_matrix(column_major)

// Bring Engine — GPU-Instanced SoA Billboard Particle Shader
struct ParticleGpuInstance
{
    float4 PosSize;   // xyz = world position, w = quad size
    float4 Color;     // rgba
    float4 UvRect;    // (u0, v0, uW, vH)
    float4 RotExtra;  // x = rotation rad, y = stretchX, z = unused, w = unused
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
    float4 ClipPos : SV_Position;
    float2 Uv      : TEXCOORD0;
    float4 Color   : TEXCOORD1;
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
    float2 rotO = float2(o.x * c - o.y * s, o.x * s + o.y * c) * p.PosSize.w;

    float3 worldPos = p.PosSize.xyz + CameraRightTime.xyz * rotO.x + CameraUpPad.xyz * rotO.y;

    PSInput outV;
    outV.ClipPos = mul(ViewProj, float4(worldPos, 1.0));
    outV.Uv = p.UvRect.xy + qUv * p.UvRect.zw;
    outV.Color = p.Color;
    return outV;
}

SamplerState PartSamp : register(s0, space2);
Texture2D PartTex : register(t0, space2);

float4 PSMain(PSInput input) : SV_Target0
{
    float4 tex = PartTex.Sample(PartSamp, input.Uv);
    float4 col = tex * input.Color;
    if (col.a <= 0.004)
        discard;
    return col;
}

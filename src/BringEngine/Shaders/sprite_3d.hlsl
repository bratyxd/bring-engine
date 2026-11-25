#pragma pack_matrix(column_major)

// Bring Engine — Unity-Style 2D Sprites in 3D World & 2D Screen HUD (Instanced Quad)
struct SpriteGpuInstance
{
    float4x4 Model;     // World-space or HUD-space oriented/billboarded quad matrix
    float4 UvRect;      // (u0, v0, uWidth, vHeight)
    float4 Color;       // rgba tint
    float4 CustomParams;// x = alphaCutoff, y = hitFlash, z = time/pulse, w = unused
};

StructuredBuffer<SpriteGpuInstance> Sprites : register(t0, space0);

cbuffer CameraUniformsVS : register(b0, space1)
{
    float4x4 ViewProj;
    float4 CameraPosTime;
};

cbuffer BatchUniformsVS : register(b1, space1)
{
    uint BaseInstance;
    uint3 _BatchPad;
};

struct PSInput
{
    float4 ClipPos      : SV_Position;
    float2 Uv           : TEXCOORD0;
    float4 Color        : TEXCOORD1;
    float4 CustomParams : TEXCOORD2;
};

static const float2 QuadPositions[4] = {
    float2(0.0, 0.0),
    float2(1.0, 0.0),
    float2(1.0, 1.0),
    float2(0.0, 1.0)
};

static const float2 QuadUvs[4] = {
    float2(0.0, 1.0),
    float2(1.0, 1.0),
    float2(1.0, 0.0),
    float2(0.0, 0.0)
};

PSInput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    SpriteGpuInstance sp = Sprites[BaseInstance + instanceId];
    float2 q = QuadPositions[vertexId & 3u];
    float2 qUv = QuadUvs[vertexId & 3u];

    float4 worldPos = mul(sp.Model, float4(q.x, q.y, 0.0, 1.0));

    PSInput o;
    o.ClipPos = mul(ViewProj, worldPos);
    o.Uv = sp.UvRect.xy + qUv * sp.UvRect.zw;
    o.Color = sp.Color;
    o.CustomParams = sp.CustomParams;
    return o;
}

SamplerState SpriteSamp : register(s0, space2);
Texture2D SpriteTex : register(t0, space2);

float4 PSMain(PSInput input) : SV_Target0
{
    float4 tex = SpriteTex.Sample(SpriteSamp, input.Uv);
    float4 col = tex * input.Color;

    // Optional white hit flash via CustomParams.y
    col.rgb = lerp(col.rgb, float3(1.0, 1.0, 1.0), saturate(input.CustomParams.y));

    if (input.CustomParams.x > 0.001 && col.a < input.CustomParams.x)
        discard;

    if (col.a <= 0.002)
        discard;

    return col;
}

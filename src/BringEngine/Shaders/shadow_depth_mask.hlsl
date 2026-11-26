#pragma pack_matrix(column_major)

// Bring Engine — Built-in Alpha-Masked Directional Shadow Map Depth Shader (SDL3_gpu / SDL_shadercross)
// Vertex space0: StorageBuffers (Instances t0, JointPalette t1)
// Vertex space1: UniformBuffers (ShadowFrameUniforms b0, BatchUniformsVS b1)
// Fragment space2: Sampler s0 + Texture t0 (Albedo)
// Renders MaterialAlphaMode.Mask static and skinned instanced 3D shadow casters with alpha-test cutout.

struct Instance3D
{
    float4x4 Model;
    float4 BaseColor;
    float4 EmissiveMeta;
    float4 Extra;           // y = alphaCutoff, z = jointOffset (<0 = static)
    float4 CustomParams;
};

StructuredBuffer<Instance3D> Instances : register(t0, space0);
StructuredBuffer<float4x4> JointPalette : register(t1, space0);

cbuffer ShadowFrameUniformsVS : register(b0, space1)
{
    float4x4 LightViewProj;
};

cbuffer BatchUniformsVS : register(b1, space1)
{
    uint BaseInstance;
    uint3 _BatchPad;
};

struct VSInput
{
    float3 Position     : TEXCOORD0;
    float3 Normal       : TEXCOORD1;
    float2 Uv0          : TEXCOORD2;
    float4 Color        : TEXCOORD3;
    uint   JointIndices : TEXCOORD4;
    float4 JointWeights : TEXCOORD5;
    float2 TangentHint  : TEXCOORD6;
    uint   InstanceId   : SV_InstanceID;
};

struct VSOutput
{
    float4 ClipPos     : SV_Position;
    float2 Uv0         : TEXCOORD0;
    float  AlphaCutoff : TEXCOORD1;
    float  BaseAlpha   : TEXCOORD2;
};

VSOutput VSMain(VSInput input)
{
    Instance3D inst = Instances[BaseInstance + input.InstanceId];
    float4 localPos = float4(input.Position, 1.0);
    // Reference all 7 Mesh3D VSInput attributes so SPIR-V/DXIL/MSL reflection keeps exact vertex layout
    localPos.xyz += (input.Normal + float3(input.TangentHint, 0.0) + input.Color.rgb) * 0.0;

    int jointOff = (int)inst.Extra.z;
    if (jointOff >= 0)
    {
        float weightSum = input.JointWeights.x + input.JointWeights.y + input.JointWeights.z + input.JointWeights.w;
        if (weightSum > 1e-5)
        {
            uint j0 = (input.JointIndices) & 0xFFu;
            uint j1 = (input.JointIndices >> 8) & 0xFFu;
            uint j2 = (input.JointIndices >> 16) & 0xFFu;
            uint j3 = (input.JointIndices >> 24) & 0xFFu;

            float4x4 skinMat =
                JointPalette[jointOff + j0] * input.JointWeights.x +
                JointPalette[jointOff + j1] * input.JointWeights.y +
                JointPalette[jointOff + j2] * input.JointWeights.z +
                JointPalette[jointOff + j3] * input.JointWeights.w;

            localPos = mul(skinMat, localPos);
        }
    }

    float4 worldPos = mul(inst.Model, localPos);
    VSOutput o;
    o.ClipPos = mul(LightViewProj, worldPos);
    o.Uv0 = input.Uv0;
    o.AlphaCutoff = inst.Extra.y;
    o.BaseAlpha = inst.BaseColor.a * input.Color.a;
    return o;
}

SamplerState AlbedoSamp : register(s0, space2);
Texture2D AlbedoTex : register(t0, space2);

void PSMain(VSOutput input)
{
    float alpha = AlbedoTex.Sample(AlbedoSamp, input.Uv0).a * input.BaseAlpha;
    if (input.AlphaCutoff > 0.001 && alpha < input.AlphaCutoff)
        discard;
}

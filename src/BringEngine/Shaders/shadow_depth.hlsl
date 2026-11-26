#pragma pack_matrix(column_major)

// Bring Engine — Built-in Directional Shadow Map Depth Shader (SDL3_gpu / SDL_shadercross)
// Vertex space0: StorageBuffers (Instances t0, JointPalette t1)
// Vertex space1: UniformBuffers (ShadowFrameUniforms b0, BatchUniformsVS b1)
// Renders both static and skinned instanced 3D shadow casters into D32Float depth map.

struct Instance3D
{
    float4x4 Model;
    float4 BaseColor;
    float4 EmissiveMeta;
    float4 Extra;           // z = jointOffset (<0 = static)
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
    float4 ClipPos : SV_Position;
};

VSOutput VSMain(VSInput input)
{
    Instance3D inst = Instances[BaseInstance + input.InstanceId];
    float4 localPos = float4(input.Position, 1.0);

    int jointOff = (int)inst.Extra.z;
    if (jointOff >= 0)
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

    float4 worldPos = mul(inst.Model, localPos);
    VSOutput o;
    o.ClipPos = mul(LightViewProj, worldPos);
    return o;
}

void PSMain(VSOutput input)
{
    // Depth-only pass: hardware writes ClipPos.z to D32Float ShadowDepthTexture.
}

#pragma pack_matrix(column_major)

// ShowcaseGame Custom HLSL Shader: Animated Neon Hologram Energy Shield / Power Core
// Matches Bring Engine's Mesh3D vertex layout & SDL_shadercross register spaces identically.
// Tweenable Material.CustomParams: x = pulseIntensity, y = scanlineSpeed, z = fresnelPower, w = dissolve

struct Instance3D
{
    float4x4 Model;
    float4 BaseColor;
    float4 EmissiveMeta;
    float4 Extra;
    float4 CustomParams;
};

StructuredBuffer<Instance3D> Instances : register(t0, space0);
StructuredBuffer<float4x4> JointPalette : register(t1, space0);

cbuffer FrameUniformsVS : register(b0, space1)
{
    float4x4 ViewProj;
    float4 CameraPosTime;
    float4 SunDirIntensity;
    float4 SunColor;
    float4 AmbientSky;
    float4 AmbientGround;
    float4 FogColorDensity;
    float4 PointLightPos[4];
    float4 PointLightColor[4];
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

struct PSInput
{
    float4 ClipPos      : SV_Position;
    float3 WorldPos     : TEXCOORD0;
    float3 WorldNormal  : TEXCOORD1;
    float2 Uv0          : TEXCOORD2;
    float4 VertexColor  : TEXCOORD3;
    float4 BaseColor    : TEXCOORD4;
    float4 CustomParams : TEXCOORD5;
};

PSInput VSMain(VSInput input)
{
    Instance3D inst = Instances[BaseInstance + input.InstanceId];
    float t = CameraPosTime.w;

    float4 localPos = float4(input.Position + input.Normal * (sin(t * 4.0 + input.Position.y * 5.0) * 0.035 * max(inst.CustomParams.x, 0.3)), 1.0);
    float3 localNorm = input.Normal;

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
        localNorm = normalize(mul((float3x3)skinMat, localNorm));
    }

    float4 worldPos = mul(inst.Model, localPos);
    float3 worldNorm = normalize(mul((float3x3)inst.Model, localNorm));

    PSInput o;
    o.ClipPos = mul(ViewProj, worldPos);
    o.WorldPos = worldPos.xyz;
    o.WorldNormal = worldNorm;
    o.Uv0 = input.Uv0 + input.TangentHint * 0.0;
    o.VertexColor = input.Color;
    o.BaseColor = inst.BaseColor;
    o.CustomParams = inst.CustomParams;
    return o;
}

SamplerState AlbedoSamp : register(s0, space2);
Texture2D AlbedoTex : register(t0, space2);

cbuffer FrameUniformsPS : register(b0, space3)
{
    float4x4 PS_ViewProj;
    float4 PS_CameraPosTime;
    float4 PS_SunDirIntensity;
    float4 PS_SunColor;
    float4 PS_AmbientSky;
    float4 PS_AmbientGround;
    float4 PS_FogColorDensity;
    float4 PS_PointLightPos[4];
    float4 PS_PointLightColor[4];
};

float4 PSMain(PSInput input) : SV_Target0
{
    float4 texSample = AlbedoTex.Sample(AlbedoSamp, input.Uv0);
    float t = PS_CameraPosTime.w;
    float3 N = normalize(input.WorldNormal);
    float3 V = normalize(PS_CameraPosTime.xyz - input.WorldPos);

    float pulse = max(input.CustomParams.x, 0.5);
    float scanSpeed = max(input.CustomParams.y, 2.5);
    float fresnelPow = max(input.CustomParams.z, 2.0);

    float fresnel = pow(1.0 - saturate(abs(dot(N, V))), fresnelPow);
    float scanline = 0.65 + 0.35 * sin(input.WorldPos.y * 28.0 - t * scanSpeed * 4.0);
    float energyBand = pow(0.5 + 0.5 * sin(input.WorldPos.y * 6.0 - t * 3.5), 4.0);

    float3 tint = input.BaseColor.rgb * input.VertexColor.rgb * texSample.rgb;
    float3 coreCol = tint * (0.45 + 0.85 * fresnel + energyBand * 0.95) * scanline * pulse;
    coreCol += float3(0.45, 0.95, 1.0) * (fresnel * 1.2);

    float alpha = saturate(input.BaseColor.a * texSample.a * (0.42 + fresnel * 0.58 + energyBand * 0.25));
    return float4(saturate(coreCol), alpha);
}

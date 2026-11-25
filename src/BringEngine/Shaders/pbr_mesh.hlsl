#pragma pack_matrix(column_major)

// Bring Engine — Built-in 3D PBR Instanced & Skinned Shader (SDL3_gpu / SDL_shadercross)
// Vertex space0: StorageBuffers (Instances t0, JointPalette t1)
// Vertex space1: UniformBuffer (FrameUniforms b0)
// Fragment space2: Sampler s0 + Texture t0
// Fragment space3: UniformBuffer (FrameUniforms b0)

struct Instance3D
{
    float4x4 Model;
    float4 BaseColor;       // rgba (material * entity tint)
    float4 EmissiveMeta;    // xyz = emissive, w = roughness
    float4 Extra;           // x = metallic, y = alphaCutoff, z = jointOffset (<0 = static), w = pad
    float4 CustomParams;    // tweenable custom params
};

StructuredBuffer<Instance3D> Instances : register(t0, space0);
StructuredBuffer<float4x4> JointPalette : register(t1, space0);

cbuffer FrameUniformsVS : register(b0, space1)
{
    float4x4 ViewProj;
    float4 CameraPosTime;   // xyz = camPos, w = time
    float4 SunDirIntensity; // xyz = normalized dir TOWARDS sun, w = intensity
    float4 SunColor;
    float4 AmbientSky;
    float4 AmbientGround;
    float4 FogColorDensity; // xyz = color, w = density
    float4 PointLightPos[4];   // xyz = pos, w = radius
    float4 PointLightColor[4]; // xyz = color, w = intensity
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
    float4 EmissiveMeta : TEXCOORD5;
    float4 Extra        : TEXCOORD6;
    float4 CustomParams : TEXCOORD7;
};

PSInput VSMain(VSInput input)
{
    Instance3D inst = Instances[BaseInstance + input.InstanceId];

    float4 localPos = float4(input.Position, 1.0);
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
    o.Uv0 = input.Uv0;
    o.VertexColor = input.Color;
    o.BaseColor = inst.BaseColor;
    o.EmissiveMeta = inst.EmissiveMeta;
    o.Extra = inst.Extra;
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
    float4 albedo = texSample * input.BaseColor * input.VertexColor;

    if (input.Extra.y > 0.001 && albedo.a < input.Extra.y)
        discard;

    float3 N = normalize(input.WorldNormal);
    float3 V = normalize(PS_CameraPosTime.xyz - input.WorldPos);
    float roughness = clamp(input.EmissiveMeta.w, 0.08, 1.0);
    float metallic = clamp(input.Extra.x, 0.0, 1.0);
    float specPower = lerp(96.0, 8.0, roughness * roughness);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo.rgb, metallic);

    // Hemisphere ambient
    float hemi = N.y * 0.5 + 0.5;
    float3 ambient = lerp(PS_AmbientGround.rgb, PS_AmbientSky.rgb, hemi) * albedo.rgb * (1.0 - metallic * 0.45);

    // Directional Sun (Blinn-PBR)
    float3 L = normalize(PS_SunDirIntensity.xyz);
    float NdotL = max(dot(N, L), 0.0);
    float3 H = normalize(L + V);
    float NdotH = max(dot(N, H), 0.0);
    float spec = pow(NdotH, specPower) * (0.25 + (1.0 - roughness) * 0.85);
    float3 sunLight = (albedo.rgb * (1.0 - metallic * 0.7) + F0 * spec * 2.2) * PS_SunColor.rgb * (NdotL * PS_SunDirIntensity.w);

    // Dynamic Point Lights
    float3 pointAccum = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float radius = PS_PointLightPos[i].w;
        if (radius > 0.01)
        {
            float3 toLight = PS_PointLightPos[i].xyz - input.WorldPos;
            float dist = length(toLight);
            float atten = saturate(1.0 - dist / radius);
            atten = atten * atten;
            if (atten > 0.001)
            {
                float3 pL = toLight / max(dist, 0.001);
                float pNdotL = max(dot(N, pL), 0.0);
                float3 pH = normalize(pL + V);
                float pSpec = pow(max(dot(N, pH), 0.0), specPower) * (1.0 - roughness * 0.5);
                pointAccum += (albedo.rgb + F0 * pSpec * 2.0) * PS_PointLightColor[i].rgb * (pNdotL * atten * PS_PointLightColor[i].w);
            }
        }
    }

    // Subtle rim highlight for crisp silhouette readability
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.2) * 0.22;
    float3 color = ambient + sunLight + pointAccum + input.EmissiveMeta.rgb + PS_AmbientSky.rgb * rim;

    // Distance fog
    float fogDensity = PS_FogColorDensity.w;
    if (fogDensity > 0.0001)
    {
        float camDist = length(PS_CameraPosTime.xyz - input.WorldPos);
        float fogFactor = 1.0 - exp(-camDist * fogDensity);
        color = lerp(color, PS_FogColorDensity.rgb, saturate(fogFactor));
    }

    // Filmic ACES-lite tone mapping
    color = (color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14);
    return float4(saturate(color), albedo.a);
}

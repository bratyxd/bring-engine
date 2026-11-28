#pragma pack_matrix(column_major)

// Bring Engine — Batched 2D Skeletal Rig & Deformable Mesh Shader (SDL3_gpu / SDL_shadercross)
// Vertex space1: CameraUniformsVS (b0)
// Fragment space2: Sampler s0 + Texture t0 (Atlas / Slot Texture)

cbuffer CameraUniformsVS : register(b0, space1)
{
    float4x4 ViewProj;
    float4 CameraPosTime;
};

struct VSInput
{
    float3 Position    : TEXCOORD0;
    float2 Uv          : TEXCOORD1;
    float4 LightColor  : TEXCOORD2;
    float4 DarkColor   : TEXCOORD3; // rgb = two-color dark tint, a = hitFlash [0..1]
    float  AlphaCutoff : TEXCOORD4; // > 0 alpha mask cutoff; < -0.5 alpha-safe Multiply blend mode
};

struct PSInput
{
    float4 ClipPos     : SV_Position;
    float2 Uv          : TEXCOORD0;
    float4 LightColor  : TEXCOORD1;
    float4 DarkColor   : TEXCOORD2;
    float  AlphaCutoff : TEXCOORD3;
};

PSInput VSMain(VSInput input)
{
    PSInput o;
    o.ClipPos = mul(ViewProj, float4(input.Position, 1.0));
    o.Uv = input.Uv;
    o.LightColor = input.LightColor;
    o.DarkColor = input.DarkColor;
    o.AlphaCutoff = input.AlphaCutoff;
    return o;
}

SamplerState RigSamp : register(s0, space2);
Texture2D RigTex : register(t0, space2);

float4 PSMain(PSInput input) : SV_Target0
{
    float4 tex = RigTex.Sample(RigSamp, input.Uv);
    float alpha = tex.a * input.LightColor.a;

    if (input.AlphaCutoff > 0.001 && alpha < input.AlphaCutoff)
        discard;

    if (alpha <= 0.002)
        discard;

    // Spine-grade two-color tint + white hit-flash (DarkColor.a)
    float3 rgb = tex.rgb * input.LightColor.rgb + (1.0 - tex.rgb) * input.DarkColor.rgb;
    rgb = lerp(rgb, float3(1.0, 1.0, 1.0), saturate(input.DarkColor.a));

    // Negative AlphaCutoff (< -0.5) signals alpha-safe Multiply blend (SrcColor=DstColor, DstColor=OneMinusSrcAlpha)
    if (input.AlphaCutoff < -0.5)
        return float4(rgb * alpha, alpha);

    return float4(rgb, alpha);
}

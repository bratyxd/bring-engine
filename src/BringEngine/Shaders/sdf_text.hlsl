#pragma pack_matrix(column_major)

// Bring Engine — Advanced SDF Text Shader (3D World & 2D Screen Space)
// Supports razor-sharp zoom-independent AA, colored outline, outer glow, and shine sweep!

struct GlyphGpuInstance
{
    float4 OriginAndScale; // xyz = bottom-left world corner of glyph quad, w = pad
    float4 RightVec;       // xyz = world right edge vector (scaled by glyph width), w = shinePhase
    float4 UpVec;          // xyz = world up edge vector (scaled by glyph height), w = charNormT
    float4 UvRect;         // (u0, v0, uW, vH)
    float4 FillColor;      // rgba
    float4 OutlineColor;   // rgba
    float4 GlowColor;      // rgba
    float4 StyleParams;    // x = onEdge (0.706), y = outlineWidth, z = softness, w = glowIntensity
};

StructuredBuffer<GlyphGpuInstance> Glyphs : register(t0, space0);

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
    float4 FillColor    : TEXCOORD1;
    float4 OutlineColor : TEXCOORD2;
    float4 GlowColor    : TEXCOORD3;
    float4 StyleParams  : TEXCOORD4;
    float2 Effects      : TEXCOORD5; // x = shinePhase, y = localQuadY
};

static const float2 QuadCorner[4] = {
    float2(0.0, 0.0),
    float2(1.0, 0.0),
    float2(1.0, 1.0),
    float2(0.0, 1.0)
};

static const float2 QuadUv[4] = {
    float2(0.0, 1.0),
    float2(1.0, 1.0),
    float2(1.0, 0.0),
    float2(0.0, 0.0)
};

PSInput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    GlyphGpuInstance g = Glyphs[BaseInstance + instanceId];
    float2 c = QuadCorner[vertexId & 3u];
    float2 qUv = QuadUv[vertexId & 3u];

    float3 worldPos = g.OriginAndScale.xyz + g.RightVec.xyz * c.x + g.UpVec.xyz * c.y;

    PSInput o;
    o.ClipPos = mul(ViewProj, float4(worldPos, 1.0));
    o.Uv = g.UvRect.xy + qUv * g.UvRect.zw;
    o.FillColor = g.FillColor;
    o.OutlineColor = g.OutlineColor;
    o.GlowColor = g.GlowColor;
    o.StyleParams = g.StyleParams;
    o.Effects = float2(g.RightVec.w, c.y);
    return o;
}

SamplerState FontSamp : register(s0, space2);
Texture2D FontAtlas : register(t0, space2);

float4 PSMain(PSInput input) : SV_Target0
{
    float sdf = FontAtlas.Sample(FontSamp, input.Uv).r;
    float edge = input.StyleParams.x;
    float outlineW = input.StyleParams.y;
    float softness = max(input.StyleParams.z, 0.005);
    float glowIntensity = input.StyleParams.w;

    // Screen-space derivative AA keeps SDF crisp at any 3D distance or zoom
    float aa = max(fwidth(sdf) * 0.75, softness);

    float fillAlpha = smoothstep(edge - aa, edge + aa, sdf);
    float outlineEdge = max(0.05, edge - outlineW);
    float outlineAlpha = outlineW > 0.001 ? smoothstep(outlineEdge - aa, outlineEdge + aa, sdf) : fillAlpha;

    // Optional animated shine band across glyph
    float3 fillRgb = input.FillColor.rgb;
    if (input.Effects.x > 0.001)
    {
        float shine = exp(-pow((input.Effects.y - input.Effects.x) * 4.5, 2.0));
        fillRgb += float3(0.75, 0.75, 0.75) * shine;
    }

    float3 baseRgb = lerp(input.OutlineColor.rgb, fillRgb, fillAlpha);
    float baseAlpha = max(fillAlpha * input.FillColor.a, outlineAlpha * input.OutlineColor.a);

    // Outer SDF glow halo
    float glowAlpha = 0.0;
    if (glowIntensity > 0.001)
    {
        float glowBand = smoothstep(max(0.02, outlineEdge - 0.32), outlineEdge, sdf);
        glowAlpha = glowBand * (1.0 - outlineAlpha) * glowIntensity * input.GlowColor.a;
    }

    float finalAlpha = saturate(baseAlpha + glowAlpha);
    if (finalAlpha <= 0.005)
        discard;

    float3 finalRgb = (baseRgb * baseAlpha + input.GlowColor.rgb * glowAlpha) / max(finalAlpha, 0.001);
    return float4(finalRgb, finalAlpha);
}

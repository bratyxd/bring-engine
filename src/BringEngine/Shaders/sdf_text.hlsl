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
    float4 StyleParams;    // x = onEdge (0.502), y = outlineWidth, z = softness, w = glowIntensity
    float4 CustomParams;   // tweenable user parameters forwarded from TextData.CustomParams
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
    float4 CustomParams : TEXCOORD6;
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
    o.CustomParams = g.CustomParams;
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

    // Bounded screen-space derivative AA keeps SDF razor-sharp and 100% opaque even at small HUD sizes
    float fw = fwidth(sdf);
    float aa = clamp(fw * 0.55, softness, 0.14);

    // Slight inner tightening ensures thin strokes reach full 1.0 alpha instead of looking translucent
    float fillAlpha = smoothstep(edge - aa, edge + aa * 0.50, sdf);
    float outlineEdge = max(0.04, edge - outlineW);
    float hasOutline = outlineW > 0.001 ? 1.0 : 0.0;
    float outlineCov = hasOutline > 0.5 ? smoothstep(outlineEdge - aa, outlineEdge + aa * 0.65, sdf) : 0.0;
    float outerAlpha = hasOutline > 0.5 ? outlineCov : fillAlpha;

    // Optional animated shine band across glyph
    float3 fillRgb = input.FillColor.rgb;
    if (input.Effects.x > 0.001)
    {
        float shine = exp(-pow((input.Effects.y - input.Effects.x) * 4.5, 2.0));
        fillRgb += float3(0.75, 0.75, 0.75) * shine;
    }

    // Porter-Duff premultiplied fill-over-outline compositing:
    // Outline coverage is strictly outside fill and disabled when outlineW <= 0.001 so
    // translucent text/drop shadows never darken against OutlineColor and OutlineColor.a == 0 has zero fringe.
    float fillA = fillAlpha * input.FillColor.a;
    float outA = outlineCov * (1.0 - fillAlpha) * input.OutlineColor.a;
    float3 basePremul = fillRgb * fillA + input.OutlineColor.rgb * outA * (1.0 - fillA);
    float baseAlpha = fillA + outA * (1.0 - fillA);

    // Outer SDF glow halo
    float glowAlpha = 0.0;
    if (glowIntensity > 0.001)
    {
        float glowBand = smoothstep(max(0.02, outlineEdge - 0.32), outlineEdge, sdf);
        glowAlpha = glowBand * (1.0 - outerAlpha) * glowIntensity * input.GlowColor.a;
    }

    float finalAlpha = saturate(baseAlpha + glowAlpha * (1.0 - baseAlpha));
    if (finalAlpha <= 0.005)
        discard;

    float3 finalRgb = (basePremul + input.GlowColor.rgb * glowAlpha * (1.0 - baseAlpha)) / max(finalAlpha, 0.001);
    return float4(finalRgb, finalAlpha);
}

using System.Runtime.InteropServices;
using BringEngine.Core.Math;

namespace BringEngine.Rendering;

/// <summary>
/// Unmanaged scene lighting & atmosphere parameters matching pbr_mesh.hlsl FrameUniforms.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SceneLighting
{
    public Vec3 SunDirection;
    public float SunIntensity;
    public ColorF SunColor;
    public ColorF AmbientSky;
    public ColorF AmbientGround;
    public ColorF FogColor;
    public float FogDensity;

    // 4 dynamic point lights: xyz = pos, w = radius; color: xyz = rgb, w = intensity
    public Vec4 PointLightPos0, PointLightPos1, PointLightPos2, PointLightPos3;
    public Vec4 PointLightColor0, PointLightColor1, PointLightColor2, PointLightColor3;

    public static SceneLighting Default => new SceneLighting
    {
        SunDirection = new Vec3(0.45f, 0.85f, 0.35f).Normalized(),
        SunIntensity = 1.65f,
        SunColor = new ColorF(1.0f, 0.96f, 0.88f, 1f),
        AmbientSky = new ColorF(0.28f, 0.38f, 0.58f, 1f),
        AmbientGround = new ColorF(0.10f, 0.09f, 0.14f, 1f),
        FogColor = new ColorF(0.06f, 0.08f, 0.14f, 1f),
        FogDensity = 0.012f,
    };

    public void SetPointLight(int index, Vec3 pos, float radius, ColorF color, float intensity)
    {
        Vec4 p = new Vec4(pos.X, pos.Y, pos.Z, radius);
        Vec4 c = new Vec4(color.R, color.G, color.B, intensity);
        switch (index)
        {
            case 0: PointLightPos0 = p; PointLightColor0 = c; break;
            case 1: PointLightPos1 = p; PointLightColor1 = c; break;
            case 2: PointLightPos2 = p; PointLightColor2 = c; break;
            case 3: PointLightPos3 = p; PointLightColor3 = c; break;
        }
    }
}

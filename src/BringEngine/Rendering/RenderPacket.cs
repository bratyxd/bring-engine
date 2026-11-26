using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering.Gpu;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;

namespace BringEngine.Rendering;

/// <summary>
/// Built-in shader handles copied once after ShaderSystem.CompileBuiltinShaders so the Logic Thread
/// can extract RenderPackets without touching GpuContext.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderShaderDefaults
{
    public ShaderHandle Pbr;
    public ShaderHandle Sprite;
    public ShaderHandle SdfText;
    public ShaderHandle Particle;
}

[StructLayout(LayoutKind.Sequential)]
public struct Instance3DGpu
{
    public Mat4 Model;
    public ColorF BaseColor;
    public Vec4 EmissiveRough; // xyz = emissive, w = roughness
    public Vec4 Extra;         // x = metallic, y = alphaCutoff, z = jointOffset (<0 static), w = pad
    public Vec4 CustomParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FrameUniformsGpu
{
    public Mat4 ViewProj;
    public Vec4 CameraPosTime;
    public Vec4 SunDirIntensity;
    public ColorF SunColor;
    public ColorF AmbientSky;
    public ColorF AmbientGround;
    public Vec4 FogColorDensity;
    public Vec4 PointLightPos0, PointLightPos1, PointLightPos2, PointLightPos3;
    public Vec4 PointLightColor0, PointLightColor1, PointLightColor2, PointLightColor3;
    public Mat4 LightViewProj;
    public Vec4 ShadowParams; // x = bias, y = strength (0 = disabled), z = invMapSize, w = normalBias
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShadowFrameUniformsGpu
{
    public Mat4 LightViewProj;
}

[StructLayout(LayoutKind.Sequential)]
public struct SpriteGpuInstance
{
    public Mat4 Model;
    public Vec4 UvRect;
    public ColorF Color;
    public Vec4 CustomParams; // x = alphaCutoff, y = hitFlash, z = time, w = pad
}

[StructLayout(LayoutKind.Sequential)]
public struct ParticleGpuInstance
{
    public Vec4 PosSize;
    public ColorF Color;
    public Vec4 UvRect;
    public Vec4 RotExtra;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SimpleCameraUniformsGpu
{
    public Mat4 ViewProj;
    public Vec4 CameraPosTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleCameraUniformsGpu
{
    public Mat4 ViewProj;
    public Vec4 CameraRightTime;
    public Vec4 CameraUpPad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BatchUniformsGpu
{
    public uint BaseInstance;
    public uint P1, P2, P3;
}

[StructLayout(LayoutKind.Sequential)]
public struct Batch3DCmd
{
    public int MeshGpuSlot;
    public uint MeshGpuGen;
    public int TextureGpuSlot;
    public uint TextureGpuGen;
    public ShaderHandle Shader;
    public MaterialAlphaMode AlphaMode;
    public byte DoubleSided;
    public uint BaseInstance;
    public uint InstanceCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct SpriteBatchCmd
{
    public int TextureGpuSlot;
    public uint TextureGpuGen;
    public ShaderHandle Shader;
    public SpriteBlendMode BlendMode;
    public byte DepthTest;
    public uint BaseInstance;
    public uint InstanceCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct TextBatchCmd
{
    public int AtlasTexGpuSlot;
    public uint AtlasTexGpuGen;
    public ShaderHandle Shader;
    public byte DepthTest;
    public uint BaseInstance;
    public uint InstanceCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct ParticleBatchCmd
{
    public int TextureGpuSlot;
    public uint TextureGpuGen;
    public ShaderHandle Shader;
    public ParticleBlendMode BlendMode;
    public uint BaseInstance;
    public uint InstanceCount;
}

/// <summary>
/// Immutable per-camera render snapshot copied into the FrameArena during ExtractRenderPacket
/// so the Render Thread never reads EngineWorld.Cameras during concurrent frame execution.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderCameraSnapshot
{
    public Vec4 ViewportRect;
    public ColorF ClearColor;
    public CameraClearFlags ClearFlags;
    public Vec3 ShakenPosition;
    public Vec3 Right;
    public Vec3 CameraUp;
    public Mat4 ViewProjMatrix;

    public int Batch3DStart, Batch3DCount;
    public int SpriteBatchStart, SpriteBatchCount;
    public int TextBatchStart, TextBatchCount;
    public int ParticleBatchStart, ParticleBatchCount;
}

/// <summary>
/// Thread-safe, self-contained immutable render command packet for a single frame.
/// Produced on the Logic Thread into FrameArena[writeSlot] and consumed on the Render Thread
/// from FrameArena[readSlot] one frame later with zero shared mutable state and zero GC allocations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderPacket
{
    public float WorldTotalTime;
    public SceneLighting Lighting;

    public NativeSlice<RenderCameraSnapshot> Cameras;
    public NativeList<Instance3DGpu> Instances3D;
    public NativeList<Batch3DCmd> Batches3D;
    public NativeList<Batch3DCmd> ShadowBatches3D; // Shadow caster batches referencing Instances3D
    public Mat4 ShadowLightViewProj;
    public uint ShadowMapSize;
    public byte ShadowsActive;
    public NativeSlice<Mat4> JointPalette;
    public NativeList<SpriteGpuInstance> Sprites;
    public NativeList<SpriteBatchCmd> SpriteBatches;
    public NativeList<GlyphGpuInstance> Glyphs;
    public NativeList<TextBatchCmd> TextBatches;
    public NativeList<ParticleGpuInstance> Particles;
    public NativeList<ParticleBatchCmd> ParticleBatches;

    public RenderStats ExtractedStats;
    public nuint PacketArenaUsedBytes;
    public byte Valid;
}

/// <summary>
/// CPU & GPU timing breakdown returned by BatchRenderer.SubmitRenderPacket on the Render Thread
/// (merged safely into FrameProfiler at the frame barrier without any locks).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderSubmitTimings
{
    public double UploadMs;
    public double EncodeMs;
    public double TotalRenderCpuMs;
    public double GpuMs;
    public long GpuWaitTicks;
}

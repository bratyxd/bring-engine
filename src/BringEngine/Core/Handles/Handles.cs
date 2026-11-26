using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BringEngine.Core.Handles;

/// <summary>
/// Bit-packing helpers for 32-bit generational handles:
/// Bits [0..19] = 20-bit slot index (up to 1,048,575 slots)
/// Bits [20..31] = 12-bit generation (1..4095, 0 reserved for Invalid)
/// </summary>
public static class HandlePacking
{
    public const uint IndexMask = 0x000F_FFFFu;
    public const int GenerationShift = 20;
    public const uint GenerationMask = 0x0FFFu;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(uint index, uint generation)
    {
        uint gen = generation & GenerationMask;
        if (gen == 0) gen = 1;
        return (gen << GenerationShift) | (index & IndexMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint UnpackIndex(uint raw) => raw & IndexMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint UnpackGeneration(uint raw) => (raw >> GenerationShift) & GenerationMask;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct EntityId : IEquatable<EntityId>
{
    public readonly uint Raw;
    public static readonly EntityId Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityId(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EntityId Create(uint index, uint generation) => new EntityId(HandlePacking.Pack(index, generation));

    public bool Equals(EntityId other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is EntityId other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(EntityId a, EntityId b) => a.Raw == b.Raw;
    public static bool operator !=(EntityId a, EntityId b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"Entity({Index}:g{Generation})" : "Entity(Invalid)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct SceneId : IEquatable<SceneId>
{
    public readonly uint Raw;
    public static readonly SceneId Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public SceneId(uint raw) => Raw = raw;
    public static SceneId Create(uint index, uint generation) => new SceneId(HandlePacking.Pack(index, generation));
    public bool Equals(SceneId other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is SceneId other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(SceneId a, SceneId b) => a.Raw == b.Raw;
    public static bool operator !=(SceneId a, SceneId b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct BundleId : IEquatable<BundleId>
{
    public readonly uint Raw;
    public static readonly BundleId Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public BundleId(uint raw) => Raw = raw;
    public static BundleId Create(uint index, uint generation) => new BundleId(HandlePacking.Pack(index, generation));
    public bool Equals(BundleId other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is BundleId other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(BundleId a, BundleId b) => a.Raw == b.Raw;
    public static bool operator !=(BundleId a, BundleId b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct MeshHandle : IEquatable<MeshHandle>
{
    public readonly uint Raw;
    public static readonly MeshHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public MeshHandle(uint raw) => Raw = raw;
    public static MeshHandle Create(uint index, uint generation) => new MeshHandle(HandlePacking.Pack(index, generation));
    public bool Equals(MeshHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is MeshHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(MeshHandle a, MeshHandle b) => a.Raw == b.Raw;
    public static bool operator !=(MeshHandle a, MeshHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct TextureHandle : IEquatable<TextureHandle>
{
    public readonly uint Raw;
    public static readonly TextureHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public TextureHandle(uint raw) => Raw = raw;
    public static TextureHandle Create(uint index, uint generation) => new TextureHandle(HandlePacking.Pack(index, generation));
    public bool Equals(TextureHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is TextureHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(TextureHandle a, TextureHandle b) => a.Raw == b.Raw;
    public static bool operator !=(TextureHandle a, TextureHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct MaterialHandle : IEquatable<MaterialHandle>
{
    public readonly uint Raw;
    public static readonly MaterialHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public MaterialHandle(uint raw) => Raw = raw;
    public static MaterialHandle Create(uint index, uint generation) => new MaterialHandle(HandlePacking.Pack(index, generation));
    public bool Equals(MaterialHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is MaterialHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(MaterialHandle a, MaterialHandle b) => a.Raw == b.Raw;
    public static bool operator !=(MaterialHandle a, MaterialHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct ShaderHandle : IEquatable<ShaderHandle>
{
    public readonly uint Raw;
    public static readonly ShaderHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public ShaderHandle(uint raw) => Raw = raw;
    public static ShaderHandle Create(uint index, uint generation) => new ShaderHandle(HandlePacking.Pack(index, generation));
    public bool Equals(ShaderHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is ShaderHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(ShaderHandle a, ShaderHandle b) => a.Raw == b.Raw;
    public static bool operator !=(ShaderHandle a, ShaderHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct FontHandle : IEquatable<FontHandle>
{
    public readonly uint Raw;
    public static readonly FontHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public FontHandle(uint raw) => Raw = raw;
    public static FontHandle Create(uint index, uint generation) => new FontHandle(HandlePacking.Pack(index, generation));
    public bool Equals(FontHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is FontHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(FontHandle a, FontHandle b) => a.Raw == b.Raw;
    public static bool operator !=(FontHandle a, FontHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct GltfHandle : IEquatable<GltfHandle>
{
    public readonly uint Raw;
    public static readonly GltfHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public GltfHandle(uint raw) => Raw = raw;
    public static GltfHandle Create(uint index, uint generation) => new GltfHandle(HandlePacking.Pack(index, generation));
    public bool Equals(GltfHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is GltfHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(GltfHandle a, GltfHandle b) => a.Raw == b.Raw;
    public static bool operator !=(GltfHandle a, GltfHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct AnimClipHandle : IEquatable<AnimClipHandle>
{
    public readonly uint Raw;
    public static readonly AnimClipHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public AnimClipHandle(uint raw) => Raw = raw;
    public static AnimClipHandle Create(uint index, uint generation) => new AnimClipHandle(HandlePacking.Pack(index, generation));
    public bool Equals(AnimClipHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is AnimClipHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(AnimClipHandle a, AnimClipHandle b) => a.Raw == b.Raw;
    public static bool operator !=(AnimClipHandle a, AnimClipHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct SkeletonHandle : IEquatable<SkeletonHandle>
{
    public readonly uint Raw;
    public static readonly SkeletonHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public SkeletonHandle(uint raw) => Raw = raw;
    public static SkeletonHandle Create(uint index, uint generation) => new SkeletonHandle(HandlePacking.Pack(index, generation));
    public bool Equals(SkeletonHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is SkeletonHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(SkeletonHandle a, SkeletonHandle b) => a.Raw == b.Raw;
    public static bool operator !=(SkeletonHandle a, SkeletonHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct TweenHandle : IEquatable<TweenHandle>
{
    public readonly uint Raw;
    public static readonly TweenHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public TweenHandle(uint raw) => Raw = raw;
    public static TweenHandle Create(uint index, uint generation) => new TweenHandle(HandlePacking.Pack(index, generation));
    public bool Equals(TweenHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is TweenHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(TweenHandle a, TweenHandle b) => a.Raw == b.Raw;
    public static bool operator !=(TweenHandle a, TweenHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct EmitterHandle : IEquatable<EmitterHandle>
{
    public readonly uint Raw;
    public static readonly EmitterHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public EmitterHandle(uint raw) => Raw = raw;
    public static EmitterHandle Create(uint index, uint generation) => new EmitterHandle(HandlePacking.Pack(index, generation));
    public bool Equals(EmitterHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is EmitterHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(EmitterHandle a, EmitterHandle b) => a.Raw == b.Raw;
    public static bool operator !=(EmitterHandle a, EmitterHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct CameraHandle : IEquatable<CameraHandle>
{
    public readonly uint Raw;
    public static readonly CameraHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public CameraHandle(uint raw) => Raw = raw;
    public static CameraHandle Create(uint index, uint generation) => new CameraHandle(HandlePacking.Pack(index, generation));
    public bool Equals(CameraHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is CameraHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(CameraHandle a, CameraHandle b) => a.Raw == b.Raw;
    public static bool operator !=(CameraHandle a, CameraHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct SpriteHandle : IEquatable<SpriteHandle>
{
    public readonly uint Raw;
    public static readonly SpriteHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public SpriteHandle(uint raw) => Raw = raw;
    public static SpriteHandle Create(uint index, uint generation) => new SpriteHandle(HandlePacking.Pack(index, generation));
    public bool Equals(SpriteHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is SpriteHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(SpriteHandle a, SpriteHandle b) => a.Raw == b.Raw;
    public static bool operator !=(SpriteHandle a, SpriteHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct TextHandle : IEquatable<TextHandle>
{
    public readonly uint Raw;
    public static readonly TextHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    public TextHandle(uint raw) => Raw = raw;
    public static TextHandle Create(uint index, uint generation) => new TextHandle(HandlePacking.Pack(index, generation));
    public bool Equals(TextHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is TextHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(TextHandle a, TextHandle b) => a.Raw == b.Raw;
    public static bool operator !=(TextHandle a, TextHandle b) => a.Raw != b.Raw;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct JobHandle : IEquatable<JobHandle>
{
    public readonly uint Raw;
    public static readonly JobHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JobHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JobHandle Create(uint index, uint generation) => new JobHandle(HandlePacking.Pack(index, generation));
    public bool Equals(JobHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is JobHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(JobHandle a, JobHandle b) => a.Raw == b.Raw;
    public static bool operator !=(JobHandle a, JobHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"Job({Index}:g{Generation})" : "Job(Invalid)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct HttpRequestHandle : IEquatable<HttpRequestHandle>
{
    public readonly uint Raw;
    public static readonly HttpRequestHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HttpRequestHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static HttpRequestHandle Create(uint index, uint generation) => new HttpRequestHandle(HandlePacking.Pack(index, generation));
    public bool Equals(HttpRequestHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is HttpRequestHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(HttpRequestHandle a, HttpRequestHandle b) => a.Raw == b.Raw;
    public static bool operator !=(HttpRequestHandle a, HttpRequestHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"HttpReq({Index}:g{Generation})" : "HttpReq(Invalid)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct WebSocketHandle : IEquatable<WebSocketHandle>
{
    public readonly uint Raw;
    public static readonly WebSocketHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WebSocketHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WebSocketHandle Create(uint index, uint generation) => new WebSocketHandle(HandlePacking.Pack(index, generation));
    public bool Equals(WebSocketHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is WebSocketHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(WebSocketHandle a, WebSocketHandle b) => a.Raw == b.Raw;
    public static bool operator !=(WebSocketHandle a, WebSocketHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"WebSocket({Index}:g{Generation})" : "WebSocket(Invalid)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct LogFileHandle : IEquatable<LogFileHandle>
{
    public readonly uint Raw;
    public static readonly LogFileHandle Default = default;
    public static readonly LogFileHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LogFileHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LogFileHandle Create(uint index, uint generation) => new LogFileHandle(HandlePacking.Pack(index, generation));
    public bool Equals(LogFileHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is LogFileHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(LogFileHandle a, LogFileHandle b) => a.Raw == b.Raw;
    public static bool operator !=(LogFileHandle a, LogFileHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"LogFile({Index}:g{Generation})" : "LogFile(Default)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct SaveOpHandle : IEquatable<SaveOpHandle>
{
    public readonly uint Raw;
    public static readonly SaveOpHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SaveOpHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SaveOpHandle Create(uint index, uint generation) => new SaveOpHandle(HandlePacking.Pack(index, generation));
    public bool Equals(SaveOpHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is SaveOpHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(SaveOpHandle a, SaveOpHandle b) => a.Raw == b.Raw;
    public static bool operator !=(SaveOpHandle a, SaveOpHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"SaveOp({Index}:g{Generation})" : "SaveOp(Invalid)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct AudioClipHandle : IEquatable<AudioClipHandle>
{
    public readonly uint Raw;
    public static readonly AudioClipHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AudioClipHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioClipHandle Create(uint index, uint generation) => new AudioClipHandle(HandlePacking.Pack(index, generation));
    public bool Equals(AudioClipHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is AudioClipHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(AudioClipHandle a, AudioClipHandle b) => a.Raw == b.Raw;
    public static bool operator !=(AudioClipHandle a, AudioClipHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"AudioClip({Index}:g{Generation})" : "AudioClip(Invalid)";
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct AudioVoiceHandle : IEquatable<AudioVoiceHandle>
{
    public readonly uint Raw;
    public static readonly AudioVoiceHandle Invalid = default;
    public bool IsValid => Raw != 0;
    public uint Index => HandlePacking.UnpackIndex(Raw);
    public uint Generation => HandlePacking.UnpackGeneration(Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AudioVoiceHandle(uint raw) => Raw = raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AudioVoiceHandle Create(uint index, uint generation) => new AudioVoiceHandle(HandlePacking.Pack(index, generation));
    public bool Equals(AudioVoiceHandle other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is AudioVoiceHandle other && Raw == other.Raw;
    public override int GetHashCode() => (int)Raw;
    public static bool operator ==(AudioVoiceHandle a, AudioVoiceHandle b) => a.Raw == b.Raw;
    public static bool operator !=(AudioVoiceHandle a, AudioVoiceHandle b) => a.Raw != b.Raw;
    public override string ToString() => IsValid ? $"AudioVoice({Index}:g{Generation})" : "AudioVoice(Invalid)";
}

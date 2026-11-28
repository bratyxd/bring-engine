using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.RenderData;

namespace BringEngine.Simulation.Rig2D;

/// <summary>
/// Column-major 2D affine transform matrix (24 bytes):
/// [ A  C  Tx ]
/// [ B  D  Ty ]
/// [ 0  0   1 ]
/// Deterministic across platforms using DetMath.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rig2DAffine2D : IEquatable<Rig2DAffine2D>
{
    public float A, B, C, D, Tx, Ty;

    public static readonly Rig2DAffine2D Identity = new Rig2DAffine2D(1f, 0f, 0f, 1f, 0f, 0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rig2DAffine2D(float a, float b, float c, float d, float tx, float ty)
    {
        A = a; B = b; C = c; D = d; Tx = tx; Ty = ty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rig2DAffine2D FromTrsShear(
        float x,
        float y,
        float rotDeg,
        float sx,
        float sy,
        float shearXDeg = 0f,
        float shearYDeg = 0f)
    {
        float rx = (rotDeg + shearXDeg) * DetMath.Deg2Rad;
        float ry = (rotDeg + 90f + shearYDeg) * DetMath.Deg2Rad;
        float a = DetMath.Cos(rx) * sx;
        float b = DetMath.Sin(rx) * sx;
        float c = DetMath.Cos(ry) * sy;
        float d = DetMath.Sin(ry) * sy;
        return new Rig2DAffine2D(a, b, c, d, x, y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rig2DAffine2D Multiply(in Rig2DAffine2D parent, in Rig2DAffine2D local) => new Rig2DAffine2D(
        parent.A * local.A + parent.C * local.B,
        parent.B * local.A + parent.D * local.B,
        parent.A * local.C + parent.C * local.D,
        parent.B * local.C + parent.D * local.D,
        parent.A * local.Tx + parent.C * local.Ty + parent.Tx,
        parent.B * local.Tx + parent.D * local.Ty + parent.Ty);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryInvert(in Rig2DAffine2D m, out Rig2DAffine2D inv)
    {
        float det = m.A * m.D - m.B * m.C;
        if (!(DetMath.Abs(det) >= 1e-12f))
        {
            inv = Identity;
            return false;
        }
        float invDet = 1f / det;
        float ia = m.D * invDet;
        float ib = -m.B * invDet;
        float ic = -m.C * invDet;
        float id = m.A * invDet;
        float itx = -(ia * m.Tx + ic * m.Ty);
        float ity = -(ib * m.Tx + id * m.Ty);
        inv = new Rig2DAffine2D(ia, ib, ic, id, itx, ity);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rig2DAffine2D Invert(in Rig2DAffine2D m)
        => TryInvert(in m, out Rig2DAffine2D inv) ? inv : Identity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 TransformPoint(in Rig2DAffine2D m, Vec2 p)
        => new Vec2(m.A * p.X + m.C * p.Y + m.Tx, m.B * p.X + m.D * p.Y + m.Ty);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 TransformVector(in Rig2DAffine2D m, Vec2 v)
        => new Vec2(m.A * v.X + m.C * v.Y, m.B * v.X + m.D * v.Y);

    public readonly float WorldRotationRad => DetMath.Atan2(B, A);
    public readonly float WorldScaleX => DetMath.Sqrt(A * A + B * B);
    public readonly float WorldScaleY => DetMath.Sqrt(C * C + D * D);
    public readonly float Determinant => A * D - B * C;

    public readonly bool Equals(Rig2DAffine2D other)
        => A == other.A && B == other.B && C == other.C && D == other.D && Tx == other.Tx && Ty == other.Ty;
}

public enum Rig2DInheritMode : byte
{
    Normal = 0,
    OnlyTranslation = 1,
    NoRotation = 2,
    NoScale = 3,
}

public enum Rig2DAttachmentKind : byte
{
    None = 0,
    Region = 1,
    WeightedMesh = 2,
    Sequence = 3,
}

public enum Rig2DBlendMode : byte
{
    AlphaBlend = 0,
    Additive = 1,
    Multiply = 2,
}

public enum Rig2DSequenceLoop : byte
{
    Hold = 0,
    Loop = 1,
    PingPong = 2,
}

public enum Rig2DConstraintKind : byte
{
    Ik = 0,
    Transform = 1,
    Spring = 2,
}

public enum Rig2DCurveKind : byte
{
    Linear = 0,
    Step = 1,
    Bezier = 2,
}

public enum Rig2DTimelineTarget : byte
{
    BoneRotate = 0,
    BoneTranslate = 1,
    BoneScale = 2,
    BoneShear = 3,
    SlotAttachment = 4,
    SlotColor = 5,
    SlotDarkColor = 6,
    SlotSequenceFrame = 7,
    ConstraintMix = 8,
    MeshDeform = 9,
    DrawOrder = 10,
}

public enum Rig2DBoneOverrideMode : byte
{
    None = 0,
    AdditiveLocal = 1,
    WorldPositionTarget = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DBoneSetupData
{
    public StringId Name;
    public FixedString64 NameText;
    public int ParentIndex; // -1 for root; topological order guarantees ParentIndex < boneIndex
    public float X;
    public float Y;
    public float RotationDeg;
    public float ScaleX;
    public float ScaleY;
    public float ShearX;
    public float ShearY;
    public float Length;
    public Rig2DInheritMode Inherit;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DSlotSetupData
{
    public StringId Name;
    public FixedString64 NameText;
    public int BoneIndex;
    public StringId DefaultAttachmentName;
    public ColorF LightColor;
    public ColorF DarkColor;
    public Rig2DBlendMode BlendMode;
}

/// <summary>
/// 40-byte weighted 2D mesh vertex in skeleton bind space.
/// Skinning: sum_k w_k * (BoneWorld[b_k] * BoneInvBind[b_k] * (SetupPos + ffdDelta)).
/// When WeightSum &lt;= 1e-5f, binds rigidly to the slot's owner bone.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rig2DMeshVertex
{
    public Vec2 SetupPos;
    public Vec2 Uv;
    public uint PackedBoneIndices; // 4 x byte bone indices [0..254]
    public Vec4 BoneWeights;
    public float WeightSum;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PackBones(byte b0, byte b1, byte b2, byte b3)
        => (uint)b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DAttachmentData
{
    public StringId Name;
    public FixedString64 NameText;
    public int SlotIndex;
    public Rig2DAttachmentKind Kind;
    public float X;
    public float Y;
    public float RotationDeg;
    public float ScaleX;
    public float ScaleY;
    public float Width;
    public float Height;
    public StringId SpriteRegionName;
    public Vec4 FallbackUvRect;
    public TextureHandle FallbackTexture;
    public int VertexStart;
    public int VertexCount;
    public int IndexStart;
    public int IndexCount;
    public int SeqFrameStart;
    public int SeqFrameCount;
    public float SeqFps;
    public Rig2DSequenceLoop SeqLoop;
    public int SeqStartIndex;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DSkinData
{
    public StringId Name;
    public FixedString64 NameText;
    public int EntryStart;
    public int EntryCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DSkinEntry
{
    public int SlotIndex;
    public StringId AttachmentName;
    public int AttachmentIndex;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DIkConstraintData
{
    public StringId Name;
    public int Order;
    public int BoneA;
    public int BoneB; // -1 = 1-bone aim constraint
    public int TargetBone;
    public float Mix;
    public float Softness;
    public sbyte BendDirection; // +1 or -1
    public byte Stretch;
    public byte Compress;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DTransformConstraintData
{
    public StringId Name;
    public int Order;
    public int BoneIndex;
    public int TargetBone;
    public float MixRotate;
    public float MixX;
    public float MixY;
    public float MixScaleX;
    public float MixScaleY;
    public float MixShearY;
    public float OffsetRotateDeg;
    public float OffsetX;
    public float OffsetY;
    public float OffsetScaleX;
    public float OffsetScaleY;
    public float OffsetShearY;
    public byte Local;
    public byte Relative;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DSpringConstraintData
{
    public StringId Name;
    public int Order;
    public int BoneIndex;
    public float Inertia;
    public float Strength;
    public float Damping;
    public float Mass;
    public float Gravity;
    public float Wind;
    public float MaxAngleDeg;
    public float Mix;
    public float PosScaleX;
    public float PosScaleY;
    public float RotateScale;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DConstraintOrderItem
{
    public Rig2DConstraintKind Kind;
    public int Index;
    public int Order;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DKeyframeData
{
    public float Time;
    public float V0;
    public float V1;
    public float V2;
    public float V3;
    public Rig2DCurveKind Curve;
    public float Cx1;
    public float Cy1;
    public float Cx2;
    public float Cy2;
    public int IntVal;
    public ulong HashVal;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DTimelineData
{
    public Rig2DTimelineTarget Target;
    public int TargetIndex;
    public int KeyStart;
    public int KeyCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DEventKeyData
{
    public float Time;
    public StringId EventName;
    public int IntVal;
    public float FloatVal;
    public StringId StringVal;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DAnimClipData
{
    public StringId Name;
    public FixedString64 NameText;
    public float Duration;
    public int TimelineStart;
    public int TimelineCount;
    public int EventStart;
    public int EventCount;
}

/// <summary>
/// Immutable 2D skeletal rig asset backed by flat unmanaged slices in a Bundle LinearArena.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Rig2DAssetData
{
    public const int MaxBonesPerRig = 255;

    public Rig2DAssetHandle Handle;
    public BundleId OwnerBundle;
    public StringId Name;
    public FixedString64 NameText;
    public float PixelsPerUnit;
    public Vec2 OriginOffset;
    public Vec2 BoundsSize;
    public TextureHandle DefaultAtlasTexture;

    public Rig2DBoneSetupData* Bones;
    public Rig2DAffine2D* BoneInvBind;
    public int BoneCount;

    public Rig2DSlotSetupData* Slots;
    public int SlotCount;

    public Rig2DAttachmentData* Attachments;
    public int AttachmentCount;

    public Rig2DMeshVertex* MeshVertices;
    public int MeshVertexCount;

    public uint* MeshIndices;
    public int MeshIndexCount;

    public StringId* SequenceFrameNames;
    public int SequenceFrameNameCount;

    public Rig2DSkinData* Skins;
    public int SkinCount;

    public Rig2DSkinEntry* SkinEntries;
    public int SkinEntryCount;

    public Rig2DIkConstraintData* IkConstraints;
    public int IkConstraintCount;

    public Rig2DTransformConstraintData* TransformConstraints;
    public int TransformConstraintCount;

    public Rig2DSpringConstraintData* SpringConstraints;
    public int SpringConstraintCount;

    public Rig2DConstraintOrderItem* ConstraintOrder;
    public int ConstraintOrderCount;

    public Rig2DAnimClipData* Clips;
    public int ClipCount;

    public Rig2DTimelineData* Timelines;
    public int TimelineCount;

    public Rig2DKeyframeData* Keyframes;
    public int KeyframeCount;

    public Rig2DEventKeyData* EventKeys;
    public int EventKeyCount;

    public Vec2* DeformPool;
    public int DeformPoolCount;

    public int* DrawOrderPool;
    public int DrawOrderPoolCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DTrackState
{
    public int ClipIndex;
    public float Time;
    public float Speed;
    public float Alpha;
    public byte Loop;
    public byte FromLoop;
    public byte Additive;
    public byte Active;
    public byte JustStarted;
    public int FromClipIndex;
    public float FromTime;
    public float MixElapsed;
    public float MixDuration;
    public float FadeOutElapsed;
    public float FadeOutDuration;
    public float FadeOutStartAlpha;
}

[InlineArray(8)]
public struct Rig2DTrackArray8
{
    private Rig2DTrackState _element0;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DSpringRuntimeState
{
    public float AngleOffsetDeg;
    public float AngleVelDeg;
    public float PosOffsetX;
    public float PosOffsetY;
    public float VelX;
    public float VelY;
    public float PrevWorldX;
    public float PrevWorldY;
    public float PrevWorldAngleDeg;
    public byte Initialized;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DBoneOverride
{
    public int BoneIndex;
    public float AddX;
    public float AddY;
    public float AddRotDeg;
    public float SetWorldTargetX;
    public float SetWorldTargetY;
    public float SetWorldTargetZ;
    public Rig2DBoneOverrideMode Mode;
    public float Weight;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DRuntimeSlotState
{
    public int ActiveAttachmentIndex; // -1 = hidden / empty
    public ColorF LightColor;
    public ColorF DarkColor;
    public float SequenceTime;
    public int SequenceFrame;
    public int DeformVertexOffset; // Offset into Rig2DTable.FrameDeformDeltas
    public int DeformVertexCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct Rig2DFiredEvent
{
    public Rig2DInstanceHandle Instance;
    public EntityId Entity;
    public StringId EventName;
    public int IntVal;
    public float FloatVal;
    public StringId StringVal;
    public float Time;
}

/// <summary>
/// Live posed 2D rig component attached to an EntityId.
/// All variable-length bone/slot/spring/override arrays are backed by Rig2DTable's coalescing native pools.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rig2DInstanceData
{
    public Rig2DInstanceHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;
    public Rig2DAssetHandle Asset;
    public int ActiveSkinIndex;
    public ShaderHandle CustomShader;

    public ColorF Tint;
    public ColorF DarkTint;
    public float HitFlash;
    public Vec4 CustomParams;
    public Vec2 Scale;

    public uint LayerMask;
    public short SortingLayer;
    public short OrderInLayer;
    public SpriteBillboardMode Billboard;
    public byte FlipX;
    public byte FlipY;
    public byte Visible;
    public byte DepthTest;

    public Rig2DTrackArray8 Tracks;

    public int BoneSliceStart;
    public int BoneSliceCount;
    public int SlotSliceStart;
    public int SlotSliceCount;
    public int SpringSliceStart;
    public int SpringSliceCount;
    public int OverrideSliceStart;
    public int OverrideSliceCount;

    public Vec3 PrevEntityPos;
    public byte HasPrevEntityPos;
    public byte PosedOnce;
}

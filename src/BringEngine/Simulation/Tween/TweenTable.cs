using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.World;

namespace BringEngine.Simulation.Tween;

public enum TweenLoopMode : byte
{
    Once = 0,
    Loop = 1,
    PingPong = 2,
}

public enum TweenTargetKind : byte
{
    None = 0,

    // Direct unmanaged memory pointers — animate ANY field in ANY game or engine struct!
    PtrFloat = 1,
    PtrVec2 = 2,
    PtrVec3 = 3,
    PtrVec4 = 4,
    PtrColorF = 5,
    PtrQuat = 6,
    PtrFix32 = 7,
    PtrFixVec3 = 8,

    // Handle-safe engine component targets
    TransformPosition = 20,
    TransformScale = 21,
    TransformRotation = 22,
    CameraPosition = 30,
    CameraFov = 31,
    CameraOrthoHeight = 32,
    CameraOrbitYaw = 33,
    CameraOrbitDistance = 34,

    // Custom callback target (fires delegate*<uint, Vec4, void> every step with interpolated value)
    CustomCallback = 60,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TweenData
{
    public TweenHandle Handle;
    public SceneId OwnerScene;
    public uint GroupId;

    public TweenTargetKind TargetKind;
    public EaseType Ease;
    public TweenLoopMode LoopMode;
    public byte Flags; // bit 0: Active, bit 1: Paused, bit 2: Reversed, bit 3: UseCustomCurve, bit 4: WaitingInSequence

    public float Elapsed;
    public float Delay;
    public float Duration;
    public float TimeScale;
    public int RemainingLoops; // -1 = infinite, 0 = finish after current pass

    public Vec4 StartValue;
    public Vec4 EndValue;
    public CubicCurve Curve;

    // Target payload (handle or direct native pointer)
    public uint TargetHandleRaw;
    public void* TargetPtr;

    // Sequence chaining & unmanaged callbacks
    public TweenHandle NextInSequence;
    public delegate*<uint, Vec4, void> OnStep;
    public delegate*<uint, void> OnComplete;
    public uint UserData;

    public readonly bool IsPaused => (Flags & 2) != 0;
    public readonly bool IsReversed => (Flags & 4) != 0;
    public readonly bool UseCustomCurve => (Flags & 8) != 0;
    public readonly bool IsWaitingInSequence => (Flags & 16) != 0;
}

/// <summary>
/// Dense contiguous pool of active tweens with O(1) handle lookup and swap-back completion removal.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct TweenTable
{
    public TweenData* Dense;
    public int* SlotToDense;
    public uint* Generations;
    public uint* FreeSlots;
    public int Capacity;
    public int FreeCount;
    public int NextSlot;
    public int ActiveCount;

    public static TweenTable Create(ref LinearArena arena, int maxTweens = 4096)
    {
        TweenData* dense = arena.Alloc<TweenData>(maxTweens, 64);
        int* slotToDense = arena.AllocUninitialized<int>(maxTweens, 64);
        uint* gens = arena.AllocUninitialized<uint>(maxTweens, 64);
        uint* freeSlots = arena.AllocUninitialized<uint>(maxTweens, 64);
        for (int i = 0; i < maxTweens; i++)
        {
            slotToDense[i] = -1;
            gens[i] = 1;
        }

        return new TweenTable
        {
            Dense = dense,
            SlotToDense = slotToDense,
            Generations = gens,
            FreeSlots = freeSlots,
            Capacity = maxTweens,
            FreeCount = 0,
            NextSlot = 0,
            ActiveCount = 0,
        };
    }
}

public static unsafe class TweenSystem
{
    public static TweenHandle Spawn(ref TweenTable table, in TweenData template)
    {
        uint slot;
        if (table.FreeCount > 0)
        {
            slot = table.FreeSlots[--table.FreeCount];
        }
        else
        {
            if (table.NextSlot >= table.Capacity)
                throw new OutOfMemoryException($"TweenTable capacity ({table.Capacity}) exceeded.");
            slot = (uint)table.NextSlot++;
        }

        TweenHandle handle = TweenHandle.Create(slot, table.Generations[slot]);
        int denseIdx = table.ActiveCount++;
        table.SlotToDense[slot] = denseIdx;

        TweenData data = template;
        data.Handle = handle;
        data.Flags |= 1; // Active
        if (data.TimeScale <= 0f) data.TimeScale = 1f;
        if (data.Duration <= 0f) data.Duration = 0.0001f;
        table.Dense[denseIdx] = data;
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TweenData* TryGetPtr(in TweenTable table, TweenHandle handle)
    {
        if (!handle.IsValid) return null;
        uint slot = handle.Index;
        if (slot >= (uint)table.Capacity) return null;
        if (table.Generations[slot] != handle.Generation) return null;
        int d = table.SlotToDense[slot];
        return (d >= 0 && d < table.ActiveCount) ? table.Dense + d : null;
    }

    public static bool IsActive(in TweenTable table, TweenHandle handle) => TryGetPtr(in table, handle) != null;

    // --- Ergonomic Data Builders ---

    public static TweenHandle TweenFloatPtr(
        ref TweenTable table, float* target, float from, float to, float duration,
        EaseType ease = EaseType.OutQuad, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.PtrFloat,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = new Vec4(from, 0, 0, 0),
            EndValue = new Vec4(to, 0, 0, 0),
            TargetPtr = target,
        });
    }

    public static TweenHandle TweenVec3Ptr(
        ref TweenTable table, Vec3* target, Vec3 from, Vec3 to, float duration,
        EaseType ease = EaseType.OutQuad, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.PtrVec3,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = new Vec4(from.X, from.Y, from.Z, 0),
            EndValue = new Vec4(to.X, to.Y, to.Z, 0),
            TargetPtr = target,
        });
    }

    public static TweenHandle TweenColorPtr(
        ref TweenTable table, ColorF* target, ColorF from, ColorF to, float duration,
        EaseType ease = EaseType.OutQuad, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.PtrColorF,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = from.ToVec4(),
            EndValue = to.ToVec4(),
            TargetPtr = target,
        });
    }

    public static TweenHandle TweenFix32Ptr(
        ref TweenTable table, Fix32* target, Fix32 from, Fix32 to, float duration,
        EaseType ease = EaseType.OutQuad, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.PtrFix32,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = new Vec4(from.ToFloat(), 0, 0, 0),
            EndValue = new Vec4(to.ToFloat(), 0, 0, 0),
            TargetPtr = target,
        });
    }

    public static TweenHandle TweenTransformPos(
        ref TweenTable table, EntityId entity, Vec3 from, Vec3 to, float duration,
        EaseType ease = EaseType.OutCubic, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.TransformPosition,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = new Vec4(from, 0),
            EndValue = new Vec4(to, 0),
            TargetHandleRaw = entity.Raw,
        });
    }

    public static TweenHandle TweenTransformScale(
        ref TweenTable table, EntityId entity, Vec3 from, Vec3 to, float duration,
        EaseType ease = EaseType.OutBack, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.TransformScale,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = new Vec4(from, 0),
            EndValue = new Vec4(to, 0),
            TargetHandleRaw = entity.Raw,
        });
    }

    public static TweenHandle TweenTransformRot(
        ref TweenTable table, EntityId entity, Quat from, Quat to, float duration,
        EaseType ease = EaseType.InOutQuad, float delay = 0f,
        TweenLoopMode loopMode = TweenLoopMode.Once, int loops = 0,
        SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.TransformRotation,
            Ease = ease,
            LoopMode = loopMode,
            Delay = delay,
            Duration = duration,
            RemainingLoops = loops,
            StartValue = new Vec4(from.X, from.Y, from.Z, from.W),
            EndValue = new Vec4(to.X, to.Y, to.Z, to.W),
            TargetHandleRaw = entity.Raw,
        });
    }

    public static TweenHandle TweenCameraFov(
        ref TweenTable table, CameraHandle camera, float fromFov, float toFov, float duration,
        EaseType ease = EaseType.OutCubic, SceneId scene = default, uint groupId = 0)
    {
        return Spawn(ref table, new TweenData
        {
            OwnerScene = scene,
            GroupId = groupId,
            TargetKind = TweenTargetKind.CameraFov,
            Ease = ease,
            Duration = duration,
            StartValue = new Vec4(fromFov, 0, 0, 0),
            EndValue = new Vec4(toFov, 0, 0, 0),
            TargetHandleRaw = camera.Raw,
        });
    }

    /// <summary>
    /// Chains 'next' to start automatically when 'first' completes (Tween Sequence!).
    /// </summary>
    public static void Chain(ref TweenTable table, TweenHandle first, TweenHandle next)
    {
        TweenData* f = TryGetPtr(in table, first);
        TweenData* n = TryGetPtr(in table, next);
        if (f == null || n == null) return;
        f->NextInSequence = next;
        n->Flags |= 16; // WaitingInSequence
    }

    public static bool Kill(ref TweenTable table, TweenHandle handle)
    {
        TweenData* ptr = TryGetPtr(in table, handle);
        if (ptr == null) return false;
        int denseIdx = (int)(ptr - table.Dense);
        RemoveDenseAt(ref table, denseIdx);
        return true;
    }

    public static void KillByScene(ref TweenTable table, SceneId scene)
    {
        for (int i = table.ActiveCount - 1; i >= 0; i--)
        {
            if (table.Dense[i].OwnerScene == scene)
                RemoveDenseAt(ref table, i);
        }
    }

    public static void KillGroup(ref TweenTable table, uint groupId)
    {
        for (int i = table.ActiveCount - 1; i >= 0; i--)
        {
            if (table.Dense[i].GroupId == groupId)
                RemoveDenseAt(ref table, i);
        }
    }

    public static void SetGroupPaused(ref TweenTable table, uint groupId, bool paused)
    {
        for (int i = 0; i < table.ActiveCount; i++)
        {
            if (table.Dense[i].GroupId == groupId)
            {
                if (paused) table.Dense[i].Flags |= 2;
                else table.Dense[i].Flags = (byte)(table.Dense[i].Flags & ~2);
            }
        }
    }

    private static void RemoveDenseAt(ref TweenTable table, int denseIdx)
    {
        TweenHandle h = table.Dense[denseIdx].Handle;
        uint slot = h.Index;
        uint nextGen = (table.Generations[slot] + 1) & HandlePacking.GenerationMask;
        table.Generations[slot] = nextGen == 0 ? 1u : nextGen;
        table.SlotToDense[slot] = -1;
        table.FreeSlots[table.FreeCount++] = slot;

        int last = --table.ActiveCount;
        if (denseIdx != last)
        {
            table.Dense[denseIdx] = table.Dense[last];
            table.SlotToDense[table.Dense[denseIdx].Handle.Index] = denseIdx;
        }
    }

    /// <summary>
    /// Advances all active tweens linearly in cache-friendly order and writes interpolated values.
    /// Zero heap allocations.
    /// </summary>
    public static void StepAll(
        ref TweenTable table,
        ref TransformTable transforms,
        ref CameraTable cameras,
        float dt)
    {
        int i = 0;
        while (i < table.ActiveCount)
        {
            ref TweenData tw = ref table.Dense[i];
            if (tw.IsPaused || tw.IsWaitingInSequence)
            {
                i++;
                continue;
            }

            float step = dt * tw.TimeScale;
            if (tw.Delay > 0f)
            {
                tw.Delay -= step;
                if (tw.Delay > 0f)
                {
                    i++;
                    continue;
                }
                step = -tw.Delay;
                tw.Delay = 0f;
            }

            tw.Elapsed += step;
            float rawT = tw.Elapsed / tw.Duration;
            bool finishedPass = rawT >= 1f;
            float clampedT = DetMath.Clamp01(rawT);
            float evalT = tw.IsReversed ? (1f - clampedT) : clampedT;
            float eased = tw.UseCustomCurve ? tw.Curve.Evaluate(evalT) : Easing.Evaluate(tw.Ease, evalT);

            ApplyValue(in tw, eased, ref transforms, ref cameras);

            if (finishedPass)
            {
                bool shouldLoop = tw.LoopMode != TweenLoopMode.Once && (tw.RemainingLoops != 0);
                if (shouldLoop)
                {
                    if (tw.RemainingLoops > 0) tw.RemainingLoops--;
                    tw.Elapsed -= tw.Duration;
                    if (tw.LoopMode == TweenLoopMode.PingPong)
                        tw.Flags ^= 4; // toggle Reversed
                    i++;
                }
                else
                {
                    TweenHandle next = tw.NextInSequence;
                    var onComplete = tw.OnComplete;
                    uint userData = tw.UserData;

                    RemoveDenseAt(ref table, i);

                    if (next.IsValid)
                    {
                        TweenData* nextPtr = TryGetPtr(in table, next);
                        if (nextPtr != null)
                            nextPtr->Flags = (byte)(nextPtr->Flags & ~16); // release sequence wait
                    }

                    if (onComplete != null)
                        onComplete(userData);
                    // Note: do NOT increment i because swap-back moved last item into index i
                }
            }
            else
            {
                i++;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyValue(in TweenData tw, float t, ref TransformTable transforms, ref CameraTable cameras)
    {
        Vec4 v = Vec4.Lerp(tw.StartValue, tw.EndValue, t);

        switch (tw.TargetKind)
        {
            case TweenTargetKind.PtrFloat:
                if (tw.TargetPtr != null) *(float*)tw.TargetPtr = v.X;
                break;
            case TweenTargetKind.PtrVec2:
                if (tw.TargetPtr != null) *(Vec2*)tw.TargetPtr = new Vec2(v.X, v.Y);
                break;
            case TweenTargetKind.PtrVec3:
                if (tw.TargetPtr != null) *(Vec3*)tw.TargetPtr = new Vec3(v.X, v.Y, v.Z);
                break;
            case TweenTargetKind.PtrVec4:
                if (tw.TargetPtr != null) *(Vec4*)tw.TargetPtr = v;
                break;
            case TweenTargetKind.PtrColorF:
                if (tw.TargetPtr != null) *(ColorF*)tw.TargetPtr = new ColorF(v.X, v.Y, v.Z, v.W);
                break;
            case TweenTargetKind.PtrQuat:
                if (tw.TargetPtr != null)
                {
                    Quat qa = new Quat(tw.StartValue.X, tw.StartValue.Y, tw.StartValue.Z, tw.StartValue.W);
                    Quat qb = new Quat(tw.EndValue.X, tw.EndValue.Y, tw.EndValue.Z, tw.EndValue.W);
                    *(Quat*)tw.TargetPtr = Quat.Slerp(qa, qb, t);
                }
                break;
            case TweenTargetKind.PtrFix32:
                if (tw.TargetPtr != null) *(Fix32*)tw.TargetPtr = Fix32.FromFloat(v.X);
                break;
            case TweenTargetKind.PtrFixVec3:
                if (tw.TargetPtr != null) *(FixVec3*)tw.TargetPtr = FixVec3.FromFloat(v.X, v.Y, v.Z);
                break;

            case TweenTargetKind.TransformPosition:
                TransformSystem.SetLocalPosition(ref transforms, new EntityId(tw.TargetHandleRaw), new Vec3(v.X, v.Y, v.Z));
                break;
            case TweenTargetKind.TransformScale:
                TransformSystem.SetLocalScale(ref transforms, new EntityId(tw.TargetHandleRaw), new Vec3(v.X, v.Y, v.Z));
                break;
            case TweenTargetKind.TransformRotation:
                {
                    Quat qa = new Quat(tw.StartValue.X, tw.StartValue.Y, tw.StartValue.Z, tw.StartValue.W);
                    Quat qb = new Quat(tw.EndValue.X, tw.EndValue.Y, tw.EndValue.Z, tw.EndValue.W);
                    TransformSystem.SetLocalRotation(ref transforms, new EntityId(tw.TargetHandleRaw), Quat.Slerp(qa, qb, t));
                }
                break;

            case TweenTargetKind.CameraPosition:
                {
                    CameraData* cam = CameraSystem.TryGetPtr(in cameras, new CameraHandle(tw.TargetHandleRaw));
                    if (cam != null) cam->Position = new Vec3(v.X, v.Y, v.Z);
                }
                break;
            case TweenTargetKind.CameraFov:
                {
                    CameraData* cam = CameraSystem.TryGetPtr(in cameras, new CameraHandle(tw.TargetHandleRaw));
                    if (cam != null) cam->FovYDeg = v.X;
                }
                break;
            case TweenTargetKind.CameraOrthoHeight:
                {
                    CameraData* cam = CameraSystem.TryGetPtr(in cameras, new CameraHandle(tw.TargetHandleRaw));
                    if (cam != null) cam->OrthoHeight = v.X;
                }
                break;
            case TweenTargetKind.CameraOrbitYaw:
                {
                    CameraData* cam = CameraSystem.TryGetPtr(in cameras, new CameraHandle(tw.TargetHandleRaw));
                    if (cam != null) cam->OrbitYaw = v.X;
                }
                break;
            case TweenTargetKind.CameraOrbitDistance:
                {
                    CameraData* cam = CameraSystem.TryGetPtr(in cameras, new CameraHandle(tw.TargetHandleRaw));
                    if (cam != null) cam->OrbitDistance = v.X;
                }
                break;

            case TweenTargetKind.CustomCallback:
                if (tw.OnStep != null) tw.OnStep(tw.UserData, v);
                break;
        }
    }
}

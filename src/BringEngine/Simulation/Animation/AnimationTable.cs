using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.RenderData;

namespace BringEngine.Simulation.Animation;

public enum AnimInterpolation : byte
{
    Linear = 0,
    Step = 1,
    CubicSpline = 2,
}

public enum AnimTargetPath : byte
{
    Translation = 0,
    Rotation = 1,
    Scale = 2,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AnimSamplerData
{
    public float* Times;
    public float* Values; // 3 floats per key (T/S) or 4 floats per key (R); x3 for CubicSpline (inTangent, val, outTangent)
    public int KeyframeCount;
    public byte ComponentsPerValue; // 3 or 4
    public AnimInterpolation Interpolation;
}

[StructLayout(LayoutKind.Sequential)]
public struct AnimChannelData
{
    public int SamplerIndex;
    public int TargetJointIndex;
    public AnimTargetPath Path;
    public short TargetSkinPlusOne; // 0 = any/all skins; skinIdx + 1 = targets glTF skin skinIdx
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AnimClipData
{
    public AnimClipHandle Handle;
    public StringId Name;
    public BundleId OwnerBundle;
    public float Duration;
    public AnimSamplerData* Samplers;
    public int SamplerCount;
    public AnimChannelData* Channels;
    public int ChannelCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SkeletonData
{
    public SkeletonHandle Handle;
    public BundleId OwnerBundle;
    public int JointCount;
    public int* ParentIndices;        // -1 for root joint; topological order (parent < child)
    public Mat4* InverseBindMatrices; // Inverse bind pose per joint
    public Vec3* BindPositions;
    public Quat* BindRotations;
    public Vec3* BindScales;
    public short SkinIndexPlusOne;    // 0 = any/untagged; skinIdx + 1 = glTF skin skinIdx
}

[StructLayout(LayoutKind.Sequential)]
public struct AnimTrackState
{
    public AnimClipHandle Clip;
    public AnimClipHandle FromClip;
    public float Time;
    public float FromTime;
    public float CrossfadeElapsed;
    public float CrossfadeDuration;
    public float Speed;
    public float Weight;
    public float FadeOutElapsed;
    public float FadeOutDuration;
    public float FadeOutStartWeight;
    public ulong JointMaskLo;
    public ulong JointMaskHi;
    public byte Loop;
    public byte FromLoop;
    public byte UseJointMask;
    public byte Additive;
    public byte Active;
}

[InlineArray(3)]
public struct AnimTrackArray3
{
    private AnimTrackState _element0;
}

[StructLayout(LayoutKind.Sequential)]
public struct AnimPlayerData
{
    public AnimPlayerHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;
    public SkeletonHandle Skeleton;
    public AnimClipHandle Clip;
    public AnimClipHandle NextClip;
    public float Time;
    public float NextTime;
    public float CrossfadeElapsed;
    public float CrossfadeDuration;
    public float Speed;
    public byte Loop;
    public byte Playing;
    public int FrameJointOffset; // Start index into FrameJointMatrices slice for GPU upload
    public int FrameJointCount;

    // Track 0 extended blending/fade/mask metadata (v0.12.0)
    public float Track0Weight;
    public float Track0FadeOutElapsed;
    public float Track0FadeOutDuration;
    public float Track0FadeOutStartWeight;
    public ulong Track0JointMaskLo;
    public ulong Track0JointMaskHi;
    public byte Track0UseJointMask;
    public byte Track0FromLoop;
    public byte Track0Additive;
    public byte Track0Active;

    // Overlay / blend tracks 1..3 (zero heap allocations)
    public AnimTrackArray3 ExtraTracks;
}

public enum SpriteAnimLoopMode : byte
{
    Hold = 0,
    Loop = 1,
    PingPong = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct SpriteAnimClipData
{
    public SpriteAnimClipHandle Handle;
    public StringId Name;
    public SceneId OwnerScene;
    public BundleId OwnerBundle;
    public int StartFrame;
    public int FrameCount;
    public int Columns;
    public int Rows;
    public float Fps;
    public byte Loop;
    public SpriteAnimLoopMode LoopMode;
    public Vec4 BaseUvRect; // (u0, v0, uWidth, vHeight); Z <= 0 or W <= 0 treated as (0, 0, 1, 1)
}

[StructLayout(LayoutKind.Sequential)]
public struct SpriteAnimPlayerData
{
    public SpriteAnimPlayerHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;
    public SpriteAnimClipHandle Clip;
    public SpriteHandle TargetSprite; // Optional direct SpriteHandle binding (auto-updated in StepAll)
    public float Time;
    public float Speed;
    public int CurrentFrame;
    public Vec4 CurrentUvRect; // (u0, v0, uWidth, vHeight)
    public byte Playing;
}

/// <summary>
/// Central data-driven storage for skeletons, keyframe animation clips, skeletal players,
/// and 2D sprite-sheet animation clips/players.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AnimationTable
{
    public SkeletonData* Skeletons;
    public uint* SkeletonGens;
    public byte* SkeletonAlive;
    public int MaxSkeletons;

    public AnimClipData* Clips;
    public uint* ClipGens;
    public byte* ClipAlive;
    public int MaxClips;

    public AnimPlayerData* Players;
    public int* PlayerSlotToDense;
    public uint* PlayerGens;
    public uint* PlayerFreeSlots;
    public int PlayerCount;
    public int MaxPlayers;
    public int PlayerFreeCount;
    public int PlayerNextSlot;

    public SpriteAnimClipData* SpriteClips;
    public uint* SpriteClipGens;
    public byte* SpriteClipAlive;
    public int SpriteClipCount;
    public int MaxSpriteClips;

    public SpriteAnimPlayerData* SpritePlayers;
    public int* SpritePlayerSlotToDense;
    public uint* SpritePlayerGens;
    public uint* SpritePlayerFreeSlots;
    public int SpritePlayerCount;
    public int MaxSpritePlayers;
    public int SpritePlayerFreeCount;
    public int SpritePlayerNextSlot;

    // Populated each frame in FrameArena during AnimationSystem.StepAll
    public NativeSlice<Mat4> FrameJointPalette;
    public byte* FrameJointPaletteArenaBase;
    public uint FrameJointPaletteArenaReset;

    public static AnimationTable Create(
        ref LinearArena arena,
        int maxSkeletons = 256,
        int maxClips = 512,
        int maxPlayers = 1024,
        int maxSpriteClips = 256,
        int maxSpritePlayers = 2048)
    {
        const int maxCap = (int)(HandlePacking.IndexMask + 1u);
        if (maxSkeletons < 1 || maxSkeletons > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxSkeletons), maxSkeletons, $"Capacity must be in [1, {maxCap}].");
        if (maxClips < 1 || maxClips > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxClips), maxClips, $"Capacity must be in [1, {maxCap}].");
        if (maxPlayers < 1 || maxPlayers > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxPlayers), maxPlayers, $"Capacity must be in [1, {maxCap}].");
        if (maxSpriteClips < 1 || maxSpriteClips > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxSpriteClips), maxSpriteClips, $"Capacity must be in [1, {maxCap}].");
        if (maxSpritePlayers < 1 || maxSpritePlayers > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxSpritePlayers), maxSpritePlayers, $"Capacity must be in [1, {maxCap}].");

        uint* skelGens = arena.AllocUninitialized<uint>(maxSkeletons, 16);
        for (int i = 0; i < maxSkeletons; i++) skelGens[i] = 1;
        uint* clipGens = arena.AllocUninitialized<uint>(maxClips, 16);
        for (int i = 0; i < maxClips; i++) clipGens[i] = 1;
        uint* spriteClipGens = arena.AllocUninitialized<uint>(maxSpriteClips, 16);
        for (int i = 0; i < maxSpriteClips; i++) spriteClipGens[i] = 1;

        int* pSlotToDense = arena.AllocUninitialized<int>(maxPlayers, 64);
        uint* pGens = arena.AllocUninitialized<uint>(maxPlayers, 64);
        for (int i = 0; i < maxPlayers; i++) { pSlotToDense[i] = -1; pGens[i] = 1; }

        int* spSlotToDense = arena.AllocUninitialized<int>(maxSpritePlayers, 64);
        uint* spGens = arena.AllocUninitialized<uint>(maxSpritePlayers, 64);
        for (int i = 0; i < maxSpritePlayers; i++) { spSlotToDense[i] = -1; spGens[i] = 1; }

        return new AnimationTable
        {
            Skeletons = arena.Alloc<SkeletonData>(maxSkeletons, 64),
            SkeletonGens = skelGens,
            SkeletonAlive = arena.Alloc<byte>(maxSkeletons, 16),
            MaxSkeletons = maxSkeletons,

            Clips = arena.Alloc<AnimClipData>(maxClips, 64),
            ClipGens = clipGens,
            ClipAlive = arena.Alloc<byte>(maxClips, 16),
            MaxClips = maxClips,

            Players = arena.Alloc<AnimPlayerData>(maxPlayers, 64),
            PlayerSlotToDense = pSlotToDense,
            PlayerGens = pGens,
            PlayerFreeSlots = arena.AllocUninitialized<uint>(maxPlayers, 64),
            PlayerCount = 0,
            MaxPlayers = maxPlayers,
            PlayerFreeCount = 0,
            PlayerNextSlot = 0,

            SpriteClips = arena.Alloc<SpriteAnimClipData>(maxSpriteClips, 64),
            SpriteClipGens = spriteClipGens,
            SpriteClipAlive = arena.Alloc<byte>(maxSpriteClips, 16),
            SpriteClipCount = 0,
            MaxSpriteClips = maxSpriteClips,

            SpritePlayers = arena.Alloc<SpriteAnimPlayerData>(maxSpritePlayers, 64),
            SpritePlayerSlotToDense = spSlotToDense,
            SpritePlayerGens = spGens,
            SpritePlayerFreeSlots = arena.AllocUninitialized<uint>(maxSpritePlayers, 64),
            SpritePlayerCount = 0,
            MaxSpritePlayers = maxSpritePlayers,
            SpritePlayerFreeCount = 0,
            SpritePlayerNextSlot = 0,
            FrameJointPalette = default,
            FrameJointPaletteArenaBase = null,
            FrameJointPaletteArenaReset = 0,
        };
    }
}

public static unsafe class AnimationSystem
{
    public const int TrackCount = 4;

    private const byte TouchPos = 1 << 0;
    private const byte TouchRot = 1 << 1;
    private const byte TouchScl = 1 << 2;

    public static SkeletonHandle RegisterSkeleton(ref AnimationTable table, in SkeletonData data)
    {
        if (data.JointCount < 0)
            throw new ArgumentOutOfRangeException(nameof(data), data.JointCount, "Skeleton JointCount must be >= 0.");
        if (data.JointCount > 0)
        {
            if (data.ParentIndices == null) throw new ArgumentNullException(nameof(data), "Skeleton ParentIndices must not be null when JointCount > 0.");
            if (data.InverseBindMatrices == null) throw new ArgumentNullException(nameof(data), "Skeleton InverseBindMatrices must not be null when JointCount > 0.");
            if (data.BindPositions == null) throw new ArgumentNullException(nameof(data), "Skeleton BindPositions must not be null when JointCount > 0.");
            if (data.BindRotations == null) throw new ArgumentNullException(nameof(data), "Skeleton BindRotations must not be null when JointCount > 0.");
            if (data.BindScales == null) throw new ArgumentNullException(nameof(data), "Skeleton BindScales must not be null when JointCount > 0.");
        }

        for (int i = 0; i < table.MaxSkeletons; i++)
        {
            if (table.SkeletonAlive[i] == 0)
            {
                var h = SkeletonHandle.Create((uint)i, table.SkeletonGens[i]);
                table.SkeletonAlive[i] = 1;
                table.Skeletons[i] = data;
                table.Skeletons[i].Handle = h;
                return h;
            }
        }
        throw new OutOfMemoryException("AnimationTable skeleton slots exhausted.");
    }

    public static AnimClipHandle RegisterClip(ref AnimationTable table, in AnimClipData data)
    {
        for (int i = 0; i < table.MaxClips; i++)
        {
            if (table.ClipAlive[i] == 0)
            {
                var h = AnimClipHandle.Create((uint)i, table.ClipGens[i]);
                table.ClipAlive[i] = 1;
                table.Clips[i] = data;
                table.Clips[i].Handle = h;
                return h;
            }
        }
        throw new OutOfMemoryException("AnimationTable clip slots exhausted.");
    }

    internal static void UnregisterSkeleton(ref AnimationTable table, SkeletonHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.MaxSkeletons) return;
        uint i = handle.Index;
        if (table.SkeletonAlive[i] == 0 || table.SkeletonGens[i] != handle.Generation) return;
        table.SkeletonAlive[i] = 0;
        table.SkeletonGens[i] = HandlePacking.NextGeneration(table.SkeletonGens[i]);
    }

    internal static void UnregisterClip(ref AnimationTable table, AnimClipHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.MaxClips) return;
        uint i = handle.Index;
        if (table.ClipAlive[i] == 0 || table.ClipGens[i] != handle.Generation) return;
        table.ClipAlive[i] = 0;
        table.ClipGens[i] = HandlePacking.NextGeneration(table.ClipGens[i]);
    }

    public static void UnregisterByBundle(ref AnimationTable table, BundleId bundle)
    {
        if (!bundle.IsValid) return;
        for (int i = 0; i < table.MaxSkeletons; i++)
        {
            if (table.SkeletonAlive[i] != 0 && table.Skeletons[i].OwnerBundle == bundle)
            {
                table.SkeletonAlive[i] = 0;
                table.SkeletonGens[i] = HandlePacking.NextGeneration(table.SkeletonGens[i]);
            }
        }
        for (int i = 0; i < table.MaxClips; i++)
        {
            if (table.ClipAlive[i] != 0 && table.Clips[i].OwnerBundle == bundle)
            {
                table.ClipAlive[i] = 0;
                table.ClipGens[i] = HandlePacking.NextGeneration(table.ClipGens[i]);
            }
        }
        for (int i = 0; i < table.MaxSpriteClips; i++)
        {
            if (table.SpriteClipAlive[i] != 0 && table.SpriteClips[i].OwnerBundle == bundle)
            {
                table.SpriteClipAlive[i] = 0;
                table.SpriteClipCount--;
                table.SpriteClipGens[i] = HandlePacking.NextGeneration(table.SpriteClipGens[i]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SkeletonData* TryGetSkeleton(in AnimationTable table, SkeletonHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.MaxSkeletons) return null;
        if (table.SkeletonAlive[handle.Index] == 0 || table.SkeletonGens[handle.Index] != handle.Generation) return null;
        return table.Skeletons + handle.Index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AnimClipData* TryGetClip(in AnimationTable table, AnimClipHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.MaxClips) return null;
        if (table.ClipAlive[handle.Index] == 0 || table.ClipGens[handle.Index] != handle.Generation) return null;
        return table.Clips + handle.Index;
    }

    public static AnimPlayerHandle AddPlayer(
        ref AnimationTable table,
        EntityId entity,
        SkeletonHandle skeleton,
        AnimClipHandle clip,
        SceneId scene = default,
        float speed = 1f,
        bool loop = true)
    {
        uint slot;
        if (table.PlayerFreeCount > 0)
        {
            slot = table.PlayerFreeSlots[--table.PlayerFreeCount];
        }
        else
        {
            if (table.PlayerNextSlot >= table.MaxPlayers)
                throw new OutOfMemoryException("AnimationTable players exhausted.");
            slot = (uint)table.PlayerNextSlot++;
        }

        var handle = AnimPlayerHandle.Create(slot, table.PlayerGens[slot]);
        int idx = table.PlayerCount++;
        table.PlayerSlotToDense[slot] = idx;
        byte loopByte = (byte)(loop ? 1 : 0);
        var player = new AnimPlayerData
        {
            Handle = handle,
            Entity = entity,
            OwnerScene = scene,
            Skeleton = skeleton,
            Clip = clip,
            Speed = speed,
            Loop = loopByte,
            Playing = 1,
            Track0Weight = 1f,
            Track0FromLoop = loopByte,
            Track0Active = 1,
        };
        for (int t = 0; t < TrackCount - 1; t++)
        {
            player.ExtraTracks[t] = new AnimTrackState
            {
                Speed = 1f,
                Weight = 0f,
                Loop = 1,
                FromLoop = 1,
                Active = 0,
            };
        }
        table.Players[idx] = player;
        return handle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AnimPlayerData* TryGetPlayer(in AnimationTable table, AnimPlayerHandle handle)
    {
        if (!handle.IsValid) return null;
        uint slot = handle.Index;
        if (slot >= (uint)table.MaxPlayers) return null;
        if (table.PlayerGens[slot] != handle.Generation) return null;
        int d = table.PlayerSlotToDense[slot];
        return (d >= 0 && d < table.PlayerCount) ? table.Players + d : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AnimPlayerData* TryGetPlayerPtr(in AnimationTable table, AnimPlayerHandle handle)
        => TryGetPlayer(in table, handle);

    public static bool RemovePlayer(ref AnimationTable table, AnimPlayerHandle handle)
    {
        AnimPlayerData* ptr = TryGetPlayer(in table, handle);
        if (ptr == null) return false;
        RemovePlayerDenseAt(ref table, (int)(ptr - table.Players));
        return true;
    }

    private static void RemovePlayerDenseAt(ref AnimationTable table, int denseIdx)
    {
        uint slot = table.Players[denseIdx].Handle.Index;
        table.PlayerGens[slot] = HandlePacking.NextGeneration(table.PlayerGens[slot]);
        table.PlayerSlotToDense[slot] = -1;
        table.PlayerFreeSlots[table.PlayerFreeCount++] = slot;

        int last = --table.PlayerCount;
        if (denseIdx != last)
        {
            table.Players[denseIdx] = table.Players[last];
            table.PlayerSlotToDense[table.Players[denseIdx].Handle.Index] = denseIdx;
        }
    }

    private static void CrossfadeCore(in AnimationTable table, AnimPlayerData* p, AnimClipHandle nextClip, float duration, bool overrideNextLoop, bool nextLoop)
    {
        bool liveCrossfade = p->NextClip.IsValid && TryGetClip(in table, p->NextClip) != null && p->CrossfadeDuration > 1e-5f;
        if (!liveCrossfade)
            p->Track0FromLoop = p->Loop;
        if (overrideNextLoop)
            p->Loop = (byte)(nextLoop ? 1 : 0);
        p->NextClip = nextClip;
        p->NextTime = 0f;
        p->CrossfadeElapsed = 0f;
        p->CrossfadeDuration = (float.IsFinite(duration) && duration > 0f) ? duration : 0.001f;
        p->Track0FadeOutElapsed = 0f;
        p->Track0FadeOutDuration = 0f;
        p->Track0FadeOutStartWeight = 0f;
        p->Track0Active = 1;
        if (!float.IsFinite(p->Track0Weight) || p->Track0Weight <= 1e-5f)
            p->Track0Weight = 1f;
    }

    public static void Crossfade(ref AnimationTable table, EntityId entity, AnimClipHandle nextClip, float duration)
    {
        for (int i = 0; i < table.PlayerCount; i++)
        {
            if (table.Players[i].Entity == entity)
            {
                CrossfadeCore(in table, table.Players + i, nextClip, duration, overrideNextLoop: false, nextLoop: false);
                return;
            }
        }
    }

    public static void Crossfade(ref AnimationTable table, EntityId entity, AnimClipHandle nextClip, float duration, bool nextLoop)
    {
        for (int i = 0; i < table.PlayerCount; i++)
        {
            if (table.Players[i].Entity == entity)
            {
                CrossfadeCore(in table, table.Players + i, nextClip, duration, overrideNextLoop: true, nextLoop: nextLoop);
                return;
            }
        }
    }

    public static void Crossfade(ref AnimationTable table, AnimPlayerHandle handle, AnimClipHandle nextClip, float duration)
    {
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        CrossfadeCore(in table, p, nextClip, duration, overrideNextLoop: false, nextLoop: false);
    }

    public static void Crossfade(ref AnimationTable table, AnimPlayerHandle handle, AnimClipHandle nextClip, float duration, bool nextLoop)
    {
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        CrossfadeCore(in table, p, nextClip, duration, overrideNextLoop: true, nextLoop: nextLoop);
    }

    public static bool SetTrack(
        ref AnimationTable table,
        AnimPlayerHandle handle,
        int track,
        AnimClipHandle clip,
        bool loop = true,
        float crossfadeDuration = 0.15f,
        float speed = 1f,
        float weight = 1f,
        bool additive = false)
    {
        if ((uint)track >= TrackCount) return false;
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return false;
        if (TryGetClip(in table, clip) == null) return false;

        float spd = float.IsFinite(speed) ? speed : 1f;
        float w = DetMath.Clamp01(float.IsFinite(weight) ? weight : 1f);
        float cf = (float.IsFinite(crossfadeDuration) && crossfadeDuration > 1e-5f) ? crossfadeDuration : 0f;
        byte loopByte = (byte)(loop ? 1 : 0);
        byte addByte = (byte)(additive ? 1 : 0);

        if (track == 0)
        {
            p->Track0FadeOutElapsed = 0f;
            p->Track0FadeOutDuration = 0f;
            p->Track0FadeOutStartWeight = 0f;

            bool liveCrossfade = p->Track0Active != 0 && p->NextClip.IsValid && TryGetClip(in table, p->NextClip) != null && p->CrossfadeDuration > 1e-5f;
            if (liveCrossfade && p->NextClip != clip && (p->Clip == clip || p->CrossfadeElapsed >= p->CrossfadeDuration * 0.5f))
            {
                p->Clip = p->NextClip;
                p->Time = p->NextTime;
                p->Track0FromLoop = p->Loop;
                p->NextClip = AnimClipHandle.Invalid;
                p->NextTime = 0f;
                p->CrossfadeElapsed = 0f;
                p->CrossfadeDuration = 0f;
                liveCrossfade = false;
            }

            bool canMix = p->Track0Active != 0 && p->Clip.IsValid && TryGetClip(in table, p->Clip) != null && p->Clip != clip && cf > 0f;
            if (canMix)
            {
                if (!liveCrossfade)
                    p->Track0FromLoop = p->Loop;
                p->NextClip = clip;
                p->NextTime = 0f;
                p->CrossfadeElapsed = 0f;
                p->CrossfadeDuration = cf;
            }
            else
            {
                p->Clip = clip;
                p->Time = 0f;
                p->NextClip = AnimClipHandle.Invalid;
                p->NextTime = 0f;
                p->CrossfadeElapsed = 0f;
                p->CrossfadeDuration = 0f;
                p->Track0FromLoop = loopByte;
            }

            p->Speed = spd;
            p->Loop = loopByte;
            p->Track0Weight = w;
            p->Track0Additive = addByte;
            p->Track0Active = 1;
            return true;
        }

        ref AnimTrackState tr = ref p->ExtraTracks[track - 1];
        tr.FadeOutElapsed = 0f;
        tr.FadeOutDuration = 0f;
        tr.FadeOutStartWeight = 0f;

        bool canTrackMix = tr.Active != 0 && tr.Clip.IsValid && TryGetClip(in table, tr.Clip) != null && tr.Clip != clip && cf > 0f;
        if (canTrackMix)
        {
            tr.FromClip = tr.Clip;
            tr.FromTime = tr.Time;
            tr.FromLoop = tr.Loop;
            tr.CrossfadeElapsed = 0f;
            tr.CrossfadeDuration = cf;
        }
        else
        {
            tr.FromClip = AnimClipHandle.Invalid;
            tr.FromTime = 0f;
            tr.FromLoop = loopByte;
            tr.CrossfadeElapsed = 0f;
            tr.CrossfadeDuration = 0f;
        }

        tr.Clip = clip;
        tr.Time = 0f;
        tr.Speed = spd;
        tr.Weight = w;
        tr.Loop = loopByte;
        tr.Additive = addByte;
        tr.Active = 1;
        return true;
    }

    public static void SetTrackWeight(ref AnimationTable table, AnimPlayerHandle handle, int track, float weight)
    {
        if ((uint)track >= TrackCount) return;
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        float w = DetMath.Clamp01(float.IsFinite(weight) ? weight : 0f);
        if (track == 0)
        {
            p->Track0Weight = w;
            p->Track0FadeOutElapsed = 0f;
            p->Track0FadeOutDuration = 0f;
            p->Track0FadeOutStartWeight = 0f;
        }
        else
        {
            ref AnimTrackState tr = ref p->ExtraTracks[track - 1];
            tr.Weight = w;
            tr.FadeOutElapsed = 0f;
            tr.FadeOutDuration = 0f;
            tr.FadeOutStartWeight = 0f;
        }
    }

    public static void SetTrackSpeed(ref AnimationTable table, AnimPlayerHandle handle, int track, float speed)
    {
        if ((uint)track >= TrackCount) return;
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        float s = float.IsFinite(speed) ? speed : 1f;
        if (track == 0) p->Speed = s;
        else p->ExtraTracks[track - 1].Speed = s;
    }

    public static void SetTrackJointMask(ref AnimationTable table, AnimPlayerHandle handle, int track, ulong maskLo, ulong maskHi = 0UL)
    {
        if ((uint)track >= TrackCount) return;
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        if (track == 0)
        {
            p->Track0JointMaskLo = maskLo;
            p->Track0JointMaskHi = maskHi;
            p->Track0UseJointMask = 1;
        }
        else
        {
            ref AnimTrackState tr = ref p->ExtraTracks[track - 1];
            tr.JointMaskLo = maskLo;
            tr.JointMaskHi = maskHi;
            tr.UseJointMask = 1;
        }
    }

    public static void ClearTrackJointMask(ref AnimationTable table, AnimPlayerHandle handle, int track)
    {
        if ((uint)track >= TrackCount) return;
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        if (track == 0)
        {
            p->Track0JointMaskLo = 0UL;
            p->Track0JointMaskHi = 0UL;
            p->Track0UseJointMask = 0;
        }
        else
        {
            ref AnimTrackState tr = ref p->ExtraTracks[track - 1];
            tr.JointMaskLo = 0UL;
            tr.JointMaskHi = 0UL;
            tr.UseJointMask = 0;
        }
    }

    public static void ClearTrack(ref AnimationTable table, AnimPlayerHandle handle, int track, float fadeOutDuration = 0f)
    {
        if ((uint)track >= TrackCount) return;
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        float fd = (float.IsFinite(fadeOutDuration) && fadeOutDuration > 1e-5f) ? fadeOutDuration : 0f;

        if (track == 0)
        {
            if (fd <= 0f || p->Track0Active == 0 || !float.IsFinite(p->Track0Weight) || p->Track0Weight <= 1e-5f)
            {
                p->Clip = AnimClipHandle.Invalid;
                p->NextClip = AnimClipHandle.Invalid;
                p->Time = 0f;
                p->NextTime = 0f;
                p->CrossfadeElapsed = 0f;
                p->CrossfadeDuration = 0f;
                p->Track0Weight = 0f;
                p->Track0FadeOutElapsed = 0f;
                p->Track0FadeOutDuration = 0f;
                p->Track0FadeOutStartWeight = 0f;
                p->Track0JointMaskLo = 0UL;
                p->Track0JointMaskHi = 0UL;
                p->Track0UseJointMask = 0;
                p->Track0Additive = 0;
                p->Track0FromLoop = p->Loop;
                p->Track0Active = 0;
            }
            else
            {
                p->Track0FadeOutStartWeight = DetMath.Clamp01(p->Track0Weight);
                p->Track0FadeOutElapsed = 0f;
                p->Track0FadeOutDuration = fd;
            }
            return;
        }

        ref AnimTrackState tr = ref p->ExtraTracks[track - 1];
        if (fd <= 0f || tr.Active == 0 || !float.IsFinite(tr.Weight) || tr.Weight <= 1e-5f)
        {
            tr = new AnimTrackState
            {
                Speed = 1f,
                Weight = 0f,
                Loop = 1,
                FromLoop = 1,
                Active = 0,
            };
        }
        else
        {
            tr.FadeOutStartWeight = DetMath.Clamp01(tr.Weight);
            tr.FadeOutElapsed = 0f;
            tr.FadeOutDuration = fd;
        }
    }

    /// <summary>
    /// Continuous 2-clip blend (e.g. Idle &lt;-&gt; Run) on Track 0 (clipA, weight 1) and Track 1 (clipB, weight Clamp01(blendWeight)).
    /// Preserves clip clocks whenever clipA / clipB are already active on Tracks 0 / 1. Zero GC allocations.
    /// </summary>
    public static bool BlendTwoClips(
        ref AnimationTable table,
        AnimPlayerHandle handle,
        AnimClipHandle clipA,
        AnimClipHandle clipB,
        float blendWeight,
        bool loop = true,
        float speedA = 1f,
        float speedB = 1f)
    {
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return false;
        if (TryGetClip(in table, clipA) == null || TryGetClip(in table, clipB) == null) return false;

        float bw = DetMath.Clamp01(float.IsFinite(blendWeight) ? blendWeight : 0f);
        float sA = float.IsFinite(speedA) ? speedA : 1f;
        float sB = float.IsFinite(speedB) ? speedB : 1f;
        byte loopByte = (byte)(loop ? 1 : 0);

        if (p->Track0Active == 0 || p->Clip != clipA)
        {
            p->Clip = clipA;
            p->Time = 0f;
        }
        p->NextClip = AnimClipHandle.Invalid;
        p->NextTime = 0f;
        p->CrossfadeElapsed = 0f;
        p->CrossfadeDuration = 0f;
        p->Speed = sA;
        p->Loop = loopByte;
        p->Track0FromLoop = loopByte;
        p->Track0Weight = 1f;
        p->Track0FadeOutElapsed = 0f;
        p->Track0FadeOutDuration = 0f;
        p->Track0FadeOutStartWeight = 0f;
        p->Track0JointMaskLo = 0UL;
        p->Track0JointMaskHi = 0UL;
        p->Track0UseJointMask = 0;
        p->Track0Additive = 0;
        p->Track0Active = 1;

        ref AnimTrackState t1 = ref p->ExtraTracks[0];
        if (t1.Active == 0 || t1.Clip != clipB)
        {
            t1.Clip = clipB;
            t1.Time = 0f;
        }
        t1.UseJointMask = 0;
        t1.JointMaskLo = 0UL;
        t1.JointMaskHi = 0UL;
        t1.FromClip = AnimClipHandle.Invalid;
        t1.FromTime = 0f;
        t1.CrossfadeElapsed = 0f;
        t1.CrossfadeDuration = 0f;
        t1.FadeOutElapsed = 0f;
        t1.FadeOutDuration = 0f;
        t1.FadeOutStartWeight = 0f;
        t1.Speed = sB;
        t1.Weight = bw;
        t1.Loop = loopByte;
        t1.FromLoop = loopByte;
        t1.Additive = 0;
        t1.Active = 1;
        return true;
    }

    public static AnimPlayerHandle FindPlayerByEntity(in AnimationTable table, EntityId entity)
    {
        for (int i = 0; i < table.PlayerCount; i++)
            if (table.Players[i].Entity == entity) return table.Players[i].Handle;
        return AnimPlayerHandle.Invalid;
    }

    public static SpriteAnimClipHandle RegisterSpriteClip(ref AnimationTable table, in SpriteAnimClipData clip)
    {
        if (clip.Name.Value != 0)
        {
            int clipFpsBits = BitConverter.SingleToInt32Bits(clip.Fps);
            for (int i = 0; i < table.MaxSpriteClips; i++)
            {
                if (table.SpriteClipAlive[i] != 0)
                {
                    ref readonly SpriteAnimClipData existing = ref table.SpriteClips[i];
                    if (existing.Name == clip.Name &&
                        existing.OwnerScene == clip.OwnerScene &&
                        existing.OwnerBundle == clip.OwnerBundle &&
                        existing.Columns == clip.Columns &&
                        existing.Rows == clip.Rows &&
                        existing.StartFrame == clip.StartFrame &&
                        existing.FrameCount == clip.FrameCount &&
                        BitConverter.SingleToInt32Bits(existing.Fps) == clipFpsBits &&
                        existing.Loop == clip.Loop &&
                        existing.LoopMode == clip.LoopMode &&
                        existing.BaseUvRect.Equals(clip.BaseUvRect))
                    {
                        return existing.Handle;
                    }
                }
            }
        }
        for (int i = 0; i < table.MaxSpriteClips; i++)
        {
            if (table.SpriteClipAlive[i] == 0)
            {
                var h = SpriteAnimClipHandle.Create((uint)i, table.SpriteClipGens[i]);
                table.SpriteClipAlive[i] = 1;
                table.SpriteClips[i] = clip;
                table.SpriteClips[i].Handle = h;
                table.SpriteClipCount++;
                return h;
            }
        }
        throw new OutOfMemoryException("SpriteAnimClips exhausted.");
    }

    /// <summary>
    /// Registers an atlas-aware 2D sprite flipbook clip slicing a (columns x rows) grid strictly inside
    /// a packed atlas SpriteRegionData sub-rectangle. Zero GC allocations.
    /// </summary>
    public static SpriteAnimClipHandle RegisterSpriteClipFromRegion(
        ref AnimationTable table,
        StringId name,
        in SpriteRegionData region,
        int columns,
        int rows,
        float fps,
        int startFrame = 0,
        int frameCount = 0,
        SpriteAnimLoopMode loopMode = SpriteAnimLoopMode.Loop,
        SceneId scene = default,
        BundleId bundle = default)
    {
        int cols = columns > 0 ? columns : 1;
        int rws = rows > 0 ? rows : 1;
        int start = startFrame < 0 ? 0 : startFrame;
        long gridTotal = (long)cols * (long)rws;
        long autoCount = gridTotal - start;
        if (autoCount < 1L) autoCount = 1L;
        else if (autoCount > int.MaxValue) autoCount = int.MaxValue;
        int fc = frameCount > 0 ? frameCount : (int)autoCount;
        return RegisterSpriteClip(ref table, new SpriteAnimClipData
        {
            Name = name,
            OwnerScene = scene,
            OwnerBundle = bundle,
            StartFrame = start,
            FrameCount = fc,
            Columns = cols,
            Rows = rws,
            Fps = fps,
            Loop = (byte)(loopMode != SpriteAnimLoopMode.Hold ? 1 : 0),
            LoopMode = loopMode,
            BaseUvRect = region.UvRect,
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteAnimClipData* TryGetSpriteClip(in AnimationTable table, SpriteAnimClipHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.MaxSpriteClips) return null;
        if (table.SpriteClipAlive[handle.Index] == 0 || table.SpriteClipGens[handle.Index] != handle.Generation) return null;
        return table.SpriteClips + handle.Index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vec4 ComputeSpriteFrameUv(in SpriteAnimClipData clip, int frame)
    {
        int col = frame % clip.Columns;
        int row = frame / clip.Columns;
        if (row >= clip.Rows) row = clip.Rows - 1;

        Vec4 baseUv = (float.IsFinite(clip.BaseUvRect.X) && float.IsFinite(clip.BaseUvRect.Y) &&
                       float.IsFinite(clip.BaseUvRect.Z) && float.IsFinite(clip.BaseUvRect.W) &&
                       clip.BaseUvRect.Z > 0f && clip.BaseUvRect.W > 0f)
            ? clip.BaseUvRect
            : new Vec4(0f, 0f, 1f, 1f);

        float uW = baseUv.Z / clip.Columns;
        float vH = baseUv.W / clip.Rows;
        return new Vec4(baseUv.X + col * uW, baseUv.Y + row * vH, uW, vH);
    }

    public static SpriteAnimPlayerHandle AddSpritePlayer(
        ref AnimationTable table,
        EntityId entity,
        SpriteAnimClipHandle clip,
        SceneId scene = default,
        float speed = 1f,
        SpriteHandle targetSprite = default)
    {
        uint slot;
        if (table.SpritePlayerFreeCount > 0)
        {
            slot = table.SpritePlayerFreeSlots[--table.SpritePlayerFreeCount];
        }
        else
        {
            if (table.SpritePlayerNextSlot >= table.MaxSpritePlayers)
                throw new OutOfMemoryException("SpriteAnimPlayers exhausted.");
            slot = (uint)table.SpritePlayerNextSlot++;
        }

        int initialFrame = 0;
        Vec4 initialUv = new Vec4(0f, 0f, 1f, 1f);
        SpriteAnimClipData* clipPtr = TryGetSpriteClip(in table, clip);
        if (clipPtr != null && clipPtr->Columns > 0 && clipPtr->Rows > 0)
        {
            initialFrame = clipPtr->StartFrame < 0 ? 0 : clipPtr->StartFrame;
            initialUv = ComputeSpriteFrameUv(in *clipPtr, initialFrame);
        }

        var handle = SpriteAnimPlayerHandle.Create(slot, table.SpritePlayerGens[slot]);
        int idx = table.SpritePlayerCount++;
        table.SpritePlayerSlotToDense[slot] = idx;
        table.SpritePlayers[idx] = new SpriteAnimPlayerData
        {
            Handle = handle,
            Entity = entity,
            OwnerScene = scene,
            Clip = clip,
            TargetSprite = targetSprite,
            Speed = speed,
            CurrentFrame = initialFrame,
            CurrentUvRect = initialUv,
            Playing = 1,
        };
        return handle;
    }

    public static void BindSpritePlayer(ref AnimationTable table, SpriteAnimPlayerHandle handle, SpriteHandle targetSprite)
    {
        SpriteAnimPlayerData* sp = TryGetSpritePlayer(in table, handle);
        if (sp != null)
            sp->TargetSprite = targetSprite;
    }

    public static void SetSpriteClip(ref AnimationTable table, SpriteAnimPlayerHandle handle, SpriteAnimClipHandle clip, bool resetTime = true)
    {
        SpriteAnimPlayerData* sp = TryGetSpritePlayer(in table, handle);
        if (sp == null) return;
        sp->Clip = clip;
        if (resetTime)
            sp->Time = 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteAnimPlayerData* TryGetSpritePlayer(in AnimationTable table, SpriteAnimPlayerHandle handle)
    {
        if (!handle.IsValid) return null;
        uint slot = handle.Index;
        if (slot >= (uint)table.MaxSpritePlayers) return null;
        if (table.SpritePlayerGens[slot] != handle.Generation) return null;
        int d = table.SpritePlayerSlotToDense[slot];
        return (d >= 0 && d < table.SpritePlayerCount) ? table.SpritePlayers + d : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteAnimPlayerData* TryGetSpritePlayerPtr(in AnimationTable table, SpriteAnimPlayerHandle handle)
        => TryGetSpritePlayer(in table, handle);

    public static bool RemoveSpritePlayer(ref AnimationTable table, SpriteAnimPlayerHandle handle)
    {
        SpriteAnimPlayerData* ptr = TryGetSpritePlayer(in table, handle);
        if (ptr == null) return false;
        RemoveSpritePlayerDenseAt(ref table, (int)(ptr - table.SpritePlayers));
        return true;
    }

    private static void RemoveSpritePlayerDenseAt(ref AnimationTable table, int denseIdx)
    {
        uint slot = table.SpritePlayers[denseIdx].Handle.Index;
        table.SpritePlayerGens[slot] = HandlePacking.NextGeneration(table.SpritePlayerGens[slot]);
        table.SpritePlayerSlotToDense[slot] = -1;
        table.SpritePlayerFreeSlots[table.SpritePlayerFreeCount++] = slot;

        int last = --table.SpritePlayerCount;
        if (denseIdx != last)
        {
            table.SpritePlayers[denseIdx] = table.SpritePlayers[last];
            table.SpritePlayerSlotToDense[table.SpritePlayers[denseIdx].Handle.Index] = denseIdx;
        }
    }

    public static void RemoveByScene(ref AnimationTable table, SceneId scene)
    {
        for (int i = table.PlayerCount - 1; i >= 0; i--)
        {
            if (table.Players[i].OwnerScene == scene)
                RemovePlayerDenseAt(ref table, i);
        }
        for (int i = table.SpritePlayerCount - 1; i >= 0; i--)
        {
            if (table.SpritePlayers[i].OwnerScene == scene)
                RemoveSpritePlayerDenseAt(ref table, i);
        }
        if (scene.IsValid)
        {
            for (int i = 0; i < table.MaxSpriteClips; i++)
            {
                if (table.SpriteClipAlive[i] != 0 && table.SpriteClips[i].OwnerScene == scene)
                {
                    table.SpriteClipAlive[i] = 0;
                    table.SpriteClipCount--;
                    table.SpriteClipGens[i] = HandlePacking.NextGeneration(table.SpriteClipGens[i]);
                }
            }
        }
    }

    public static void RemoveByEntity(ref AnimationTable table, EntityId entity)
    {
        for (int i = table.PlayerCount - 1; i >= 0; i--)
        {
            if (table.Players[i].Entity == entity)
                RemovePlayerDenseAt(ref table, i);
        }
        for (int i = table.SpritePlayerCount - 1; i >= 0; i--)
        {
            if (table.SpritePlayers[i].Entity == entity)
                RemoveSpritePlayerDenseAt(ref table, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasValidJointBuffers(SkeletonData* skel)
        => skel->JointCount > 0
        && skel->ParentIndices != null
        && skel->InverseBindMatrices != null
        && skel->BindPositions != null
        && skel->BindRotations != null
        && skel->BindScales != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdvanceClipTime(ref float time, float dt, float speed, float duration, bool loop)
    {
        if (dt <= 0f || !float.IsFinite(speed) || duration <= 0f) return;
        time += dt * speed;
        if (!float.IsFinite(time)) time = 0f;
        if (loop)
        {
            time %= duration;
            if (time < 0f) time += duration;
        }
        else
        {
            time = DetMath.Clamp(time, 0f, duration);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsJointInMask(int jointIndex, ulong maskLo, ulong maskHi)
    {
        if ((uint)jointIndex < 64u)
            return (maskLo & (1UL << jointIndex)) != 0UL;
        if ((uint)jointIndex < 128u)
            return (maskHi & (1UL << (jointIndex - 64))) != 0UL;
        return false;
    }

    private static void BlendTrackPoseIntoWorking(
        SkeletonData* skel,
        Vec3* pos,
        Quat* rot,
        Vec3* scl,
        Vec3* posT,
        Quat* rotT,
        Vec3* sclT,
        byte* touchedT,
        int jc,
        float w,
        bool useMask,
        ulong maskLo,
        ulong maskHi,
        bool additive)
    {
        for (int j = 0; j < jc; j++)
        {
            if (useMask && !IsJointInMask(j, maskLo, maskHi)) continue;
            byte t = touchedT[j];
            if (t == 0) continue;

            if (!additive)
            {
                if ((t & TouchPos) != 0) pos[j] = Vec3.Lerp(pos[j], posT[j], w);
                if ((t & TouchRot) != 0) rot[j] = Quat.Slerp(rot[j], rotT[j], w);
                if ((t & TouchScl) != 0) scl[j] = Vec3.Lerp(scl[j], sclT[j], w);
            }
            else
            {
                if ((t & TouchPos) != 0)
                    pos[j] += (posT[j] - skel->BindPositions[j]) * w;
                if ((t & TouchRot) != 0)
                {
                    Quat invBind = skel->BindRotations[j].Normalized().Conjugate();
                    Quat deltaR = Quat.Multiply(rotT[j].Normalized(), invBind).Normalized();
                    rot[j] = Quat.Multiply(Quat.Slerp(Quat.Identity, deltaR, w), rot[j]).Normalized();
                }
                if ((t & TouchScl) != 0)
                {
                    Vec3 bScl = skel->BindScales[j];
                    Vec3 ratio = new Vec3(
                        DetMath.Abs(bScl.X) > 1e-5f ? (sclT[j].X / bScl.X) : 1f,
                        DetMath.Abs(bScl.Y) > 1e-5f ? (sclT[j].Y / bScl.Y) : 1f,
                        DetMath.Abs(bScl.Z) > 1e-5f ? (sclT[j].Z / bScl.Z) : 1f);
                    scl[j] = scl[j] * Vec3.Lerp(Vec3.One, ratio, w);
                }
            }
        }
    }

    /// <summary>
    /// Advances skeletal &amp; sprite animation players and builds the frame joint skinning palette in FrameArena.
    /// Zero GC allocations.
    /// </summary>
    public static void StepAll(ref AnimationTable table, float dt, ref LinearArena frameArena, ref LinearArena scratchArena)
        => StepAll(ref table, dt, ref frameArena, ref scratchArena, null);

    /// <summary>
    /// Advances multi-track 3D skeletal players and atlas-aware 2D sprite flipbook players (optionally writing
    /// updated UvRects directly to bound SpriteHandles in <paramref name="renderComponents"/>). Zero GC allocations.
    /// </summary>
    public static void StepAll(
        ref AnimationTable table,
        float dt,
        ref LinearArena frameArena,
        ref LinearArena scratchArena,
        RenderComponentTables* renderComponents)
    {
        if (!float.IsFinite(dt) || dt < 0f)
            dt = 0f;

        // 1. Compute total joints needed this frame
        long totalJoints = 0;
        for (int i = 0; i < table.PlayerCount; i++)
        {
            SkeletonData* skel = TryGetSkeleton(in table, table.Players[i].Skeleton);
            if (skel != null && HasValidJointBuffers(skel))
                totalJoints += skel->JointCount;
        }
        if (totalJoints > int.MaxValue)
            throw new OverflowException($"Total active skeleton joints ({totalJoints}) exceeds int.MaxValue.");

        if (totalJoints > 0)
        {
            table.FrameJointPalette = frameArena.AllocSlice<Mat4>((int)totalJoints, 64, zeroed: false);
            table.FrameJointPaletteArenaBase = frameArena.BasePtr;
            table.FrameJointPaletteArenaReset = frameArena.ResetCount;
        }
        else
        {
            table.FrameJointPalette = default;
            table.FrameJointPaletteArenaBase = null;
            table.FrameJointPaletteArenaReset = 0;
        }

        int jointCursor = 0;
        for (int i = 0; i < table.PlayerCount; i++)
        {
            ref AnimPlayerData p = ref table.Players[i];
            SkeletonData* skel = TryGetSkeleton(in table, p.Skeleton);
            if (skel == null || !HasValidJointBuffers(skel))
            {
                p.FrameJointOffset = 0;
                p.FrameJointCount = 0;
                continue;
            }

            if (!float.IsFinite(p.Time)) p.Time = 0f;
            if (!float.IsFinite(p.NextTime)) p.NextTime = 0f;
            if (!float.IsFinite(p.CrossfadeElapsed) || p.CrossfadeElapsed < 0f) p.CrossfadeElapsed = 0f;
            if (!float.IsFinite(p.CrossfadeDuration) || p.CrossfadeDuration < 0f) p.CrossfadeDuration = 0f;

            // Legacy guard: direct writes to p.Clip on zero-initialized AnimPlayerData activate Track 0 cleanly
            if (p.Clip.IsValid && p.Track0Active == 0 && p.Track0FadeOutDuration <= 1e-5f)
            {
                p.Track0Active = 1;
                if (!float.IsFinite(p.Track0Weight) || p.Track0Weight <= 1e-5f)
                    p.Track0Weight = 1f;
                p.Track0FromLoop = p.Loop;
            }

            // Advance Track 0 fade-out & clocks
            if (p.Track0Active != 0 && p.Track0FadeOutDuration > 1e-5f)
            {
                if (!float.IsFinite(p.Track0FadeOutElapsed) || p.Track0FadeOutElapsed < 0f) p.Track0FadeOutElapsed = 0f;
                if (!float.IsFinite(p.Track0FadeOutStartWeight)) p.Track0FadeOutStartWeight = DetMath.Clamp01(p.Track0Weight);
                if (p.Playing != 0 && dt > 0f)
                    p.Track0FadeOutElapsed += dt;
                if (p.Track0FadeOutElapsed >= p.Track0FadeOutDuration)
                {
                    p.Clip = AnimClipHandle.Invalid;
                    p.NextClip = AnimClipHandle.Invalid;
                    p.Time = 0f;
                    p.NextTime = 0f;
                    p.CrossfadeElapsed = 0f;
                    p.CrossfadeDuration = 0f;
                    p.Track0Weight = 0f;
                    p.Track0FadeOutElapsed = 0f;
                    p.Track0FadeOutDuration = 0f;
                    p.Track0FadeOutStartWeight = 0f;
                    p.Track0JointMaskLo = 0UL;
                    p.Track0JointMaskHi = 0UL;
                    p.Track0UseJointMask = 0;
                    p.Track0Additive = 0;
                    p.Track0FromLoop = p.Loop;
                    p.Track0Active = 0;
                }
                else
                {
                    float fade = 1f - DetMath.Clamp01(p.Track0FadeOutElapsed / p.Track0FadeOutDuration);
                    p.Track0Weight = p.Track0FadeOutStartWeight * fade;
                }
            }

            AnimClipData* clip = null;
            AnimClipData* nextClip = null;
            float blendAlpha = 0f;

            if (p.Track0Active != 0)
            {
                clip = TryGetClip(in table, p.Clip);
                nextClip = TryGetClip(in table, p.NextClip);
                if (p.NextClip.IsValid && nextClip == null)
                {
                    p.NextClip = AnimClipHandle.Invalid;
                    p.NextTime = 0f;
                    p.CrossfadeElapsed = 0f;
                    p.CrossfadeDuration = 0f;
                }
                bool track0Crossfading = nextClip != null && p.CrossfadeDuration > 1e-5f;
                bool clip0Loop = track0Crossfading ? (p.Track0FromLoop != 0) : (p.Loop != 0);

                if (p.Playing != 0 && dt > 0f && clip != null)
                    AdvanceClipTime(ref p.Time, dt, p.Speed, clip->Duration, clip0Loop);

                if (track0Crossfading)
                {
                    if (p.Playing != 0 && dt > 0f)
                    {
                        AdvanceClipTime(ref p.NextTime, dt, p.Speed, nextClip->Duration, p.Loop != 0);
                        p.CrossfadeElapsed += dt;
                    }
                    blendAlpha = DetMath.Clamp01(p.CrossfadeElapsed / p.CrossfadeDuration);
                    if (blendAlpha >= 1f)
                    {
                        p.Clip = p.NextClip;
                        p.Time = p.NextTime;
                        p.NextClip = AnimClipHandle.Invalid;
                        p.NextTime = 0f;
                        p.CrossfadeElapsed = 0f;
                        p.CrossfadeDuration = 0f;
                        p.Track0FromLoop = p.Loop;
                        clip = nextClip;
                        nextClip = null;
                        blendAlpha = 0f;
                    }
                }
            }

            // Advance ExtraTracks (tracks 1..3) fade-out & clocks
            bool anyExtraActive = false;
            for (int ti = 0; ti < TrackCount - 1; ti++)
            {
                ref AnimTrackState tr = ref p.ExtraTracks[ti];
                if (tr.Active == 0) continue;

                if (!float.IsFinite(tr.Time)) tr.Time = 0f;
                if (!float.IsFinite(tr.FromTime)) tr.FromTime = 0f;
                if (!float.IsFinite(tr.CrossfadeElapsed) || tr.CrossfadeElapsed < 0f) tr.CrossfadeElapsed = 0f;
                if (!float.IsFinite(tr.CrossfadeDuration) || tr.CrossfadeDuration < 0f) tr.CrossfadeDuration = 0f;

                if (tr.FadeOutDuration > 1e-5f)
                {
                    if (!float.IsFinite(tr.FadeOutElapsed) || tr.FadeOutElapsed < 0f) tr.FadeOutElapsed = 0f;
                    if (!float.IsFinite(tr.FadeOutStartWeight)) tr.FadeOutStartWeight = DetMath.Clamp01(tr.Weight);
                    if (p.Playing != 0 && dt > 0f)
                        tr.FadeOutElapsed += dt;
                    if (tr.FadeOutElapsed >= tr.FadeOutDuration)
                    {
                        tr = new AnimTrackState
                        {
                            Speed = 1f,
                            Weight = 0f,
                            Loop = 1,
                            FromLoop = 1,
                            Active = 0,
                        };
                        continue;
                    }
                    float fade = 1f - DetMath.Clamp01(tr.FadeOutElapsed / tr.FadeOutDuration);
                    tr.Weight = tr.FadeOutStartWeight * fade;
                }

                AnimClipData* tClip = TryGetClip(in table, tr.Clip);
                if (tClip == null)
                {
                    tr.Active = 0;
                    continue;
                }

                if (p.Playing != 0 && dt > 0f)
                    AdvanceClipTime(ref tr.Time, dt, tr.Speed, tClip->Duration, tr.Loop != 0);

                AnimClipData* tFromClip = TryGetClip(in table, tr.FromClip);
                if (tFromClip != null && tr.CrossfadeDuration > 1e-5f)
                {
                    if (p.Playing != 0 && dt > 0f)
                    {
                        AdvanceClipTime(ref tr.FromTime, dt, tr.Speed, tFromClip->Duration, tr.FromLoop != 0);
                        tr.CrossfadeElapsed += dt;
                    }
                    float mixT = DetMath.Clamp01(tr.CrossfadeElapsed / tr.CrossfadeDuration);
                    if (mixT >= 1f)
                    {
                        tr.FromClip = AnimClipHandle.Invalid;
                        tr.CrossfadeElapsed = 0f;
                        tr.CrossfadeDuration = 0f;
                    }
                }

                if (DetMath.Clamp01(float.IsFinite(tr.Weight) ? tr.Weight : 0f) > 1e-5f)
                    anyExtraActive = true;
            }

            int jc = skel->JointCount;
            ArenaMarker mark = scratchArena.SaveMarker();
            try
            {
                Vec3* pos = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                Quat* rot = scratchArena.AllocUninitialized<Quat>(jc, 16);
                Vec3* scl = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                Mat4* localMats = scratchArena.AllocUninitialized<Mat4>(jc, 64);
                Mat4* worldMats = scratchArena.AllocUninitialized<Mat4>(jc, 64);
                byte* computed = scratchArena.Alloc<byte>(jc, 16);

                for (int j = 0; j < jc; j++)
                {
                    pos[j] = skel->BindPositions[j];
                    rot[j] = skel->BindRotations[j];
                    scl[j] = skel->BindScales[j];
                }

                Vec3* posA = null;
                Quat* rotA = null;
                Vec3* sclA = null;
                byte* touchedA = null;

                Vec3* posB = null;
                Quat* rotB = null;
                Vec3* sclB = null;
                byte* touchedB = null;

                // Track 0 evaluation
                if (p.Track0Active != 0)
                {
                    float w0 = DetMath.Clamp01(float.IsFinite(p.Track0Weight) ? p.Track0Weight : 1f);
                    if (w0 > 1e-5f && (clip != null || (nextClip != null && blendAlpha > 0f)))
                    {
                        if (w0 >= 0.9999f && p.Track0UseJointMask == 0 && p.Track0Additive == 0)
                        {
                            // Zero-overhead fast path preserving exact SIM-16 behavior
                            if (clip != null)
                                SampleClipIntoPose(clip, p.Time, pos, rot, scl, jc, weight: 1f, skel->SkinIndexPlusOne);

                            if (nextClip != null && blendAlpha > 0f)
                            {
                                posB = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                                rotB = scratchArena.AllocUninitialized<Quat>(jc, 16);
                                sclB = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                                touchedB = scratchArena.AllocUninitialized<byte>(jc, 16);
                                for (int j = 0; j < jc; j++)
                                {
                                    posB[j] = skel->BindPositions[j];
                                    rotB[j] = skel->BindRotations[j];
                                    sclB[j] = skel->BindScales[j];
                                }
                                SampleClipIntoPose(nextClip, p.NextTime, posB, rotB, sclB, jc, weight: 1f, skel->SkinIndexPlusOne);
                                for (int j = 0; j < jc; j++)
                                {
                                    pos[j] = Vec3.Lerp(pos[j], posB[j], blendAlpha);
                                    rot[j] = Quat.Slerp(rot[j], rotB[j], blendAlpha);
                                    scl[j] = Vec3.Lerp(scl[j], sclB[j], blendAlpha);
                                }
                            }
                        }
                        else
                        {
                            posA = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                            rotA = scratchArena.AllocUninitialized<Quat>(jc, 16);
                            sclA = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                            touchedA = scratchArena.AllocUninitialized<byte>(jc, 16);
                            for (int j = 0; j < jc; j++)
                            {
                                posA[j] = skel->BindPositions[j];
                                rotA[j] = skel->BindRotations[j];
                                sclA[j] = skel->BindScales[j];
                                touchedA[j] = 0;
                            }
                            if (clip != null)
                                SampleClipIntoPose(clip, p.Time, posA, rotA, sclA, jc, weight: 1f, skel->SkinIndexPlusOne, touchedA);

                            if (nextClip != null && blendAlpha > 0f)
                            {
                                posB = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                                rotB = scratchArena.AllocUninitialized<Quat>(jc, 16);
                                sclB = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                                touchedB = scratchArena.AllocUninitialized<byte>(jc, 16);
                                for (int j = 0; j < jc; j++)
                                {
                                    posB[j] = skel->BindPositions[j];
                                    rotB[j] = skel->BindRotations[j];
                                    sclB[j] = skel->BindScales[j];
                                    touchedB[j] = 0;
                                }
                                SampleClipIntoPose(nextClip, p.NextTime, posB, rotB, sclB, jc, weight: 1f, skel->SkinIndexPlusOne, touchedB);
                                for (int j = 0; j < jc; j++)
                                {
                                    posA[j] = Vec3.Lerp(posA[j], posB[j], blendAlpha);
                                    rotA[j] = Quat.Slerp(rotA[j], rotB[j], blendAlpha);
                                    sclA[j] = Vec3.Lerp(sclA[j], sclB[j], blendAlpha);
                                    touchedA[j] |= touchedB[j];
                                }
                            }

                            BlendTrackPoseIntoWorking(
                                skel, pos, rot, scl, posA, rotA, sclA, touchedA, jc, w0,
                                p.Track0UseJointMask != 0, p.Track0JointMaskLo, p.Track0JointMaskHi, p.Track0Additive != 0);
                        }
                    }
                }

                // Overlay / blend tracks 1..3 evaluation
                if (anyExtraActive)
                {
                    if (posA == null)
                    {
                        posA = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                        rotA = scratchArena.AllocUninitialized<Quat>(jc, 16);
                        sclA = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                        touchedA = scratchArena.AllocUninitialized<byte>(jc, 16);
                    }

                    for (int ti = 0; ti < TrackCount - 1; ti++)
                    {
                        ref readonly AnimTrackState tr = ref p.ExtraTracks[ti];
                        if (tr.Active == 0) continue;
                        float w = DetMath.Clamp01(float.IsFinite(tr.Weight) ? tr.Weight : 0f);
                        if (w <= 1e-5f) continue;

                        AnimClipData* tClip = TryGetClip(in table, tr.Clip);
                        if (tClip == null) continue;

                        for (int j = 0; j < jc; j++)
                        {
                            posA[j] = skel->BindPositions[j];
                            rotA[j] = skel->BindRotations[j];
                            sclA[j] = skel->BindScales[j];
                            touchedA[j] = 0;
                        }
                        SampleClipIntoPose(tClip, tr.Time, posA, rotA, sclA, jc, weight: 1f, skel->SkinIndexPlusOne, touchedA);

                        AnimClipData* tFromClip = TryGetClip(in table, tr.FromClip);
                        if (tFromClip != null && tr.CrossfadeDuration > 1e-5f)
                        {
                            float mixT = DetMath.Clamp01(tr.CrossfadeElapsed / tr.CrossfadeDuration);
                            if (posB == null)
                            {
                                posB = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                                rotB = scratchArena.AllocUninitialized<Quat>(jc, 16);
                                sclB = scratchArena.AllocUninitialized<Vec3>(jc, 16);
                                touchedB = scratchArena.AllocUninitialized<byte>(jc, 16);
                            }
                            for (int j = 0; j < jc; j++)
                            {
                                posB[j] = skel->BindPositions[j];
                                rotB[j] = skel->BindRotations[j];
                                sclB[j] = skel->BindScales[j];
                                touchedB[j] = 0;
                            }
                            SampleClipIntoPose(tFromClip, tr.FromTime, posB, rotB, sclB, jc, weight: 1f, skel->SkinIndexPlusOne, touchedB);
                            for (int j = 0; j < jc; j++)
                            {
                                posA[j] = Vec3.Lerp(posB[j], posA[j], mixT);
                                rotA[j] = Quat.Slerp(rotB[j], rotA[j], mixT);
                                sclA[j] = Vec3.Lerp(sclB[j], sclA[j], mixT);
                                touchedA[j] |= touchedB[j];
                            }
                        }

                        BlendTrackPoseIntoWorking(
                            skel, pos, rot, scl, posA, rotA, sclA, touchedA, jc, w,
                            tr.UseJointMask != 0, tr.JointMaskLo, tr.JointMaskHi, tr.Additive != 0);
                    }
                }

                bool anyUnresolved = false;
                for (int j = 0; j < jc; j++)
                {
                    localMats[j] = Mat4.CreateTRS(pos[j], rot[j], scl[j]);
                    int parent = skel->ParentIndices[j];
                    if ((uint)parent >= (uint)jc || parent == j)
                    {
                        worldMats[j] = localMats[j];
                        computed[j] = 2;
                    }
                    else if (parent < j && computed[parent] == 2)
                    {
                        worldMats[j] = Mat4.Multiply(in worldMats[parent], in localMats[j]);
                        computed[j] = 2;
                    }
                    else
                    {
                        anyUnresolved = true;
                    }
                }

                if (anyUnresolved)
                {
                    int* stack = scratchArena.AllocUninitialized<int>(jc, 16);
                    for (int j = 0; j < jc; j++)
                    {
                        if (computed[j] == 2) continue;
                        int sp = 0;
                        int cur = j;
                        while (true)
                        {
                            computed[cur] = 1;
                            stack[sp++] = cur;
                            int parent = skel->ParentIndices[cur];
                            if ((uint)parent >= (uint)jc || parent == cur || computed[parent] == 2 || computed[parent] == 1)
                                break;
                            cur = parent;
                        }
                        for (int s = sp - 1; s >= 0; s--)
                        {
                            int node = stack[s];
                            int parent = skel->ParentIndices[node];
                            worldMats[node] = ((uint)parent < (uint)jc && computed[parent] == 2)
                                ? Mat4.Multiply(in worldMats[parent], in localMats[node])
                                : localMats[node];
                            computed[node] = 2;
                        }
                    }
                }

                p.FrameJointOffset = jointCursor;
                p.FrameJointCount = jc;

                for (int j = 0; j < jc; j++)
                    table.FrameJointPalette[jointCursor + j] = Mat4.Multiply(in worldMats[j], in skel->InverseBindMatrices[j]);

                jointCursor += jc;
            }
            finally
            {
                scratchArena.Rollback(mark);
            }
        }

        // 2. Step 2D sprite sheet animations (atlas-aware BaseUvRect, Hold/Loop/PingPong, direct TargetSprite binding)
        for (int i = 0; i < table.SpritePlayerCount; i++)
        {
            ref SpriteAnimPlayerData sp = ref table.SpritePlayers[i];
            SpriteAnimClipData* clipPtr = TryGetSpriteClip(in table, sp.Clip);
            if (clipPtr == null) continue;
            ref SpriteAnimClipData clip = ref *clipPtr;
            if (clip.FrameCount <= 0 || clip.Columns <= 0 || clip.Rows <= 0) continue;

            SpriteAnimLoopMode mode = clip.LoopMode;
            if (mode == SpriteAnimLoopMode.Hold && clip.Loop != 0)
                mode = SpriteAnimLoopMode.Loop;

            int cycleFrames = mode == SpriteAnimLoopMode.PingPong
                ? (clip.FrameCount > 1 ? 2 * (clip.FrameCount - 1) : 1)
                : clip.FrameCount;

            if (!float.IsFinite(sp.Time))
                sp.Time = 0f;

            if (sp.Playing != 0 && dt > 0f && float.IsFinite(sp.Speed))
            {
                double nextTime = (double)sp.Time + (double)dt * (double)sp.Speed;
                if (!double.IsFinite(nextTime))
                    nextTime = 0.0;
                if (mode != SpriteAnimLoopMode.Hold && float.IsFinite(clip.Fps) && clip.Fps > 0f)
                {
                    double period = (double)cycleFrames / (double)clip.Fps;
                    if (period > 0.0 && double.IsFinite(period))
                    {
                        nextTime %= period;
                        if (nextTime < 0.0) nextTime += period;
                    }
                }
                sp.Time = (float)nextTime;
                if (!float.IsFinite(sp.Time)) sp.Time = 0f;
            }
            else if (mode != SpriteAnimLoopMode.Hold && float.IsFinite(clip.Fps) && clip.Fps > 0f)
            {
                double period = (double)cycleFrames / (double)clip.Fps;
                if (period > 0.0 && double.IsFinite(period))
                {
                    double wrapped = (double)sp.Time % period;
                    if (wrapped < 0.0) wrapped += period;
                    sp.Time = (float)wrapped;
                    if (!float.IsFinite(sp.Time)) sp.Time = 0f;
                }
            }

            int frameOffset = 0;
            if (float.IsFinite(clip.Fps) && clip.Fps != 0f)
            {
                double frameD = Math.Floor((double)sp.Time * (double)clip.Fps);
                if (!double.IsFinite(frameD))
                {
                    frameOffset = 0;
                }
                else if (mode == SpriteAnimLoopMode.Loop)
                {
                    double fc = (double)clip.FrameCount;
                    double mod = frameD % fc;
                    if (mod < 0.0) mod += fc;
                    int f = (int)mod;
                    if (f < 0) f = 0;
                    else if (f >= clip.FrameCount) f = clip.FrameCount - 1;
                    frameOffset = f;
                }
                else if (mode == SpriteAnimLoopMode.PingPong)
                {
                    if (clip.FrameCount <= 1)
                    {
                        frameOffset = 0;
                    }
                    else
                    {
                        double cyc = (double)cycleFrames;
                        double mod = frameD % cyc;
                        if (mod < 0.0) mod += cyc;
                        int idx = (int)mod;
                        if (idx < 0) idx = 0;
                        else if (idx >= cycleFrames) idx = cycleFrames - 1;
                        frameOffset = idx < clip.FrameCount ? idx : (cycleFrames - idx);
                    }
                }
                else
                {
                    if (frameD <= 0.0) frameOffset = 0;
                    else if (frameD >= (double)(clip.FrameCount - 1)) frameOffset = clip.FrameCount - 1;
                    else frameOffset = (int)frameD;
                }
            }

            int startFrame = clip.StartFrame < 0 ? 0 : clip.StartFrame;
            long frameLong = (long)startFrame + frameOffset;
            int frame = frameLong > int.MaxValue ? int.MaxValue : (int)frameLong;
            sp.CurrentFrame = frame;

            sp.CurrentUvRect = ComputeSpriteFrameUv(in clip, frame);

            if (renderComponents != null && sp.TargetSprite.IsValid)
            {
                SpriteData* target = RenderComponentSystem.TryGetSprite(in *renderComponents, sp.TargetSprite);
                if (target != null)
                    target->UvRect = sp.CurrentUvRect;
            }
        }
    }

    private static void SampleClipIntoPose(
        AnimClipData* clip, float time, Vec3* pos, Quat* rot, Vec3* scl, int jointCount, float weight, short skeletonSkinPlusOne, byte* touched = null)
    {
        for (int c = 0; c < clip->ChannelCount; c++)
        {
            AnimChannelData ch = clip->Channels[c];
            if (ch.TargetSkinPlusOne != 0 && skeletonSkinPlusOne != 0 && ch.TargetSkinPlusOne != skeletonSkinPlusOne) continue;
            if ((uint)ch.TargetJointIndex >= (uint)jointCount) continue;
            if ((uint)ch.SamplerIndex >= (uint)clip->SamplerCount) continue;
            ref AnimSamplerData sampler = ref clip->Samplers[ch.SamplerIndex];
            if (sampler.KeyframeCount <= 0) continue;

            // Locate bracketing keyframes
            int k0 = 0, k1 = 0;
            float alpha = 0f;
            float keyDelta = 1f;
            if (sampler.KeyframeCount == 1 || time <= sampler.Times[0])
            {
                k0 = k1 = 0;
            }
            else if (time >= sampler.Times[sampler.KeyframeCount - 1])
            {
                k0 = k1 = sampler.KeyframeCount - 1;
            }
            else
            {
                int lo = 0, hi = sampler.KeyframeCount - 1;
                while (lo + 1 < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (sampler.Times[mid] <= time) lo = mid;
                    else hi = mid;
                }
                k0 = lo;
                k1 = hi;
                keyDelta = sampler.Times[k1] - sampler.Times[k0];
                alpha = keyDelta > 1e-6f ? (time - sampler.Times[k0]) / keyDelta : 0f;
            }

            if (sampler.Interpolation == AnimInterpolation.Step) alpha = 0f;

            int stride = sampler.Interpolation == AnimInterpolation.CubicSpline
                ? sampler.ComponentsPerValue * 3
                : sampler.ComponentsPerValue;
            int valOffset = sampler.Interpolation == AnimInterpolation.CubicSpline ? sampler.ComponentsPerValue : 0;

            float* p0 = sampler.Values + k0 * stride + valOffset;
            float* p1 = sampler.Values + k1 * stride + valOffset;

            if (sampler.Interpolation == AnimInterpolation.CubicSpline && k0 != k1)
            {
                float t = alpha;
                float t2 = t * t;
                float t3 = t2 * t;
                float h00 = 2f * t3 - 3f * t2 + 1f;
                float h10 = (t3 - 2f * t2 + t) * keyDelta;
                float h01 = -2f * t3 + 3f * t2;
                float h11 = (t3 - t2) * keyDelta;

                float* b0 = sampler.Values + k0 * stride + 2 * valOffset; // out-tangent of k0
                float* a1 = sampler.Values + k1 * stride;                   // in-tangent of k1

                if (ch.Path == AnimTargetPath.Translation)
                {
                    if (sampler.ComponentsPerValue < 3) continue;
                    Vec3 sampled = new Vec3(
                        h00 * p0[0] + h10 * b0[0] + h01 * p1[0] + h11 * a1[0],
                        h00 * p0[1] + h10 * b0[1] + h01 * p1[1] + h11 * a1[1],
                        h00 * p0[2] + h10 * b0[2] + h01 * p1[2] + h11 * a1[2]);
                    pos[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Vec3.Lerp(pos[ch.TargetJointIndex], sampled, weight);
                    if (touched != null) touched[ch.TargetJointIndex] |= TouchPos;
                }
                else if (ch.Path == AnimTargetPath.Scale)
                {
                    if (sampler.ComponentsPerValue < 3) continue;
                    Vec3 sampled = new Vec3(
                        h00 * p0[0] + h10 * b0[0] + h01 * p1[0] + h11 * a1[0],
                        h00 * p0[1] + h10 * b0[1] + h01 * p1[1] + h11 * a1[1],
                        h00 * p0[2] + h10 * b0[2] + h01 * p1[2] + h11 * a1[2]);
                    scl[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Vec3.Lerp(scl[ch.TargetJointIndex], sampled, weight);
                    if (touched != null) touched[ch.TargetJointIndex] |= TouchScl;
                }
                else if (ch.Path == AnimTargetPath.Rotation)
                {
                    if (sampler.ComponentsPerValue < 4) continue;
                    Quat sampled = new Quat(
                        h00 * p0[0] + h10 * b0[0] + h01 * p1[0] + h11 * a1[0],
                        h00 * p0[1] + h10 * b0[1] + h01 * p1[1] + h11 * a1[1],
                        h00 * p0[2] + h10 * b0[2] + h01 * p1[2] + h11 * a1[2],
                        h00 * p0[3] + h10 * b0[3] + h01 * p1[3] + h11 * a1[3]).Normalized();
                    rot[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Quat.Slerp(rot[ch.TargetJointIndex], sampled, weight);
                    if (touched != null) touched[ch.TargetJointIndex] |= TouchRot;
                }
                continue;
            }

            if (ch.Path == AnimTargetPath.Translation)
            {
                if (sampler.ComponentsPerValue < 3) continue;
                Vec3 v0 = new Vec3(p0[0], p0[1], p0[2]);
                Vec3 v1 = new Vec3(p1[0], p1[1], p1[2]);
                Vec3 sampled = Vec3.Lerp(v0, v1, alpha);
                pos[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Vec3.Lerp(pos[ch.TargetJointIndex], sampled, weight);
                if (touched != null) touched[ch.TargetJointIndex] |= TouchPos;
            }
            else if (ch.Path == AnimTargetPath.Scale)
            {
                if (sampler.ComponentsPerValue < 3) continue;
                Vec3 v0 = new Vec3(p0[0], p0[1], p0[2]);
                Vec3 v1 = new Vec3(p1[0], p1[1], p1[2]);
                Vec3 sampled = Vec3.Lerp(v0, v1, alpha);
                scl[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Vec3.Lerp(scl[ch.TargetJointIndex], sampled, weight);
                if (touched != null) touched[ch.TargetJointIndex] |= TouchScl;
            }
            else if (ch.Path == AnimTargetPath.Rotation)
            {
                if (sampler.ComponentsPerValue < 4) continue;
                Quat q0 = new Quat(p0[0], p0[1], p0[2], p0[3]).Normalized();
                Quat q1 = new Quat(p1[0], p1[1], p1[2], p1[3]).Normalized();
                Quat sampled = k0 == k1 ? q0 : Quat.Slerp(q0, q1, alpha);
                rot[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Quat.Slerp(rot[ch.TargetJointIndex], sampled, weight);
                if (touched != null) touched[ch.TargetJointIndex] |= TouchRot;
            }
        }
    }
}

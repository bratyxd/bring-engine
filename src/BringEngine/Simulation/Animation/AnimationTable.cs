using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

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
}

[StructLayout(LayoutKind.Sequential)]
public struct SpriteAnimClipData
{
    public StringId Name;
    public SceneId OwnerScene;
    public BundleId OwnerBundle;
    public int StartFrame;
    public int FrameCount;
    public int Columns;
    public int Rows;
    public float Fps;
    public byte Loop;
}

[StructLayout(LayoutKind.Sequential)]
public struct SpriteAnimPlayerData
{
    public SpriteAnimPlayerHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;
    public int ClipIndex;
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

    public static AnimationTable Create(
        ref LinearArena arena,
        int maxSkeletons = 256,
        int maxClips = 512,
        int maxPlayers = 1024,
        int maxSpriteClips = 256,
        int maxSpritePlayers = 2048)
    {
        uint* skelGens = arena.AllocUninitialized<uint>(maxSkeletons, 16);
        for (int i = 0; i < maxSkeletons; i++) skelGens[i] = 1;
        uint* clipGens = arena.AllocUninitialized<uint>(maxClips, 16);
        for (int i = 0; i < maxClips; i++) clipGens[i] = 1;

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
        };
    }
}

public static unsafe class AnimationSystem
{
    public static SkeletonHandle RegisterSkeleton(ref AnimationTable table, in SkeletonData data)
    {
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

    public static void UnregisterByBundle(ref AnimationTable table, BundleId bundle)
    {
        for (int i = 0; i < table.MaxSkeletons; i++)
        {
            if (table.SkeletonAlive[i] != 0 && table.Skeletons[i].OwnerBundle == bundle)
            {
                table.SkeletonAlive[i] = 0;
                uint g = (table.SkeletonGens[i] + 1) & HandlePacking.GenerationMask;
                table.SkeletonGens[i] = g == 0 ? 1u : g;
            }
        }
        for (int i = 0; i < table.MaxClips; i++)
        {
            if (table.ClipAlive[i] != 0 && table.Clips[i].OwnerBundle == bundle)
            {
                table.ClipAlive[i] = 0;
                uint g = (table.ClipGens[i] + 1) & HandlePacking.GenerationMask;
                table.ClipGens[i] = g == 0 ? 1u : g;
            }
        }
        if (bundle.IsValid)
        {
            for (int i = 0; i < table.MaxSpriteClips; i++)
            {
                if (table.SpriteClipAlive[i] != 0 && table.SpriteClips[i].OwnerBundle == bundle)
                {
                    table.SpriteClipAlive[i] = 0;
                    table.SpriteClipCount--;
                }
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
        table.Players[idx] = new AnimPlayerData
        {
            Handle = handle,
            Entity = entity,
            OwnerScene = scene,
            Skeleton = skeleton,
            Clip = clip,
            Speed = speed,
            Loop = (byte)(loop ? 1 : 0),
            Playing = 1,
        };
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

    public static void Crossfade(ref AnimationTable table, EntityId entity, AnimClipHandle nextClip, float duration)
    {
        for (int i = 0; i < table.PlayerCount; i++)
        {
            if (table.Players[i].Entity == entity)
            {
                table.Players[i].NextClip = nextClip;
                table.Players[i].NextTime = 0f;
                table.Players[i].CrossfadeElapsed = 0f;
                table.Players[i].CrossfadeDuration = duration > 0f ? duration : 0.001f;
                return;
            }
        }
    }

    public static void Crossfade(ref AnimationTable table, AnimPlayerHandle handle, AnimClipHandle nextClip, float duration)
    {
        AnimPlayerData* p = TryGetPlayer(in table, handle);
        if (p == null) return;
        p->NextClip = nextClip;
        p->NextTime = 0f;
        p->CrossfadeElapsed = 0f;
        p->CrossfadeDuration = duration > 0f ? duration : 0.001f;
    }

    public static AnimPlayerHandle FindPlayerByEntity(in AnimationTable table, EntityId entity)
    {
        for (int i = 0; i < table.PlayerCount; i++)
            if (table.Players[i].Entity == entity) return table.Players[i].Handle;
        return AnimPlayerHandle.Invalid;
    }

    public static int RegisterSpriteClip(ref AnimationTable table, in SpriteAnimClipData clip)
    {
        if (clip.Name.Value != 0)
        {
            for (int i = 0; i < table.MaxSpriteClips; i++)
            {
                if (table.SpriteClipAlive[i] != 0 && table.SpriteClips[i].Name == clip.Name)
                    return i;
            }
        }
        for (int i = 0; i < table.MaxSpriteClips; i++)
        {
            if (table.SpriteClipAlive[i] == 0)
            {
                table.SpriteClipAlive[i] = 1;
                table.SpriteClips[i] = clip;
                table.SpriteClipCount++;
                return i;
            }
        }
        throw new OutOfMemoryException("SpriteAnimClips exhausted.");
    }

    public static SpriteAnimPlayerHandle AddSpritePlayer(ref AnimationTable table, EntityId entity, int clipIndex, SceneId scene = default, float speed = 1f)
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

        var handle = SpriteAnimPlayerHandle.Create(slot, table.SpritePlayerGens[slot]);
        int idx = table.SpritePlayerCount++;
        table.SpritePlayerSlotToDense[slot] = idx;
        table.SpritePlayers[idx] = new SpriteAnimPlayerData
        {
            Handle = handle,
            Entity = entity,
            OwnerScene = scene,
            ClipIndex = clipIndex,
            Speed = speed,
            Playing = 1,
            CurrentUvRect = new Vec4(0, 0, 1, 1),
        };
        return handle;
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

    /// <summary>
    /// Advances skeletal & sprite animation players and builds the frame joint skinning palette in FrameArena.
    /// Zero GC allocations.
    /// </summary>
    public static void StepAll(ref AnimationTable table, float dt, ref LinearArena frameArena, ref LinearArena scratchArena)
    {
        // 1. Compute total joints needed this frame
        int totalJoints = 0;
        for (int i = 0; i < table.PlayerCount; i++)
        {
            SkeletonData* skel = TryGetSkeleton(in table, table.Players[i].Skeleton);
            if (skel != null) totalJoints += skel->JointCount;
        }

        table.FrameJointPalette = totalJoints > 0
            ? frameArena.AllocSlice<Mat4>(totalJoints, 64, zeroed: false)
            : default;

        int jointCursor = 0;
        for (int i = 0; i < table.PlayerCount; i++)
        {
            ref AnimPlayerData p = ref table.Players[i];
            SkeletonData* skel = TryGetSkeleton(in table, p.Skeleton);
            if (skel == null)
            {
                p.FrameJointOffset = 0;
                p.FrameJointCount = 0;
                continue;
            }

            AnimClipData* clip = TryGetClip(in table, p.Clip);
            if (p.Playing != 0 && clip != null && clip->Duration > 0f)
            {
                p.Time += dt * p.Speed;
                if (p.Loop != 0)
                {
                    p.Time %= clip->Duration;
                    if (p.Time < 0f) p.Time += clip->Duration;
                }
                else
                {
                    p.Time = DetMath.Clamp(p.Time, 0f, clip->Duration);
                }
            }

            float blendAlpha = 0f;
            AnimClipData* nextClip = TryGetClip(in table, p.NextClip);
            if (nextClip != null && p.CrossfadeDuration > 0f)
            {
                if (p.Playing != 0)
                {
                    if (nextClip->Duration > 0f)
                    {
                        p.NextTime += dt * p.Speed;
                        if (p.Loop != 0)
                        {
                            p.NextTime %= nextClip->Duration;
                            if (p.NextTime < 0f) p.NextTime += nextClip->Duration;
                        }
                        else
                        {
                            p.NextTime = DetMath.Clamp(p.NextTime, 0f, nextClip->Duration);
                        }
                    }
                    p.CrossfadeElapsed += dt;
                }
                blendAlpha = DetMath.Clamp01(p.CrossfadeElapsed / p.CrossfadeDuration);
                if (blendAlpha >= 1f)
                {
                    p.Clip = p.NextClip;
                    p.Time = p.NextTime;
                    p.NextClip = AnimClipHandle.Invalid;
                    p.CrossfadeDuration = 0f;
                    clip = nextClip;
                    nextClip = null;
                    blendAlpha = 0f;
                }
            }

            int jc = skel->JointCount;
            if (jc <= 0)
            {
                p.FrameJointOffset = jointCursor;
                p.FrameJointCount = 0;
                continue;
            }

            ArenaMarker mark = scratchArena.SaveMarker();
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

            if (clip != null)
                SampleClipIntoPose(clip, p.Time, pos, rot, scl, jc, weight: 1f);

            if (nextClip != null && blendAlpha > 0f)
                SampleClipIntoPose(nextClip, p.NextTime, pos, rot, scl, jc, weight: blendAlpha);

            p.FrameJointOffset = jointCursor;
            p.FrameJointCount = jc;

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

            for (int j = 0; j < jc; j++)
                table.FrameJointPalette[jointCursor + j] = Mat4.Multiply(in worldMats[j], in skel->InverseBindMatrices[j]);

            jointCursor += jc;
            scratchArena.Rollback(mark);
        }

        // 2. Step 2D sprite sheet animations
        for (int i = 0; i < table.SpritePlayerCount; i++)
        {
            ref SpriteAnimPlayerData sp = ref table.SpritePlayers[i];
            if ((uint)sp.ClipIndex >= (uint)table.MaxSpriteClips || table.SpriteClipAlive[sp.ClipIndex] == 0) continue;
            ref SpriteAnimClipData clip = ref table.SpriteClips[sp.ClipIndex];
            if (clip.FrameCount <= 0 || clip.Columns <= 0 || clip.Rows <= 0) continue;

            if (sp.Playing != 0)
                sp.Time += dt * sp.Speed;

            int frameOffset = (int)MathF.Floor(sp.Time * clip.Fps);
            if (clip.Loop != 0)
                frameOffset = ((frameOffset % clip.FrameCount) + clip.FrameCount) % clip.FrameCount;
            else
                frameOffset = frameOffset < 0 ? 0 : (frameOffset >= clip.FrameCount ? clip.FrameCount - 1 : frameOffset);

            int frame = clip.StartFrame + frameOffset;
            sp.CurrentFrame = frame;

            int col = frame % clip.Columns;
            int row = frame / clip.Columns;
            float uW = 1f / clip.Columns;
            float vH = 1f / clip.Rows;
            sp.CurrentUvRect = new Vec4(col * uW, row * vH, uW, vH);
        }
    }

    private static void SampleClipIntoPose(
        AnimClipData* clip, float time, Vec3* pos, Quat* rot, Vec3* scl, int jointCount, float weight)
    {
        for (int c = 0; c < clip->ChannelCount; c++)
        {
            AnimChannelData ch = clip->Channels[c];
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
                }
                else if (ch.Path == AnimTargetPath.Scale)
                {
                    if (sampler.ComponentsPerValue < 3) continue;
                    Vec3 sampled = new Vec3(
                        h00 * p0[0] + h10 * b0[0] + h01 * p1[0] + h11 * a1[0],
                        h00 * p0[1] + h10 * b0[1] + h01 * p1[1] + h11 * a1[1],
                        h00 * p0[2] + h10 * b0[2] + h01 * p1[2] + h11 * a1[2]);
                    scl[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Vec3.Lerp(scl[ch.TargetJointIndex], sampled, weight);
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
            }
            else if (ch.Path == AnimTargetPath.Scale)
            {
                if (sampler.ComponentsPerValue < 3) continue;
                Vec3 v0 = new Vec3(p0[0], p0[1], p0[2]);
                Vec3 v1 = new Vec3(p1[0], p1[1], p1[2]);
                Vec3 sampled = Vec3.Lerp(v0, v1, alpha);
                scl[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Vec3.Lerp(scl[ch.TargetJointIndex], sampled, weight);
            }
            else if (ch.Path == AnimTargetPath.Rotation)
            {
                if (sampler.ComponentsPerValue < 4) continue;
                Quat q0 = new Quat(p0[0], p0[1], p0[2], p0[3]).Normalized();
                Quat q1 = new Quat(p1[0], p1[1], p1[2], p1[3]).Normalized();
                Quat sampled = k0 == k1 ? q0 : Quat.Slerp(q0, q1, alpha);
                rot[ch.TargetJointIndex] = weight >= 0.999f ? sampled : Quat.Slerp(rot[ch.TargetJointIndex], sampled, weight);
            }
        }
    }
}

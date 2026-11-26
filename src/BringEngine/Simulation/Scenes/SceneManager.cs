using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BringEngine.Assets.Bundles;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Memory;
using BringEngine.Simulation.World;

namespace BringEngine.Simulation.Scenes;

public enum SceneLoadMode : byte
{
    Single = 0,   // Unloads all currently active scenes & unreferenced bundles first (minimal memory)
    Additive = 1, // Keeps currently active scenes loaded (e.g., Gameplay + HUD overlay)
}

public unsafe struct SceneContext
{
    public SceneId Scene;
    public LinearArena* SceneArena;
    public EngineWorld* World;
    public EngineAllocators* Allocators;
    public BundleManager* Bundles;
    public void* UserData;
    public float DeltaTime;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SceneDefinition
{
    public StringId Name;
    public FixedString64 NameText;
    public BundleId* RequiredBundles;
    public int RequiredBundleCount;
    public nuint SceneArenaBytes;

    public delegate*<ref SceneContext, void> OnLoad;
    public delegate*<ref SceneContext, void> OnUnload;
    public delegate*<ref SceneContext, void> OnFixedUpdate;
    public delegate*<ref SceneContext, void> OnViewSync;
    public void* UserData;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ActiveSceneData
{
    public SceneId Handle;
    public int DefinitionIndex;
    public LinearArena SceneArena;
    public byte Active;
}

/// <summary>
/// Unity-like Scene Manager with Single & Additive loading, dedicated per-scene LinearArena,
/// automatic bundle ref-counting, and deterministic entity/component cleanup on unload.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SceneManager
{
    public SceneDefinition* Definitions;
    public int DefinitionCount;
    public int MaxDefinitions;
    public NativeHashMap<int> NameToDef;

    public ActiveSceneData* ActiveScenes;
    public uint* SceneGens;
    public int MaxActiveScenes;
    public int ActiveCount;
    public LogState* Log;

    public static SceneManager Create(
        ref LinearArena persistentArena,
        int maxDefinitions = 32,
        int maxActiveScenes = 8,
        LogState* log = null)
    {
        const int maxCap = (int)(HandlePacking.IndexMask + 1u);
        if (maxDefinitions < 1 || maxDefinitions > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxDefinitions), maxDefinitions, $"Capacity must be in [1, {maxCap}].");
        if (maxActiveScenes < 1 || maxActiveScenes > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxActiveScenes), maxActiveScenes, $"Capacity must be in [1, {maxCap}].");

        uint* gens = persistentArena.AllocUninitialized<uint>(maxActiveScenes, 16);
        for (int i = 0; i < maxActiveScenes; i++) gens[i] = 1;

        return new SceneManager
        {
            Definitions = persistentArena.Alloc<SceneDefinition>(maxDefinitions, 64),
            DefinitionCount = 0,
            MaxDefinitions = maxDefinitions,
            NameToDef = NativeHashMap<int>.Create(ref persistentArena, maxDefinitions),

            ActiveScenes = persistentArena.Alloc<ActiveSceneData>(maxActiveScenes, 64),
            SceneGens = gens,
            MaxActiveScenes = maxActiveScenes,
            ActiveCount = 0,
            Log = log,
        };
    }
}

public static unsafe class SceneSystem
{
    public static void SetLogger(ref SceneManager mgr, LogState* log) => mgr.Log = log;

    public static int RegisterScene(
        ref SceneManager mgr,
        ref LinearArena persistentArena,
        ReadOnlySpan<char> name,
        ReadOnlySpan<BundleId> requiredBundles,
        nuint sceneArenaBytes,
        delegate*<ref SceneContext, void> onLoad,
        delegate*<ref SceneContext, void> onUnload = null,
        delegate*<ref SceneContext, void> onFixedUpdate = null,
        delegate*<ref SceneContext, void> onViewSync = null,
        void* userData = null)
    {
        StringId id = StringId.Hash(name);
        if (mgr.NameToDef.TryGet(id.Value, out int _))
            throw new InvalidOperationException($"Scene '{name.ToString()}' (hash={id.Value}) is already registered.");

        if (mgr.DefinitionCount >= mgr.MaxDefinitions)
            throw new OutOfMemoryException("SceneManager definition capacity exceeded.");

        int idx = mgr.DefinitionCount;
        BundleId* bundleCopy = null;
        if (requiredBundles.Length > 0)
        {
            bundleCopy = persistentArena.AllocUninitialized<BundleId>(requiredBundles.Length, 16);
            requiredBundles.CopyTo(new Span<BundleId>(bundleCopy, requiredBundles.Length));
        }

        mgr.Definitions[idx] = new SceneDefinition
        {
            Name = id,
            NameText = FixedString64.From(name),
            RequiredBundles = bundleCopy,
            RequiredBundleCount = requiredBundles.Length,
            SceneArenaBytes = sceneArenaBytes,
            OnLoad = onLoad,
            OnUnload = onUnload,
            OnFixedUpdate = onFixedUpdate,
            OnViewSync = onViewSync,
            UserData = userData,
        };
        mgr.NameToDef.Set(id.Value, idx);
        mgr.DefinitionCount = idx + 1;
        return idx;
    }

    private static int FindFreeActiveSlot(in SceneManager mgr)
    {
        for (int i = 0; i < mgr.MaxActiveScenes; i++)
            if (mgr.ActiveScenes[i].Active == 0) return i;
        return -1;
    }

    public static SceneId LoadScene(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        StringId sceneName,
        SceneLoadMode mode = SceneLoadMode.Single,
        LogState* log = null)
    {
        if (!mgr.NameToDef.TryGet(sceneName.Value, out int defIdx))
            throw new InvalidOperationException($"Scene not registered (hash={sceneName.Value}).");

        LogState* activeLog = log != null ? log : mgr.Log;
        long startTicks = Stopwatch.GetTimestamp();
        ref SceneDefinition newDef = ref mgr.Definitions[defIdx];

        int slot;
        if (mode == SceneLoadMode.Single)
        {
            if (mgr.MaxActiveScenes <= 0)
                throw new OutOfMemoryException("Max active scenes exceeded.");

            ArenaMarker mark = alloc.Scratch.SaveMarker();
            byte* acquired = newDef.RequiredBundleCount > 0 ? alloc.Scratch.Alloc<byte>(newDef.RequiredBundleCount) : null;
            try
            {
                // Acquire shared already-loaded bundles first so Single transitions keep them resident without reload spikes
                for (int b = 0; b < newDef.RequiredBundleCount; b++)
                {
                    if (BundleSystem.IsLoaded(in bundles, newDef.RequiredBundles[b]))
                    {
                        BundleSystem.Acquire(ref bundles, newDef.RequiredBundles[b], ref world, ref alloc.Scratch);
                        acquired[b] = 1;
                    }
                }

                // Unload all currently active scenes (frees unshared bundles + scene arenas + entities immediately!)
                UnloadAllScenes(ref mgr, ref world, ref alloc, ref bundles, activeLog);

                // Acquire remaining new bundles
                for (int b = 0; b < newDef.RequiredBundleCount; b++)
                {
                    if (acquired[b] == 0)
                    {
                        BundleSystem.Acquire(ref bundles, newDef.RequiredBundles[b], ref world, ref alloc.Scratch);
                        acquired[b] = 1;
                    }
                }
            }
            catch
            {
                for (int b = 0; b < newDef.RequiredBundleCount; b++)
                {
                    if (acquired != null && acquired[b] != 0)
                        BundleSystem.Release(ref bundles, newDef.RequiredBundles[b], ref world);
                }
                throw;
            }
            finally
            {
                alloc.Scratch.Rollback(mark);
            }

            slot = FindFreeActiveSlot(in mgr);
            if (slot < 0)
            {
                for (int b = 0; b < newDef.RequiredBundleCount; b++)
                    BundleSystem.Release(ref bundles, newDef.RequiredBundles[b], ref world);
                throw new OutOfMemoryException("Max active scenes exceeded.");
            }
        }
        else
        {
            // Check free active scene slot BEFORE acquiring any bundles to prevent bundle RefCount/arena leaks
            slot = FindFreeActiveSlot(in mgr);
            if (slot < 0)
                throw new OutOfMemoryException("Max active scenes exceeded.");

            int acquiredCount = 0;
            try
            {
                for (int b = 0; b < newDef.RequiredBundleCount; b++)
                {
                    BundleSystem.Acquire(ref bundles, newDef.RequiredBundles[b], ref world, ref alloc.Scratch);
                    acquiredCount++;
                }
            }
            catch
            {
                for (int b = acquiredCount - 1; b >= 0; b--)
                    BundleSystem.Release(ref bundles, newDef.RequiredBundles[b], ref world);
                throw;
            }
        }

        LinearArena sceneArena;
        try
        {
            sceneArena = LinearArena.Create(newDef.SceneArenaBytes, newDef.Name.Value);
        }
        catch
        {
            for (int b = 0; b < newDef.RequiredBundleCount; b++)
                BundleSystem.Release(ref bundles, newDef.RequiredBundles[b], ref world);
            throw;
        }

        SceneId handle = SceneId.Create((uint)slot, mgr.SceneGens[slot]);
        ref ActiveSceneData inst = ref mgr.ActiveScenes[slot];
        inst.Handle = handle;
        inst.DefinitionIndex = defIdx;
        inst.SceneArena = sceneArena;
        inst.Active = 1; // Loading
        mgr.ActiveCount++;

        if (newDef.OnLoad != null)
        {
            try
            {
                fixed (LinearArena* sa = &inst.SceneArena)
                fixed (EngineWorld* wp = &world)
                fixed (EngineAllocators* ap = &alloc)
                fixed (BundleManager* bp = &bundles)
                {
                    var ctx = new SceneContext
                    {
                        Scene = handle,
                        SceneArena = sa,
                        World = wp,
                        Allocators = ap,
                        Bundles = bp,
                        UserData = newDef.UserData,
                    };
                    newDef.OnLoad(ref ctx);
                }
            }
            catch
            {
                if (inst.Handle == handle && (inst.Active == 1 || inst.Active == 2))
                {
                    inst.Active = 3; // Unloading
                    try
                    {
                        EngineWorldSystem.DestroySceneObjects(ref world, handle, ref alloc.Scratch);
                    }
                    finally
                    {
                        inst.SceneArena.Dispose();
                        inst.Active = 0;
                        inst.Handle = SceneId.Invalid;
                        mgr.SceneGens[slot] = HandlePacking.NextGeneration(mgr.SceneGens[slot]);
                        mgr.ActiveCount--;
                        for (int b = 0; b < newDef.RequiredBundleCount; b++)
                            BundleSystem.Release(ref bundles, newDef.RequiredBundles[b], ref world);
                    }
                }
                if (activeLog != null)
                    LogSystem.Error(activeLog, $"Scene '{newDef.NameText}' OnLoad failed; rolled back (handle={handle})");
                throw;
            }
        }

        if (inst.Handle == handle && inst.Active == 1)
            inst.Active = 2; // Active

        if (activeLog != null)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            LogSystem.Info(activeLog, $"Scene '{newDef.NameText}' loaded (mode={(mode == SceneLoadMode.Single ? "Single"u8 : "Additive"u8)}, handle={handle}, entities={world.Entities.AliveCount}, bundles={newDef.RequiredBundleCount}, {elapsedMs:F2}ms)");
        }

        return handle;
    }

    public static bool UnloadScene(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        SceneId sceneId,
        LogState* log = null)
    {
        if (!sceneId.IsValid || sceneId.Index >= (uint)mgr.MaxActiveScenes) return false;
        ref ActiveSceneData inst = ref mgr.ActiveScenes[sceneId.Index];
        if ((inst.Active != 1 && inst.Active != 2) || inst.Handle != sceneId) return false;

        // Transition immediately to Unloading (3) before OnUnload so re-entrant UnloadScene/UnloadAllScenes skips this slot
        inst.Active = 3;
        int defIdx = inst.DefinitionIndex;

        LogState* activeLog = log != null ? log : mgr.Log;
        long startTicks = Stopwatch.GetTimestamp();
        int entitiesBefore = world.Entities.AliveCount;
        nuint arenaBytes = inst.SceneArena.Capacity;
        ref SceneDefinition def = ref mgr.Definitions[defIdx];

        try
        {
            if (def.OnUnload != null)
            {
                fixed (LinearArena* sa = &inst.SceneArena)
                fixed (EngineWorld* wp = &world)
                fixed (EngineAllocators* ap = &alloc)
                fixed (BundleManager* bp = &bundles)
                {
                    var ctx = new SceneContext
                    {
                        Scene = sceneId,
                        SceneArena = sa,
                        World = wp,
                        Allocators = ap,
                        Bundles = bp,
                        UserData = def.UserData,
                    };
                    def.OnUnload(ref ctx);
                }
            }
        }
        finally
        {
            // Clean up all world objects belonging to this scene
            EngineWorldSystem.DestroySceneObjects(ref world, sceneId, ref alloc.Scratch);

            // Dispose per-scene arena immediately
            inst.SceneArena.Dispose();
            inst.Active = 0;
            inst.Handle = SceneId.Invalid;
            mgr.SceneGens[sceneId.Index] = HandlePacking.NextGeneration(mgr.SceneGens[sceneId.Index]);
            mgr.ActiveCount--;

            // Release bundles (unloads any bundle whose RefCount hits 0)
            for (int b = 0; b < def.RequiredBundleCount; b++)
                BundleSystem.Release(ref bundles, def.RequiredBundles[b], ref world);
        }

        if (activeLog != null)
        {
            int entitiesRemoved = entitiesBefore - world.Entities.AliveCount;
            double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            LogSystem.Info(activeLog, $"Scene '{def.NameText}' unloaded (handle={sceneId}, entitiesRemoved={entitiesRemoved}, arenaBytes={arenaBytes}, {elapsedMs:F2}ms)");
        }

        return true;
    }

    public static void UnloadAllScenes(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        LogState* log = null)
    {
        for (int i = mgr.MaxActiveScenes - 1; i >= 0; i--)
        {
            byte st = mgr.ActiveScenes[i].Active;
            if (st == 1 || st == 2)
                UnloadScene(ref mgr, ref world, ref alloc, ref bundles, mgr.ActiveScenes[i].Handle, log);
        }
    }

    public static void FixedUpdateActiveScenes(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        float dt)
    {
        int maxSlots = mgr.MaxActiveScenes;
        if (maxSlots <= 0 || mgr.ActiveCount <= 0) return;

        SceneId* stackSnap = stackalloc SceneId[64];
        ArenaMarker mark = default;
        bool usedScratch = false;
        SceneId* snap = stackSnap;
        if (maxSlots > 64)
        {
            mark = alloc.Scratch.SaveMarker();
            snap = alloc.Scratch.AllocUninitialized<SceneId>(maxSlots, 16);
            usedScratch = true;
        }

        try
        {
            int snapCount = 0;
            for (int i = 0; i < maxSlots; i++)
            {
                if (mgr.ActiveScenes[i].Active == 2)
                    snap[snapCount++] = mgr.ActiveScenes[i].Handle;
            }

            for (int s = 0; s < snapCount; s++)
            {
                SceneId snapHandle = snap[s];
                uint slot = snapHandle.Index;
                if (slot >= (uint)mgr.MaxActiveScenes) continue;
                ref ActiveSceneData inst = ref mgr.ActiveScenes[slot];
                if (inst.Active != 2 || inst.Handle != snapHandle) continue;
                ref SceneDefinition def = ref mgr.Definitions[inst.DefinitionIndex];
                if (def.OnFixedUpdate == null) continue;

                fixed (LinearArena* sa = &inst.SceneArena)
                fixed (EngineWorld* wp = &world)
                fixed (EngineAllocators* ap = &alloc)
                fixed (BundleManager* bp = &bundles)
                {
                    var ctx = new SceneContext
                    {
                        Scene = snapHandle,
                        SceneArena = sa,
                        World = wp,
                        Allocators = ap,
                        Bundles = bp,
                        UserData = def.UserData,
                        DeltaTime = dt,
                    };
                    def.OnFixedUpdate(ref ctx);
                }
            }
        }
        finally
        {
            if (usedScratch)
                alloc.Scratch.Rollback(mark);
        }
    }

    public static void ViewSyncActiveScenes(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        float dt)
    {
        int maxSlots = mgr.MaxActiveScenes;
        if (maxSlots <= 0 || mgr.ActiveCount <= 0) return;

        SceneId* stackSnap = stackalloc SceneId[64];
        ArenaMarker mark = default;
        bool usedScratch = false;
        SceneId* snap = stackSnap;
        if (maxSlots > 64)
        {
            mark = alloc.Scratch.SaveMarker();
            snap = alloc.Scratch.AllocUninitialized<SceneId>(maxSlots, 16);
            usedScratch = true;
        }

        try
        {
            int snapCount = 0;
            for (int i = 0; i < maxSlots; i++)
            {
                if (mgr.ActiveScenes[i].Active == 2)
                    snap[snapCount++] = mgr.ActiveScenes[i].Handle;
            }

            for (int s = 0; s < snapCount; s++)
            {
                SceneId snapHandle = snap[s];
                uint slot = snapHandle.Index;
                if (slot >= (uint)mgr.MaxActiveScenes) continue;
                ref ActiveSceneData inst = ref mgr.ActiveScenes[slot];
                if (inst.Active != 2 || inst.Handle != snapHandle) continue;
                ref SceneDefinition def = ref mgr.Definitions[inst.DefinitionIndex];
                if (def.OnViewSync == null) continue;

                fixed (LinearArena* sa = &inst.SceneArena)
                fixed (EngineWorld* wp = &world)
                fixed (EngineAllocators* ap = &alloc)
                fixed (BundleManager* bp = &bundles)
                {
                    var ctx = new SceneContext
                    {
                        Scene = snapHandle,
                        SceneArena = sa,
                        World = wp,
                        Allocators = ap,
                        Bundles = bp,
                        UserData = def.UserData,
                        DeltaTime = dt,
                    };
                    def.OnViewSync(ref ctx);
                }
            }
        }
        finally
        {
            if (usedScratch)
                alloc.Scratch.Rollback(mark);
        }
    }
}

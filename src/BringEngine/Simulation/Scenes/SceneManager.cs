using System;
using System.Runtime.InteropServices;
using BringEngine.Assets.Bundles;
using BringEngine.Core.Handles;
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

    public static SceneManager Create(ref LinearArena persistentArena, int maxDefinitions = 32, int maxActiveScenes = 8)
    {
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
        };
    }
}

public static unsafe class SceneSystem
{
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
        if (mgr.DefinitionCount >= mgr.MaxDefinitions)
            throw new OutOfMemoryException("SceneManager definition capacity exceeded.");

        int idx = mgr.DefinitionCount++;
        BundleId* bundleCopy = null;
        if (requiredBundles.Length > 0)
        {
            bundleCopy = persistentArena.AllocUninitialized<BundleId>(requiredBundles.Length, 16);
            requiredBundles.CopyTo(new Span<BundleId>(bundleCopy, requiredBundles.Length));
        }

        StringId id = StringId.Hash(name);
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
        return idx;
    }

    public static SceneId LoadScene(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        StringId sceneName,
        SceneLoadMode mode = SceneLoadMode.Single)
    {
        if (!mgr.NameToDef.TryGet(sceneName.Value, out int defIdx))
            throw new InvalidOperationException($"Scene not registered (hash={sceneName.Value}).");

        ref SceneDefinition newDef = ref mgr.Definitions[defIdx];

        if (mode == SceneLoadMode.Single)
        {
            // Acquire shared bundles first if we want zero-reload transitions, OR unload old scenes first for minimal peak RAM!
            // To guarantee MINIMUM peak memory usage, we acquire bundles already loaded (ref++)? Wait:
            // Actually acquiring shared bundles that are ALREADY loaded keeps them alive with zero RAM spike,
            // while unloading unshared old bundles BEFORE loading new unshared bundles keeps peak RAM strictly minimal!
            ArenaMarker mark = alloc.Scratch.SaveMarker();
            byte* alreadyHeld = newDef.RequiredBundleCount > 0 ? alloc.Scratch.Alloc<byte>(newDef.RequiredBundleCount) : null;
            for (int b = 0; b < newDef.RequiredBundleCount; b++)
            {
                if (BundleSystem.IsLoaded(in bundles, newDef.RequiredBundles[b]))
                {
                    BundleSystem.Acquire(ref bundles, newDef.RequiredBundles[b], ref world, ref alloc.Scratch);
                    alreadyHeld[b] = 1;
                }
            }

            // Unload all currently active scenes (frees unshared bundles + scene arenas + entities immediately!)
            UnloadAllScenes(ref mgr, ref world, ref alloc, ref bundles);

            // Now acquire remaining new bundles
            for (int b = 0; b < newDef.RequiredBundleCount; b++)
            {
                if (alreadyHeld[b] == 0)
                    BundleSystem.Acquire(ref bundles, newDef.RequiredBundles[b], ref world, ref alloc.Scratch);
            }
            alloc.Scratch.Rollback(mark);
        }
        else
        {
            for (int b = 0; b < newDef.RequiredBundleCount; b++)
                BundleSystem.Acquire(ref bundles, newDef.RequiredBundles[b], ref world, ref alloc.Scratch);
        }

        // Find free active slot
        int slot = -1;
        for (int i = 0; i < mgr.MaxActiveScenes; i++)
        {
            if (mgr.ActiveScenes[i].Active == 0) { slot = i; break; }
        }
        if (slot < 0) throw new OutOfMemoryException("Max active scenes exceeded.");

        SceneId handle = SceneId.Create((uint)slot, mgr.SceneGens[slot]);
        ref ActiveSceneData inst = ref mgr.ActiveScenes[slot];
        inst.Handle = handle;
        inst.DefinitionIndex = defIdx;
        inst.SceneArena = LinearArena.Create(newDef.SceneArenaBytes, newDef.Name.Value);
        inst.Active = 1;
        mgr.ActiveCount++;

        if (newDef.OnLoad != null)
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

        return handle;
    }

    public static bool UnloadScene(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        SceneId sceneId)
    {
        if (!sceneId.IsValid || sceneId.Index >= (uint)mgr.MaxActiveScenes) return false;
        ref ActiveSceneData inst = ref mgr.ActiveScenes[sceneId.Index];
        if (inst.Active == 0 || inst.Handle != sceneId) return false;

        ref SceneDefinition def = ref mgr.Definitions[inst.DefinitionIndex];

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

        // Clean up all world objects belonging to this scene
        EngineWorldSystem.DestroySceneObjects(ref world, sceneId, ref alloc.Scratch);

        // Dispose per-scene arena immediately
        inst.SceneArena.Dispose();
        inst.Active = 0;
        uint g = (mgr.SceneGens[sceneId.Index] + 1) & HandlePacking.GenerationMask;
        mgr.SceneGens[sceneId.Index] = g == 0 ? 1u : g;
        mgr.ActiveCount--;

        // Release bundles (unloads any bundle whose RefCount hits 0)
        for (int b = 0; b < def.RequiredBundleCount; b++)
            BundleSystem.Release(ref bundles, def.RequiredBundles[b], ref world);

        return true;
    }

    public static void UnloadAllScenes(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles)
    {
        for (int i = mgr.MaxActiveScenes - 1; i >= 0; i--)
        {
            if (mgr.ActiveScenes[i].Active != 0)
                UnloadScene(ref mgr, ref world, ref alloc, ref bundles, mgr.ActiveScenes[i].Handle);
        }
    }

    public static void FixedUpdateActiveScenes(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        float dt)
    {
        for (int i = 0; i < mgr.MaxActiveScenes; i++)
        {
            if (mgr.ActiveScenes[i].Active == 0) continue;
            ref ActiveSceneData inst = ref mgr.ActiveScenes[i];
            ref SceneDefinition def = ref mgr.Definitions[inst.DefinitionIndex];
            if (def.OnFixedUpdate == null) continue;

            fixed (LinearArena* sa = &inst.SceneArena)
            fixed (EngineWorld* wp = &world)
            fixed (EngineAllocators* ap = &alloc)
            fixed (BundleManager* bp = &bundles)
            {
                var ctx = new SceneContext
                {
                    Scene = inst.Handle,
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

    public static void ViewSyncActiveScenes(
        ref SceneManager mgr,
        ref EngineWorld world,
        ref EngineAllocators alloc,
        ref BundleManager bundles,
        float dt)
    {
        for (int i = 0; i < mgr.MaxActiveScenes; i++)
        {
            if (mgr.ActiveScenes[i].Active == 0) continue;
            ref ActiveSceneData inst = ref mgr.ActiveScenes[i];
            ref SceneDefinition def = ref mgr.Definitions[inst.DefinitionIndex];
            if (def.OnViewSync == null) continue;

            fixed (LinearArena* sa = &inst.SceneArena)
            fixed (EngineWorld* wp = &world)
            fixed (EngineAllocators* ap = &alloc)
            fixed (BundleManager* bp = &bundles)
            {
                var ctx = new SceneContext
                {
                    Scene = inst.Handle,
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
}

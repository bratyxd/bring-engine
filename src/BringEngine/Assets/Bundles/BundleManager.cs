using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Packing;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.World;

namespace BringEngine.Assets.Bundles;

/// <summary>
/// Passed by ref to programmatic bundle loader callbacks so bundles populate their own LinearArena.
/// </summary>
public unsafe struct BundleLoadContext
{
    public BundleId Bundle;
    public LinearArena* BundleArena;
    public LinearArena* ScratchArena;
    public EngineWorld* World;
    public void* UserData;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct AssetBundleData
{
    public BundleId Handle;
    public StringId Name;
    public FixedString64 NameText;
    public FixedString256 ManifestPath;
    public FixedString256 PackPath;
    public LinearArena Arena;
    public nuint ArenaCapacityBytes;
    public int RefCount;
    public byte Loaded;
    public delegate*<ref BundleLoadContext, void> LoaderFunc;
    public void* UserData;
}

/// <summary>
/// Reference-counted Asset Bundle manager.
/// Each loaded bundle owns a dedicated LinearArena; when RefCount drops to 0, all CPU & GPU assets
/// belonging to the bundle are unloaded and its LinearArena is disposed immediately for minimal RAM.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BundleManager
{
    public AssetBundleData* Bundles;
    public int Count;
    public int Capacity;
    public NativeHashMap<int> NameToSlot;

    public static BundleManager Create(ref LinearArena persistentArena, int maxBundles = 64)
    {
        return new BundleManager
        {
            Bundles = persistentArena.Alloc<AssetBundleData>(maxBundles, 64),
            Count = 0,
            Capacity = maxBundles,
            NameToSlot = NativeHashMap<int>.Create(ref persistentArena, maxBundles),
        };
    }

    public readonly nuint TotalLoadedArenaBytes
    {
        get
        {
            nuint total = 0;
            for (int i = 0; i < Count; i++)
                if (Bundles[i].Loaded != 0) total += Bundles[i].Arena.Capacity;
            return total;
        }
    }

    public readonly int LoadedBundleCount
    {
        get
        {
            int c = 0;
            for (int i = 0; i < Count; i++)
                if (Bundles[i].Loaded != 0) c++;
            return c;
        }
    }
}

public static unsafe class BundleSystem
{
    public static BundleId Register(
        ref BundleManager mgr,
        ReadOnlySpan<char> name,
        nuint arenaCapacityBytes,
        delegate*<ref BundleLoadContext, void> loaderFunc,
        void* userData = null)
    {
        if (mgr.Count >= mgr.Capacity)
            throw new OutOfMemoryException("BundleManager capacity exceeded.");

        StringId id = StringId.Hash(name);
        if (mgr.NameToSlot.TryGet(id.Value, out int existing))
            return mgr.Bundles[existing].Handle;

        int slot = mgr.Count++;
        var handle = BundleId.Create((uint)slot, 1);
        mgr.Bundles[slot] = new AssetBundleData
        {
            Handle = handle,
            Name = id,
            NameText = FixedString64.From(name),
            ArenaCapacityBytes = arenaCapacityBytes,
            LoaderFunc = loaderFunc,
            UserData = userData,
        };
        mgr.NameToSlot.Set(id.Value, slot);
        return handle;
    }

    public static BundleId RegisterManifest(
        ref BundleManager mgr,
        ReadOnlySpan<char> name,
        nuint arenaCapacityBytes,
        ReadOnlySpan<char> manifestJsonPath)
    {
        var handle = Register(ref mgr, name, arenaCapacityBytes, null);
        mgr.Bundles[handle.Index].ManifestPath = FixedString256.From(manifestJsonPath);
        return handle;
    }

    /// <summary>
    /// Registers a compressed .bpack asset bundle (containing BC1/BC3 atlases, 9-slice sprite regions,
    /// compressed .glb models, and SDF fonts — no loose textures required in final builds).
    /// </summary>
    public static BundleId RegisterPack(
        ref BundleManager mgr,
        ReadOnlySpan<char> name,
        nuint arenaCapacityBytes,
        ReadOnlySpan<char> bpackPath,
        delegate*<ref BundleLoadContext, void> postLoadHook = null,
        void* userData = null)
    {
        var handle = Register(ref mgr, name, arenaCapacityBytes, postLoadHook, userData);
        mgr.Bundles[handle.Index].PackPath = FixedString256.From(bpackPath);
        return handle;
    }

    public static BundleId Find(in BundleManager mgr, StringId name)
        => mgr.NameToSlot.TryGet(name.Value, out int slot) ? mgr.Bundles[slot].Handle : BundleId.Invalid;

    public static bool IsLoaded(in BundleManager mgr, BundleId id)
        => id.IsValid && id.Index < (uint)mgr.Count && mgr.Bundles[id.Index].Loaded != 0;

    public static int GetRefCount(in BundleManager mgr, BundleId id)
        => (id.IsValid && id.Index < (uint)mgr.Count) ? mgr.Bundles[id.Index].RefCount : 0;

    /// <summary>
    /// Increments bundle RefCount; allocates bundle LinearArena and loads assets if transitioning 0 -> 1.
    /// </summary>
    public static void Acquire(
        ref BundleManager mgr,
        BundleId id,
        ref EngineWorld world,
        ref LinearArena scratchArena)
    {
        if (!id.IsValid || id.Index >= (uint)mgr.Count) return;
        ref AssetBundleData b = ref mgr.Bundles[id.Index];
        b.RefCount++;
        if (b.Loaded != 0) return;

        b.Arena = LinearArena.Create(b.ArenaCapacityBytes, b.Name.Value);
        b.Loaded = 1;

        if (b.PackPath.Length > 0)
        {
            string packPath = b.PackPath.ToString();
            if (File.Exists(packPath))
                AssetPackLoader.LoadIntoBundle(packPath, ref b.Arena, ref scratchArena, ref world, id);
        }

        if (b.ManifestPath.Length > 0)
        {
            string path = b.ManifestPath.ToString();
            if (File.Exists(path))
                LoadFromManifestJson(path, id, ref b.Arena, ref scratchArena, ref world);
        }

        if (b.LoaderFunc != null)
        {
            fixed (LinearArena* bArenaPtr = &b.Arena)
            fixed (LinearArena* sArenaPtr = &scratchArena)
            fixed (EngineWorld* wPtr = &world)
            {
                var ctx = new BundleLoadContext
                {
                    Bundle = id,
                    BundleArena = bArenaPtr,
                    ScratchArena = sArenaPtr,
                    World = wPtr,
                    UserData = b.UserData,
                };
                b.LoaderFunc(ref ctx);
            }
        }
    }

    /// <summary>
    /// Decrements bundle RefCount; immediately frees all GPU/CPU assets and disposes bundle LinearArena when RefCount == 0.
    /// </summary>
    public static void Release(ref BundleManager mgr, BundleId id, ref EngineWorld world)
    {
        if (!id.IsValid || id.Index >= (uint)mgr.Count) return;
        ref AssetBundleData b = ref mgr.Bundles[id.Index];
        if (b.RefCount <= 0) return;
        b.RefCount--;
        if (b.RefCount > 0 || b.Loaded == 0) return;

        AssetSystem.UnloadByBundle(ref world.Assets, id);
        AnimationSystem.UnregisterByBundle(ref world.Animations, id);
        b.Arena.Dispose();
        b.Loaded = 0;
    }

    public static void UnloadAll(ref BundleManager mgr, ref EngineWorld world)
    {
        for (int i = 0; i < mgr.Count; i++)
        {
            ref AssetBundleData b = ref mgr.Bundles[i];
            if (b.Loaded != 0)
            {
                AssetSystem.UnloadByBundle(ref world.Assets, b.Handle);
                AnimationSystem.UnregisterByBundle(ref world.Animations, b.Handle);
                b.Arena.Dispose();
                b.Loaded = 0;
                b.RefCount = 0;
            }
        }
    }

    /// <summary>
    /// Zero-reflection JSON manifest loader:
    /// { "assets": [ { "type": "gltf|texture|font", "name": "...", "path": "..." } ] }
    /// </summary>
    private static void LoadFromManifestJson(
        string manifestPath,
        BundleId bundle,
        ref LinearArena bundleArena,
        ref LinearArena scratchArena,
        ref EngineWorld world)
    {
        byte[] jsonBytes = File.ReadAllBytes(manifestPath);
        string baseDir = Path.GetDirectoryName(manifestPath) ?? ".";
        var r = new Utf8JsonReader(jsonBytes, isFinalBlock: true, state: default);

        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.PropertyName && r.ValueTextEquals("assets"u8))
            {
                r.Read(); // StartArray
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    string type = "", name = "", relPath = "";
                    float fontSize = 48f;
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        if (r.ValueTextEquals("type"u8)) { r.Read(); type = r.GetString() ?? ""; }
                        else if (r.ValueTextEquals("name"u8)) { r.Read(); name = r.GetString() ?? ""; }
                        else if (r.ValueTextEquals("path"u8)) { r.Read(); relPath = r.GetString() ?? ""; }
                        else if (r.ValueTextEquals("pixelSize"u8)) { r.Read(); fontSize = r.GetSingle(); }
                        else { r.Read(); r.Skip(); }
                    }

                    string full = string.IsNullOrEmpty(relPath) ? "" : Path.Combine(baseDir, relPath);
                    StringId nameId = StringId.Hash(name);
                    if (type == "gltf" && File.Exists(full))
                        GltfLoader.LoadFromFile(full, ref bundleArena, ref scratchArena, ref world.Assets, ref world.Animations, bundle, nameId);
                    else if (type == "texture" && File.Exists(full))
                        TextureLoader.LoadFromFile(full, ref bundleArena, ref world.Assets, bundle, nameId);
                    else if (type == "font")
                        SdfFontLoader.BakeSdfFromSystemOrFile(full, ref bundleArena, ref world.Assets, bundle, nameId, fontSize);
                }
            }
        }
    }
}

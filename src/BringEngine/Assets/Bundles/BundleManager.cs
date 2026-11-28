using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Fonts;
using BringEngine.Assets.Gltf;
using BringEngine.Assets.Packing;
using BringEngine.Assets.Textures;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Rig2D;
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
    public LogState* Log;

    public static BundleManager Create(ref LinearArena persistentArena, int maxBundles = 64, LogState* log = null)
    {
        return new BundleManager
        {
            Bundles = persistentArena.Alloc<AssetBundleData>(maxBundles, 64),
            Count = 0,
            Capacity = maxBundles,
            NameToSlot = NativeHashMap<int>.Create(ref persistentArena, maxBundles),
            Log = log,
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
    public static void SetLogger(ref BundleManager mgr, LogState* log) => mgr.Log = log;

    public static BundleId Register(
        ref BundleManager mgr,
        ReadOnlySpan<char> name,
        nuint arenaCapacityBytes,
        delegate*<ref BundleLoadContext, void> loaderFunc,
        void* userData = null)
    {
        StringId id = StringId.Hash(name);
        if (mgr.NameToSlot.TryGet(id.Value, out int existing))
            return mgr.Bundles[existing].Handle;

        if (mgr.Count >= mgr.Capacity)
            throw new OutOfMemoryException("BundleManager capacity exceeded.");

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

    private static void ValidateBundlePathLength(ReadOnlySpan<char> path, string paramName)
    {
        if (!path.IsEmpty && Encoding.UTF8.GetByteCount(path) > FixedString256.MaxBytes)
            throw new ArgumentException($"Bundle path exceeds FixedString256 limit ({FixedString256.MaxBytes} UTF-8 bytes).", paramName);
    }

    public static BundleId RegisterManifest(
        ref BundleManager mgr,
        ReadOnlySpan<char> name,
        nuint arenaCapacityBytes,
        ReadOnlySpan<char> manifestJsonPath)
    {
        ValidateBundlePathLength(manifestJsonPath, nameof(manifestJsonPath));
        StringId id = StringId.Hash(name);
        bool alreadyRegistered = mgr.NameToSlot.TryGet(id.Value, out _);
        var handle = Register(ref mgr, name, arenaCapacityBytes, null);
        if (!alreadyRegistered)
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
        ValidateBundlePathLength(bpackPath, nameof(bpackPath));
        StringId id = StringId.Hash(name);
        bool alreadyRegistered = mgr.NameToSlot.TryGet(id.Value, out _);
        var handle = Register(ref mgr, name, arenaCapacityBytes, postLoadHook, userData);
        if (!alreadyRegistered)
            mgr.Bundles[handle.Index].PackPath = FixedString256.From(bpackPath);
        return handle;
    }

    public static BundleId Find(in BundleManager mgr, StringId name)
        => mgr.NameToSlot.TryGet(name.Value, out int slot) ? mgr.Bundles[slot].Handle : BundleId.Invalid;

    public static bool IsLoaded(in BundleManager mgr, BundleId id)
        => id.IsValid && id.Index < (uint)mgr.Count && mgr.Bundles[id.Index].Handle == id && mgr.Bundles[id.Index].Loaded != 0;

    public static int GetRefCount(in BundleManager mgr, BundleId id)
        => (id.IsValid && id.Index < (uint)mgr.Count && mgr.Bundles[id.Index].Handle == id) ? mgr.Bundles[id.Index].RefCount : 0;

    /// <summary>
    /// Increments bundle RefCount; allocates bundle LinearArena and loads assets if transitioning 0 -> 1.
    /// Rolls back partial assets and native arena cleanly if loading throws.
    /// </summary>
    public static void Acquire(
        ref BundleManager mgr,
        BundleId id,
        ref EngineWorld world,
        ref LinearArena scratchArena,
        LogState* log = null)
    {
        if (!id.IsValid || id.Index >= (uint)mgr.Count) return;
        ref AssetBundleData b = ref mgr.Bundles[id.Index];
        if (b.Handle != id) return;
        b.RefCount++;
        if (b.Loaded != 0) return;

        LogState* activeLog = log != null ? log : mgr.Log;
        long startTicks = Stopwatch.GetTimestamp();
        ArenaMarker scratchMark = scratchArena.SaveMarker();

        try
        {
            b.Arena = LinearArena.Create(b.ArenaCapacityBytes, b.Name.Value);
            b.Loaded = 1;

            if (b.PackPath.Length > 0)
            {
                string packPath = b.PackPath.ToString();
                if (!File.Exists(packPath))
                    throw new FileNotFoundException($"Bundle '{b.NameText}' .bpack file not found: '{packPath}'.", packPath);
                AssetPackLoader.LoadIntoBundle(packPath, ref b.Arena, ref scratchArena, ref world, b.Handle);
            }

            if (b.ManifestPath.Length > 0)
            {
                string path = b.ManifestPath.ToString();
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Bundle '{b.NameText}' manifest file not found: '{path}'.", path);
                LoadFromManifestJson(path, b.Handle, ref b.Arena, ref scratchArena, ref world, activeLog);
            }

            if (b.LoaderFunc != null)
            {
                fixed (LinearArena* bArenaPtr = &b.Arena)
                fixed (LinearArena* sArenaPtr = &scratchArena)
                fixed (EngineWorld* wPtr = &world)
                {
                    var ctx = new BundleLoadContext
                    {
                        Bundle = b.Handle,
                        BundleArena = bArenaPtr,
                        ScratchArena = sArenaPtr,
                        World = wPtr,
                        UserData = b.UserData,
                    };
                    b.LoaderFunc(ref ctx);
                }
            }
        }
        catch (Exception ex)
        {
            AssetSystem.UnloadByBundle(ref world.Assets, b.Handle);
            AnimationSystem.UnregisterByBundle(ref world.Animations, b.Handle);
            Rig2DSystem.UnregisterByBundle(ref world.Rigs, b.Handle);
            if (b.Arena.IsValid)
                b.Arena.Dispose();
            b.Loaded = 0;
            b.RefCount--;
            if (activeLog != null)
                LogSystem.Error(activeLog, $"Bundle '{b.NameText}' acquire failed ({ex.GetType().Name}); rolled back");
            throw;
        }
        finally
        {
            scratchArena.Rollback(scratchMark);
        }

        if (activeLog != null)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            LogSystem.Info(activeLog, $"Bundle '{b.NameText}' loaded (handle={b.Handle}, used={b.Arena.Offset}/{b.Arena.Capacity}B, {elapsedMs:F2}ms)");
        }
    }

    /// <summary>
    /// Decrements bundle RefCount; immediately frees all GPU/CPU assets and disposes bundle LinearArena when RefCount == 0.
    /// </summary>
    public static void Release(ref BundleManager mgr, BundleId id, ref EngineWorld world, LogState* log = null)
    {
        if (!id.IsValid || id.Index >= (uint)mgr.Count) return;
        ref AssetBundleData b = ref mgr.Bundles[id.Index];
        if (b.Handle != id) return;
        if (b.RefCount <= 0) return;
        b.RefCount--;
        if (b.RefCount > 0 || b.Loaded == 0) return;

        LogState* activeLog = log != null ? log : mgr.Log;
        nuint reclaimedBytes = b.Arena.Capacity;

        AssetSystem.UnloadByBundle(ref world.Assets, b.Handle);
        AnimationSystem.UnregisterByBundle(ref world.Animations, b.Handle);
        Rig2DSystem.UnregisterByBundle(ref world.Rigs, b.Handle);
        b.Arena.Dispose();
        b.Loaded = 0;

        if (activeLog != null)
            LogSystem.Info(activeLog, $"Bundle '{b.NameText}' unloaded (handle={b.Handle}, reclaimed={reclaimedBytes}B)");
    }

    public static void UnloadAll(ref BundleManager mgr, ref EngineWorld world, LogState* log = null)
    {
        LogState* activeLog = log != null ? log : mgr.Log;
        for (int i = 0; i < mgr.Count; i++)
        {
            ref AssetBundleData b = ref mgr.Bundles[i];
            if (b.Loaded != 0)
            {
                nuint reclaimedBytes = b.Arena.Capacity;
                AssetSystem.UnloadByBundle(ref world.Assets, b.Handle);
                AnimationSystem.UnregisterByBundle(ref world.Animations, b.Handle);
                Rig2DSystem.UnregisterByBundle(ref world.Rigs, b.Handle);
                b.Arena.Dispose();
                b.Loaded = 0;
                b.RefCount = 0;
                if (activeLog != null)
                    LogSystem.Info(activeLog, $"Bundle '{b.NameText}' unloaded (handle={b.Handle}, reclaimed={reclaimedBytes}B)");
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
        ref EngineWorld world,
        LogState* log)
    {
        byte[] jsonBytes = File.ReadAllBytes(manifestPath);
        ReadOnlySpan<byte> jsonSpan = jsonBytes;
        if (jsonSpan.Length >= 3 && jsonSpan[0] == 0xEF && jsonSpan[1] == 0xBB && jsonSpan[2] == 0xBF)
            jsonSpan = jsonSpan.Slice(3);
        string baseDir = Path.GetDirectoryName(manifestPath) ?? ".";
        var r = new Utf8JsonReader(jsonSpan, isFinalBlock: true, state: default);

        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException($"Bundle manifest '{manifestPath}' root must be a JSON object.");

        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            if (r.ValueTextEquals("assets"u8))
            {
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray)
                    throw new InvalidDataException($"Bundle manifest '{manifestPath}' 'assets' must be a JSON array.");
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject)
                        throw new InvalidDataException($"Bundle manifest '{manifestPath}' 'assets' item must be a JSON object.");

                    string type = "", name = "", relPath = "";
                    float fontSize = 48f;
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        if (r.TokenType != JsonTokenType.PropertyName) continue;
                        if (r.ValueTextEquals("type"u8))
                        {
                            if (!r.Read() || r.TokenType != JsonTokenType.String) throw new InvalidDataException("Expected string for asset.type.");
                            type = r.GetString() ?? "";
                        }
                        else if (r.ValueTextEquals("name"u8))
                        {
                            if (!r.Read() || r.TokenType != JsonTokenType.String) throw new InvalidDataException("Expected string for asset.name.");
                            name = r.GetString() ?? "";
                        }
                        else if (r.ValueTextEquals("path"u8))
                        {
                            if (!r.Read() || (r.TokenType != JsonTokenType.String && r.TokenType != JsonTokenType.Null)) throw new InvalidDataException("Expected string for asset.path.");
                            relPath = r.TokenType == JsonTokenType.Null ? "" : (r.GetString() ?? "");
                        }
                        else if (r.ValueTextEquals("pixelSize"u8))
                        {
                            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetSingle(out fontSize))
                                throw new InvalidDataException("Expected number for asset.pixelSize.");
                        }
                        else { r.Read(); r.Skip(); }
                    }

                    string full = "";
                    if (!string.IsNullOrEmpty(relPath))
                    {
                        if (!PackCompressionSystem.TryResolveSafeRelativePath(baseDir, relPath, out full))
                            throw new InvalidDataException($"Unsafe asset path rejected in manifest '{manifestPath}': '{relPath}'.");
                    }

                    StringId nameId = StringId.Hash(name);
                    if (type == "gltf")
                    {
                        if (string.IsNullOrEmpty(full) || !File.Exists(full))
                            throw new FileNotFoundException($"Manifest glTF asset '{name}' not found: '{full}'.", full);
                        GltfLoader.LoadFromFile(full, ref bundleArena, ref scratchArena, ref world.Assets, ref world.Animations, bundle, nameId);
                    }
                    else if (type == "texture")
                    {
                        if (string.IsNullOrEmpty(full) || !File.Exists(full))
                            throw new FileNotFoundException($"Manifest texture asset '{name}' not found: '{full}'.", full);
                        TextureLoader.LoadFromFile(full, ref bundleArena, ref world.Assets, bundle, nameId);
                    }
                    else if (type == "font")
                    {
                        SdfFontLoader.BakeSdfFromSystemOrFile(string.IsNullOrEmpty(full) ? null : full, ref bundleArena, ref world.Assets, bundle, nameId, fontSize, log: log);
                    }
                    else if (type == "rig2d")
                    {
                        if (string.IsNullOrEmpty(full) || !File.Exists(full))
                            throw new FileNotFoundException($"Manifest rig2d asset '{name}' not found: '{full}'.", full);
                        byte[] brigBytes = full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            ? Rig2DCompiler.CompileFromFile(full)
                            : File.ReadAllBytes(full);
                        Rig2DLoader.LoadFromMemory(brigBytes, ref bundleArena, ref world.Rigs, bundle, nameId);
                    }
                    else
                    {
                        throw new InvalidDataException($"Unrecognized asset type '{type}' in bundle manifest '{manifestPath}'.");
                    }
                }
            }
            else
            {
                if (!r.Read()) throw new InvalidDataException($"Unexpected EOF in manifest '{manifestPath}'.");
                r.Skip();
            }
        }
    }
}

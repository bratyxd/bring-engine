using System;
using System.Text;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Profiling;

namespace BringEngine.Tests;

public static unsafe class CoreTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Phase 2: Core Memory, Containers & Deterministic Math ===");
        Assert.Run("LinearArena_AllocResetRollbackZeroLeak", &TestLinearArena);
        Assert.Run("PoolAndFreeListAllocator_CoalesceZeroLeak", &TestPoolAndFreeList);
        Assert.Run("NativeSparseSet_DensePackingAndSwapRemove", &TestSparseSet);
        Assert.Run("NativeHashMap_InsertLookupDelete", &TestHashMap);
        Assert.Run("FixedMath_DeterministicTrigSqrtVectors", &TestFixedMath);
        Assert.Run("RenderMath_Mat4InverseFrustumAndDetRng", &TestRenderMath);
        Assert.Run("ZeroGcAllocation_HotLoop", &TestZeroGcHotLoop);
        Assert.Run("Handles_AllTypesPackingAndWraparound", &TestHandlesAllTypesAndWraparound);
        Assert.Run("Memory_OverflowBoundsDoubleFreeAndTracker", &TestMemoryEdgeCases);
        Assert.Run("Containers_TombstonesOverflowBoundsAndUtf8Strings", &TestContainersAndStringsEdgeCases);
        Assert.Run("Math_Fix64LargeVectorsDetMathAcosPowExpBounds3", &TestMathComprehensiveEdgeCases);
        Assert.Run("FrameProfiler_MaxScopesDisabledZeroTicksAndReset", &TestFrameProfilerEdgeCases);
        Assert.Run("Core_AllocatorOomRollbackAlignmentAndMathEdgeCases", &TestAllocatorOomRollbackAlignmentAndMathEdgeCases);
    }

    private static void TestLinearArena()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var arena = LinearArena.Create(64 * 1024, StringId.Hash("TestArena").Value);
        Assert.True(arena.IsValid, "Arena should be valid");
        Assert.True(((nuint)arena.BasePtr & 63) == 0, "Arena base must be 64-byte aligned");

        int* a = arena.Alloc<int>(16, 64);
        Assert.True(((nuint)a & 63) == 0, "Aligned alloc must be 64-byte aligned");
        for (int i = 0; i < 16; i++) a[i] = i * 3;

        ArenaMarker mark = arena.SaveMarker();
        byte* temp = arena.Alloc<byte>(1024);
        temp[512] = 0xAB;
        Assert.True(arena.Offset > mark.Offset, "Offset advanced");

        arena.Rollback(mark);
        Assert.Equal(mark.Offset, arena.Offset, "Rollback restores offset");
        Assert.Equal(15 * 3, a[15], "Data before marker preserved");

        arena.Reset();
        Assert.Equal((nuint)0, arena.Offset, "Reset rewinds to 0");
        Assert.Equal(1u, arena.ResetCount, "ResetCount incremented");
        Assert.True(arena.PeakOffset >= 1024, "Peak tracked");

        arena.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after dispose");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after dispose");
    }

    private static void TestPoolAndFreeList()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 128 * 1024);

        var pool = PoolAllocator<Vec3>.Create(ref alloc.Persistent, 32);
        int i0 = pool.RentIndex();
        int i1 = pool.RentIndex();
        pool.GetRef(i0) = new Vec3(1, 2, 3);
        pool.GetRef(i1) = new Vec3(4, 5, 6);
        Assert.Equal(2, pool.ActiveCount, "Pool active count");
        pool.ReturnIndex(i0);
        int i2 = pool.RentIndex();
        Assert.Equal(i0, i2, "Pool LIFO slot reuse");

        // FreeList coalescing test
        void* p1 = alloc.Dynamic.Alloc(1024);
        void* p2 = alloc.Dynamic.Alloc(2048);
        void* p3 = alloc.Dynamic.Alloc(1024);
        Assert.True(alloc.Dynamic.UsedBytes >= 4096, "FreeList used bytes tracked");
        alloc.Dynamic.Free(p2);
        alloc.Dynamic.Free(p1);
        alloc.Dynamic.Free(p3);
        Assert.Equal((nuint)0, alloc.Dynamic.UsedBytes, "FreeList fully coalesced");

        // Large alloc after full coalescing must succeed
        void* big = alloc.Dynamic.Alloc(100 * 1024);
        Assert.True(big != null, "Coalesced block fits large alloc");
        alloc.Dynamic.Free(big);

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "EngineAllocators zero leak");
    }

    private static void TestSparseSet()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var arena = LinearArena.Create(256 * 1024);
        var set = NativeSparseSet<int>.Create(ref arena, maxEntityId: 1024, denseCapacity: 128);

        set.AddOrReplace(10, 100);
        set.AddOrReplace(50, 500);
        set.AddOrReplace(99, 990);
        Assert.Equal(3, set.Count, "Count == 3");
        Assert.True(set.Has(50), "Has 50");
        Assert.Equal(500, set.GetRef(50), "GetRef 50");

        Assert.True(set.Remove(50), "Remove 50");
        Assert.True(!set.Has(50), "50 removed");
        Assert.Equal(2, set.Count, "Count == 2");
        Assert.Equal(990, set.GetRef(99), "99 still accessible after swap-back");
        Assert.Equal(100, set.GetRef(10), "10 still accessible");

        // Dense iteration sum
        int sum = 0;
        for (int i = 0; i < set.Count; i++) sum += set.DenseValues[i];
        Assert.Equal(1090, sum, "Dense values tightly packed");

        arena.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestHashMap()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var arena = LinearArena.Create(256 * 1024);
        var map = NativeHashMap<EntityId>.Create(ref arena, 64);

        var k1 = StringId.Hash("player");
        var k2 = StringId.Hash("boss");
        var e1 = EntityId.Create(7, 2);
        var e2 = EntityId.Create(42, 5);

        map.Set(k1.Value, e1);
        map.Set(k2.Value, e2);
        Assert.True(map.TryGet(k1.Value, out var out1) && out1 == e1, "Lookup player");
        Assert.True(map.TryGet(k2.Value, out var out2) && out2 == e2, "Lookup boss");
        Assert.True(map.Remove(k1.Value), "Remove player");
        Assert.True(!map.TryGet(k1.Value, out _), "Player gone");
        Assert.True(map.TryGet(k2.Value, out var out3) && out3 == e2, "Boss survives tombstone");

        arena.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestFixedMath()
    {
        Fix32 a = Fix32.FromFloat(3.0f);
        Fix32 b = Fix32.FromFloat(4.0f);
        Fix32 hyp = Fix32.Sqrt(a * a + b * b);
        Assert.Near(5.0f, hyp.ToFloat(), 0.001f, "Fix32 Sqrt(3^2+4^2)");

        Assert.Near(0.0f, Fix32.Sin(Fix32.Zero).ToFloat(), 0.001f, "Sin(0)");
        Assert.Near(1.0f, Fix32.Sin(Fix32.HalfPi).ToFloat(), 0.001f, "Sin(pi/2)");
        Assert.Near(0.0f, Fix32.Cos(Fix32.HalfPi).ToFloat(), 0.001f, "Cos(pi/2)");
        Assert.Near(-1.0f, Fix32.Cos(Fix32.Pi).ToFloat(), 0.001f, "Cos(pi)");
        Assert.Near(0.7071f, Fix32.Sin(Fix32.FromFloat(DetMath.Pi * 0.25f)).ToFloat(), 0.002f, "Sin(pi/4)");
        Assert.Near(DetMath.Pi * 0.25f, Fix32.Atan2(Fix32.One, Fix32.One).ToFloat(), 0.01f, "Atan2(1,1)");

        FixQuat q = FixQuat.FromAxisAngle(FixVec3.UnitY, Fix32.HalfPi);
        FixVec3 rotated = q.Rotate(FixVec3.UnitX);
        Assert.Near(0f, rotated.X.ToFloat(), 0.01f, "Quat rot X");
        Assert.Near(-1f, rotated.Z.ToFloat(), 0.01f, "Quat rot Z");
    }

    private static void TestRenderMath()
    {
        Assert.Equal(16, sizeof(Vec4), "sizeof(Vec4) == 16");
        Assert.Equal(64, sizeof(Mat4), "sizeof(Mat4) == 64");

        Mat4 trs = Mat4.CreateTRS(new Vec3(3, 5, -7), Quat.FromAxisAngle(Vec3.UnitY, 0.7f), new Vec3(2, 2, 2));
        Assert.True(Mat4.TryInvert(in trs, out Mat4 inv), "TRS invertible");
        Mat4 id = trs * inv;
        Assert.Near(1f, id.C0.X, 1e-4f, "M*inv C0.X");
        Assert.Near(1f, id.C1.Y, 1e-4f, "M*inv C1.Y");
        Assert.Near(1f, id.C2.Z, 1e-4f, "M*inv C2.Z");
        Assert.Near(1f, id.C3.W, 1e-4f, "M*inv C3.W");
        Assert.Near(0f, id.C3.X, 1e-4f, "M*inv C3.X");

        Mat4 view = Mat4.CreateLookAt(new Vec3(0, 0, 10), Vec3.Zero, Vec3.Up);
        Mat4 proj = Mat4.CreatePerspective(60f * DetMath.Deg2Rad, 16f / 9f, 0.1f, 100f);
        Mat4 vp = proj * view;
        Frustum frustum = Frustum.FromViewProjection(in vp);

        Assert.True(frustum.IntersectsSphere(Vec3.Zero, 1f), "Origin inside frustum");
        Assert.True(!frustum.IntersectsSphere(new Vec3(0, 0, 25), 1f), "Behind camera culled");
        Assert.True(!frustum.IntersectsSphere(new Vec3(0, 0, -200), 1f), "Beyond far plane culled");

        // DetRng determinism check
        var rngA = DetRng.Create(123456789UL);
        var rngB = DetRng.Create(123456789UL);
        for (int i = 0; i < 100; i++)
            Assert.Equal(rngA.NextUInt(), rngB.NextUInt(), "DetRng bit-exact");
    }

    private static void TestZeroGcHotLoop()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 256 * 1024, 64 * 1024, 64 * 1024);
        var rng = DetRng.Create(42);

        // Warmup once
        alloc.BeginFrame();
        var warm = alloc.Frame.AllocSlice<FixVec3>(256);
        warm[0] = FixVec3.One;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 200; frame++)
        {
            alloc.BeginFrame();
            var positions = alloc.Frame.AllocSlice<FixVec3>(512, 64, zeroed: false);
            for (int i = 0; i < positions.Length; i++)
            {
                Fix32 v = rng.NextFix32Range(Fix32.NegOne, Fix32.One);
                positions[i] = new FixVec3(v, v, v);
            }
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before, "Hot frame loop must allocate 0 managed GC bytes");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leak");
    }

    private static void TestHandlesAllTypesAndWraparound()
    {
        Assert.True(!default(EntityId).IsValid, "default EntityId invalid");
        Assert.True(!new EntityId(1).IsValid, "Generation == 0 handle must be invalid");

        uint p0 = HandlePacking.Pack(0, 0);
        Assert.True(p0 != 0 && HandlePacking.UnpackGeneration(p0) == 1, "Pack(0,0) promotes gen 0 -> 1");
        Assert.Equal(4095u, HandlePacking.UnpackGeneration(HandlePacking.Pack(5, 4095)), "Pack gen 4095");
        Assert.Equal(1u, HandlePacking.UnpackGeneration(HandlePacking.Pack(5, 4096)), "Pack gen 4096 wraps -> 1");
        Assert.Equal(1u, HandlePacking.NextGeneration(4095), "NextGeneration(4095) wraps -> 1");
        Assert.Equal(2u, HandlePacking.NextGeneration(1), "NextGeneration(1) == 2");

        bool threwIdx = false;
        try { _ = HandlePacking.Pack(HandlePacking.IndexMask + 1u, 1); }
        catch (ArgumentOutOfRangeException) { threwIdx = true; }
        Assert.True(threwIdx, "Pack rejects index > IndexMask");

        // Verify all 25 handle structs (including AnimPlayerHandle & SpriteAnimPlayerHandle)
        Assert.True(EntityId.Create(3, 2).IsValid && EntityId.Create(3, 2).Index == 3 && EntityId.Create(3, 2).Generation == 2, "EntityId");
        Assert.True(SceneId.Create(3, 2).IsValid && !SceneId.Invalid.IsValid, "SceneId");
        Assert.True(BundleId.Create(3, 2).IsValid && !BundleId.Invalid.IsValid, "BundleId");
        Assert.True(MeshHandle.Create(3, 2).IsValid && !MeshHandle.Invalid.IsValid, "MeshHandle");
        Assert.True(TextureHandle.Create(3, 2).IsValid && !TextureHandle.Invalid.IsValid, "TextureHandle");
        Assert.True(MaterialHandle.Create(3, 2).IsValid && !MaterialHandle.Invalid.IsValid, "MaterialHandle");
        Assert.True(ShaderHandle.Create(3, 2).IsValid && !ShaderHandle.Invalid.IsValid, "ShaderHandle");
        Assert.True(FontHandle.Create(3, 2).IsValid && !FontHandle.Invalid.IsValid, "FontHandle");
        Assert.True(GltfHandle.Create(3, 2).IsValid && !GltfHandle.Invalid.IsValid, "GltfHandle");
        Assert.True(AnimClipHandle.Create(3, 2).IsValid && !AnimClipHandle.Invalid.IsValid, "AnimClipHandle");
        Assert.True(SkeletonHandle.Create(3, 2).IsValid && !SkeletonHandle.Invalid.IsValid, "SkeletonHandle");
        Assert.True(AnimPlayerHandle.Create(7, 9).IsValid && AnimPlayerHandle.Create(7, 9).Index == 7 && AnimPlayerHandle.Create(7, 9).Generation == 9 && !AnimPlayerHandle.Invalid.IsValid, "AnimPlayerHandle");
        Assert.True(SpriteAnimPlayerHandle.Create(11, 4).IsValid && SpriteAnimPlayerHandle.Create(11, 4).Index == 11 && SpriteAnimPlayerHandle.Create(11, 4).Generation == 4 && !SpriteAnimPlayerHandle.Invalid.IsValid, "SpriteAnimPlayerHandle");
        Assert.True(SpriteAnimClipHandle.Create(13, 6).IsValid && SpriteAnimClipHandle.Create(13, 6).Index == 13 && SpriteAnimClipHandle.Create(13, 6).Generation == 6 && !SpriteAnimClipHandle.Invalid.IsValid, "SpriteAnimClipHandle");
        Assert.True(TweenHandle.Create(3, 2).IsValid && !TweenHandle.Invalid.IsValid, "TweenHandle");
        Assert.True(EmitterHandle.Create(3, 2).IsValid && !EmitterHandle.Invalid.IsValid, "EmitterHandle");
        Assert.True(CameraHandle.Create(3, 2).IsValid && !CameraHandle.Invalid.IsValid, "CameraHandle");
        Assert.True(SpriteHandle.Create(3, 2).IsValid && !SpriteHandle.Invalid.IsValid, "SpriteHandle");
        Assert.True(TextHandle.Create(3, 2).IsValid && !TextHandle.Invalid.IsValid, "TextHandle");
        Assert.True(JobHandle.Create(3, 2).IsValid && !JobHandle.Invalid.IsValid, "JobHandle");
        Assert.True(HttpRequestHandle.Create(3, 2).IsValid && !HttpRequestHandle.Invalid.IsValid, "HttpRequestHandle");
        Assert.True(WebSocketHandle.Create(3, 2).IsValid && !WebSocketHandle.Invalid.IsValid, "WebSocketHandle");
        Assert.True(LogFileHandle.Create(3, 2).IsValid && !LogFileHandle.Invalid.IsValid, "LogFileHandle");
        Assert.True(SaveOpHandle.Create(3, 2).IsValid && !SaveOpHandle.Invalid.IsValid, "SaveOpHandle");
        Assert.True(AudioClipHandle.Create(3, 2).IsValid && !AudioClipHandle.Invalid.IsValid, "AudioClipHandle");
        Assert.True(AudioVoiceHandle.Create(3, 2).IsValid && !AudioVoiceHandle.Invalid.IsValid, "AudioVoiceHandle");
    }

    private static void TestMemoryEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();

        // NativeMemoryTracker zero-size & non-power-of-2 alignment
        void* zPtr = NativeMemoryTracker.AlignedAlloc(0, 64);
        Assert.True(zPtr != null, "AlignedAlloc(0, 64) returns valid pointer");
        NativeMemoryTracker.AlignedFree(zPtr, 0);
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "AlignedAlloc(0)+Free(0) leaves LiveBytes == 0");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "AlignedAlloc(0)+Free(0) leaves LiveBlocks == 0");

        bool threwAlign = false;
        try { NativeMemoryTracker.AlignedAlloc(16, 3); }
        catch (ArgumentOutOfRangeException) { threwAlign = true; }
        Assert.True(threwAlign, "AlignedAlloc rejects non-power-of-2 alignment");

        // LinearArena overflow wraparound, Create(0), forward rollback, alignment check
        var zeroArena = LinearArena.Create(0);
        Assert.True(zeroArena.IsValid && zeroArena.Capacity >= 64, "Create(0) creates valid 64B arena");
        zeroArena.Dispose();

        var arena = LinearArena.Create(4096);
        _ = arena.Alloc<int>(8, 16);
        nuint savedOffset = arena.Offset;
        bool threwWrap = false;
        try { arena.AllocBytes(nuint.MaxValue - 8, 16); }
        catch (OutOfMemoryException) { threwWrap = true; }
        Assert.True(threwWrap && arena.Offset == savedOffset, "LinearArena overflow wraparound throws OOM without corrupting Offset");

        bool threwBadAlign = false;
        try { arena.AllocBytes(16, 3); }
        catch (ArgumentException) { threwBadAlign = true; }
        Assert.True(threwBadAlign, "LinearArena rejects non-power-of-2 alignment");

        ArenaMarker mark = arena.SaveMarker();
        _ = arena.Alloc<int>(4);
        ArenaMarker forwardMark = arena.SaveMarker();
        arena.Rollback(mark);
        bool threwForward = false;
        try { arena.Rollback(forwardMark); }
        catch (InvalidOperationException) { threwForward = true; }
        Assert.True(threwForward, "LinearArena rejects forward Rollback");
        arena.Dispose();

        // PoolAllocator standalone, exhaustion, double-free, CreateStandalone(0)
        bool threwPoolZero = false;
        try { _ = PoolAllocator<int>.CreateStandalone(0); }
        catch (ArgumentOutOfRangeException) { threwPoolZero = true; }
        Assert.True(threwPoolZero, "PoolAllocator.CreateStandalone(0) throws");

        var pool = PoolAllocator<int>.CreateStandalone(4);
        Assert.True(pool.TryRentIndex(out int p0), "Rent 0");
        Assert.True(pool.TryRentIndex(out int p1), "Rent 1");
        Assert.True(pool.TryRentIndex(out int p2), "Rent 2");
        Assert.True(pool.TryRentIndex(out int p3), "Rent 3");
        Assert.True(!pool.TryRentIndex(out _), "Exhausted TryRentIndex returns false");

        pool.ReturnIndex(p0);
        bool threwDoubleFree = false;
        try { pool.ReturnIndex(p0); }
        catch (InvalidOperationException) { threwDoubleFree = true; }
        Assert.True(threwDoubleFree, "PoolAllocator rejects double-free");

        pool.Reset();
        Assert.Equal(4, pool.FreeCount, "Pool Reset restores FreeCount");
        Assert.Equal(0, pool.ActiveCount, "Pool Reset restores ActiveCount");
        pool.Dispose();

        // FreeListAllocator overflow wraparound, double-free, foreign pointer, null free
        var fl = FreeListAllocator.Create(4096);
        fl.Free(null); // no-op
        void* blk = fl.Alloc(64);
        fl.Free(blk);

        bool threwFlDouble = false;
        try { fl.Free(blk); }
        catch (InvalidOperationException) { threwFlDouble = true; }
        Assert.True(threwFlDouble, "FreeList rejects double-free");

        bool threwFlForeign = false;
        try { fl.Free(fl.BasePtr); }
        catch (ArgumentException) { threwFlForeign = true; }
        Assert.True(threwFlForeign, "FreeList rejects BasePtr/invalid pointer");

        bool threwFlWrap = false;
        try { fl.Alloc(nuint.MaxValue - 8); }
        catch (OutOfMemoryException) { threwFlWrap = true; }
        Assert.True(threwFlWrap, "FreeList rejects nuint wraparound");

        fl.Dispose();

        // EngineAllocators SwapFrameArenas & GetTelemetry
        var eng = EngineAllocators.Create(16384, 8192, 8192, 8192);
        _ = eng.Frame.Alloc<int>(16);
        eng.SwapFrameArenas();
        Assert.Equal((nuint)0, eng.Frame.Offset, "SwapFrameArenas gives fresh Frame arena");
        var tel = eng.GetTelemetry();
        Assert.True(tel.PersistentCapacity >= 16384 && tel.DynamicCapacity >= 8192, "Telemetry populated");
        eng.Dispose();

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after memory edge cases");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after memory edge cases");
    }

    private static void TestContainersAndStringsEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var arena = LinearArena.Create(512 * 1024);

        // NativeList: growth, RemoveAtSwapBack bounds, EnsureCapacity overflow
        var list = NativeList<int>.CreateGrowable(4);
        for (int i = 0; i < 20; i++) list.Add(i * 10);
        Assert.Equal(20, list.Count, "NativeList grew to 20");
        list.RemoveAtSwapBack(3);
        Assert.Equal(190, list[3], "RemoveAtSwapBack swapped last element");

        bool threwListOob = false;
        try { list.RemoveAtSwapBack(99); }
        catch (ArgumentOutOfRangeException) { threwListOob = true; }
        Assert.True(threwListOob, "NativeList RemoveAtSwapBack OOB throws");

        list.Clear();
        bool threwListEmpty = false;
        try { list.RemoveAtSwapBack(0); }
        catch (ArgumentOutOfRangeException) { threwListEmpty = true; }
        Assert.True(threwListEmpty, "NativeList RemoveAtSwapBack on empty throws");

        bool threwEnsureOom = false;
        try { list.EnsureCapacity(int.MaxValue); }
        catch (OutOfMemoryException) { threwEnsureOom = true; }
        Assert.True(threwEnsureOom, "NativeList EnsureCapacity(int.MaxValue) throws OOM instead of hanging");
        list.Dispose();

        // NativeSparseSet bounds
        bool threwSparseZero = false;
        try { _ = NativeSparseSet<int>.Create(ref arena, 0, 8); }
        catch (ArgumentOutOfRangeException) { threwSparseZero = true; }
        Assert.True(threwSparseZero, "NativeSparseSet.Create(0, 8) throws");

        var sset = NativeSparseSet<int>.Create(ref arena, 16, 8);
        bool threwAddOob = false;
        try { sset.AddOrReplace(16, 123); }
        catch (ArgumentOutOfRangeException) { threwAddOob = true; }
        Assert.True(threwAddOob, "NativeSparseSet.AddOrReplace OOB throws");

        bool threwGetMissing = false;
        try { _ = sset.GetRef(5); }
        catch (InvalidOperationException) { threwGetMissing = true; }
        Assert.True(threwGetMissing, "NativeSparseSet.GetRef missing component throws");

        // NativeHashMap tombstone saturation + key 0 + update + TryGetPtr + Clear
        var map = NativeHashMap<int>.Create(ref arena, 8); // Capacity = 16
        for (int i = 1; i <= map.Capacity; i++)
            map.Set((ulong)i, i * 100);
        for (int i = 1; i <= map.Capacity; i++)
            Assert.True(map.Remove((ulong)i), $"Removed key {i}");
        Assert.Equal(0, map.Count, "HashMap empty after removing all");

        for (int i = 100; i < 100 + map.Capacity; i++)
            map.Set((ulong)i, i);
        Assert.Equal(map.Capacity, map.Count, "HashMap re-filled to capacity after tombstone saturation");
        for (int i = 100; i < 100 + map.Capacity; i++)
            Assert.True(map.TryGet((ulong)i, out int val) && val == i, $"Recovered key {i} after saturation");

        map.Clear();
        map.Set(0UL, 77);
        map.Set(0UL, 88);
        int* ptr0 = map.TryGetPtr(0UL);
        Assert.True(ptr0 != null && *ptr0 == 88, "HashMap key 0 update and TryGetPtr");

        // StringId UTF-8 parity & zero-GC check + FixedString non-ASCII truncation
        string uni = "hero_café_世界_🚀";
        byte[] uniUtf8 = Encoding.UTF8.GetBytes(uni);
        Assert.Equal(StringId.HashUtf8(uniUtf8).Value, StringId.Hash(uni.AsSpan()).Value, "StringId.Hash == HashUtf8 on Unicode");

        FixedString64 fs64 = FixedString64.From(uni.AsSpan());
        Assert.Equal(uni, fs64.ToString(), "FixedString64 Unicode roundtrip");
        Assert.Equal(fs64.ToStringId().Value, StringId.Hash(uni.AsSpan()).Value, "FixedString64.ToStringId == StringId.Hash");

        // Huge string O(1) truncation + rune-boundary preservation
        string huge = new string('世', 5000);
        FixedString256 fs256 = FixedString256.From(huge.AsSpan());
        Assert.True(fs256.Length <= FixedString256.MaxBytes && (fs256.Length % 3) == 0, "FixedString256 truncated cleanly on UTF-8 rune boundary");

        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        ulong hashAcc = 0;
        for (int i = 0; i < 100; i++)
            hashAcc ^= StringId.Hash(uni.AsSpan()).Value;
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(hashAcc != 0 || hashAcc == 0, "Keep hashAcc live");
        Assert.Equal(0L, gcAfter - gcBefore, "StringId.Hash Unicode allocates 0 GC bytes");

        arena.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after containers/strings test");
    }

    private static void TestMathComprehensiveEdgeCases()
    {
        // Fix32 boundary cases
        Assert.Equal(32768, Fix32.MaxValue.ToIntRound(), "Fix32.MaxValue.ToIntRound() == 32768");
        Assert.Near(Fix32.Cos(Fix32.MaxValue - Fix32.TwoPi).ToFloat(), Fix32.Cos(Fix32.MaxValue).ToFloat(), 0.001f, "Fix32.Cos(MaxValue) no overflow");
        Assert.Equal(Fix32.MaxValue.Raw, Fix32.Abs(Fix32.MinValue).Raw, "Fix32.Abs(MinValue) saturates");
        Assert.Equal(0, Fix32.FromFloat(float.NaN).Raw, "Fix32.FromFloat(NaN) == 0");
        Assert.Equal(Fix32.MaxValue.Raw, Fix32.FromFloat(1e9f).Raw, "Fix32.FromFloat(+large) == MaxValue");
        Assert.Equal(Fix32.MinValue.Raw, Fix32.FromFloat(-1e9f).Raw, "Fix32.FromFloat(-large) == MinValue");

        // FixVec3 / FixVec2 / FixQuat 64-bit length & normalization
        FixVec3 big = FixVec3.FromFloat(300f, 400f, 0f);
        Assert.Near(500f, big.Length().ToFloat(), 0.01f, "FixVec3(300,400,0).Length() == 500");
        Assert.Near(500f, FixVec3.Distance(FixVec3.Zero, big).ToFloat(), 0.01f, "FixVec3.Distance == 500");
        FixVec3 bigN = big.Normalized();
        Assert.Near(0.6f, bigN.X.ToFloat(), 0.001f, "FixVec3 big normalized X");
        Assert.Near(0.8f, bigN.Y.ToFloat(), 0.001f, "FixVec3 big normalized Y");

        FixVec3 tiny = new FixVec3(new Fix32(10), Fix32.Zero, Fix32.Zero);
        Assert.Equal(FixVec3.UnitX.X.Raw, tiny.Normalized().X.Raw, "FixVec3 tiny normalized == UnitX");
        Assert.True(FixQuat.FromAxisAngle(FixVec3.Zero, Fix32.Pi).Equals(FixQuat.Identity), "FixQuat zero axis -> Identity");

        FixVec2 big2 = FixVec2.FromFloat(300f, 400f);
        Assert.Near(500f, big2.Length().ToFloat(), 0.01f, "FixVec2(300,400).Length() == 500");

        // Fix64 ops
        Fix64 f64 = Fix64.FromRatio(5, 2);
        Assert.Near(2.5f, (float)f64.ToDouble(), 1e-5f, "Fix64.FromRatio");
        Assert.True(Fix64.Abs(-f64) == f64 && Fix64.Clamp(f64, Fix64.Zero, Fix64.One) == Fix64.One, "Fix64 Abs/Clamp");

        // DetMath deterministic Acos, Exp, Exp2, Log, Pow, Clamp01, SmoothStep
        Assert.Near(0f, DetMath.Acos(1f), 1e-6f, "Acos(1)");
        Assert.Near(DetMath.Pi, DetMath.Acos(-1f), 1e-6f, "Acos(-1)");
        Assert.Near(DetMath.HalfPi, DetMath.Acos(0f), 1e-6f, "Acos(0)");
        Assert.Near(DetMath.Pi / 3f, DetMath.Acos(0.5f), 1e-6f, "Acos(0.5)");
        Assert.Near(2.7182818f, DetMath.Exp(1f), 1e-5f, "Exp(1)");
        Assert.Near(8f, DetMath.Exp2(3f), 1e-5f, "Exp2(3)");
        Assert.Near(1f, DetMath.Log(DetMath.Exp(1f)), 1e-5f, "Log(e)");
        Assert.Near(8f, DetMath.Pow(2f, 3f), 1e-5f, "Pow(2,3)");
        Assert.Near(-8f, DetMath.Pow(-2f, 3f), 1e-5f, "Pow(-2,3)");
        Assert.True(float.IsNaN(DetMath.Pow(-2f, 0.5f)), "Pow(-2, 0.5) == NaN");
        Assert.Equal(0f, DetMath.Clamp01(float.NaN), "Clamp01(NaN) == 0");
        Assert.True(!float.IsNaN(DetMath.SmoothStep(1f, 1f, 1f)), "SmoothStep equal edges not NaN");

        // Quat.Slerp antipodal & normal, LookRotation UnitX fallback
        Quat qA = Quat.FromAxisAngle(Vec3.UnitY, 0.2f);
        Quat qB = new Quat(-qA.X, -qA.Y, -qA.Z, -qA.W);
        Quat qS = Quat.Slerp(qA, qB, 0.5f);
        Assert.Near(1f, qS.X * qS.X + qS.Y * qS.Y + qS.Z * qS.Z + qS.W * qS.W, 1e-4f, "Quat.Slerp antipodal unit");

        Quat qX = Quat.LookRotation(Vec3.UnitX, Vec3.UnitX);
        Assert.Near(1f, qX.X * qX.X + qX.Y * qX.Y + qX.Z * qX.Z + qX.W * qX.W, 1e-4f, "LookRotation parallel UnitX valid");

        // Bounds3.Empty & Frustum & NaN Mat4.TryInvert
        Mat4 proj = Mat4.CreatePerspective(60f * DetMath.Deg2Rad, 16f / 9f, 0.1f, 100f);
        Mat4 view = Mat4.CreateLookAt(new Vec3(0, 0, 10), Vec3.Zero, Vec3.Up);
        Mat4 vp = proj * view;
        Frustum frustum = Frustum.FromViewProjection(in vp);
        Assert.True(!frustum.IntersectsBounds(Bounds3.Empty), "Frustum must NOT intersect Bounds3.Empty");
        Assert.True(Bounds3.Empty.Transform(Mat4.Identity).IsEmpty, "Bounds3.Empty.Transform remains Empty without NaN");

        Mat4 nanMat = Mat4.Identity;
        nanMat.C0.X = float.NaN;
        Assert.True(!Mat4.TryInvert(in nanMat, out _), "NaN Mat4.TryInvert returns false");

        Mat4 singular = default;
        Assert.True(!Mat4.TryInvert(in singular, out _), "Zero Mat4.TryInvert returns false");

        // DetRng inverted range
        var rng = DetRng.Create(123);
        Assert.Equal(10, rng.NextInt(10, 5), "NextInt(10, 5) returns minInclusive");
    }

    private static void TestFrameProfilerEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var arena = LinearArena.Create(64 * 1024);
        var prof = FrameProfiler.Create(ref arena, enabled: true);

        ProfilerSystem.BeginFrame(ref prof);
        for (int i = 0; i < FrameProfiler.MaxScopesPerFrame + 10; i++)
        {
            int idx = ProfilerSystem.BeginScope(ref prof, "S");
            if (i < FrameProfiler.MaxScopesPerFrame)
            {
                Assert.True(idx == i, $"Scope {i} accepted");
                ProfilerSystem.EndScope(ref prof, idx);
            }
            else
            {
                Assert.Equal(-1, idx, "Overflow scope returns -1 cleanly");
            }
        }
        Assert.Equal(FrameProfiler.MaxScopesPerFrame, prof.ActiveScopeCount, "ActiveScopeCount capped at MaxScopesPerFrame");
        Assert.Equal(0, prof.CurrentDepth, "CurrentDepth balanced");
        ProfilerSystem.EndCpuWork(ref prof);
        ProfilerSystem.EndFrame(ref prof);
        Assert.True(double.IsFinite(prof.InstantFps), "InstantFps finite on immediate EndFrame");

        // Disabled profiler no-ops
        prof.Enabled = 0;
        Assert.Equal(-1, ProfilerSystem.BeginScope(ref prof, "Disabled"), "Disabled BeginScope returns -1");
        ProfilerSystem.ResetHistory(ref prof);
        Assert.Equal(0, prof.HistoryCount, "ResetHistory resets HistoryCount");

        arena.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after FrameProfiler test");
    }

    private static void TestAllocatorOomRollbackAlignmentAndMathEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();

        // CORE-06: EngineAllocators.Create rolls back earlier native allocations on OOM
        long liveBefore = NativeMemoryTracker.LiveBytes;
        long blocksBefore = NativeMemoryTracker.LiveBlocks;
        bool threwEngOom = false;
        try { _ = EngineAllocators.Create(4096, 4096, 4096, nuint.MaxValue); }
        catch (OutOfMemoryException) { threwEngOom = true; }
        Assert.True(threwEngOom, "EngineAllocators.Create throws OOM on overflowing dynamicBytes");
        Assert.Equal(liveBefore, NativeMemoryTracker.LiveBytes, "EngineAllocators.Create OOM restores exact LiveBytes");
        Assert.Equal(blocksBefore, NativeMemoryTracker.LiveBlocks, "EngineAllocators.Create OOM restores exact LiveBlocks");

        // CORE-14: LinearArena absolute address alignment for alignment > 64 (256 and 4096)
        var arenas = new LinearArena[8];
        try
        {
            for (int i = 0; i < arenas.Length; i++)
            {
                arenas[i] = LinearArena.Create(16384, (ulong)(i + 1));
                _ = arenas[i].AllocBytes((nuint)((i + 1) * 13), 1);
                void* p256 = arenas[i].AllocBytes(64, 256);
                Assert.True(((nuint)p256 & 255u) == 0, $"Arena {i} 256-byte aligned pointer");
                void* p4096 = arenas[i].AllocBytes(64, 4096);
                Assert.True(((nuint)p4096 & 4095u) == 0, $"Arena {i} 4096-byte aligned pointer");
            }
        }
        finally
        {
            for (int i = 0; i < arenas.Length; i++)
                arenas[i].Dispose();
        }

        // CORE-18: Multi-allocation Create helpers roll back LinearArena.Offset on mid-way OOM
        var tight = LinearArena.Create(1024);
        try
        {
            _ = tight.AllocBytes(64, 64);
            nuint markOffset = tight.Offset;

            bool threwPool = false;
            try { _ = PoolAllocator<int>.Create(ref tight, 128); } // slots(512) ok, freeStack(512) OOM
            catch (OutOfMemoryException) { threwPool = true; }
            Assert.True(threwPool && tight.Offset == markOffset, "PoolAllocator.Create mid-way OOM restores arena.Offset");

            bool threwSparse = false;
            try { _ = NativeSparseSet<int>.Create(ref tight, maxEntityId: 128, denseCapacity: 128); } // sparse(512) ok, denseIds(512) OOM
            catch (OutOfMemoryException) { threwSparse = true; }
            Assert.True(threwSparse && tight.Offset == markOffset, "NativeSparseSet.Create mid-way OOM restores arena.Offset");

            bool threwMap = false;
            try { _ = NativeHashMap<Vec4>.Create(ref tight, minCapacity: 32); } // cap=64: keys(512) ok, values(1024) OOM
            catch (OutOfMemoryException) { threwMap = true; }
            Assert.True(threwMap && tight.Offset == markOffset, "NativeHashMap.Create mid-way OOM restores arena.Offset");
        }
        finally
        {
            tight.Dispose();
        }

        // CORE-13: DetMath.Sin/Cos large-angle range reduction
        float[] largeAngles = { 1e6f, 1e7f, 1e9f, 1e20f, float.MaxValue, -float.MaxValue };
        for (int i = 0; i < largeAngles.Length; i++)
        {
            float a = largeAngles[i];
            float s = DetMath.Sin(a);
            float c = DetMath.Cos(a);
            Assert.True(float.IsFinite(s) && DetMath.Abs(s) <= 1.0001f, $"DetMath.Sin({a}) finite and bounded: {s}");
            Assert.True(float.IsFinite(c) && DetMath.Abs(c) <= 1.0001f, $"DetMath.Cos({a}) finite and bounded: {c}");
        }

        // CORE-19: Quat.FromAxisAngle zero-axis returns Identity
        Quat qZero = Quat.FromAxisAngle(Vec3.Zero, DetMath.Pi);
        Assert.True(qZero.Equals(Quat.Identity), "Quat.FromAxisAngle(Vec3.Zero, Pi) == Quat.Identity");

        // CORE-21: DetMath.Atan2(±Inf, ±Inf) and Fix64.FromRatio with large numerator
        Assert.Near(DetMath.Pi * 0.25f, DetMath.Atan2(float.PositiveInfinity, float.PositiveInfinity), 1e-5f, "Atan2(+Inf,+Inf)");
        Assert.Near(-DetMath.Pi * 0.25f, DetMath.Atan2(float.NegativeInfinity, float.PositiveInfinity), 1e-5f, "Atan2(-Inf,+Inf)");
        Assert.Near(DetMath.Pi * 0.75f, DetMath.Atan2(float.PositiveInfinity, float.NegativeInfinity), 1e-5f, "Atan2(+Inf,-Inf)");
        Assert.Near(-DetMath.Pi * 0.75f, DetMath.Atan2(float.NegativeInfinity, float.NegativeInfinity), 1e-5f, "Atan2(-Inf,-Inf)");

        Fix64 bigRatio = Fix64.FromRatio(1_000_000_000_000L, 1_000_000_000L);
        Assert.True(System.Math.Abs(bigRatio.ToDouble() - 1000.0) < 1e-6, $"Fix64.FromRatio large numerator exact: {bigRatio.ToDouble()}");

        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after core audit regression tests");
        Assert.Equal(0L, NativeMemoryTracker.LiveBlocks, "Zero live blocks after core audit regression tests");
    }
}

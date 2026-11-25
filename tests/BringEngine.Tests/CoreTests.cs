using System;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

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
}

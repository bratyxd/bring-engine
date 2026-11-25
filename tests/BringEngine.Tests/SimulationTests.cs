using System;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class SimulationTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Phase 3: ECS World, Transform Hierarchy, Camera & Input ===");
        Assert.Run("EntityTable_SpawnDestroyGenerationAndSceneCollect", &TestEntities);
        Assert.Run("TransformTable_ParentChildHierarchyPropagation", &TestTransformHierarchy);
        Assert.Run("CameraSystem_FollowShakeFrustumAndRayPick", &TestCameraSystem);
        Assert.Run("InputState_BitsetEdgesAndWASD", &TestInputState);
    }

    private static void TestEntities()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var table = EntityTable.Create(ref alloc.Persistent, 256);

        var sceneA = SceneId.Create(1, 1);
        var sceneB = SceneId.Create(2, 1);

        var e1 = EntitySystem.Spawn(ref table, sceneA);
        var e2 = EntitySystem.Spawn(ref table, sceneB);
        var e3 = EntitySystem.Spawn(ref table, sceneA);
        Assert.Equal(3, table.AliveCount, "3 alive");
        Assert.True(EntitySystem.IsAlive(in table, e1), "e1 alive");

        Assert.True(EntitySystem.Destroy(ref table, e2), "destroy e2");
        Assert.True(!EntitySystem.IsAlive(in table, e2), "e2 dead");
        Assert.Equal(2, table.AliveCount, "2 alive");

        // Reuse slot -> new generation invalidates old e2 handle
        var e4 = EntitySystem.Spawn(ref table, sceneA);
        Assert.Equal(e2.Index, e4.Index, "slot reused");
        Assert.True(e4.Generation != e2.Generation, "generation incremented");
        Assert.True(!EntitySystem.IsAlive(in table, e2), "stale handle rejected");
        Assert.True(EntitySystem.IsAlive(in table, e4), "new handle alive");

        alloc.BeginFrame();
        var sceneAEntities = EntitySystem.CollectByScene(in table, sceneA, ref alloc.Frame);
        Assert.Equal(3, sceneAEntities.Length, "3 entities in sceneA");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestTransformHierarchy()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 256);
        var transforms = TransformTable.Create(ref alloc.Persistent, 256, 256);

        var root = EntitySystem.Spawn(ref entities);
        var child = EntitySystem.Spawn(ref entities);
        var grandChild = EntitySystem.Spawn(ref entities);

        // Root at (10, 0, 0) rotated 90 deg around Y (+X becomes -Z)
        TransformSystem.Add(ref transforms, root, new Vec3(10, 0, 0), Quat.FromAxisAngle(Vec3.UnitY, DetMath.HalfPi), Vec3.One);
        TransformSystem.Add(ref transforms, child, new Vec3(5, 0, 0), Quat.Identity, new Vec3(2, 2, 2), root);
        TransformSystem.Add(ref transforms, grandChild, new Vec3(0, 3, 0), Quat.Identity, Vec3.One, child);

        TransformSystem.PropagateHierarchy(ref transforms);

        Vec3 childWorld = TransformSystem.GetWorldPosition(in transforms, child);
        Assert.Near(10f, childWorld.X, 1e-3f, "child world X");
        Assert.Near(0f, childWorld.Y, 1e-3f, "child world Y");
        Assert.Near(-5f, childWorld.Z, 1e-3f, "child world Z");

        Vec3 grandWorld = TransformSystem.GetWorldPosition(in transforms, grandChild);
        Assert.Near(10f, grandWorld.X, 1e-3f, "grandChild world X");
        Assert.Near(6f, grandWorld.Y, 1e-3f, "grandChild world Y (scaled by parent 2x)");
        Assert.Near(-5f, grandWorld.Z, 1e-3f, "grandChild world Z");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestCameraSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 64);
        var transforms = TransformTable.Create(ref alloc.Persistent, 64, 64);
        var cameras = CameraTable.Create(ref alloc.Persistent, 16);

        var target = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, target, new Vec3(0, 2, 0));
        TransformSystem.PropagateHierarchy(ref transforms);

        var mainCam = CameraSystem.CreatePerspective(ref cameras, new Vec3(0, 5, 10), Vec3.Zero, 60f, 0.1f, 200f, depthOrder: 0);
        var uiCam = CameraSystem.CreateOrthographicUI(ref cameras, 1280, 720, depthOrder: 100);

        var camPtr = CameraSystem.TryGetPtr(in cameras, mainCam);
        camPtr->Mode = CameraMode.FollowTarget;
        camPtr->TargetEntity = target;
        camPtr->FollowOffset = new Vec3(0, 4, 10);
        camPtr->LookOffset = Vec3.Zero;
        camPtr->FollowDamping = 0f; // instant snap

        CameraSystem.AddTrauma(ref cameras, mainCam, 0.5f);
        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);

        Assert.Near(6f, camPtr->Position.Y, 1e-3f, "Follow cam snapped Y");
        Assert.True(camPtr->Frustum.IntersectsSphere(new Vec3(0, 2, 0), 0.5f), "Target visible in frustum");

        // Screen center ray should point towards target
        Ray centerRay = CameraSystem.ScreenToWorldRay(in *camPtr, 640f, 360f, 1280f, 720f);
        Assert.True(Vec3.Dot(centerRay.Direction, camPtr->Forward) > 0.99f, "Center ray aligns with camera forward");

        // WorldToScreen of look target should be near screen center
        Assert.True(CameraSystem.WorldToScreen(in *camPtr, camPtr->LookTarget, 1280f, 720f, out Vec3 sp), "LookTarget projects onscreen");
        Assert.Near(640f, sp.X, 40f, "Projected X near center");
        Assert.Near(360f, sp.Y, 40f, "Projected Y near center");

        // Sorted camera order
        alloc.BeginFrame();
        var sorted = CameraSystem.GetSortedActiveCameras(in cameras, ref alloc.Frame);
        Assert.Equal(2, sorted.Length, "2 active cameras");
        Assert.Equal(mainCam, sorted[0], "Main camera renders first");
        Assert.Equal(uiCam, sorted[1], "UI camera renders second");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestInputState()
    {
        InputState input = default;
        input.BeginFrame();
        input.SetKey(KeyCode.W, true);
        Assert.True(input.IsKeyDown(KeyCode.W), "W down");
        Assert.True(input.IsKeyPressed(KeyCode.W), "W just pressed");

        var axis = input.GetMoveAxisWASD();
        Assert.Near(0f, axis.X, 1e-4f, "WASD X");
        Assert.Near(1f, axis.Y, 1e-4f, "WASD Y");

        input.BeginFrame();
        Assert.True(input.IsKeyDown(KeyCode.W), "W still down");
        Assert.True(!input.IsKeyPressed(KeyCode.W), "W not newly pressed");

        input.SetKey(KeyCode.W, false);
        Assert.True(input.IsKeyReleased(KeyCode.W), "W released");
    }
}

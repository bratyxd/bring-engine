using System;
using System.IO;
using BringEngine.Assets;
using BringEngine.Assets.Bundles;
using BringEngine.Core.Handles;
using BringEngine.Core.Logging;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.Scenes;
using BringEngine.Simulation.Tween;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class SimulationTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Phase 3: ECS World, Transform Hierarchy, Camera, Input, RenderData & Scenes ===");
        Assert.Run("EntityTable_SpawnDestroyGenerationAndSceneCollect", &TestEntities);
        Assert.Run("TransformTable_ParentChildHierarchyPropagation", &TestTransformHierarchy);
        Assert.Run("TransformTable_CyclesChildBeforeParentReparentRemoveAndParallelParity", &TestTransformEdgeCasesAndParallel);
        Assert.Run("EngineWorld_StepCoreDestroyDescendantsAndSceneCleanup", &TestEngineWorldAndCleanup);
        Assert.Run("CameraSystem_FollowShakeFrustumAndRayPick", &TestCameraSystem);
        Assert.Run("CameraSystem_ModesBoundsOrthoShakeAndTargetRemoval", &TestCameraEdgeCases);
        Assert.Run("InputState_BitsetEdgesAndWASD", &TestInputState);
        Assert.Run("InputState_ExtendedKeysMouseReleaseTextInputFocusAndZeroGc", &TestInputPlatformEssentials);
        Assert.Run("InputState_Utf8RuneBoundsUndersizedSpanMouseAndGamepadNan", &TestInputEdgeCases);
        Assert.Run("GamepadInput_ButtonsAxesRadialDeadzoneMultiSlotAndCombinedMoveZeroGc", &TestGamepadInput);
        Assert.Run("RenderComponentSystem_SpritesTextCapacityGuardsAndCleanup", &TestRenderComponentTables);
        Assert.Run("SceneManager_CapacityGuardDuplicateRejectRollbackAndLogging", &TestSceneManagerEdgeCases);
        Assert.Run("TransformTable_ForwardRefCyclesUnresolvedFlagLeafRemoveAndDeepSerial", &TestTransformAuditRegressions);
        Assert.Run("EngineWorld_DestroyEntity300ChildrenOverflowZeroGc", &TestDestroyEntity300ChildrenOverflow);
        Assert.Run("CameraSystem_ViewportRectSubProjectionAndDegenerateOrbitPoleGuards", &TestCameraAuditRegressions);
        Assert.Run("InputState_OrphanUtf8ContinuationTrimAndApplyFocusLost", &TestInputAuditRegressions);
        Assert.Run("SceneManager_ArenaOomReentrantCallbacksRegisterOomAndTickSnapshot", &TestSceneManagerAuditRegressions);
        Assert.Run("SimulationTables_CapacityBoundsValidation", &TestSimulationTableCapacityBounds);
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

    private static void TestTransformEdgeCasesAndParallel()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 2048);
        var transforms = TransformTable.Create(ref alloc.Persistent, 2048, 2048);

        // 1. Invalid / out-of-range EntityId rejected
        Assert.Equal(-1, TransformSystem.Add(ref transforms, EntityId.Invalid, Vec3.Zero), "Reject Invalid EntityId");
        Assert.Equal(-1, TransformSystem.Add(ref transforms, EntityId.Create(5000, 1), Vec3.Zero), "Reject out-of-range EntityId");
        Assert.Equal(0, transforms.Count, "Count remains 0");

        // 2. Self-parenting & 2-node cycle rejection
        var a = EntitySystem.Spawn(ref entities);
        var b = EntitySystem.Spawn(ref entities);
        int da = TransformSystem.Add(ref transforms, a, new Vec3(1, 0, 0), Quat.Identity, Vec3.One, parent: a);
        Assert.True(da >= 0 && !transforms.Parent[da].IsValid, "Self-parenting rejected");
        TransformSystem.Add(ref transforms, b, new Vec3(2, 0, 0), Quat.Identity, Vec3.One, parent: a);
        TransformSystem.SetParent(ref transforms, a, b); // Would create A -> B -> A cycle
        Assert.True(!transforms.Parent[TransformSystem.GetDenseIndex(in transforms, a)].IsValid, "Cycle A->B->A rejected");

        // 3. Child added BEFORE parent + re-parenting + Remove detaches children & updates MaxDepth
        var lateParent = EntitySystem.Spawn(ref entities);
        var earlyChild = EntitySystem.Spawn(ref entities);
        var grandChild = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, earlyChild, new Vec3(3, 0, 0), Quat.Identity, Vec3.One, parent: lateParent);
        TransformSystem.Add(ref transforms, grandChild, new Vec3(0, 4, 0), Quat.Identity, Vec3.One, parent: earlyChild);
        TransformSystem.Add(ref transforms, lateParent, new Vec3(10, 0, 0));
        TransformSystem.PropagateHierarchy(ref transforms);
        Assert.Near(13f, TransformSystem.GetWorldPosition(in transforms, earlyChild).X, 1e-4f, "Child-before-parent propagated X");
        Assert.Near(4f, TransformSystem.GetWorldPosition(in transforms, grandChild).Y, 1e-4f, "Grandchild propagated Y");
        Assert.Equal((byte)2, transforms.MaxDepth, "MaxDepth == 2");

        // Re-parent earlyChild under b (at x=2) via Add
        TransformSystem.Add(ref transforms, earlyChild, new Vec3(3, 0, 0), Quat.Identity, Vec3.One, parent: b);
        TransformSystem.PropagateHierarchy(ref transforms);
        Assert.Near(6f, TransformSystem.GetWorldPosition(in transforms, earlyChild).X, 1e-4f, "Re-parented via Add X=1+2+3");
        Assert.Equal((byte)3, transforms.MaxDepth, "MaxDepth updated to 3 after re-parenting");

        // Remove b -> earlyChild becomes root, MaxDepth drops
        Assert.True(TransformSystem.Remove(ref transforms, b), "Remove b");
        TransformSystem.PropagateHierarchy(ref transforms);
        Assert.True(!transforms.Parent[TransformSystem.GetDenseIndex(in transforms, earlyChild)].IsValid, "Child detached on parent Remove");
        Assert.Near(3f, TransformSystem.GetWorldPosition(in transforms, earlyChild).X, 1e-4f, "Detached child world X");
        Assert.Equal((byte)1, transforms.MaxDepth, "MaxDepth recomputed after Remove");

        // Basis vectors check (90 deg yaw around Y: forward=-X, right=+Z, up=+Y)
        var basisEnt = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, basisEnt, Vec3.Zero, Quat.FromAxisAngle(Vec3.UnitY, DetMath.HalfPi), Vec3.One);
        TransformSystem.PropagateHierarchy(ref transforms);
        Vec3 fwd = TransformSystem.GetWorldForward(in transforms, basisEnt);
        Vec3 right = TransformSystem.GetWorldRight(in transforms, basisEnt);
        Vec3 up = TransformSystem.GetWorldUp(in transforms, basisEnt);
        Assert.Near(-1f, fwd.X, 1e-4f, "WorldForward X");
        Assert.Near(-1f, right.Z, 1e-4f, "WorldRight Z");
        Assert.Near(1f, up.Y, 1e-4f, "WorldUp Y");

        // 4. Deep chain > 32 (depth 40) + >= 1024 parallel vs single-threaded bit-exact parity
        var tSingle = TransformTable.Create(ref alloc.Persistent, 2048, 2048);
        var tParallel = TransformTable.Create(ref alloc.Persistent, 2048, 2048);
        var ents = stackalloc EntityId[1200];
        for (int i = 0; i < 1200; i++)
        {
            ents[i] = EntitySystem.Spawn(ref entities);
            EntityId p = i == 0 ? EntityId.Invalid : (i <= 40 ? ents[i - 1] : ents[i % 40]);
            Vec3 pos = new Vec3(0.5f, (i & 7) * 0.25f, -0.1f);
            Quat rot = Quat.FromAxisAngle(Vec3.UnitY, (i & 15) * 0.05f);
            TransformSystem.Add(ref tSingle, ents[i], pos, rot, Vec3.One, p);
            TransformSystem.Add(ref tParallel, ents[i], pos, rot, Vec3.One, p);
        }
        Assert.Equal((byte)40, tSingle.MaxDepth, "Chain depth 40 > 32 supported");

        JobScheduler* jobs = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(jobs, ref alloc.Persistent, workerCount: 2);

        TransformSystem.PropagateHierarchy(ref tSingle, null);
        // Warmup parallel once, then verify 0 GC
        TransformSystem.PropagateHierarchy(ref tParallel, jobs);
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        TransformSystem.PropagateHierarchy(ref tParallel, jobs);
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "Parallel PropagateHierarchy allocates 0 GC bytes");

        for (int i = 0; i < tSingle.Count; i++)
        {
            Mat4 m1 = tSingle.WorldMatrix[i];
            Mat4 m2 = tParallel.WorldMatrix[i];
            Assert.Equal(BitConverter.SingleToUInt32Bits(m1.C3.X), BitConverter.SingleToUInt32Bits(m2.C3.X), "Parallel parity C3.X");
            Assert.Equal(BitConverter.SingleToUInt32Bits(m1.C3.Y), BitConverter.SingleToUInt32Bits(m2.C3.Y), "Parallel parity C3.Y");
            Assert.Equal(BitConverter.SingleToUInt32Bits(m1.C3.Z), BitConverter.SingleToUInt32Bits(m2.C3.Z), "Parallel parity C3.Z");
            Assert.Equal(BitConverter.SingleToUInt32Bits(m1.C0.X), BitConverter.SingleToUInt32Bits(m2.C0.X), "Parallel parity C0.X");
        }

        JobSystem.Stop(jobs);
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestEngineWorldAndCleanup()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 128 * 1024, 128 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 512, 128);

        var sceneA = SceneId.Create(1, 1);
        var sceneB = SceneId.Create(2, 1);

        // Build hierarchy in sceneA with components (including OwnerScene=default components and tweens!)
        var root = EntitySystem.Spawn(ref world.Entities, sceneA);
        var child = EntitySystem.Spawn(ref world.Entities, sceneA);
        var grand = EntitySystem.Spawn(ref world.Entities, sceneA);
        TransformSystem.Add(ref world.Transforms, root, Vec3.Zero);
        TransformSystem.Add(ref world.Transforms, child, Vec3.One, Quat.Identity, Vec3.One, root);
        TransformSystem.Add(ref world.Transforms, grand, Vec3.UnitY, Quat.Identity, Vec3.One, child);

        var skel = AnimationSystem.RegisterSkeleton(ref world.Animations, new SkeletonData { JointCount = 0 });
        var animA = AnimationSystem.AddPlayer(ref world.Animations, grand, skel, AnimClipHandle.Invalid, default);
        RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, child, MeshHandle.Create(1, 1), MaterialHandle.Create(1, 1), default, animPlayer: animA);
        RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = grand, OwnerScene = default, Visible = 1 });
        float* ptrVal = alloc.Persistent.Alloc<float>(1);
        var twChild = TweenSystem.TweenFloatPtr(ref world.Tweens, ptrVal, 0f, 10f, 1f, ownerEntity: child);
        var twGrand = TweenSystem.TweenTransformPos(ref world.Tweens, grand, Vec3.Zero, Vec3.One, 1f);

        // Surviving entity in sceneB
        var entB = EntitySystem.Spawn(ref world.Entities, sceneB);
        TransformSystem.Add(ref world.Transforms, entB, new Vec3(5, 0, 0));
        var animB = AnimationSystem.AddPlayer(ref world.Animations, entB, skel, AnimClipHandle.Invalid, sceneB);

        // DestroyEntity on root must recursively destroy root, child, grand + all components & tweens, leaving entB intact
        EngineWorldSystem.DestroyEntity(ref world, root);
        Assert.True(!EntitySystem.IsAlive(in world.Entities, root), "root destroyed");
        Assert.True(!EntitySystem.IsAlive(in world.Entities, child), "child destroyed recursively");
        Assert.True(!EntitySystem.IsAlive(in world.Entities, grand), "grandchild destroyed recursively");
        Assert.Equal(1, world.Entities.AliveCount, "Only entB alive");
        Assert.Equal(1, world.Transforms.Count, "Only entB transform alive");
        Assert.Equal(0, world.RenderComponents.MeshRendererCount, "child mesh renderer removed");
        Assert.Equal(0, world.RenderComponents.SpriteCount, "grand sprite removed");
        Assert.True(!TweenSystem.IsActive(in world.Tweens, twChild), "child owner tween killed");
        Assert.True(!TweenSystem.IsActive(in world.Tweens, twGrand), "grand transform tween killed");
        Assert.True(AnimationSystem.TryGetPlayer(in world.Animations, animA) == null, "grand AnimPlayer removed");
        Assert.True(AnimationSystem.TryGetPlayer(in world.Animations, animB) != null, "sceneB AnimPlayer survived");

        // DestroySceneObjects removes components attached to scene entities even when component OwnerScene == default
        var orphanCompEnt = EntitySystem.Spawn(ref world.Entities, sceneA);
        TransformSystem.Add(ref world.Transforms, orphanCompEnt, Vec3.Zero);
        RenderComponentSystem.AddMeshRenderer(ref world.RenderComponents, orphanCompEnt, MeshHandle.Create(2, 1), MaterialHandle.Create(2, 1), default);
        RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = orphanCompEnt, OwnerScene = default, Visible = 1 });
        RenderComponentSystem.AddText(ref world.RenderComponents, new TextData { Entity = orphanCompEnt, OwnerScene = default, Visible = 1 });
        AnimationSystem.AddPlayer(ref world.Animations, orphanCompEnt, skel, AnimClipHandle.Invalid, default);
        TweenSystem.TweenTransformScale(ref world.Tweens, orphanCompEnt, Vec3.One, Vec3.One * 2f, 1f);

        EngineWorldSystem.DestroySceneObjects(ref world, sceneA, ref alloc.Scratch);
        Assert.Equal(0, world.RenderComponents.MeshRendererCount, "DestroySceneObjects cleaned default-scene MeshRenderer by entity");
        Assert.Equal(0, world.RenderComponents.SpriteCount, "DestroySceneObjects cleaned default-scene Sprite by entity");
        Assert.Equal(0, world.RenderComponents.TextCount, "DestroySceneObjects cleaned default-scene Text by entity");
        Assert.Equal(1, world.Animations.PlayerCount, "Only sceneB AnimPlayer remains");
        Assert.Equal(0, world.Tweens.ActiveCount, "SceneA entity tween killed");

        // StepCoreSystems zero-GC verification
        alloc.BeginFrame();
        EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f);
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++)
        {
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "EngineWorldSystem.StepCoreSystems allocates 0 GC bytes");

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

    private static void TestCameraEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 64);
        var transforms = TransformTable.Create(ref alloc.Persistent, 64, 64);
        var cameras = CameraTable.Create(ref alloc.Persistent, 16);

        var target = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, target, new Vec3(10, 2, 5));
        TransformSystem.PropagateHierarchy(ref transforms);

        var orbitCam = CameraSystem.CreatePerspective(ref cameras, Vec3.Zero, Vec3.Zero, 60f, ownerScene: SceneId.Create(1, 1));
        var ptr = CameraSystem.TryGetPtr(in cameras, orbitCam);
        ptr->Mode = CameraMode.OrbitTarget;
        ptr->TargetEntity = target;
        ptr->OrbitDistance = 8f;
        ptr->OrbitYaw = 0f;
        ptr->OrbitPitch = 0f;
        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);
        Assert.Near(13f, ptr->Position.Z, 1e-3f, "OrbitTarget Z = target.Z + distance");

        // Remove target entity from TransformTable -> camera holds last valid position instead of snapping to Vec3.Zero
        Vec3 lastPos = ptr->Position;
        TransformSystem.Remove(ref transforms, target);
        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);
        Assert.Near(lastPos.X, ptr->Position.X, 1e-4f, "Camera holds X when target removed");
        Assert.Near(lastPos.Z, ptr->Position.Z, 1e-4f, "Camera holds Z when target removed");

        // Zero viewport guard in ScreenToWorldRay
        Ray zeroRay = CameraSystem.ScreenToWorldRay(in *ptr, 0f, 0f, 0f, 0f);
        Assert.True(float.IsFinite(zeroRay.Origin.X) && float.IsFinite(zeroRay.Direction.X), "ScreenToWorldRay safe on 0 viewport");

        // WorldToScreen behind camera returns false
        Assert.True(!CameraSystem.WorldToScreen(in *ptr, ptr->Position + new Vec3(0, 0, 50f), 1280f, 720f, out _), "Behind camera returns false");

        // Orthographic trauma shake translates without tilting forward axis
        var orthoCam = CameraSystem.CreateOrthographicUI(ref cameras, 1280f, 720f);
        var orthoPtr = CameraSystem.TryGetPtr(in cameras, orthoCam);
        orthoPtr->MaxShakePos = 10f;
        orthoPtr->MaxShakeAngle = 0f;
        CameraSystem.AddTrauma(ref cameras, orthoCam, 1f);
        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);
        Assert.Near(-1f, orthoPtr->Forward.Z, 1e-4f, "Ortho shake preserves -Z forward without tilt");

        // Frustum.IntersectsBounds & Destroy / DestroyByScene
        Assert.True(ptr->Frustum.IntersectsBounds(new Bounds3 { Min = new Vec3(9, 1, 4), Max = new Vec3(11, 3, 6) }), "Frustum intersects target bounds");
        Assert.True(CameraSystem.Destroy(ref cameras, orthoCam), "Destroy orthoCam");
        Assert.True(CameraSystem.TryGetPtr(in cameras, orthoCam) == null, "Stale orthoCam handle rejected");
        CameraSystem.DestroyByScene(ref cameras, SceneId.Create(1, 1));
        Assert.Equal(0, cameras.ActiveCount, "DestroyByScene cleared remaining cameras");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestInputEdgeCases()
    {
        InputState input = default;
        input.BeginFrame();

        // 1. Undersized CopyTextInputChars does not throw ArgumentException
        input.AppendTextInput("HelloWorld1234567890");
        Span<char> small = stackalloc char[4];
        int written = input.CopyTextInputChars(small);
        Assert.Equal(4, written, "CopyTextInputChars truncated to 4 without throwing");
        Assert.True(small.SequenceEqual("Hell"), "Truncated chars match prefix");

        // 2. AppendTextInputUtf8 trims at UTF-8 rune boundaries (22 x 3-byte '€' = 66 bytes -> 63 bytes = 21 runes)
        input.BeginFrame();
        Span<byte> euros66 = stackalloc byte[66];
        for (int i = 0; i < 22; i++)
        {
            euros66[i * 3 + 0] = 0xE2;
            euros66[i * 3 + 1] = 0x82;
            euros66[i * 3 + 2] = 0xAC;
        }
        input.AppendTextInputUtf8(euros66);
        Assert.Equal((byte)63, input.TextInputLength, "UTF-8 buffer trimmed to complete 3-byte rune boundary (63)");
        Span<char> decoded = stackalloc char[32];
        int euroChars = input.CopyTextInputChars(decoded);
        Assert.Equal(21, euroChars, "Decoded 21 complete Euro runes");
        for (int i = 0; i < euroChars; i++)
            Assert.Equal('\u20AC', decoded[i], "No replacement corruption in UTF-8 rune");

        // 3. Mouse button out-of-range bounds check (-1 and >= 32 must not alias button 0)
        input.BeginFrame();
        input.MouseButtonsDown = 1u; // button 0 pressed
        Assert.True(input.IsMouseDown(0) && input.IsMousePressed(0), "Button 0 down & pressed");
        Assert.True(!input.IsMouseDown(-1) && !input.IsMouseDown(32), "Out-of-range IsMouseDown returns false");
        Assert.True(!input.IsMousePressed(-1) && !input.IsMousePressed(32), "Out-of-range IsMousePressed returns false");
        input.BeginFrame();
        input.MouseButtonsDown = 0u;
        Assert.True(input.IsMouseReleased(0), "Button 0 released");
        Assert.True(!input.IsMouseReleased(-1) && !input.IsMouseReleased(32), "Out-of-range IsMouseReleased returns false");

        // 4. Gamepad NaN sanitization & CopyGamepadsFrom edge preservation
        input.SetGamepadConnected(0, true, 77u);
        input.SetGamepadAxis(0, GamepadAxis.LeftX, float.NaN);
        input.SetGamepadAxis(0, GamepadAxis.LeftY, float.PositiveInfinity);
        input.SetGamepadAxis(0, GamepadAxis.LeftTrigger, float.NaN);
        var pad = input.GetGamepad(0);
        Assert.Equal(0f, pad.LeftX, "NaN LeftX sanitized to 0");
        Assert.Equal(0f, pad.LeftY, "Infinity LeftY sanitized to 0");
        Assert.Equal(0f, pad.LeftTrigger, "NaN trigger sanitized to 0");
        Vec2 nanStick = GamepadState.ApplyRadialDeadzone(float.NaN, 0.5f, 0.15f);
        Assert.True(float.IsFinite(nanStick.X) && float.IsFinite(nanStick.Y), "ApplyRadialDeadzone sanitizes NaN");

        InputState frameSrc = default;
        frameSrc.SetGamepadConnected(0, true, 77u);
        frameSrc.SetGamepadButton(0, GamepadButton.South, true);
        input.BeginFrame();
        input.CopyGamepadsFrom(in frameSrc);
        Assert.True(input.IsGamepadButtonPressed(GamepadButton.South, 0), "CopyGamepadsFrom preserves pressed edge");
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

    private static void TestInputPlatformEssentials()
    {
        InputState input = default;
        // Default state must be headless-friendly: focused, not minimized, mouse unlocked
        Assert.True(input.IsWindowFocused, "Default InputState is window-focused");
        Assert.True(!input.IsWindowMinimized, "Default InputState is not minimized");
        Assert.True(!input.IsMouseLocked, "Default InputState mouse is unlocked");

        // Extended KeyCode scancodes
        input.BeginFrame();
        input.SetKey(KeyCode.Num0, true);
        input.SetKey(KeyCode.Num9, true);
        input.SetKey(KeyCode.LeftAlt, true);
        input.SetKey(KeyCode.RightShift, true);
        input.SetKey(KeyCode.Delete, true);
        input.SetKey(KeyCode.Home, true);
        input.SetKey(KeyCode.PageDown, true);
        input.SetKey(KeyCode.Grave, true);
        input.SetKey(KeyCode.KpEnter, true);
        Assert.True(input.IsKeyPressed(KeyCode.Num0) && input.IsKeyPressed(KeyCode.Num9), "Num0/Num9 pressed");
        Assert.True(input.IsKeyPressed(KeyCode.LeftAlt) && input.IsKeyPressed(KeyCode.RightShift), "Modifier keys pressed");
        Assert.True(input.IsKeyPressed(KeyCode.Delete) && input.IsKeyPressed(KeyCode.Home) && input.IsKeyPressed(KeyCode.PageDown), "Nav keys pressed");
        Assert.True(input.IsKeyPressed(KeyCode.Grave) && input.IsKeyPressed(KeyCode.KpEnter), "Punctuation & keypad keys pressed");

        // Mouse button pressed / down / released edge transitions
        input.MouseButtonsDown |= 1u << 0; // Left button down
        Assert.True(input.IsMousePressed(0) && input.IsMouseDown(0) && !input.IsMouseReleased(0), "LMB pressed edge");
        input.BeginFrame();
        Assert.True(!input.IsMousePressed(0) && input.IsMouseDown(0) && !input.IsMouseReleased(0), "LMB held");
        input.MouseButtonsDown &= ~(1u << 0);
        Assert.True(!input.IsMouseDown(0) && input.IsMouseReleased(0), "LMB released edge");

        // Window focus & minimize edges
        input.BeginFrame();
        input.SetWindowFocused(false);
        input.SetWindowMinimized(true);
        input.SetMouseLocked(true);
        Assert.True(!input.IsWindowFocused && input.WasWindowFocusLost, "Focus lost edge detected");
        Assert.True(input.IsWindowMinimized && input.IsMouseLocked, "Minimized & mouse lock flags tracked");
        input.BeginFrame();
        Assert.True(!input.WasWindowFocusLost && !input.IsWindowFocused, "Focus lost edge clears next frame");
        input.SetWindowFocused(true);
        Assert.True(input.IsWindowFocused && input.WasWindowFocusGained, "Focus gained edge detected");

        // UTF-8 text input buffer (ASCII + multi-byte UTF-8 + frame clear + capacity clamp)
        input.BeginFrame();
        Assert.True(!input.HasTextInput, "Text input cleared on BeginFrame");
        input.AppendTextInput("Bring-");
        input.AppendTextInputUtf8("123"u8);
        Assert.True(input.HasTextInput && input.TextInputLength == 9, "Appended 9 UTF-8 text bytes");

        Span<char> chars = stackalloc char[32];
        int charCount = input.CopyTextInputChars(chars);
        Assert.True(chars.Slice(0, charCount).SequenceEqual("Bring-123"), "Decoded text input matches");

        // Overflow clamp safety (> MaxTextInputBytes must not throw or corrupt)
        for (int i = 0; i < 20; i++) input.AppendTextInput("ABCDEFGH");
        Assert.Equal((byte)InputState.MaxTextInputBytes, input.TextInputLength, "Text input clamped cleanly to MaxTextInputBytes");

        // Zero-GC hot loop across 500 frames
        Span<byte> utf8Out = stackalloc byte[InputState.MaxTextInputBytes];
        Span<char> charOut = stackalloc char[InputState.MaxTextInputBytes];
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        int totalChars = 0;
        for (int i = 0; i < 500; i++)
        {
            input.BeginFrame();
            input.AppendTextInput("ab");
            int b = input.CopyTextInputUtf8(utf8Out);
            int c = input.CopyTextInputChars(charOut);
            bool rel = input.IsMouseReleased(0);
            totalChars += b + c + (rel ? 1 : 0);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "Text input + mouse release + focus hot loop allocates 0 GC bytes");
        Assert.True(totalChars > 0, "Text input loop read characters");
    }

    private static void TestGamepadInput()
    {
        InputState input = default;
        Assert.Equal((byte)0, input.ConnectedGamepadCount, "Initially 0 gamepads");
        Assert.True(!input.IsGamepadConnected(0), "Slot 0 disconnected");

        // Connect two gamepads (slot 0 & slot 2)
        input.SetGamepadConnected(0, true, instanceId: 101u);
        input.SetGamepadConnected(2, true, instanceId: 202u);
        Assert.Equal((byte)2, input.ConnectedGamepadCount, "2 connected gamepads");
        Assert.True(input.IsGamepadConnected(0) && input.IsGamepadConnected(2), "Slots 0 and 2 connected");

        // Frame 1: press South (A) + Steam Deck LeftPaddle1 + trigger + sticks
        input.BeginFrame();
        input.SetGamepadButton(0, GamepadButton.South, true);
        input.SetGamepadButton(0, GamepadButton.LeftPaddle1, true);
        input.SetGamepadButton(2, GamepadButton.Start, true);
        input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.08f);  // inside 0.15 radial deadzone
        input.SetGamepadAxis(0, GamepadAxis.LeftY, 0.06f);  // magnitude 0.10 <= 0.15 -> Zero!
        input.SetGamepadAxis(0, GamepadAxis.RightX, 1.0f);
        input.SetGamepadAxis(0, GamepadAxis.RightY, 1.0f);  // diagonal clamped to length 1.0
        input.SetGamepadAxis(0, GamepadAxis.LeftTrigger, 0.75f);
        input.SetGamepadAxis(0, GamepadAxis.RightTrigger, 1.5f); // clamped to 1.0

        Assert.True(input.IsGamepadButtonDown(GamepadButton.South, 0), "South down");
        Assert.True(input.IsGamepadButtonPressed(GamepadButton.South, 0), "South newly pressed");
        Assert.True(input.IsGamepadButtonPressed(GamepadButton.LeftPaddle1, 0), "Steam Deck LeftPaddle1 pressed");
        Assert.True(input.IsAnyGamepadButtonPressed(GamepadButton.Start), "Any-controller Start pressed on slot 2");

        Vec2 deadStick = input.GetGamepadLeftStick(0, deadzone: 0.15f);
        Assert.Near(0f, deadStick.X, 1e-5f, "Inside radial deadzone X == 0");
        Assert.Near(0f, deadStick.Y, 1e-5f, "Inside radial deadzone Y == 0");

        Vec2 clampedRight = input.GetGamepadRightStick(0, deadzone: 0.15f);
        float rightLen = DetMath.Sqrt(clampedRight.X * clampedRight.X + clampedRight.Y * clampedRight.Y);
        Assert.Near(1f, rightLen, 1e-4f, "Diagonal stick clamped to unit length");
        Assert.Near(0.75f, input.GetGamepadLeftTrigger(0), 1e-4f, "Left trigger 0.75");
        Assert.Near(1.0f, input.GetGamepadRightTrigger(0), 1e-4f, "Right trigger clamped to 1.0");

        // Halfway past deadzone (0.15 + 0.5 * 0.85 = 0.575) -> exact 0.50 scaled output
        input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.575f);
        input.SetGamepadAxis(0, GamepadAxis.LeftY, 0f);
        Vec2 halfStick = input.GetGamepadLeftStick(0, deadzone: 0.15f);
        Assert.Near(0.5f, halfStick.X, 1e-4f, "Scaled radial deadzone midpoint == 0.5");

        // Unified GetMoveAxis combines keyboard + left stick + D-pad, clamped <= 1
        input.SetKey(KeyCode.W, true);
        Vec2 combined = input.GetMoveAxis(gamepadSlot: 0, deadzone: 0.15f);
        float combLen = DetMath.Sqrt(combined.X * combined.X + combined.Y * combined.Y);
        Assert.True(combined.X > 0.3f && combined.Y > 0.7f, "Combined keyboard W + stick right produces diagonal");
        Assert.True(combLen <= 1.0001f, $"Combined move vector clamped to unit circle (got {combLen:F4})");

        // Frame 2: hold South, release LeftPaddle1
        input.BeginFrame();
        input.SetGamepadButton(0, GamepadButton.LeftPaddle1, false);
        Assert.True(input.IsGamepadButtonDown(GamepadButton.South, 0), "South still down");
        Assert.True(!input.IsGamepadButtonPressed(GamepadButton.South, 0), "South not newly pressed");
        Assert.True(input.IsGamepadButtonReleased(GamepadButton.LeftPaddle1, 0), "LeftPaddle1 released");

        // Disconnect clears slot cleanly
        input.SetGamepadConnected(0, false);
        Assert.Equal((byte)1, input.ConnectedGamepadCount, "1 connected gamepad after disconnect");
        Assert.True(!input.IsGamepadButtonDown(GamepadButton.South, 0), "Disconnected pad reports false");

        // Zero-GC hot loop verification across 1000 frames
        input.SetGamepadConnected(0, true, 101u);
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        float acc = 0f;
        for (int i = 0; i < 1000; i++)
        {
            input.BeginFrame();
            input.SetGamepadAxis(0, GamepadAxis.LeftX, (i & 1) == 0 ? 0.6f : -0.6f);
            input.SetGamepadButton(0, GamepadButton.South, (i & 3) == 0);
            Vec2 m = input.GetMoveAxis(0);
            acc += m.X + (input.IsGamepadButtonPressed(GamepadButton.South, 0) ? 1f : 0f);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "Gamepad input hot loop allocates 0 GC bytes");
        Assert.True(acc > 0f, "Gamepad loop produced non-zero inputs");
    }

    private static void TestRenderComponentTables()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var rc = RenderComponentTables.Create(ref alloc.Persistent, maxMeshRenderers: 4, maxSprites: 2, maxTexts: 2);
        var e1 = EntityId.Create(1, 1);
        var e2 = EntityId.Create(2, 1);
        var scA = SceneId.Create(1, 1);

        // Capacity check before NextSlot++: exhausting AddSprite/AddText then freeing must allow reuse!
        var s1 = RenderComponentSystem.AddSprite(ref rc, new SpriteData { Entity = e1, OwnerScene = scA, Visible = 1 });
        var s2 = RenderComponentSystem.AddSprite(ref rc, new SpriteData { Entity = e2, OwnerScene = scA, Visible = 1 });
        bool spThrew = false;
        try { RenderComponentSystem.AddSprite(ref rc, new SpriteData { Entity = e1 }); }
        catch (OutOfMemoryException) { spThrew = true; }
        Assert.True(spThrew, "AddSprite throws on full capacity");
        Assert.True(RenderComponentSystem.RemoveSprite(ref rc, s1), "Remove s1");
        Assert.True(RenderComponentSystem.TryGetSprite(in rc, s1) == null, "Stale SpriteHandle rejected");
        var s3 = RenderComponentSystem.AddSprite(ref rc, new SpriteData { Entity = e1, OwnerScene = scA, Visible = 1 });
        Assert.True(s3.IsValid && s3.Generation != s1.Generation, "Freed sprite slot reused cleanly after OOM");

        var t1 = RenderComponentSystem.AddText(ref rc, RenderComponentSystem.DefaultHudText(e1, FontHandle.Create(1, 1), "A", scA));
        var t2 = RenderComponentSystem.AddText(ref rc, RenderComponentSystem.DefaultWorldText(e2, FontHandle.Create(1, 1), "B", scA));
        bool txtThrew = false;
        try { RenderComponentSystem.AddText(ref rc, new TextData { Entity = e1 }); }
        catch (OutOfMemoryException) { txtThrew = true; }
        Assert.True(txtThrew, "AddText throws on full capacity");
        Assert.True(RenderComponentSystem.RemoveText(ref rc, t1), "Remove t1");
        Assert.True(RenderComponentSystem.TryGetText(in rc, t1) == null, "Stale TextHandle rejected");
        var t3 = RenderComponentSystem.AddText(ref rc, RenderComponentSystem.DefaultHudText(e1, FontHandle.Create(1, 1), "C", scA));
        Assert.True(t3.IsValid && t3.Generation != t1.Generation, "Freed text slot reused cleanly after OOM");

        // SpriteFromRegion & DefaultUiSlicedPanel & StepTextAnimClocks
        var region = new SpriteRegionData
        {
            AtlasTexture = TextureHandle.Create(1, 1),
            UvRect = new Vec4(0f, 0f, 0.5f, 0.5f),
            Pivot = new Vec2(0.5f, 0.5f),
            PixelSize = new Vec2(64f, 64f),
            SliceBorders = new Vec4(8f, 8f, 8f, 8f),
        };
        var sliced = RenderComponentSystem.DefaultUiSlicedPanel(e1, in region, new Vec2(200, 80), scA);
        Assert.Equal((byte)SpriteDrawMode.Sliced, (byte)sliced.DrawMode, "DefaultUiSlicedPanel sets Sliced");
        Assert.Equal(Vec2.Zero, sliced.Pivot, "DefaultUiSlicedPanel sets Pivot=(0,0)");
        RenderComponentSystem.StepTextAnimClocks(ref rc, 0.25f);
        Assert.Near(0.25f, RenderComponentSystem.TryGetText(in rc, t3)->AnimTime, 1e-5f, "StepTextAnimClocks advances AnimTime");

        RenderComponentSystem.RemoveByEntity(ref rc, e1);
        Assert.Equal(1, rc.SpriteCount, "RemoveByEntity removed e1 sprites");
        Assert.Equal(1, rc.TextCount, "RemoveByEntity removed e1 texts");
        RenderComponentSystem.RemoveByScene(ref rc, scA);
        Assert.Equal(0, rc.SpriteCount, "RemoveByScene removed remaining sprites");
        Assert.Equal(0, rc.TextCount, "RemoveByScene removed remaining texts");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static int s_sceneFixedCount;
    private static int s_sceneViewCount;

    private static void LoadDummyBundle(ref BundleLoadContext ctx)
    {
        _ = ctx.Bundle;
    }

    private static void OnLoadOk(ref SceneContext ctx)
    {
        EntitySystem.Spawn(ref ctx.World->Entities, ctx.Scene);
    }

    private static void OnFixedOk(ref SceneContext ctx) => s_sceneFixedCount++;
    private static void OnViewOk(ref SceneContext ctx) => s_sceneViewCount++;

    private static void OnLoadThrows(ref SceneContext ctx)
    {
        EntitySystem.Spawn(ref ctx.World->Entities, ctx.Scene);
        throw new InvalidOperationException("Simulated OnLoad failure");
    }

    private static void TestSceneManagerEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        s_sceneFixedCount = 0;
        s_sceneViewCount = 0;
        string tempLogDir = Path.Combine(Path.GetTempPath(), "bring_scene_log_" + Guid.NewGuid().ToString("N"));

        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        LogState* log = alloc.Persistent.Alloc<LogState>(1, 64);
        LogSystem.Start(log, ref alloc.Persistent, appName: "SceneTest", directoryOverride: tempLogDir, minLevel: LogLevel.Info, mirrorToConsole: false, hookExceptions: false);

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, 128, 32);
            var bundles = BundleManager.Create(ref alloc.Persistent, 8);
            var scenes = SceneManager.Create(ref alloc.Persistent, maxDefinitions: 8, maxActiveScenes: 1, log: log);

            var b1 = BundleSystem.Register(ref bundles, "b1", 64 * 1024, &LoadDummyBundle);
            var b2 = BundleSystem.Register(ref bundles, "b2", 64 * 1024, &LoadDummyBundle);
            Span<BundleId> deps1 = stackalloc BundleId[] { b1 };
            Span<BundleId> deps2 = stackalloc BundleId[] { b2 };

            SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S1", deps1, 32 * 1024, &OnLoadOk, onFixedUpdate: &OnFixedOk, onViewSync: &OnViewOk);
            SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S2", deps2, 32 * 1024, &OnLoadOk);
            SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "SFail", deps2, 32 * 1024, &OnLoadThrows);

            // 1. Duplicate scene name rejected
            bool dupThrew = false;
            try { SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S1", deps1, 32 * 1024, &OnLoadOk); }
            catch (InvalidOperationException) { dupThrew = true; }
            Assert.True(dupThrew, "Duplicate RegisterScene name rejected");

            // 2. Load S1 (fills MaxActiveScenes=1)
            SceneId s1 = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S1"), SceneLoadMode.Single);
            Assert.Equal(1, scenes.ActiveCount, "S1 active");

            // 3. Additive LoadScene when MaxActiveScenes full must throw BEFORE acquiring b2!
            bool maxThrew = false;
            try { SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S2"), SceneLoadMode.Additive); }
            catch (OutOfMemoryException) { maxThrew = true; }
            Assert.True(maxThrew, "Additive LoadScene throws when MaxActiveScenes exceeded");
            Assert.True(!BundleSystem.IsLoaded(in bundles, b2), "b2 was NOT loaded when MaxActiveScenes full");
            Assert.Equal(0, BundleSystem.GetRefCount(in bundles, b2), "b2 RefCount == 0 (no leak)");

            // 4. FixedUpdateActiveScenes & ViewSyncActiveScenes
            SceneSystem.FixedUpdateActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
            SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
            Assert.Equal(1, s_sceneFixedCount, "FixedUpdateActiveScenes invoked");
            Assert.Equal(1, s_sceneViewCount, "ViewSyncActiveScenes invoked");

            // 5. OnLoad exception rollback cleans up scene slot, arena, entities, and bundle RefCount
            bool loadFailed = false;
            try { SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("SFail"), SceneLoadMode.Single); }
            catch (InvalidOperationException) { loadFailed = true; }
            Assert.True(loadFailed, "SFail rethrew OnLoad exception");
            Assert.Equal(0, scenes.ActiveCount, "SFail rolled back ActiveCount");
            Assert.Equal(0, world.Entities.AliveCount, "SFail rolled back spawned entities");
            Assert.Equal(0, BundleSystem.GetRefCount(in bundles, b2), "SFail rolled back bundle RefCount");
            Assert.True(!BundleSystem.IsLoaded(in bundles, b2), "SFail unloaded rolled-back bundle");

            // 6. Stale UnloadScene returns false
            Assert.True(!SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, s1), "Stale UnloadScene returns false");

            LogSystem.Flush(log);
            Assert.True(log->TotalWritten.Value >= 2, "SceneManager emitted lifecycle logs");
        }
        finally
        {
            LogSystem.Stop(log);
            alloc.Dispose();
            if (Directory.Exists(tempLogDir)) Directory.Delete(tempLogDir, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestTransformAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 512);
        var transforms = TransformTable.Create(ref alloc.Persistent, 512, 512);

        // SIM-04: 2-node forward-ref cycle A->B then B->A must be rejected
        var a = EntitySystem.Spawn(ref entities);
        var b = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, a, new Vec3(1f, 0f, 0f), Quat.Identity, Vec3.One, parent: b);
        TransformSystem.Add(ref transforms, b, new Vec3(2f, 0f, 0f), Quat.Identity, Vec3.One, parent: a);
        Assert.True(!transforms.Parent[TransformSystem.GetDenseIndex(in transforms, b)].IsValid, "SIM-04: 2-node forward-ref cycle rejected");
        TransformSystem.PropagateHierarchy(ref transforms);
        Assert.Equal((byte)1, transforms.MaxDepth, "SIM-04: 2-node MaxDepth == 1");
        Vec3 posA0 = TransformSystem.GetWorldPosition(in transforms, a);
        Vec3 posB0 = TransformSystem.GetWorldPosition(in transforms, b);
        for (int iter = 0; iter < 10; iter++)
            TransformSystem.PropagateHierarchy(ref transforms);
        Assert.Near(posA0.X, TransformSystem.GetWorldPosition(in transforms, a).X, 1e-5f, "SIM-04: 2-node A world X stable over 10 propagations");
        Assert.Near(posB0.X, TransformSystem.GetWorldPosition(in transforms, b).X, 1e-5f, "SIM-04: 2-node B world X stable over 10 propagations");
        Assert.Near(3f, posA0.X, 1e-4f, "SIM-04: A world X == 1 + 2");
        Assert.Near(2f, posB0.X, 1e-4f, "SIM-04: B world X == 2");

        // SIM-04: 3-node forward-ref cycle X->Y->Z->X must be rejected
        var x = EntitySystem.Spawn(ref entities);
        var y = EntitySystem.Spawn(ref entities);
        var z = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, x, new Vec3(1f, 0f, 0f), Quat.Identity, Vec3.One, parent: y);
        TransformSystem.Add(ref transforms, y, new Vec3(2f, 0f, 0f), Quat.Identity, Vec3.One, parent: z);
        TransformSystem.Add(ref transforms, z, new Vec3(4f, 0f, 0f), Quat.Identity, Vec3.One, parent: x);
        Assert.True(!transforms.Parent[TransformSystem.GetDenseIndex(in transforms, z)].IsValid, "SIM-04: 3-node forward-ref cycle rejected");
        TransformSystem.PropagateHierarchy(ref transforms);
        Assert.Equal((byte)2, transforms.MaxDepth, "SIM-04: 3-node MaxDepth == 2");
        for (int iter = 0; iter < 10; iter++)
            TransformSystem.PropagateHierarchy(ref transforms);
        Assert.Near(7f, TransformSystem.GetWorldPosition(in transforms, x).X, 1e-4f, "SIM-04: 3-node X world X == 1+2+4 stable");

        // SIM-14: Missing-parent entity clears NeedsDepthRecompute after first PropagateHierarchy (no permanent O(N^2))
        var t2 = TransformTable.Create(ref alloc.Persistent, 512, 512);
        var missingParent = EntitySystem.Spawn(ref entities);
        var orphan = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref t2, orphan, new Vec3(5f, 0f, 0f), Quat.Identity, Vec3.One, parent: missingParent);
        Assert.Equal((byte)1, t2.NeedsDepthRecompute, "SIM-14: NeedsDepthRecompute set on missing parent add");
        Assert.Equal((byte)1, t2.HasUnresolvedParents, "SIM-14: HasUnresolvedParents set on missing parent add");
        TransformSystem.PropagateHierarchy(ref t2);
        Assert.Equal((byte)0, t2.NeedsDepthRecompute, "SIM-14: NeedsDepthRecompute cleared after first PropagateHierarchy");
        Assert.Equal((byte)1, t2.HasUnresolvedParents, "SIM-14: HasUnresolvedParents remains tracked separately");
        TransformSystem.PropagateHierarchy(ref t2);
        Assert.Equal((byte)0, t2.NeedsDepthRecompute, "SIM-14: NeedsDepthRecompute stays 0 on subsequent frames");

        // Adding the missing parent resolves the hierarchy on next PropagateHierarchy
        TransformSystem.Add(ref t2, missingParent, new Vec3(10f, 0f, 0f));
        Assert.Equal((byte)1, t2.NeedsDepthRecompute, "SIM-14: Adding entity while HasUnresolvedParents=1 triggers recompute");
        TransformSystem.PropagateHierarchy(ref t2);
        Assert.Equal((byte)0, t2.HasUnresolvedParents, "SIM-14: HasUnresolvedParents cleared once parent exists");
        Assert.Near(15f, TransformSystem.GetWorldPosition(in t2, orphan).X, 1e-4f, "SIM-14: Late parent resolved");

        // SIM-14: Leaf Remove keeps depths & MaxDepth exact
        var root = EntitySystem.Spawn(ref entities);
        var mid = EntitySystem.Spawn(ref entities);
        var leaf = EntitySystem.Spawn(ref entities);
        var sibLeaf = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref t2, root, Vec3.Zero);
        TransformSystem.Add(ref t2, mid, Vec3.UnitX, Quat.Identity, Vec3.One, parent: root);
        TransformSystem.Add(ref t2, leaf, Vec3.UnitX, Quat.Identity, Vec3.One, parent: mid);
        TransformSystem.Add(ref t2, sibLeaf, Vec3.UnitY, Quat.Identity, Vec3.One, parent: root);
        TransformSystem.PropagateHierarchy(ref t2);
        Assert.Equal((byte)2, t2.MaxDepth, "SIM-14: MaxDepth == 2 before leaf removes");
        Assert.True(TransformSystem.Remove(ref t2, sibLeaf), "SIM-14: Remove non-deepest leaf");
        Assert.Equal((byte)2, t2.MaxDepth, "SIM-14: MaxDepth remains 2 after non-deepest leaf remove");
        Assert.Equal((byte)2, t2.Depth[TransformSystem.GetDenseIndex(in t2, leaf)], "SIM-14: Deepest leaf depth preserved");
        Assert.True(TransformSystem.Remove(ref t2, leaf), "SIM-14: Remove deepest leaf");
        Assert.Equal((byte)1, t2.MaxDepth, "SIM-14: MaxDepth drops to 1 when deepest leaf removed");

        // SIM-14: Depth >= 254 falls back to serial safely even when JobScheduler is provided
        var tDeep = TransformTable.Create(ref alloc.Persistent, 512, 512);
        EntityId prev = EntityId.Invalid;
        EntityId lastDeep = EntityId.Invalid;
        for (int i = 0; i < 260; i++)
        {
            EntityId e = EntitySystem.Spawn(ref entities);
            TransformSystem.Add(ref tDeep, e, new Vec3(1f, 0f, 0f), Quat.Identity, Vec3.One, parent: prev);
            prev = e;
            lastDeep = e;
        }
        Assert.Equal((byte)254, tDeep.MaxDepth, "SIM-14: Deep chain clamps byte MaxDepth at 254");
        JobScheduler* jobs = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(jobs, ref alloc.Persistent, workerCount: 2);
        TransformSystem.PropagateHierarchy(ref tDeep, jobs);
        Vec3 deepPos = TransformSystem.GetWorldPosition(in tDeep, lastDeep);
        Assert.True(float.IsFinite(deepPos.X) && deepPos.X > 0f, "SIM-14: Deep chain >= 254 propagates serially without race");
        JobSystem.Stop(jobs);

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestDestroyEntity300ChildrenOverflow()
    {
        // SIM-05: Root with 300 direct children (>256 stack cap) destroys every child & component with 0 GC
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 128 * 1024, 128 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 512, 64);

        var root = EntitySystem.Spawn(ref world.Entities);
        TransformSystem.Add(ref world.Transforms, root, Vec3.Zero);

        for (int i = 0; i < 300; i++)
        {
            var child = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, child, new Vec3(i, 0f, 0f), Quat.Identity, Vec3.One, parent: root);
            RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = child, Visible = 1 });
        }

        Assert.Equal(301, world.Entities.AliveCount, "SIM-05: 301 entities alive before destroy");
        Assert.Equal(301, world.Transforms.Count, "SIM-05: 301 transforms before destroy");
        Assert.Equal(300, world.RenderComponents.SpriteCount, "SIM-05: 300 sprites before destroy");

        // Warmup JIT on a small throwaway entity before measuring 0 GC
        var warmup = EntitySystem.Spawn(ref world.Entities);
        TransformSystem.Add(ref world.Transforms, warmup, Vec3.Zero);
        EngineWorldSystem.DestroyEntity(ref world, warmup);

        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        EngineWorldSystem.DestroyEntity(ref world, root);
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, gcAfter - gcBefore, "SIM-05: DestroyEntity(300 children) allocates 0 GC bytes");
        Assert.Equal(0, world.Entities.AliveCount, "SIM-05: All 301 entities destroyed");
        Assert.Equal(0, world.Transforms.Count, "SIM-05: All 301 transforms destroyed");
        Assert.Equal(0, world.RenderComponents.SpriteCount, "SIM-05: All 300 sprites destroyed");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static bool IsPlaneFinite(in Plane p)
        => float.IsFinite(p.Normal.X) && float.IsFinite(p.Normal.Y) && float.IsFinite(p.Normal.Z) && float.IsFinite(p.D);

    private static bool IsFrustumFinite(in Frustum f)
        => IsPlaneFinite(in f.Left) && IsPlaneFinite(in f.Right)
        && IsPlaneFinite(in f.Bottom) && IsPlaneFinite(in f.Top)
        && IsPlaneFinite(in f.Near) && IsPlaneFinite(in f.Far);

    private static bool IsMat4Finite(in Mat4 m)
        => float.IsFinite(m.C0.X) && float.IsFinite(m.C0.Y) && float.IsFinite(m.C0.Z) && float.IsFinite(m.C0.W)
        && float.IsFinite(m.C1.X) && float.IsFinite(m.C1.Y) && float.IsFinite(m.C1.Z) && float.IsFinite(m.C1.W)
        && float.IsFinite(m.C2.X) && float.IsFinite(m.C2.Y) && float.IsFinite(m.C2.Z) && float.IsFinite(m.C2.W)
        && float.IsFinite(m.C3.X) && float.IsFinite(m.C3.Y) && float.IsFinite(m.C3.Z) && float.IsFinite(m.C3.W);

    private static void TestCameraAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 64);
        var transforms = TransformTable.Create(ref alloc.Persistent, 64, 64);
        var cameras = CameraTable.Create(ref alloc.Persistent, 16);

        // SIM-10: Split-screen ViewportRect=(0.5, 0, 0.5, 1) at 1280x720
        var splitCam = CameraSystem.CreatePerspective(ref cameras, new Vec3(0f, 5f, 10f), Vec3.Zero, 60f, 0.1f, 200f);
        var splitPtr = CameraSystem.TryGetPtr(in cameras, splitCam);
        splitPtr->ViewportRect = new Vec4(0.5f, 0f, 0.5f, 1f);
        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);

        Assert.True(CameraSystem.WorldToScreen(in *splitPtr, splitPtr->LookTarget, 1280f, 720f, out Vec3 sp), "SIM-10: WorldToScreen succeeds in right split viewport");
        Assert.Near(960f, sp.X, 1f, "SIM-10: WorldToScreen X == 960 (center of right half)");
        Assert.Near(360f, sp.Y, 1f, "SIM-10: WorldToScreen Y == 360");

        Ray splitRay = CameraSystem.ScreenToWorldRay(in *splitPtr, 960f, 360f, 1280f, 720f);
        Assert.True(Vec3.Dot(splitRay.Direction, splitPtr->Forward) > 0.999f, "SIM-10: ScreenToWorldRay(960, 360) aligns with cam.Forward");

        // SIM-11: Degenerate ViewportRect.Z=0, FovYDeg=0, Near==Far produce all finite matrices & frustum
        var badPersp = CameraSystem.CreatePerspective(ref cameras, new Vec3(0f, 2f, 5f), Vec3.Zero, fovYDeg: 0f, nearPlane: 10f, farPlane: 10f);
        var badPerspPtr = CameraSystem.TryGetPtr(in cameras, badPersp);
        badPerspPtr->ViewportRect = new Vec4(0f, 0f, 0f, 1f);

        var badOrtho = CameraSystem.CreateOrthographicUI(ref cameras, 1280f, 720f);
        var badOrthoPtr = CameraSystem.TryGetPtr(in cameras, badOrtho);
        badOrthoPtr->OrthoHeight = 0f;
        badOrthoPtr->NearPlane = 5f;
        badOrthoPtr->FarPlane = 5f;
        badOrthoPtr->ViewportRect = new Vec4(0f, 0f, 0f, 0f);

        // SIM-11: OrbitTarget pole pitch == HalfPi with distinct OrbitYaw values stays finite and preserves distinct yaw
        var pivotEnt = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, pivotEnt, new Vec3(0f, 0f, 0f));
        TransformSystem.PropagateHierarchy(ref transforms);

        var poleCamA = CameraSystem.CreatePerspective(ref cameras, Vec3.Zero, Vec3.Zero);
        var polePtrA = CameraSystem.TryGetPtr(in cameras, poleCamA);
        polePtrA->Mode = CameraMode.OrbitTarget;
        polePtrA->TargetEntity = pivotEnt;
        polePtrA->OrbitDistance = 10f;
        polePtrA->OrbitPitch = DetMath.HalfPi;
        polePtrA->OrbitYaw = 0f;

        var poleCamB = CameraSystem.CreatePerspective(ref cameras, Vec3.Zero, Vec3.Zero);
        var polePtrB = CameraSystem.TryGetPtr(in cameras, poleCamB);
        polePtrB->Mode = CameraMode.OrbitTarget;
        polePtrB->TargetEntity = pivotEnt;
        polePtrB->OrbitDistance = 10f;
        polePtrB->OrbitPitch = DetMath.HalfPi;
        polePtrB->OrbitYaw = DetMath.HalfPi;

        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);

        Assert.True(IsMat4Finite(in badPerspPtr->ViewProjMatrix) && IsMat4Finite(in badPerspPtr->ProjMatrix), "SIM-11: Degenerate perspective matrices finite");
        Assert.True(IsFrustumFinite(in badPerspPtr->Frustum), "SIM-11: Degenerate perspective frustum finite");
        Assert.True(IsMat4Finite(in badOrthoPtr->ViewProjMatrix) && IsMat4Finite(in badOrthoPtr->ProjMatrix) && IsFrustumFinite(in badOrthoPtr->Frustum), "SIM-11: Degenerate ortho matrices & frustum finite");
        Assert.True(IsMat4Finite(in polePtrA->ViewProjMatrix) && IsMat4Finite(in polePtrB->ViewProjMatrix), "SIM-11: OrbitTarget pole matrices finite");
        Assert.True(float.IsFinite(polePtrA->Right.X) && float.IsFinite(polePtrB->Right.X), "SIM-11: OrbitTarget pole basis finite");
        Assert.True(DetMath.Abs(Vec3.Dot(polePtrA->Right, polePtrB->Right)) < 0.1f, "SIM-11: Distinct OrbitYaw preserved at HalfPi pitch");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestInputAuditRegressions()
    {
        // SIM-19: TrimToUtf8RuneBoundary with 4+ trailing orphan continuation bytes & excess continuation bytes after valid lead
        InputState input = default;
        input.BeginFrame();

        // 66 orphan continuation bytes (0x80) crossing MaxTextInputBytes (64)
        Span<byte> orphans = stackalloc byte[66];
        orphans.Fill(0x80);
        input.AppendTextInputUtf8(orphans);
        Assert.True(input.TextInputLength <= InputState.MaxTextInputBytes - 4, "SIM-19: 4+ trailing orphan continuation bytes trimmed cleanly");

        // 60 ASCII 'A' + valid 2-byte 'é' (0xC3 0xA9) + 4 orphan continuation bytes (0x80) = 66 bytes -> truncated at 64 -> trims to 62 bytes!
        input.BeginFrame();
        Span<byte> mixed = stackalloc byte[66];
        for (int i = 0; i < 60; i++) mixed[i] = (byte)'A';
        mixed[60] = 0xC3;
        mixed[61] = 0xA9;
        mixed[62] = 0x80;
        mixed[63] = 0x80;
        mixed[64] = 0x80;
        mixed[65] = 0x80;
        input.AppendTextInputUtf8(mixed);
        Assert.Equal((byte)62, input.TextInputLength, "SIM-19: Excess trailing continuation bytes after 2-byte rune trimmed to idx + expected (62)");
        Span<char> decoded = stackalloc char[64];
        int chars = input.CopyTextInputChars(decoded);
        Assert.Equal(61, chars, "SIM-19: Decoded 60 'A' + 1 'é' without replacement corruption");
        Assert.Equal('\u00E9', decoded[60], "SIM-19: Final rune is intact 'é'");

        // Wave-A InputSystem.ApplyFocusLost clears keys, mouse, and gamepads while preserving release edges
        input.BeginFrame();
        input.SetKey(KeyCode.Space, true);
        input.MouseButtonsDown |= 1u;
        input.SetGamepadConnected(0, true, 123u);
        input.SetGamepadButton(0, GamepadButton.South, true);
        input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.8f);
        input.BeginFrame();

        InputSystem.ApplyFocusLost(ref input);
        Assert.True(!input.IsWindowFocused && input.WasWindowFocusLost, "ApplyFocusLost marks window unfocused with edge");
        Assert.True(!input.IsKeyDown(KeyCode.Space) && input.IsKeyReleased(KeyCode.Space), "ApplyFocusLost releases held key");
        Assert.True(!input.IsMouseDown(0) && input.IsMouseReleased(0), "ApplyFocusLost releases held mouse button");
        Assert.True(!input.IsGamepadButtonDown(GamepadButton.South, 0) && input.IsGamepadButtonReleased(GamepadButton.South, 0), "ApplyFocusLost releases held gamepad button");
        Assert.Near(0f, input.GetGamepad(0).LeftX, 1e-6f, "ApplyFocusLost zeroes gamepad analog axis");
    }

    private static SceneManager* s_reentrantSceneMgr;
    private static int s_reentrantUnloadCalls;
    private static int s_additiveFixedTicks;
    private static int s_additiveViewTicks;
    private static byte s_loaderTriggered;

    private static void OnUnloadSelfReentrant(ref SceneContext ctx)
    {
        s_reentrantUnloadCalls++;
        if (s_reentrantSceneMgr != null)
            SceneSystem.UnloadScene(ref *s_reentrantSceneMgr, ref *ctx.World, ref *ctx.Allocators, ref *ctx.Bundles, ctx.Scene);
    }

    private static void OnLoadSingleAndThrow(ref SceneContext ctx)
    {
        EntitySystem.Spawn(ref ctx.World->Entities, ctx.Scene);
        if (s_reentrantSceneMgr != null)
            SceneSystem.LoadScene(ref *s_reentrantSceneMgr, ref *ctx.World, ref *ctx.Allocators, ref *ctx.Bundles, StringId.Hash("S2_Survivor"), SceneLoadMode.Single);
        throw new InvalidOperationException("Simulated throw after re-entrant Single LoadScene");
    }

    private static void OnLoadSurvivor(ref SceneContext ctx)
    {
        // Write into scene arena and spawn entity so we verify arena survived intact
        int* marker = ctx.SceneArena->Alloc<int>(1);
        *marker = 42;
        EntitySystem.Spawn(ref ctx.World->Entities, ctx.Scene);
    }

    private static void OnFixedLoadAdditive(ref SceneContext ctx)
    {
        if (s_loaderTriggered == 0 && s_reentrantSceneMgr != null)
        {
            s_loaderTriggered = 1;
            SceneSystem.LoadScene(ref *s_reentrantSceneMgr, ref *ctx.World, ref *ctx.Allocators, ref *ctx.Bundles, StringId.Hash("S_AddTick"), SceneLoadMode.Additive);
        }
    }

    private static void OnFixedAdditiveTick(ref SceneContext ctx) => s_additiveFixedTicks++;
    private static void OnViewAdditiveTick(ref SceneContext ctx) => s_additiveViewTicks++;

    private static void TestSceneManagerAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 128, 32);
        var bundles = BundleManager.Create(ref alloc.Persistent, 8);
        var scenes = SceneManager.Create(ref alloc.Persistent, maxDefinitions: 16, maxActiveScenes: 4);

        var b1 = BundleSystem.Register(ref bundles, "b_shared", 32 * 1024, &LoadDummyBundle);
        Span<BundleId> deps1 = stackalloc BundleId[] { b1 };

        // SIM-01: LinearArena.Create OOM releases acquired bundles cleanly
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S_OomArena", deps1, nuint.MaxValue, &OnLoadOk);
        bool arenaOomThrew = false;
        try { SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S_OomArena"), SceneLoadMode.Single); }
        catch (OutOfMemoryException) { arenaOomThrew = true; }
        Assert.True(arenaOomThrew, "SIM-01: Huge sceneArenaBytes threw OutOfMemoryException");
        Assert.Equal(0, BundleSystem.GetRefCount(in bundles, b1), "SIM-01: Acquired bundle b1 RefCount == 0 after arena OOM");
        Assert.True(!BundleSystem.IsLoaded(in bundles, b1), "SIM-01: b1 unloaded after arena OOM");
        Assert.Equal(0, scenes.ActiveCount, "SIM-01: ActiveCount == 0 after arena OOM");

        // SIM-02 (part A): Re-entrant UnloadScene(ctx.Scene) inside OnUnload with shared bundle held by another additive scene
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S_Holder", deps1, 16 * 1024, &OnLoadOk);
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S_ReUnload", deps1, 16 * 1024, &OnLoadOk, onUnload: &OnUnloadSelfReentrant);
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S2_Survivor", deps1, 16 * 1024, &OnLoadSurvivor);
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S1_ReLoadThrow", deps1, 16 * 1024, &OnLoadSingleAndThrow);

        s_reentrantSceneMgr = &scenes;
        s_reentrantUnloadCalls = 0;
        var hHolder = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S_Holder"), SceneLoadMode.Single);
        var hReUnload = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S_ReUnload"), SceneLoadMode.Additive);
        Assert.Equal(2, scenes.ActiveCount, "SIM-02: Both additive scenes active");
        Assert.Equal(2, BundleSystem.GetRefCount(in bundles, b1), "SIM-02: Shared bundle RefCount == 2");

        Assert.True(SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, hReUnload), "SIM-02: Unload S_ReUnload succeeded");
        Assert.Equal(1, s_reentrantUnloadCalls, "SIM-02: Re-entrant UnloadScene skipped without recursion");
        Assert.Equal(1, scenes.ActiveCount, "SIM-02: S_Holder remains active (ActiveCount == 1)");
        Assert.Equal(1, BundleSystem.GetRefCount(in bundles, b1), "SIM-02: Shared bundle RefCount == 1 (no double-release)");
        Assert.True(BundleSystem.IsLoaded(in bundles, b1), "SIM-02: Shared bundle remains loaded");
        SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, hHolder);

        // SIM-02 (part B): Re-entrant LoadScene(Single) + throw inside OnLoad leaves S2_Survivor intact
        bool reThrow = false;
        try { SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S1_ReLoadThrow"), SceneLoadMode.Single); }
        catch (InvalidOperationException) { reThrow = true; }
        Assert.True(reThrow, "SIM-02: Re-entrant OnLoad exception propagated");
        Assert.Equal(1, scenes.ActiveCount, "SIM-02: S2_Survivor remains active after S1 OnLoad threw");
        Assert.Equal(1, world.Entities.AliveCount, "SIM-02: S2_Survivor entity alive");
        Assert.Equal(1, BundleSystem.GetRefCount(in bundles, b1), "SIM-02: S2_Survivor bundle RefCount == 1");
        SceneSystem.UnloadAllScenes(ref scenes, ref world, ref alloc, ref bundles);
        Assert.Equal(0, scenes.ActiveCount, "SIM-02: All scenes unloaded cleanly");
        Assert.Equal(0, BundleSystem.GetRefCount(in bundles, b1), "SIM-02: Bundle RefCount == 0 after unload");

        // SIM-18 (part A): RegisterScene OOM preserves DefinitionCount
        var tinyArena = LinearArena.Create(64);
        int defCountBefore = scenes.DefinitionCount;
        Span<BundleId> bigDeps = stackalloc BundleId[16];
        for (int i = 0; i < 16; i++) bigDeps[i] = b1;
        bool regOomThrew = false;
        try { SceneSystem.RegisterScene(ref scenes, ref tinyArena, "S_TinyFail", bigDeps, 1024, &OnLoadOk); }
        catch (OutOfMemoryException) { regOomThrew = true; }
        Assert.True(regOomThrew, "SIM-18: RegisterScene threw OOM on undersized arena");
        Assert.Equal(defCountBefore, scenes.DefinitionCount, "SIM-18: DefinitionCount preserved on OOM (no zombie slot)");
        tinyArena.Dispose();

        // SIM-18 (part B): Additive scene loaded inside OnFixedUpdate behaves identically regardless of slot order
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S_Loader", default, 8 * 1024, &OnLoadOk, onFixedUpdate: &OnFixedLoadAdditive);
        SceneSystem.RegisterScene(ref scenes, ref alloc.Persistent, "S_AddTick", default, 8 * 1024, &OnLoadOk, onFixedUpdate: &OnFixedAdditiveTick, onViewSync: &OnViewAdditiveTick);

        // Order 1: S_Loader in slot 0 -> loads S_AddTick into slot 1
        s_loaderTriggered = 0;
        s_additiveFixedTicks = 0;
        s_additiveViewTicks = 0;
        SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S_Loader"), SceneLoadMode.Single);
        SceneSystem.FixedUpdateActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
        int ticksOrder1SameFrame = s_additiveFixedTicks;
        SceneSystem.FixedUpdateActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
        SceneSystem.ViewSyncActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
        Assert.Equal(0, ticksOrder1SameFrame, "SIM-18: Order 1 newly loaded additive scene does not run on same tick");
        Assert.Equal(1, s_additiveFixedTicks, "SIM-18: Order 1 additive scene runs on next tick");
        Assert.Equal(1, s_additiveViewTicks, "SIM-18: Order 1 additive ViewSync runs cleanly");
        SceneSystem.UnloadAllScenes(ref scenes, ref world, ref alloc, ref bundles);

        // Order 2: Fill slot 0 with S_Holder, load S_Loader into slot 1, unload slot 0 -> S_Loader loads S_AddTick into slot 0
        s_loaderTriggered = 0;
        s_additiveFixedTicks = 0;
        var hSlot0 = SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S_Holder"), SceneLoadMode.Single);
        SceneSystem.LoadScene(ref scenes, ref world, ref alloc, ref bundles, StringId.Hash("S_Loader"), SceneLoadMode.Additive);
        SceneSystem.UnloadScene(ref scenes, ref world, ref alloc, ref bundles, hSlot0);
        SceneSystem.FixedUpdateActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
        int ticksOrder2SameFrame = s_additiveFixedTicks;
        SceneSystem.FixedUpdateActiveScenes(ref scenes, ref world, ref alloc, ref bundles, 1f / 60f);
        Assert.Equal(ticksOrder1SameFrame, ticksOrder2SameFrame, "SIM-18: Slot order 1 and slot order 2 produce identical tick behavior");
        Assert.Equal(1, s_additiveFixedTicks, "SIM-18: Order 2 additive scene runs on next tick");

        SceneSystem.UnloadAllScenes(ref scenes, ref world, ref alloc, ref bundles);
        s_reentrantSceneMgr = null;
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestSimulationTableCapacityBounds()
    {
        // SIM-20: Simulation table Create methods reject <= 0 and > (HandlePacking.IndexMask + 1)
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(64 * 1024, 16 * 1024, 16 * 1024, 16 * 1024);
        int overMax = (int)HandlePacking.IndexMask + 2;

        bool threw = false;
        try { EntityTable.Create(ref alloc.Persistent, overMax); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: EntityTable rejects capacity > IndexMask+1");

        threw = false;
        try { EntityTable.Create(ref alloc.Persistent, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: EntityTable rejects capacity <= 0");

        threw = false;
        try { TransformTable.Create(ref alloc.Persistent, overMax, 16); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: TransformTable rejects maxEntities > IndexMask+1");

        threw = false;
        try { CameraTable.Create(ref alloc.Persistent, overMax); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: CameraTable rejects capacity > IndexMask+1");

        threw = false;
        try { TweenTable.Create(ref alloc.Persistent, overMax); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: TweenTable rejects capacity > IndexMask+1");

        threw = false;
        try { RenderComponentTables.Create(ref alloc.Persistent, 16, overMax, 16); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: RenderComponentTables rejects maxSprites > IndexMask+1");

        threw = false;
        try { RenderComponentTables.Create(ref alloc.Persistent, 16, 16, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: RenderComponentTables rejects maxTexts <= 0");

        threw = false;
        try { SceneManager.Create(ref alloc.Persistent, 16, overMax); } catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw, "SIM-20: SceneManager rejects maxActiveScenes > IndexMask+1");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }
}

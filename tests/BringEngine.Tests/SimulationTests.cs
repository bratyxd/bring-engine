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
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 128 * 1024, 128 * 1024, 64 * 1024);
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

        var alloc = EngineAllocators.Create(1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
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
}

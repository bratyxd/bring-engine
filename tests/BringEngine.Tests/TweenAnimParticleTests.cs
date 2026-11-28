using System;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Rendering;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.Tween;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class TweenAnimParticleTests
{
    private static uint s_completedUserData;
    private static TweenTable* s_reentrantTweens;
    private static TransformTable* s_reentrantTransforms;
    private static CameraTable* s_reentrantCameras;
    private static TweenHandle s_reentrantVictim;
    private static TweenHandle s_reentrantSelf;
    private static int s_reentrantStepCount;
    private static int s_reentrantCompleteCount;

    public static void RunAll()
    {
        Console.WriteLine("=== Phase 4: Universal Tweens, Skeletal/Sprite Anim & SoA Particles ===");
        Assert.Run("Easing_All32CurvesAndCubicCurveDeterminism", &TestEasingAllCurves);
        Assert.Run("TweenSystem_PointersComponentsSequencesAndLoops", &TestTweenSystem);
        Assert.Run("TweenSystem_HelpersReentrancyKillChainAndZeroGc", &TestTweenHelpersReentrancyAndChain);
        Assert.Run("TweenSystem_ChainCycleNanOvershootStepReentrancyAndHandleSafeRenderTargets", &TestTweenAuditRegressions);
        Assert.Run("AnimationSystem_SkeletalCrossfadeAndSpriteFrames", &TestAnimationSystem);
        Assert.Run("AnimationSystem_CubicSplineCrossfadeNonTopoJointsAndHandles", &TestAnimationEdgeCases);
        Assert.Run("AnimationSystem_SkeletonGuardsScratchOomSpriteClipHandlesOverflowCrossfadeAndBounds", &TestAnimationAuditRegressions);
        Assert.Run("AnimationSystem_MultiTrackBlending_MasksAdditiveBlendTwoClipsFadeOutAndZeroGc", &TestAnimationMultiTrackBlending);
        Assert.Run("AnimationSystem_AtlasSpriteFlipbooks_PingPongAndDirectSpriteBinding", &TestAtlasSpriteFlipbooks);
        Assert.Run("ParticleSystem_SoADeterministicZeroGc", &TestParticleSystem);
        Assert.Run("ParticleSystem_ParallelParityShapesOverflowHoldPosAndMaxEmitters", &TestParticleParallelAndEdgeCases);
        Assert.Run("ParticleSystem_ManualLocalMoveBadDtOverflowAndHighAtlasClamp", &TestParticleAuditRegressions);
        Assert.Run("ParticleSystem_AtlasFlipbooks_AllModesDeterminismBlendAndZeroGc", &TestParticleAtlasFlipbooks);
    }

    private static void OnTweenDone(uint userData) => s_completedUserData = userData;

    private static void OnStepKillVictimAndSelf(uint userData, Vec4 value)
    {
        _ = userData;
        _ = value;
        s_reentrantStepCount++;
        if (s_reentrantTweens != null)
        {
            if (s_reentrantVictim.IsValid)
                TweenSystem.Kill(ref *s_reentrantTweens, s_reentrantVictim);
            if (s_reentrantSelf.IsValid)
                TweenSystem.Kill(ref *s_reentrantTweens, s_reentrantSelf);
        }
    }

    private static void OnCompleteSpawnAndKill(uint userData)
    {
        _ = userData;
        s_reentrantCompleteCount++;
        if (s_reentrantTweens != null && s_reentrantVictim.IsValid)
            TweenSystem.Kill(ref *s_reentrantTweens, s_reentrantVictim);
    }

    private static void TestEasingAllCurves()
    {
        for (int i = 0; i <= (int)EaseType.SmoothStep; i++)
        {
            EaseType ease = (EaseType)i;
            float e0 = Easing.Evaluate(ease, 0f);
            float e05 = Easing.Evaluate(ease, 0.5f);
            float e1 = Easing.Evaluate(ease, 1f);
            Assert.True(float.IsFinite(e0) && float.IsFinite(e05) && float.IsFinite(e1), $"EaseType.{ease} finite");
            Assert.Near(0f, e0, 1e-3f, $"EaseType.{ease}(0) == 0");
            Assert.Near(1f, e1, 1e-3f, $"EaseType.{ease}(1) == 1");
        }
        Assert.Near(0.5f, CubicCurve.Smooth.Evaluate(0.5f), 1e-4f, "CubicCurve.Smooth(0.5)");
        Assert.True(CubicCurve.Overshoot.Evaluate(0.5f) > 0.9f, "CubicCurve.Overshoot(0.5)");
    }

    private static void TestTweenSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(512 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 64);
        var transforms = TransformTable.Create(ref alloc.Persistent, 64, 64);
        var cameras = CameraTable.Create(ref alloc.Persistent, 8);
        var tweens = TweenTable.Create(ref alloc.Persistent, 256);

        // 1. Direct unmanaged pointer tween (any game struct field!)
        float* customHealth = alloc.Persistent.Alloc<float>(1);
        *customHealth = 0f;
        Fix32* customFix = alloc.Persistent.Alloc<Fix32>(1);
        *customFix = Fix32.Zero;

        var h1 = TweenSystem.TweenFloatPtr(ref tweens, customHealth, 0f, 100f, 0.5f, EaseType.Linear);
        var h2 = TweenSystem.TweenFloatPtr(ref tweens, customHealth, 100f, 50f, 0.5f, EaseType.Linear);
        TweenSystem.Chain(ref tweens, h1, h2);
        TweenSystem.TryGetPtr(in tweens, h2)->OnComplete = &OnTweenDone;
        TweenSystem.TryGetPtr(in tweens, h2)->UserData = 777u;

        TweenSystem.TweenFix32Ptr(ref tweens, customFix, Fix32.Zero, Fix32.FromInt(10), 1.0f, EaseType.OutQuad);

        // 2. Transform + Camera handle-safe tweens
        var e = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, e, Vec3.Zero);
        TweenSystem.TweenTransformPos(ref tweens, e, Vec3.Zero, new Vec3(10, 20, 30), 1.0f, EaseType.Linear);

        var cam = CameraSystem.CreatePerspective(ref cameras, new Vec3(0, 5, 10), Vec3.Zero, 60f);
        TweenSystem.TweenCameraFov(ref tweens, cam, 60f, 90f, 1.0f, EaseType.Linear);

        s_completedUserData = 0;

        // Step 0.5s (30 ticks at 60Hz)
        for (int t = 0; t < 30; t++)
            TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 1f / 60f);

        Assert.Near(100f, *customHealth, 2f, "Sequence step 1 finished at 100");
        Assert.True(!TweenSystem.IsActive(in tweens, h1), "h1 completed");
        Assert.True(TweenSystem.IsActive(in tweens, h2), "h2 unlocked and active");

        // Step another 0.55s -> sequence completes
        for (int t = 0; t < 35; t++)
            TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 1f / 60f);

        Assert.Near(50f, *customHealth, 0.5f, "Sequence step 2 reached 50");
        Assert.Equal(777u, s_completedUserData, "Unmanaged OnComplete callback fired");
        Assert.Near(10f, customFix->ToFloat(), 0.05f, "Fix32 pointer tween completed");

        TransformSystem.PropagateHierarchy(ref transforms);
        Vec3 ePos = TransformSystem.GetWorldPosition(in transforms, e);
        Assert.Near(10f, ePos.X, 0.05f, "Transform tween X");
        Assert.Near(20f, ePos.Y, 0.05f, "Transform tween Y");
        Assert.Near(90f, CameraSystem.TryGetPtr(in cameras, cam)->FovYDeg, 0.1f, "Camera FOV tween");

        // PingPong infinite loop check
        float* ping = alloc.Persistent.Alloc<float>(1);
        var hp = TweenSystem.TweenFloatPtr(ref tweens, ping, 0f, 10f, 0.5f, EaseType.Linear, loopMode: TweenLoopMode.PingPong, loops: -1);
        for (int t = 0; t < 45; t++) TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 1f / 60f); // 0.75s -> halfway back = 5
        Assert.Near(5f, *ping, 0.5f, "PingPong halfway back");
        Assert.True(TweenSystem.IsActive(in tweens, hp), "Infinite pingpong still active");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestTweenHelpersReentrancyAndChain()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(512 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 64);
        var transforms = TransformTable.Create(ref alloc.Persistent, 64, 64);
        var cameras = CameraTable.Create(ref alloc.Persistent, 8);
        var tweens = TweenTable.Create(ref alloc.Persistent, 256);

        // 1. All documented Ptr* and Camera* + Pause/Resume + Delay + LoopMode.Loop helpers
        Vec2* pV2 = alloc.Persistent.Alloc<Vec2>(1);
        Vec4* pV4 = alloc.Persistent.Alloc<Vec4>(1);
        Quat* pQuat = alloc.Persistent.Alloc<Quat>(1);
        FixVec3* pFixV3 = alloc.Persistent.Alloc<FixVec3>(1);
        ColorF* pCol = alloc.Persistent.Alloc<ColorF>(1);

        var scA = SceneId.Create(1, 1);
        var hV2 = TweenSystem.PtrVec2(ref tweens, pV2, Vec2.Zero, new Vec2(4, 8), 0.5f, EaseType.Linear, delay: 0.1f, scene: scA, groupId: 7);
        TweenSystem.PtrVec4(ref tweens, pV4, Vec4.Zero, Vec4.One, 0.5f, EaseType.Linear, scene: scA);
        TweenSystem.PtrQuat(ref tweens, pQuat, Quat.Identity, Quat.FromAxisAngle(Vec3.UnitY, DetMath.HalfPi), 0.5f, EaseType.Linear, scene: scA);
        TweenSystem.PtrFixVec3(ref tweens, pFixV3, FixVec3.Zero, FixVec3.FromFloat(2f, 4f, 6f), 0.5f, EaseType.Linear, scene: scA);
        TweenSystem.TweenColorPtr(ref tweens, pCol, ColorF.Black, ColorF.White, 0.5f, EaseType.Linear, scene: scA);

        var cam = CameraSystem.CreatePerspective(ref cameras, Vec3.Zero, Vec3.Forward);
        TweenSystem.CameraPosition(ref tweens, cam, Vec3.Zero, new Vec3(10, 0, 0), 0.5f, EaseType.Linear, scene: scA);
        TweenSystem.CameraOrthoHeight(ref tweens, cam, 10f, 20f, 0.5f, EaseType.Linear, scene: scA);
        TweenSystem.CameraOrbitYaw(ref tweens, cam, 0f, 1.5f, 0.5f, EaseType.Linear, scene: scA);
        TweenSystem.CameraOrbitDistance(ref tweens, cam, 5f, 12f, 0.5f, EaseType.Linear, scene: scA);

        Assert.True(TweenSystem.Pause(ref tweens, hV2), "Pause hV2");
        TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 0.25f);
        Assert.Near(0f, pV2->X, 1e-5f, "Paused tween did not advance");
        Assert.True(TweenSystem.Resume(ref tweens, hV2), "Resume hV2");

        for (int t = 0; t < 40; t++)
            TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 1f / 60f);

        Assert.Near(4f, pV2->X, 0.05f, "PtrVec2 X");
        Assert.Near(1f, pV4->W, 0.05f, "PtrVec4 W");
        Assert.Near(0.7071f, pQuat->W, 0.05f, "PtrQuat W");
        Assert.Near(6f, pFixV3->Z.ToFloat(), 0.1f, "PtrFixVec3 Z");
        Assert.Near(1f, pCol->R, 0.05f, "TweenColorPtr R");
        var camPtr = CameraSystem.TryGetPtr(in cameras, cam);
        Assert.Near(10f, camPtr->Position.X, 0.05f, "CameraPosition X");
        Assert.Near(20f, camPtr->OrthoHeight, 0.05f, "CameraOrthoHeight");
        Assert.Near(1.5f, camPtr->OrbitYaw, 0.05f, "CameraOrbitYaw");
        Assert.Near(12f, camPtr->OrbitDistance, 0.05f, "CameraOrbitDistance");

        // 2. Kill(first) cascade-kills orphaned NextInSequence
        float* seqVal = alloc.Persistent.Alloc<float>(1);
        var c1 = TweenSystem.TweenFloatPtr(ref tweens, seqVal, 0f, 1f, 1f);
        var c2 = TweenSystem.TweenFloatPtr(ref tweens, seqVal, 1f, 2f, 1f);
        TweenSystem.Chain(ref tweens, c1, c2);
        Assert.True(TweenSystem.Kill(ref tweens, c1), "Kill c1");
        Assert.True(!TweenSystem.IsActive(in tweens, c2), "Chained c2 cascade-killed with c1 (not orphaned)");

        // 3. Chain slot-order independence: reverse-order Chain(late, early) wakes cleanly next tick
        float* revVal = alloc.Persistent.Alloc<float>(1);
        *revVal = 0f;
        var hEarly = TweenSystem.TweenFloatPtr(ref tweens, revVal, 10f, 20f, 0.1f, EaseType.Linear);
        var hLate = TweenSystem.TweenFloatPtr(ref tweens, revVal, 0f, 10f, 0.1f, EaseType.Linear);
        TweenSystem.Chain(ref tweens, hLate, hEarly); // hEarly sits at index < hLate!
        TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 0.1f);
        Assert.True(!TweenSystem.IsActive(in tweens, hLate), "hLate completed");
        Assert.True(TweenSystem.IsActive(in tweens, hEarly), "hEarly active");
        Assert.True(!TweenSystem.TryGetPtr(in tweens, hEarly)->IsWaitingInSequence, "hEarly unlocked");
        Assert.Near(10f, *revVal, 1e-4f, "hEarly did not step on same frame as hLate completion");
        TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 0.05f);
        Assert.Near(15f, *revVal, 0.1f, "hEarly stepped uniformly on next tick");
        TweenSystem.Kill(ref tweens, hEarly);

        // 4. OnStep & OnComplete re-entrancy safety (killing earlier sibling + self inside callbacks)
        float* f0 = alloc.Persistent.Alloc<float>(1);
        float* f3 = alloc.Persistent.Alloc<float>(1);
        *f0 = 0f; *f3 = 0f;
        var v0 = TweenSystem.TweenFloatPtr(ref tweens, f0, 0f, 10f, 1f, EaseType.Linear);
        var selfH = TweenSystem.Spawn(ref tweens, new TweenData
        {
            TargetKind = TweenTargetKind.CustomCallback,
            Duration = 1f,
            StartValue = Vec4.Zero,
            EndValue = Vec4.One,
            OnStep = &OnStepKillVictimAndSelf,
        });
        var survivor = TweenSystem.TweenFloatPtr(ref tweens, f3, 0f, 10f, 1f, EaseType.Linear);

        s_reentrantStepCount = 0;
        s_reentrantCompleteCount = 0;
        s_reentrantTweens = &tweens;
        s_reentrantVictim = v0;
        s_reentrantSelf = selfH;

        TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 0.5f);
        Assert.Equal(1, s_reentrantStepCount, "OnStep fired once");
        Assert.True(!TweenSystem.IsActive(in tweens, v0), "Victim killed by OnStep");
        Assert.True(!TweenSystem.IsActive(in tweens, selfH), "Self killed by OnStep");
        Assert.True(TweenSystem.IsActive(in tweens, survivor), "Survivor remains active");
        Assert.Near(5f, *f3, 0.05f, "Survivor was NOT skipped when v0 was killed at lower dense index!");

        // OnComplete re-entrancy
        var v2 = TweenSystem.TweenFloatPtr(ref tweens, f0, 0f, 1f, 1f, EaseType.Linear);
        var finisher = TweenSystem.Spawn(ref tweens, new TweenData
        {
            TargetKind = TweenTargetKind.PtrFloat,
            TargetPtr = f3,
            Duration = 0.1f,
            StartValue = Vec4.Zero,
            EndValue = new Vec4(1, 0, 0, 0),
            OnComplete = &OnCompleteSpawnAndKill,
        });
        s_reentrantVictim = v2;
        TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 0.2f);
        Assert.Equal(1, s_reentrantCompleteCount, "OnComplete fired");
        Assert.True(!TweenSystem.IsActive(in tweens, v2), "v2 killed inside OnComplete");
        s_reentrantTweens = null;

        // Zero-GC StepAll assertion
        float* gcFloat = alloc.Persistent.Alloc<float>(1);
        TweenSystem.TweenFloatPtr(ref tweens, gcFloat, 0f, 100f, 1f, EaseType.OutBounce, loopMode: TweenLoopMode.Loop, loops: -1);
        TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 1f / 60f);
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 120; t++)
            TweenSystem.StepAll(ref tweens, ref transforms, ref cameras, 1f / 60f);
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "TweenSystem.StepAll allocates 0 GC bytes");

        TweenSystem.KillByScene(ref tweens, scA);
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestAnimationSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(512 * 1024, 128 * 1024, 128 * 1024, 64 * 1024);
        var anims = AnimationTable.Create(ref alloc.Persistent, 16, 16, 32, 16, 32);

        // 2-joint skeleton (root -> child)
        int* parents = alloc.Persistent.Alloc<int>(2);
        parents[0] = -1; parents[1] = 0;
        Mat4* ibm = alloc.Persistent.Alloc<Mat4>(2);
        ibm[0] = Mat4.Identity; ibm[1] = Mat4.Identity;
        Vec3* bp = alloc.Persistent.Alloc<Vec3>(2);
        bp[0] = Vec3.Zero; bp[1] = new Vec3(0, 1, 0);
        Quat* br = alloc.Persistent.Alloc<Quat>(2);
        br[0] = Quat.Identity; br[1] = Quat.Identity;
        Vec3* bs = alloc.Persistent.Alloc<Vec3>(2);
        bs[0] = Vec3.One; bs[1] = Vec3.One;

        var skel = AnimationSystem.RegisterSkeleton(ref anims, new SkeletonData
        {
            JointCount = 2,
            ParentIndices = parents,
            InverseBindMatrices = ibm,
            BindPositions = bp,
            BindRotations = br,
            BindScales = bs,
        });

        // Clip moving joint 1 from (0,1,0) to (0,5,0) over 1s
        float* times = alloc.Persistent.Alloc<float>(2);
        times[0] = 0f; times[1] = 1f;
        float* vals = alloc.Persistent.Alloc<float>(6);
        vals[0] = 0f; vals[1] = 1f; vals[2] = 0f;
        vals[3] = 0f; vals[4] = 5f; vals[5] = 0f;

        var samplers = alloc.Persistent.Alloc<AnimSamplerData>(1);
        samplers[0] = new AnimSamplerData { Times = times, Values = vals, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
        var channels = alloc.Persistent.Alloc<AnimChannelData>(1);
        channels[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 1, Path = AnimTargetPath.Translation };

        var clip = AnimationSystem.RegisterClip(ref anims, new AnimClipData
        {
            Name = StringId.Hash("Lift"),
            Duration = 1f,
            Samplers = samplers,
            SamplerCount = 1,
            Channels = channels,
            ChannelCount = 1,
        });

        var entity = EntityId.Create(1, 1);
        AnimPlayerHandle pHandle = AnimationSystem.AddPlayer(ref anims, entity, skel, clip);

        // Sprite anim clip (4x2 sheet, 4 frames at 8 fps)
        SpriteAnimClipHandle sClip = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
        {
            Name = StringId.Hash("Walk"),
            StartFrame = 0,
            FrameCount = 4,
            Columns = 4,
            Rows = 2,
            Fps = 8f,
            Loop = 1,
        });
        SpriteAnimPlayerHandle spHandle = AnimationSystem.AddSpritePlayer(ref anims, entity, sClip);

        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.5f, ref alloc.Frame, ref alloc.Scratch);

        Assert.Equal(2, anims.FrameJointPalette.Length, "2 joint matrices generated");
        AnimPlayerData* pPtr = AnimationSystem.TryGetPlayer(in anims, pHandle);
        Assert.True(pPtr != null, "AnimPlayerHandle resolves");
        Mat4 joint1Mat = anims.FrameJointPalette[pPtr->FrameJointOffset + 1];
        Assert.Near(3.0f, joint1Mat.C3.Y, 1e-3f, "Joint 1 interpolated to Y=3 at t=0.5");

        // At t=0.5s, 8fps => frame 4 % 4 = 0; step +0.125s => frame 1 (u0 = 0.25)
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.125f, ref alloc.Frame, ref alloc.Scratch);
        SpriteAnimPlayerData* spPtr = AnimationSystem.TryGetSpritePlayer(in anims, spHandle);
        Assert.True(spPtr != null, "SpriteAnimPlayerHandle resolves");
        Assert.Equal(1, spPtr->CurrentFrame, "Sprite anim frame 1");
        Assert.Near(0.25f, spPtr->CurrentUvRect.X, 1e-4f, "Sprite UV u0=0.25");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestAnimationEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 128 * 1024, 128 * 1024, 64 * 1024);
        var anims = AnimationTable.Create(ref alloc.Persistent, 16, 16, 32, 8, 32);

        // 1. Non-topological ParentIndices = { 1, -1 } (child at 0, parent at 1)
        int* parents = alloc.Persistent.Alloc<int>(2);
        parents[0] = 1; parents[1] = -1;
        Mat4* ibm = alloc.Persistent.Alloc<Mat4>(2);
        ibm[0] = Mat4.Identity; ibm[1] = Mat4.Identity;
        Vec3* bp = alloc.Persistent.Alloc<Vec3>(2);
        bp[0] = new Vec3(0, 2, 0); bp[1] = new Vec3(10, 0, 0);
        Quat* br = alloc.Persistent.Alloc<Quat>(2);
        br[0] = Quat.Identity; br[1] = Quat.Identity;
        Vec3* bs = alloc.Persistent.Alloc<Vec3>(2);
        bs[0] = Vec3.One; bs[1] = Vec3.One;
        var nonTopoSkel = AnimationSystem.RegisterSkeleton(ref anims, new SkeletonData
        {
            JointCount = 2,
            ParentIndices = parents,
            InverseBindMatrices = ibm,
            BindPositions = bp,
            BindRotations = br,
            BindScales = bs,
        });

        // 2. glTF 2.0 CubicSpline translation (v0=0, v1=0, b0=(0,10,0), a1=(0,-10,0) over [0,1] => midpoint t=0.5 is 2.5!)
        float* cTimes = alloc.Persistent.Alloc<float>(2);
        cTimes[0] = 0f; cTimes[1] = 1f;
        float* cVals = alloc.Persistent.Alloc<float>(18); // 2 keys * 3 tuples * 3 floats
        // Key 0: inTangent=(0,0,0), value=(0,0,0), outTangent=(0,10,0)
        cVals[6] = 0f; cVals[7] = 10f; cVals[8] = 0f;
        // Key 1: inTangent=(0,-10,0), value=(0,0,0), outTangent=(0,0,0)
        cVals[9] = 0f; cVals[10] = -10f; cVals[11] = 0f;

        // Also CubicSpline rotation + Step scale channel
        float* rVals = alloc.Persistent.Alloc<float>(24); // 2 keys * 3 tuples * 4 floats
        rVals[7] = 1f;  // Key 0 val = Identity (0,0,0,1)
        rVals[10] = 1f; // Key 0 outTangent z=1
        rVals[14] = -1f; // Key 1 inTangent z=-1
        rVals[19] = 1f; // Key 1 val = Identity (0,0,0,1)

        float* sVals = alloc.Persistent.Alloc<float>(6);
        sVals[0] = 1f; sVals[1] = 1f; sVals[2] = 1f;
        sVals[3] = 3f; sVals[4] = 3f; sVals[5] = 3f;

        var samplers = alloc.Persistent.Alloc<AnimSamplerData>(3);
        samplers[0] = new AnimSamplerData { Times = cTimes, Values = cVals, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.CubicSpline };
        samplers[1] = new AnimSamplerData { Times = cTimes, Values = rVals, KeyframeCount = 2, ComponentsPerValue = 4, Interpolation = AnimInterpolation.CubicSpline };
        samplers[2] = new AnimSamplerData { Times = cTimes, Values = sVals, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Step };

        var channels = alloc.Persistent.Alloc<AnimChannelData>(3);
        channels[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 0, Path = AnimTargetPath.Translation };
        channels[1] = new AnimChannelData { SamplerIndex = 1, TargetJointIndex = 0, Path = AnimTargetPath.Rotation };
        channels[2] = new AnimChannelData { SamplerIndex = 2, TargetJointIndex = 1, Path = AnimTargetPath.Scale };

        var cubicClip = AnimationSystem.RegisterClip(ref anims, new AnimClipData
        {
            Name = StringId.Hash("CubicNonTopo"),
            Duration = 1f,
            Samplers = samplers,
            SamplerCount = 3,
            Channels = channels,
            ChannelCount = 3,
        });

        var scA = SceneId.Create(1, 1);
        var scB = SceneId.Create(2, 1);
        var eA = EntityId.Create(10, 1);
        var eB = EntityId.Create(11, 1);

        var hA = AnimationSystem.AddPlayer(ref anims, eA, nonTopoSkel, cubicClip, scA, speed: 1f, loop: false);
        var hB = AnimationSystem.AddPlayer(ref anims, eB, nonTopoSkel, cubicClip, scB, speed: 1f, loop: false);

        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.5f, ref alloc.Frame, ref alloc.Scratch);

        AnimPlayerData* pA = AnimationSystem.TryGetPlayer(in anims, hA);
        Mat4 childWorld = anims.FrameJointPalette[pA->FrameJointOffset + 0];
        Assert.Near(10.0f, childWorld.C3.X, 1e-3f, "Non-topological parent X=10 propagated to child at index 0");
        Assert.Near(2.5f, childWorld.C3.Y, 1e-3f, "CubicSpline Hermite midpoint Y == 2.5");
        // At t=0.5, Step scale on joint 1 must still be 1.0 (not 3.0)
        Mat4 parentWorld = anims.FrameJointPalette[pA->FrameJointOffset + 1];
        Assert.Near(1.0f, parentWorld.C0.X, 1e-3f, "AnimInterpolation.Step holds k0 scale at t=0.5");

        // 3. AnimPlayerHandle survival across RemoveByScene swap-back compaction
        AnimationSystem.RemoveByScene(ref anims, scA);
        Assert.True(AnimationSystem.TryGetPlayer(in anims, hA) == null, "Unloaded Scene A AnimPlayerHandle invalidated");
        AnimPlayerData* pB = AnimationSystem.TryGetPlayer(in anims, hB);
        Assert.True(pB != null && pB->Entity == eB, "Surviving Scene B AnimPlayerHandle still resolves after swap-back!");

        // 4. Crossfade pause & non-looping clamp
        AnimationSystem.Crossfade(ref anims, hB, cubicClip, 0.5f);
        pB->Playing = 0;
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.3f, ref alloc.Frame, ref alloc.Scratch);
        Assert.Near(0f, pB->CrossfadeElapsed, 1e-5f, "Paused player does not advance CrossfadeElapsed");
        Assert.Near(0f, pB->NextTime, 1e-5f, "Paused player does not advance NextTime");

        pB->Playing = 1;
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 2.0f, ref alloc.Frame, ref alloc.Scratch);
        Assert.Near(1.0f, pB->Time, 1e-4f, "Non-looping completed Crossfade clamped NextTime to Duration");

        // 5. RegisterSpriteClip deduplication by Name (300 calls with MaxSpriteClips=8 must NOT exhaust!)
        var starName = StringId.Hash("pickup_star");
        SpriteAnimClipHandle firstClip = SpriteAnimClipHandle.Invalid;
        for (int i = 0; i < 300; i++)
        {
            SpriteAnimClipHandle clipH = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
            {
                Name = starName,
                OwnerScene = scB,
                StartFrame = 0, FrameCount = 4, Columns = 4, Rows = 2, Fps = 8f, Loop = 1,
            });
            if (i == 0) firstClip = clipH;
            else Assert.Equal(firstClip, clipH, "RegisterSpriteClip deduplicated by Name");
        }
        Assert.Equal(1, anims.SpriteClipCount, "SpriteClipCount remains 1 after 300 duplicate registrations");

        // Zero-GC StepAll assertion
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 60; t++)
        {
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "AnimationSystem.StepAll allocates 0 GC bytes");

        AnimationSystem.RemoveByScene(ref anims, scB);
        Assert.Equal(0, anims.SpriteClipCount, "RemoveByScene cleaned up owned sprite clip");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestParticleSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var transforms = TransformTable.Create(ref alloc.Persistent, 16, 16);
        var pA = ParticleTable.Create(ref alloc.Persistent, 2048, 16);
        var pB = ParticleTable.Create(ref alloc.Persistent, 2048, 16);

        var cfg = ParticleSystem.DefaultEmitter(new Vec3(1, 2, 3), seed: 999);
        cfg.EmitRate = 120f;
        var eA = ParticleSystem.CreateEmitter(ref pA, in cfg);
        var eB = ParticleSystem.CreateEmitter(ref pB, in cfg);
        ParticleSystem.Burst(ref pA, eA, 100);
        ParticleSystem.Burst(ref pB, eB, 100);

        // Warmup 1 tick
        ParticleSystem.Step(ref pA, in transforms, 1f / 60f);
        ParticleSystem.Step(ref pB, in transforms, 1f / 60f);

        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 120; t++)
        {
            ParticleSystem.Step(ref pA, in transforms, 1f / 60f);
            ParticleSystem.Step(ref pB, in transforms, 1f / 60f);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, gcAfter - gcBefore, "Particle simulation allocates 0 GC bytes");
        Assert.True(pA.ParticleCount > 50, "Particles alive");
        Assert.Equal(pA.ParticleCount, pB.ParticleCount, "Deterministic particle count");
        for (int i = 0; i < pA.ParticleCount; i++)
        {
            Assert.Equal(pA.PosX[i], pB.PosX[i], "Bit-identical particle PosX");
            Assert.Equal(pA.PosY[i], pB.PosY[i], "Bit-identical particle PosY");
        }

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestParticleParallelAndEdgeCases()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 32);
        var transforms = TransformTable.Create(ref alloc.Persistent, 32, 32);

        // 1. maxEmitters > 65535 guard
        bool emitterLimitThrew = false;
        try { ParticleTable.Create(ref alloc.Persistent, 16, 65536); }
        catch (ArgumentOutOfRangeException) { emitterLimitThrew = true; }
        Assert.True(emitterLimitThrew, "ParticleTable.Create rejects maxEmitters > 65535");

        // 2. EmitAccumulator overflow & small pool saturation guard
        var smallPool = ParticleTable.Create(ref alloc.Persistent, 16, 8);
        var fastCfg = ParticleSystem.DefaultEmitter(Vec3.Zero, seed: 42);
        fastCfg.EmitRate = 1e9f;
        fastCfg.LifetimeMin = 200f;
        fastCfg.LifetimeMax = 200f;
        var fastEmH = ParticleSystem.CreateEmitter(ref smallPool, in fastCfg);
        ParticleSystem.Step(ref smallPool, in transforms, 100f);
        Assert.Equal(16, smallPool.ParticleCount, "EmitAccumulator overflow clamped cleanly to capacity");
        Assert.True(ParticleSystem.DestroyEmitter(ref smallPool, fastEmH), "DestroyEmitter cleans up saturated emitter");
        Assert.Equal(0, smallPool.ParticleCount, "DestroyEmitter synchronously removed saturated particles");

        // 3. Local-space (WorldSpace = 0) FollowEntity + hold last position on entity removal + DestroyEmitter
        var localPool = ParticleTable.Create(ref alloc.Persistent, 16, 8);
        var target = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, target, new Vec3(5, 0, 0));
        TransformSystem.PropagateHierarchy(ref transforms);

        var localCfg = ParticleSystem.DefaultEmitter(Vec3.Zero, seed: 77);
        localCfg.WorldSpace = 0;
        localCfg.FollowEntity = target;
        localCfg.EmitRate = 0f;
        localCfg.Gravity = Vec3.Zero;
        localCfg.SpeedMin = 0f; localCfg.SpeedMax = 0f;
        localCfg.Turbulence = 0f;
        localCfg.LifetimeMin = 5f; localCfg.LifetimeMax = 5f;
        localCfg.AtlasColumns = 2; localCfg.AtlasRows = 2;
        var scA = SceneId.Create(1, 1);
        var emH = ParticleSystem.CreateEmitter(ref localPool, in localCfg, scA);
        ParticleSystem.Burst(ref localPool, emH, 1);
        ParticleSystem.Step(ref localPool, in transforms, 0.01f);
        int localIdx = localPool.ParticleCount - 1;
        float x0 = localPool.PosX[localIdx];

        // Move target by +10 in X -> local-space particle shifts by +10
        TransformSystem.SetLocalPosition(ref transforms, target, new Vec3(15, 0, 0));
        TransformSystem.PropagateHierarchy(ref transforms);
        ParticleSystem.Step(ref localPool, in transforms, 0.01f);
        Assert.Near(x0 + 10f, localPool.PosX[localIdx], 0.1f, "Local-space particle followed entity movement");

        // Remove target -> emitter holds last position (15, 0, 0) instead of snapping to Vec3.Zero
        TransformSystem.Remove(ref transforms, target);
        ParticleSystem.Step(ref localPool, in transforms, 0.01f);
        Assert.Near(15f, ParticleSystem.TryGetEmitter(in localPool, emH)->Position.X, 1e-4f, "Emitter holds last position on target removal");

        // DestroyEmitter invalidates handle & removes particles
        Assert.True(ParticleSystem.DestroyEmitter(ref localPool, emH), "DestroyEmitter succeeded");
        Assert.Equal(0, localPool.ParticleCount, "DestroyEmitter removed local particle");
        Assert.True(ParticleSystem.TryGetEmitter(in localPool, emH) == null, "Stale EmitterHandle rejected");
        ParticleSystem.DestroyByScene(ref localPool, scA);

        // 4. Parallel parity >= 2048 particles vs single-threaded bit-for-bit + 0 GC
        var pSingle = ParticleTable.Create(ref alloc.Persistent, 4096, 16);
        var pParallel = ParticleTable.Create(ref alloc.Persistent, 4096, 16);
        for (int sh = 0; sh <= (int)ParticleShape.CircleXZ; sh++)
        {
            var cfg = ParticleSystem.DefaultEmitter(new Vec3(sh, 1, -sh), seed: (ulong)(100 + sh));
            cfg.Shape = (ParticleShape)sh;
            cfg.EmitRate = 0f;
            cfg.AtlasColumns = 2;
            cfg.AtlasRows = 2;
            cfg.LifetimeMin = 2f; cfg.LifetimeMax = 3f;
            var h1 = ParticleSystem.CreateEmitter(ref pSingle, in cfg);
            var h2 = ParticleSystem.CreateEmitter(ref pParallel, in cfg);
            ParticleSystem.Burst(ref pSingle, h1, 500);
            ParticleSystem.Burst(ref pParallel, h2, 500);
        }

        JobScheduler* jobs = alloc.Persistent.Alloc<JobScheduler>(1, 64);
        JobSystem.Start(jobs, ref alloc.Persistent, workerCount: 2);

        ParticleSystem.Step(ref pSingle, in transforms, 1f / 60f, null);
        ParticleSystem.Step(ref pParallel, in transforms, 1f / 60f, jobs);
        Assert.True(pSingle.ParticleCount >= ParticleSystem.ParallelThreshold, "ParticleCount >= ParallelThreshold (2500)");

        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 10; t++)
        {
            ParticleSystem.Step(ref pSingle, in transforms, 1f / 60f, null);
            ParticleSystem.Step(ref pParallel, in transforms, 1f / 60f, jobs);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "Parallel ParticleSystem.Step allocates 0 GC bytes");

        Assert.Equal(pSingle.ParticleCount, pParallel.ParticleCount, "Parallel particle count matches single-threaded");
        for (int i = 0; i < pSingle.ParticleCount; i++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(pSingle.PosX[i]), BitConverter.SingleToUInt32Bits(pParallel.PosX[i]), "Bit-identical parallel PosX");
            Assert.Equal(BitConverter.SingleToUInt32Bits(pSingle.PosY[i]), BitConverter.SingleToUInt32Bits(pParallel.PosY[i]), "Bit-identical parallel PosY");
            Assert.Equal(BitConverter.SingleToUInt32Bits(pSingle.PosZ[i]), BitConverter.SingleToUInt32Bits(pParallel.PosZ[i]), "Bit-identical parallel PosZ");
            Assert.Equal(pSingle.FrameIndex[i], pParallel.FrameIndex[i], "Bit-identical parallel FrameIndex");
        }

        JobSystem.Stop(jobs);
        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void OnStepCallStepAllReentrantly(uint userData, Vec4 value)
    {
        _ = userData;
        _ = value;
        s_reentrantStepCount++;
        if (s_reentrantTweens != null && s_reentrantTransforms != null && s_reentrantCameras != null)
            TweenSystem.StepAll(ref *s_reentrantTweens, ref *s_reentrantTransforms, ref *s_reentrantCameras, 0.25f);
    }

    private static void TestTweenAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 128, 32);

        // SIM-08: Chain cycle rejection (t1 -> t2 -> t1) & overwrite orphan release (t1 -> t2 then t1 -> t3)
        float* v1 = alloc.Persistent.Alloc<float>(1);
        float* v2 = alloc.Persistent.Alloc<float>(1);
        float* v3 = alloc.Persistent.Alloc<float>(1);
        var t1 = TweenSystem.TweenFloatPtr(ref world.Tweens, v1, 0f, 1f, 0.1f, EaseType.Linear);
        var t2 = TweenSystem.TweenFloatPtr(ref world.Tweens, v2, 0f, 1f, 0.1f, EaseType.Linear);
        TweenSystem.Chain(ref world.Tweens, t1, t2);
        TweenSystem.Chain(ref world.Tweens, t2, t1); // Cycle must be rejected!
        for (int s = 0; s < 5; s++)
            TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, 0.1f);
        Assert.Equal(0, world.Tweens.ActiveCount, "SIM-08: Cycle Chain(t2, t1) rejected so both t1 and t2 complete");

        var o1 = TweenSystem.TweenFloatPtr(ref world.Tweens, v1, 0f, 1f, 0.1f, EaseType.Linear);
        var o2 = TweenSystem.TweenFloatPtr(ref world.Tweens, v2, 0f, 1f, 0.1f, EaseType.Linear);
        var o3 = TweenSystem.TweenFloatPtr(ref world.Tweens, v3, 0f, 1f, 0.1f, EaseType.Linear);
        TweenSystem.Chain(ref world.Tweens, o1, o2);
        TweenSystem.Chain(ref world.Tweens, o1, o3); // Overwriting releases o2 from WaitingInSequence
        Assert.True(!TweenSystem.TryGetPtr(in world.Tweens, o2)->IsWaitingInSequence, "SIM-08: Overwritten o2 released from WaitingInSequence");
        for (int s = 0; s < 5; s++)
            TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, 0.1f);
        Assert.Equal(0, world.Tweens.ActiveCount, "SIM-08: Overwritten o2 not orphaned; all 3 complete");

        // SIM-09: NaN Duration/Delay/TimeScale sanitized, bad dt is a no-op, multi-pass PingPong overshoot
        *v1 = 0f;
        var nanTw = TweenSystem.TweenFloatPtr(ref world.Tweens, v1, 0f, 10f, float.NaN, EaseType.Linear, delay: float.NaN);
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, float.NaN);
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, -1f);
        Assert.True(TweenSystem.IsActive(in world.Tweens, nanTw), "SIM-09: NaN and negative dt are clean no-ops");
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, 0.01f);
        Assert.True(!TweenSystem.IsActive(in world.Tweens, nanTw), "SIM-09: Sanitized NaN Duration tween completes instead of leaking");
        Assert.Near(10f, *v1, 1e-4f, "SIM-09: Sanitized NaN Duration tween reaches EndValue");

        *v2 = 0f;
        var ppInf = TweenSystem.TweenFloatPtr(ref world.Tweens, v2, 0f, 10f, 0.5f, EaseType.Linear, loopMode: TweenLoopMode.PingPong, loops: -1);
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, 2.25f); // 4 full passes (even -> forward) + 0.25s = 5.0
        TweenData* ppPtr = TweenSystem.TryGetPtr(in world.Tweens, ppInf);
        Assert.True(ppPtr != null, "SIM-09: Infinite PingPong remains active after dt=2.25f");
        Assert.Near(0.25f, ppPtr->Elapsed, 1e-4f, "SIM-09: PingPong Elapsed wrapped to 0.25f");
        Assert.True(!ppPtr->IsReversed, "SIM-09: 4 full passes -> even parity -> not reversed");
        Assert.Near(5f, *v2, 1e-3f, "SIM-09: PingPong value at t=2.25s == 5.0");
        TweenSystem.Kill(ref world.Tweens, ppInf);

        *v3 = -1f;
        var ppFin = TweenSystem.TweenFloatPtr(ref world.Tweens, v3, 0f, 10f, 0.5f, EaseType.Linear, loopMode: TweenLoopMode.PingPong, loops: 1);
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, 2.25f);
        Assert.True(!TweenSystem.IsActive(in world.Tweens, ppFin), "SIM-09: Finite PingPong overshoot completes immediately");
        Assert.Near(0f, *v3, 1e-4f, "SIM-09: Finite 2-pass PingPong finishes at StartValue (0)");

        // SIM-17: Re-entrant StepAll inside OnStep callback must not double-step sibling tweens
        *v1 = 0f;
        s_reentrantStepCount = 0;
        var reCaller = TweenSystem.Spawn(ref world.Tweens, new TweenData
        {
            TargetKind = TweenTargetKind.CustomCallback,
            Duration = 1f,
            StartValue = Vec4.Zero,
            EndValue = Vec4.One,
            OnStep = &OnStepCallStepAllReentrantly,
        });
        var sibling = TweenSystem.TweenFloatPtr(ref world.Tweens, v1, 0f, 10f, 1f, EaseType.Linear);
        s_reentrantTweens = &world.Tweens;
        s_reentrantTransforms = &world.Transforms;
        s_reentrantCameras = &world.Cameras;
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, 0.25f);
        s_reentrantTweens = null;
        s_reentrantTransforms = null;
        s_reentrantCameras = null;
        Assert.Equal(1, s_reentrantStepCount, "SIM-17: Re-entrant StepAll ignored while Stepping != 0");
        Assert.Near(2.5f, *v1, 1e-4f, "SIM-17: Sibling stepped exactly once (2.5, not 5.0)");
        TweenSystem.Kill(ref world.Tweens, reCaller);
        TweenSystem.Kill(ref world.Tweens, sibling);

        // XMOD-06: Handle-safe Text & Sprite tweens survive swap-back compaction and auto-kill on target removal
        var eA = EntitySystem.Spawn(ref world.Entities);
        var eB = EntitySystem.Spawn(ref world.Entities);
        var eC = EntitySystem.Spawn(ref world.Entities);

        var tdA = RenderComponentSystem.DefaultHudText(eA, FontHandle.Create(1, 1), "TextA");
        tdA.TypewriterProgress = 0f;
        var tdB = RenderComponentSystem.DefaultHudText(eB, FontHandle.Create(1, 1), "TextB");
        tdB.TypewriterProgress = 0f;
        tdB.GlowIntensity = 0f;
        tdB.Color = ColorF.Black;
        TextHandle txtA = RenderComponentSystem.AddText(ref world.RenderComponents, in tdA);
        TextHandle txtB = RenderComponentSystem.AddText(ref world.RenderComponents, in tdB);

        SpriteHandle spA = RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = eA, Color = ColorF.Black, Visible = 1 });
        SpriteHandle spB = RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = eB, Color = ColorF.Black, Visible = 1 });

        TweenSystem.TextTypewriter(ref world.Tweens, txtB, 0f, 1f, 0.5f, EaseType.Linear);
        TweenSystem.TextGlowIntensity(ref world.Tweens, txtB, 0f, 2f, 0.5f, EaseType.Linear);
        TweenSystem.TextColor(ref world.Tweens, txtB, ColorF.Black, ColorF.White, 0.5f, EaseType.Linear);
        TweenSystem.SpriteColor(ref world.Tweens, spB, ColorF.Black, ColorF.Red, 0.5f, EaseType.Linear);

        // Remove A (forcing swap-back B -> dense 0), then spawn C into dense 1!
        Assert.True(RenderComponentSystem.RemoveText(ref world.RenderComponents, txtA), "XMOD-06: Remove Text A");
        Assert.True(RenderComponentSystem.RemoveSprite(ref world.RenderComponents, spA), "XMOD-06: Remove Sprite A");

        var tdC = RenderComponentSystem.DefaultHudText(eC, FontHandle.Create(1, 1), "TextC");
        tdC.TypewriterProgress = 0f;
        tdC.GlowIntensity = 0f;
        tdC.Color = ColorF.Black;
        TextHandle txtC = RenderComponentSystem.AddText(ref world.RenderComponents, in tdC);
        SpriteHandle spC = RenderComponentSystem.AddSprite(ref world.RenderComponents, new SpriteData { Entity = eC, Color = ColorF.Black, Visible = 1 });

        // Step via EngineWorldSystem.StepCoreSystems (wired to 5-arg TweenSystem.StepAll)
        alloc.BeginFrame();
        EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 0.5f);

        TextData* ptrB = RenderComponentSystem.TryGetText(in world.RenderComponents, txtB);
        TextData* ptrC = RenderComponentSystem.TryGetText(in world.RenderComponents, txtC);
        SpriteData* sPtrB = RenderComponentSystem.TryGetSprite(in world.RenderComponents, spB);
        SpriteData* sPtrC = RenderComponentSystem.TryGetSprite(in world.RenderComponents, spC);
        Assert.Near(1f, ptrB->TypewriterProgress, 1e-4f, "XMOD-06: Text B TypewriterProgress reached 1.0 after swap-back");
        Assert.Near(2f, ptrB->GlowIntensity, 1e-4f, "XMOD-06: Text B GlowIntensity reached 2.0 after swap-back");
        Assert.Near(1f, ptrB->Color.R, 1e-4f, "XMOD-06: Text B Color reached White");
        Assert.Near(1f, sPtrB->Color.R, 1e-4f, "XMOD-06: Sprite B Color reached Red");
        Assert.Near(0f, ptrC->TypewriterProgress, 1e-5f, "XMOD-06: Text C at former dense slot 1 remained 0.0 (no UAF corruption!)");
        Assert.Near(0f, sPtrC->Color.R, 1e-5f, "XMOD-06: Sprite C at former dense slot 1 remained Black");

        // Auto-kill when target component is destroyed
        var orphanTw = TweenSystem.TextTypewriter(ref world.Tweens, txtC, 0f, 1f, 2f, EaseType.Linear);
        var orphanSpTw = TweenSystem.SpriteColor(ref world.Tweens, spC, ColorF.Black, ColorF.White, 2f, EaseType.Linear);
        Assert.True(RenderComponentSystem.RemoveText(ref world.RenderComponents, txtC), "XMOD-06: Remove Text C");
        Assert.True(RenderComponentSystem.RemoveSprite(ref world.RenderComponents, spC), "XMOD-06: Remove Sprite C");
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, ref world.RenderComponents, 0.1f);
        Assert.True(!TweenSystem.IsActive(in world.Tweens, orphanTw), "XMOD-06: Destroyed Text tween auto-killed");
        Assert.True(!TweenSystem.IsActive(in world.Tweens, orphanSpTw), "XMOD-06: Destroyed Sprite tween auto-killed");
        Assert.Equal(0, world.Tweens.ActiveCount, "XMOD-06: Zero lingering tweens");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestAnimationAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(1024 * 1024, 128 * 1024, 128 * 1024, 64 * 1024);

        // SIM-20 (Anim): Capacity bounds validation
        int overMax = (int)HandlePacking.IndexMask + 2;
        bool capThrew = false;
        try { AnimationTable.Create(ref alloc.Persistent, maxSkeletons: 0); } catch (ArgumentOutOfRangeException) { capThrew = true; }
        Assert.True(capThrew, "SIM-20(Anim): Rejects maxSkeletons == 0");
        capThrew = false;
        try { AnimationTable.Create(ref alloc.Persistent, maxSpriteClips: overMax); } catch (ArgumentOutOfRangeException) { capThrew = true; }
        Assert.True(capThrew, "SIM-20(Anim): Rejects maxSpriteClips > IndexMask+1");

        var anims = AnimationTable.Create(ref alloc.Persistent, 16, 16, 32, 8, 32);

        // SIM-03: Negative JointCount & null buffers rejected by RegisterSkeleton, direct mutation guarded in StepAll
        bool negThrew = false;
        try { AnimationSystem.RegisterSkeleton(ref anims, new SkeletonData { JointCount = -1 }); }
        catch (ArgumentOutOfRangeException) { negThrew = true; }
        Assert.True(negThrew, "SIM-03: RegisterSkeleton rejects negative JointCount");

        bool nullThrew = false;
        try { AnimationSystem.RegisterSkeleton(ref anims, new SkeletonData { JointCount = 2 }); }
        catch (ArgumentNullException) { nullThrew = true; }
        Assert.True(nullThrew, "SIM-03: RegisterSkeleton rejects null joint buffers when JointCount > 0");

        int* parents = alloc.Persistent.Alloc<int>(2);
        parents[0] = -1; parents[1] = 0;
        Mat4* ibm = alloc.Persistent.Alloc<Mat4>(2);
        ibm[0] = Mat4.Identity; ibm[1] = Mat4.Identity;
        Vec3* bp = alloc.Persistent.Alloc<Vec3>(2);
        bp[0] = Vec3.Zero; bp[1] = new Vec3(0f, 1f, 0f);
        Quat* br = alloc.Persistent.Alloc<Quat>(2);
        br[0] = Quat.Identity; br[1] = Quat.Identity;
        Vec3* bs = alloc.Persistent.Alloc<Vec3>(2);
        bs[0] = Vec3.One; bs[1] = Vec3.One;

        var skelMut = AnimationSystem.RegisterSkeleton(ref anims, new SkeletonData
        {
            JointCount = 2,
            ParentIndices = parents,
            InverseBindMatrices = ibm,
            BindPositions = bp,
            BindRotations = br,
            BindScales = bs,
        });
        var entMut = EntityId.Create(1, 1);
        var pMut = AnimationSystem.AddPlayer(ref anims, entMut, skelMut, AnimClipHandle.Invalid);
        AnimationSystem.TryGetSkeleton(in anims, skelMut)->JointCount = -5; // Direct memory mutation guard
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
        Assert.Equal(0, anims.FrameJointPalette.Length, "SIM-03: Corrupted negative JointCount safely skipped in StepAll");
        AnimationSystem.TryGetSkeleton(in anims, skelMut)->JointCount = 2; // Restore for SIM-06

        // SIM-06: Undersized scratchArena throws OOM and rolls back Offset cleanly
        var tinyScratch = LinearArena.Create(64);
        tinyScratch.Alloc<int>(1); // Non-zero initial offset to verify exact rollback
        nuint offsetBefore = tinyScratch.Offset;
        bool scratchOom = false;
        alloc.BeginFrame();
        try { AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref tinyScratch); }
        catch (OutOfMemoryException) { scratchOom = true; }
        Assert.True(scratchOom, "SIM-06: Undersized scratchArena threw OutOfMemoryException");
        Assert.Equal(offsetBefore, tinyScratch.Offset, "SIM-06: scratchArena.Offset rolled back cleanly on OOM");
        tinyScratch.Dispose();
        AnimationSystem.RemovePlayer(ref anims, pMut);

        // SIM-16: Skeletal crossfade from clipA (animates joint 0) to clipB (omits joint 0) interpolates smoothly to bind pose
        float* times = alloc.Persistent.Alloc<float>(2);
        times[0] = 0f; times[1] = 1f;
        float* valsA = alloc.Persistent.Alloc<float>(6);
        valsA[0] = 10f; valsA[1] = 0f; valsA[2] = 0f;
        valsA[3] = 10f; valsA[4] = 0f; valsA[5] = 0f;
        var sampA = alloc.Persistent.Alloc<AnimSamplerData>(1);
        sampA[0] = new AnimSamplerData { Times = times, Values = valsA, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
        var chanA = alloc.Persistent.Alloc<AnimChannelData>(1);
        chanA[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 0, Path = AnimTargetPath.Translation };
        var clipA = AnimationSystem.RegisterClip(ref anims, new AnimClipData
        {
            Name = StringId.Hash("ClipA_J0"),
            Duration = 1f,
            Samplers = sampA, SamplerCount = 1,
            Channels = chanA, ChannelCount = 1,
        });

        float* valsB = alloc.Persistent.Alloc<float>(6);
        valsB[0] = 0f; valsB[1] = 4f; valsB[2] = 0f;
        valsB[3] = 0f; valsB[4] = 4f; valsB[5] = 0f;
        var sampB = alloc.Persistent.Alloc<AnimSamplerData>(1);
        sampB[0] = new AnimSamplerData { Times = times, Values = valsB, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
        var chanB = alloc.Persistent.Alloc<AnimChannelData>(1);
        chanB[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 1, Path = AnimTargetPath.Translation }; // No channel on joint 0!
        var clipB = AnimationSystem.RegisterClip(ref anims, new AnimClipData
        {
            Name = StringId.Hash("ClipB_J1Only"),
            Duration = 1f,
            Samplers = sampB, SamplerCount = 1,
            Channels = chanB, ChannelCount = 1,
        });

        var pFade = AnimationSystem.AddPlayer(ref anims, EntityId.Create(2, 1), skelMut, clipA);
        AnimationSystem.Crossfade(ref anims, pFade, clipB, 1.0f);
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.5f, ref alloc.Frame, ref alloc.Scratch);
        AnimPlayerData* fadePtr = AnimationSystem.TryGetPlayer(in anims, pFade);
        Mat4 j0Mid = anims.FrameJointPalette[fadePtr->FrameJointOffset + 0];
        Assert.Near(5f, j0Mid.C3.X, 1e-3f, "SIM-16: Joint 0 omitted by clipB blends smoothly from 10 toward bind pose 0 (midpoint == 5.0)");
        AnimationSystem.RemovePlayer(ref anims, pFade);

        // SIM-20 (Anim): UnregisterByBundle(BundleId.Invalid) preserves standalone skeletons & clips
        AnimationSystem.UnregisterByBundle(ref anims, BundleId.Invalid);
        Assert.True(AnimationSystem.TryGetSkeleton(in anims, skelMut) != null, "SIM-20(Anim): Standalone skeleton preserved on Invalid bundle unregister");
        Assert.True(AnimationSystem.TryGetClip(in anims, clipA) != null, "SIM-20(Anim): Standalone clip preserved on Invalid bundle unregister");

        // SIM-07 / XMOD-11: Cross-scene same-name SpriteAnimClipHandle independence + ABA rejection
        var scA = SceneId.Create(1, 1);
        var scB = SceneId.Create(2, 1);
        var sharedName = StringId.Hash("hero_run");
        var scClipA = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
        {
            Name = sharedName, OwnerScene = scA, StartFrame = 0, FrameCount = 4, Columns = 4, Rows = 1, Fps = 8f, Loop = 1,
        });
        var scClipB = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
        {
            Name = sharedName, OwnerScene = scB, StartFrame = 0, FrameCount = 4, Columns = 4, Rows = 1, Fps = 8f, Loop = 1,
        });
        var scClipADiffGrid = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
        {
            Name = sharedName, OwnerScene = scA, StartFrame = 0, FrameCount = 8, Columns = 8, Rows = 1, Fps = 8f, Loop = 1,
        });
        Assert.True(scClipA != scClipB, "SIM-07: Same-name clips in distinct scenes get distinct handles");
        Assert.True(scClipA != scClipADiffGrid, "SIM-07: Mismatched grid clips in same scene get distinct handles");

        var stalePlayer = AnimationSystem.AddSpritePlayer(ref anims, EntityId.Create(3, 1), scClipA, scene: scB);
        AnimationSystem.RemoveByScene(ref anims, scA);
        Assert.True(AnimationSystem.TryGetSpriteClip(in anims, scClipA) == null, "SIM-07: Unloaded sceneA clip invalidated");
        Assert.True(AnimationSystem.TryGetSpriteClip(in anims, scClipB) != null, "SIM-07: SceneB clip unaffected by sceneA unload");

        // Re-register in sceneB reusing scClipA's slot index -> new generation rejects stale scClipA (ABA guard!)
        var reusedClip = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
        {
            Name = StringId.Hash("reused_slot"), OwnerScene = scB, StartFrame = 2, FrameCount = 2, Columns = 4, Rows = 1, Fps = 4f, Loop = 1,
        });
        Assert.Equal(scClipA.Index, reusedClip.Index, "SIM-07: Freed sprite clip slot reused");
        Assert.True(reusedClip.Generation != scClipA.Generation, "SIM-07: Generation bumped on slot reuse");
        Assert.True(AnimationSystem.TryGetSpriteClip(in anims, scClipA) == null, "SIM-07: Stale handle rejected after slot reuse (ABA safe)");

        // SIM-15: Large/NaN Time, bad dt, and negative StartFrame produce deterministic non-negative frame & UVs
        var negStartClip = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
        {
            Name = StringId.Hash("neg_start"), OwnerScene = scB, StartFrame = -2, FrameCount = 4, Columns = 4, Rows = 2, Fps = 8f, Loop = 1,
        });
        var spExtreme = AnimationSystem.AddSpritePlayer(ref anims, EntityId.Create(4, 1), negStartClip, scene: scB);
        SpriteAnimPlayerData* spExtPtr = AnimationSystem.TryGetSpritePlayer(in anims, spExtreme);

        spExtPtr->Time = 1e12f;
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
        Assert.True(spExtPtr->CurrentFrame >= 0 && spExtPtr->CurrentFrame < 4, "SIM-15: Time=1e12f yields deterministic non-negative frame");
        Assert.True(float.IsFinite(spExtPtr->CurrentUvRect.X) && spExtPtr->CurrentUvRect.X >= 0f && spExtPtr->CurrentUvRect.Y >= 0f, "SIM-15: Time=1e12f yields valid non-negative UVs");

        spExtPtr->Time = float.NaN;
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, float.NaN, ref alloc.Frame, ref alloc.Scratch);
        Assert.True(float.IsFinite(spExtPtr->Time), "SIM-15: NaN Time sanitized");
        Assert.True(spExtPtr->CurrentFrame >= 0, "SIM-15: NaN Time + NaN dt + negative StartFrame yields frame >= 0");
        Assert.True(spExtPtr->CurrentUvRect.X >= 0f && spExtPtr->CurrentUvRect.Y >= 0f, "SIM-15: Non-negative UVs after NaN");

        AnimationSystem.RemoveSpritePlayer(ref anims, stalePlayer);
        AnimationSystem.RemoveByScene(ref anims, scB);

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestParticleAuditRegressions()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var transforms = TransformTable.Create(ref alloc.Persistent, 16, 16);
        var pool = ParticleTable.Create(ref alloc.Persistent, 32, 8);

        // SIM-12: Manual em->Position movement on WorldSpace=0, FollowEntity=Invalid shifts live particle by exact delta
        var cfg = ParticleSystem.DefaultEmitter(new Vec3(1f, 2f, 3f), seed: 55);
        cfg.WorldSpace = 0;
        cfg.FollowEntity = EntityId.Invalid;
        cfg.Shape = ParticleShape.Point;
        cfg.EmitRate = 0f;
        cfg.Gravity = Vec3.Zero;
        cfg.SpeedMin = 0f; cfg.SpeedMax = 0f;
        cfg.Turbulence = 0f;
        cfg.LifetimeMin = 10f; cfg.LifetimeMax = 10f;
        var emH = ParticleSystem.CreateEmitter(ref pool, in cfg);
        ParticleSystem.Burst(ref pool, emH, 1);
        ParticleSystem.Step(ref pool, in transforms, 0.01f);
        Assert.Equal(1, pool.ParticleCount, "SIM-12: 1 local particle spawned");
        float x0 = pool.PosX[0], y0 = pool.PosY[0], z0 = pool.PosZ[0];

        ParticleEmitterData* emPtr = ParticleSystem.TryGetEmitter(in pool, emH);
        emPtr->Position = new Vec3(11f, 7f, 1f); // delta = (+10, +5, -2)
        ParticleSystem.Step(ref pool, in transforms, 0.01f);
        Assert.Near(x0 + 10f, pool.PosX[0], 1e-4f, "SIM-12: Manual em->Position move shifted particle X by +10");
        Assert.Near(y0 + 5f, pool.PosY[0], 1e-4f, "SIM-12: Manual em->Position move shifted particle Y by +5");
        Assert.Near(z0 - 2f, pool.PosZ[0], 1e-4f, "SIM-12: Manual em->Position move shifted particle Z by -2");

        // SIM-13: Negative / NaN dt is a complete no-op
        float ageBefore = pool.Age[0];
        float pxBefore = pool.PosX[0];
        ParticleSystem.Step(ref pool, in transforms, -1f);
        ParticleSystem.Step(ref pool, in transforms, float.NaN);
        Assert.Equal(1, pool.ParticleCount, "SIM-13: Negative/NaN dt preserves particle count");
        Assert.Equal(ageBefore, pool.Age[0], "SIM-13: Negative/NaN dt does not alter age");
        Assert.Equal(pxBefore, pool.PosX[0], "SIM-13: Negative/NaN dt does not alter position");

        // SIM-13: AtlasColumns * AtlasRows > 256 clamps FrameIndex at 255 instead of wrapping modulo 256
        emPtr->AtlasColumns = 20;
        emPtr->AtlasRows = 20; // 400 frames > 256
        pool.Age[0] = 9.5f;
        pool.Lifetime[0] = 10f;
        ParticleSystem.Step(ref pool, in transforms, 0.1f); // age=9.6, norm=0.96 -> frame=384 -> clamped to 255!
        Assert.Equal((byte)255, pool.FrameIndex[0], "SIM-13: AtlasColumns*AtlasRows > 256 clamps FrameIndex to 255");

        // SIM-13: EmitRate = 1e38f with dt = 10f overflows float to Inf, then normal EmitRate resumes emission cleanly
        ParticleSystem.DestroyEmitter(ref pool, emH);
        var ovCfg = ParticleSystem.DefaultEmitter(Vec3.Zero, seed: 88);
        ovCfg.EmitRate = 1e38f;
        ovCfg.LifetimeMin = 0.05f;
        ovCfg.LifetimeMax = 0.05f;
        var ovH = ParticleSystem.CreateEmitter(ref pool, in ovCfg);
        ParticleSystem.Step(ref pool, in transforms, 10f); // fills pool cleanly; particles also expire at dt=10f > 0.05f
        ParticleEmitterData* ovPtr = ParticleSystem.TryGetEmitter(in pool, ovH);
        Assert.True(float.IsFinite(ovPtr->EmitAccumulator), "SIM-13: EmitAccumulator remains finite after 1e38f*10f overflow");
        ovPtr->EmitRate = 100f;
        ovPtr->LifetimeMin = 5f;
        ovPtr->LifetimeMax = 5f;
        ParticleSystem.Step(ref pool, in transforms, 0.1f); // should emit ~10 new particles
        Assert.True(pool.ParticleCount >= 5, "SIM-13: Emission resumed cleanly after Inf overflow recovery");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestAnimationMultiTrackBlending()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 128 * 1024, 256 * 1024, 64 * 1024);
        var anims = AnimationTable.Create(ref alloc.Persistent, 16, 32, 32, 16, 32);

        try
        {
            // 3-joint skeleton: j0=root/lower, j1=lower child, j2=upper arm (parents: -1, 0, 0)
            int* parents = alloc.Persistent.Alloc<int>(3);
            parents[0] = -1; parents[1] = 0; parents[2] = 0;
            Mat4* ibm = alloc.Persistent.Alloc<Mat4>(3);
            ibm[0] = Mat4.Identity; ibm[1] = Mat4.Identity; ibm[2] = Mat4.Identity;
            Vec3* bp = alloc.Persistent.Alloc<Vec3>(3);
            bp[0] = Vec3.Zero; bp[1] = new Vec3(0f, 1f, 0f); bp[2] = new Vec3(0f, 2f, 0f);
            Quat* br = alloc.Persistent.Alloc<Quat>(3);
            br[0] = Quat.Identity; br[1] = Quat.Identity; br[2] = Quat.Identity;
            Vec3* bs = alloc.Persistent.Alloc<Vec3>(3);
            bs[0] = Vec3.One; bs[1] = Vec3.One; bs[2] = Vec3.One;

            var skel = AnimationSystem.RegisterSkeleton(ref anims, new SkeletonData
            {
                JointCount = 3,
                ParentIndices = parents,
                InverseBindMatrices = ibm,
                BindPositions = bp,
                BindRotations = br,
                BindScales = bs,
            });

            static AnimClipHandle MakeConstPos3Clip(ref LinearArena arena, ref AnimationTable tbl, string name, float dur, Vec3 j0, Vec3 j1, Vec3 j2)
            {
                float* t = arena.Alloc<float>(2);
                t[0] = 0f; t[1] = dur;
                var samps = arena.Alloc<AnimSamplerData>(3);
                var chans = arena.Alloc<AnimChannelData>(3);
                Vec3* pts = stackalloc Vec3[3] { j0, j1, j2 };
                for (int k = 0; k < 3; k++)
                {
                    float* v = arena.Alloc<float>(6);
                    v[0] = pts[k].X; v[1] = pts[k].Y; v[2] = pts[k].Z;
                    v[3] = pts[k].X; v[4] = pts[k].Y; v[5] = pts[k].Z;
                    samps[k] = new AnimSamplerData { Times = t, Values = v, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
                    chans[k] = new AnimChannelData { SamplerIndex = k, TargetJointIndex = k, Path = AnimTargetPath.Translation };
                }
                return AnimationSystem.RegisterClip(ref tbl, new AnimClipData
                {
                    Name = StringId.Hash(name), Duration = dur, Samplers = samps, SamplerCount = 3, Channels = chans, ChannelCount = 3,
                });
            }

            static AnimClipHandle MakeLinearXClip(ref LinearArena arena, ref AnimationTable tbl, string name, float dur, float x0, float x1)
            {
                float* t = arena.Alloc<float>(2);
                t[0] = 0f; t[1] = dur;
                float* v = arena.Alloc<float>(6);
                v[0] = x0; v[1] = 0f; v[2] = 0f;
                v[3] = x1; v[4] = 0f; v[5] = 0f;
                var samp = arena.Alloc<AnimSamplerData>(1);
                samp[0] = new AnimSamplerData { Times = t, Values = v, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
                var chan = arena.Alloc<AnimChannelData>(1);
                chan[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 0, Path = AnimTargetPath.Translation };
                return AnimationSystem.RegisterClip(ref tbl, new AnimClipData
                {
                    Name = StringId.Hash(name), Duration = dur, Samplers = samp, SamplerCount = 1, Channels = chan, ChannelCount = 1,
                });
            }

            var clipWalk = MakeConstPos3Clip(ref alloc.Persistent, ref anims, "walk", 1.0f, new Vec3(2f, 0f, 0f), new Vec3(0f, 3f, 0f), new Vec3(0f, 2f, 0f));
            var clipAttack = MakeConstPos3Clip(ref alloc.Persistent, ref anims, "attack", 0.5f, new Vec3(99f, 0f, 0f), new Vec3(0f, 99f, 0f), new Vec3(5f, 2f, 0f));
            var clipOneShot = MakeLinearXClip(ref alloc.Persistent, ref anims, "oneshot", 0.15f, 8f, 8f);
            var clipIdleTime = MakeLinearXClip(ref alloc.Persistent, ref anims, "idle_t", 1.0f, 0f, 10f);
            var clipRunTime = MakeLinearXClip(ref alloc.Persistent, ref anims, "run_t", 1.0f, 10f, 20f);

            // Additive recoil clip on j2: pos=(0, 2.5, 0) [+0.5 Y delta], rot=+30 deg Z, scale=(2, 1, 1)
            float* aTimes = alloc.Persistent.Alloc<float>(2);
            aTimes[0] = 0f; aTimes[1] = 1f;
            float* aPos = alloc.Persistent.Alloc<float>(6);
            aPos[0] = 0f; aPos[1] = 2.5f; aPos[2] = 0f; aPos[3] = 0f; aPos[4] = 2.5f; aPos[5] = 0f;
            Quat q30 = Quat.FromAxisAngle(Vec3.UnitZ, 30f * DetMath.Deg2Rad);
            float* aRot = alloc.Persistent.Alloc<float>(8);
            aRot[0] = q30.X; aRot[1] = q30.Y; aRot[2] = q30.Z; aRot[3] = q30.W;
            aRot[4] = q30.X; aRot[5] = q30.Y; aRot[6] = q30.Z; aRot[7] = q30.W;
            float* aScl = alloc.Persistent.Alloc<float>(6);
            aScl[0] = 2f; aScl[1] = 1f; aScl[2] = 1f; aScl[3] = 2f; aScl[4] = 1f; aScl[5] = 1f;
            var aSamps = alloc.Persistent.Alloc<AnimSamplerData>(3);
            aSamps[0] = new AnimSamplerData { Times = aTimes, Values = aPos, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
            aSamps[1] = new AnimSamplerData { Times = aTimes, Values = aRot, KeyframeCount = 2, ComponentsPerValue = 4, Interpolation = AnimInterpolation.Linear };
            aSamps[2] = new AnimSamplerData { Times = aTimes, Values = aScl, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
            var aChans = alloc.Persistent.Alloc<AnimChannelData>(3);
            aChans[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 2, Path = AnimTargetPath.Translation };
            aChans[1] = new AnimChannelData { SamplerIndex = 1, TargetJointIndex = 2, Path = AnimTargetPath.Rotation };
            aChans[2] = new AnimChannelData { SamplerIndex = 2, TargetJointIndex = 2, Path = AnimTargetPath.Scale };
            var clipAdditive = AnimationSystem.RegisterClip(ref anims, new AnimClipData
            {
                Name = StringId.Hash("add_recoil"), Duration = 1f, Samplers = aSamps, SamplerCount = 3, Channels = aChans, ChannelCount = 3,
            });

            var pH = AnimationSystem.AddPlayer(ref anims, EntityId.Create(1, 1), skel, clipWalk);

            // Guard checks for invalid track/clip/handle
            Assert.True(!AnimationSystem.SetTrack(ref anims, pH, -1, clipAttack), "Negative track rejected");
            Assert.True(!AnimationSystem.SetTrack(ref anims, pH, AnimationSystem.TrackCount, clipAttack), "Out-of-range track rejected");
            Assert.True(!AnimationSystem.SetTrack(ref anims, pH, 1, AnimClipHandle.Invalid), "Invalid clip rejected");

            // 1. Multi-track override with JointMask (upper body j2 only; j0 & j1 strictly equal to Track 0!)
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 1, clipAttack, loop: false, crossfadeDuration: 0f, speed: 1f, weight: 1f), "SetTrack 1 attack");
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 1, maskLo: 1UL << 2);

            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.25f, ref alloc.Frame, ref alloc.Scratch);
            AnimPlayerData* pPtr = AnimationSystem.TryGetPlayer(in anims, pH);
            Mat4 m0 = anims.FrameJointPalette[pPtr->FrameJointOffset + 0];
            Mat4 m1 = anims.FrameJointPalette[pPtr->FrameJointOffset + 1];
            Mat4 m2 = anims.FrameJointPalette[pPtr->FrameJointOffset + 2];
            Assert.Near(2f, m0.C3.X, 1e-4f, "Masked Track 1 leaves j0 strictly equal to Track 0 (X=2, not 99)");
            Assert.Near(3f, m1.C3.Y, 1e-4f, "Masked Track 1 leaves j1 strictly equal to Track 0 (Y=3, not 99)");
            Assert.Near(7f, m2.C3.X, 1e-4f, "Masked Track 1 overrides j2 local X=5 on top of parent j0 X=2 => 7");

            // 2. Additive 3D layer on Track 2 (delta pos + delta rot + scale ratio at weight=0.5)
            AnimationSystem.ClearTrack(ref anims, pH, track: 1, fadeOutDuration: 0f);
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 2, clipAdditive, loop: true, crossfadeDuration: 0f, speed: 1f, weight: 0.5f, additive: true), "SetTrack 2 additive");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            m2 = anims.FrameJointPalette[pPtr->FrameJointOffset + 2];
            Assert.Near(2.25f, m2.C3.Y, 1e-3f, "Additive translation delta (+0.5 * 0.5 = +0.25 -> Y=2.25)");
            Assert.Near(DetMath.Cos(15f * DetMath.Deg2Rad) * 1.5f, m2.C0.X, 1e-3f, "Additive rot (+15 deg Z) * scale (1.5x) C0.X");
            Assert.Near(DetMath.Sin(15f * DetMath.Deg2Rad) * 1.5f, m2.C0.Y, 1e-3f, "Additive rot (+15 deg Z) * scale (1.5x) C0.Y");
            AnimationSystem.ClearTrack(ref anims, pH, track: 2, fadeOutDuration: 0f);

            // 3. Continuous 2-clip BlendTwoClips sweep (0.0 -> 0.5 -> 1.0) preserving both clip clocks
            Assert.True(AnimationSystem.BlendTwoClips(ref anims, pH, clipIdleTime, clipRunTime, 0.0f), "BlendTwoClips w=0");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.2f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(0.2f, pPtr->Time, 1e-4f, "BlendTwoClips Track 0 clock advanced to 0.2");
            Assert.Near(0.2f, pPtr->ExtraTracks[0].Time, 1e-4f, "BlendTwoClips Track 1 clock advanced to 0.2");
            Assert.Near(2.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "BlendTwoClips w=0.0 matches clipA (2.0)");

            Assert.True(AnimationSystem.BlendTwoClips(ref anims, pH, clipIdleTime, clipRunTime, 0.5f), "BlendTwoClips w=0.5");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.2f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(0.4f, pPtr->Time, 1e-4f, "BlendTwoClips preserved Track 0 clock (0.4)");
            Assert.Near(0.4f, pPtr->ExtraTracks[0].Time, 1e-4f, "BlendTwoClips preserved Track 1 clock (0.4)");
            Assert.Near(9.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "BlendTwoClips w=0.5 == Lerp(4, 14, 0.5) == 9.0");

            Assert.True(AnimationSystem.BlendTwoClips(ref anims, pH, clipIdleTime, clipRunTime, 1.0f), "BlendTwoClips w=1.0");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(0.5f, pPtr->Time, 1e-4f, "BlendTwoClips preserved Track 0 clock (0.5)");
            Assert.Near(15.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "BlendTwoClips w=1.0 matches clipB (15.0)");
            AnimationSystem.ClearTrack(ref anims, pH, track: 1, fadeOutDuration: 0f);

            // 4. Independent FromLoop (true) vs Loop (false) crossfade + smooth ClearTrack fade-out
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipWalk, loop: true, crossfadeDuration: 0f), "Reset Track 0 to looping clipWalk");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.9f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(0.9f, pPtr->Time, 1e-4f, "Looping Track 0 at t=0.9");

            AnimationSystem.Crossfade(ref anims, pH, clipOneShot, duration: 0.4f, nextLoop: false);
            Assert.Equal((byte)1, pPtr->Track0FromLoop, "Track0FromLoop preserved looping source (1)");
            Assert.Equal((byte)0, pPtr->Loop, "Track0 target Loop set to non-looping (0)");

            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.2f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(0.1f, pPtr->Time, 1e-3f, "Source looping clip wrapped 0.9+0.2 -> 0.1 during crossfade");
            Assert.Near(0.15f, pPtr->NextTime, 1e-4f, "Target non-looping clip clamped to Duration=0.15 during crossfade");
            Assert.Near(5.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "Crossfade midpoint Lerp(2, 8, 0.5) == 5.0");

            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.25f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Equal(clipOneShot, pPtr->Clip, "Crossfade promoted clipOneShot");
            Assert.Equal((byte)0, pPtr->Loop, "Promoted Track 0 stays non-looping");
            Assert.Near(8.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "Completed crossfade reaches clipOneShot X=8.0");

            // Smooth fade-out on Track 1 & Track 0
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 1, clipAttack, loop: true, crossfadeDuration: 0f, weight: 1f), "Activate Track 1 for fade-out");
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 1, maskLo: 1UL << 2);
            AnimationSystem.SetTrackSpeed(ref anims, pH, track: 1, 2.0f);
            AnimationSystem.SetTrackWeight(ref anims, pH, track: 1, 0.8f);
            AnimationSystem.ClearTrack(ref anims, pH, track: 1, fadeOutDuration: 0.2f);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Equal((byte)1, pPtr->ExtraTracks[0].Active, "Track 1 still active halfway through fade-out");
            Assert.Near(0.4f, pPtr->ExtraTracks[0].Weight, 1e-4f, "Track 1 weight decayed to 0.4 at halfway");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Equal((byte)0, pPtr->ExtraTracks[0].Active, "Track 1 deactivated cleanly at fade-out completion");
            Assert.Near(0f, pPtr->ExtraTracks[0].Weight, 1e-5f, "Track 1 weight reached exact 0.0");

            // Track 0 smooth fade-out returns to bind pose
            AnimationSystem.ClearTrack(ref anims, pH, track: 0, fadeOutDuration: 0.2f);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(4.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "Track 0 fade-out halfway blends to bind pose (4.0)");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Equal((byte)0, pPtr->Track0Active, "Track 0 deactivated after fade-out");
            Assert.Near(0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-4f, "Track 0 deactivated returns cleanly to bind pose (0.0)");

            // 4b. Regression (F1): Masked rotation-only Track 1 preserves Track 0 Translation on joint 2
            float* trTimes = alloc.Persistent.Alloc<float>(2);
            trTimes[0] = 0f; trTimes[1] = 1f;
            float* baseJ2Pos = alloc.Persistent.Alloc<float>(6);
            baseJ2Pos[0] = 3f; baseJ2Pos[1] = 4f; baseJ2Pos[2] = 0f;
            baseJ2Pos[3] = 3f; baseJ2Pos[4] = 4f; baseJ2Pos[5] = 0f;
            float* baseJ2Rot = alloc.Persistent.Alloc<float>(8);
            baseJ2Rot[0] = 0f; baseJ2Rot[1] = 0f; baseJ2Rot[2] = 0f; baseJ2Rot[3] = 1f;
            baseJ2Rot[4] = 0f; baseJ2Rot[5] = 0f; baseJ2Rot[6] = 0f; baseJ2Rot[7] = 1f;
            var baseSamps = alloc.Persistent.Alloc<AnimSamplerData>(2);
            baseSamps[0] = new AnimSamplerData { Times = trTimes, Values = baseJ2Pos, KeyframeCount = 2, ComponentsPerValue = 3, Interpolation = AnimInterpolation.Linear };
            baseSamps[1] = new AnimSamplerData { Times = trTimes, Values = baseJ2Rot, KeyframeCount = 2, ComponentsPerValue = 4, Interpolation = AnimInterpolation.Linear };
            var baseChans = alloc.Persistent.Alloc<AnimChannelData>(2);
            baseChans[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 2, Path = AnimTargetPath.Translation };
            baseChans[1] = new AnimChannelData { SamplerIndex = 1, TargetJointIndex = 2, Path = AnimTargetPath.Rotation };
            var clipBaseTR = AnimationSystem.RegisterClip(ref anims, new AnimClipData
            {
                Name = StringId.Hash("base_j2_tr"), Duration = 1f, Samplers = baseSamps, SamplerCount = 2, Channels = baseChans, ChannelCount = 2,
            });

            Quat q90 = Quat.FromAxisAngle(Vec3.UnitZ, 90f * DetMath.Deg2Rad);
            float* maskJ2Rot = alloc.Persistent.Alloc<float>(8);
            maskJ2Rot[0] = q90.X; maskJ2Rot[1] = q90.Y; maskJ2Rot[2] = q90.Z; maskJ2Rot[3] = q90.W;
            maskJ2Rot[4] = q90.X; maskJ2Rot[5] = q90.Y; maskJ2Rot[6] = q90.Z; maskJ2Rot[7] = q90.W;
            var maskSamps = alloc.Persistent.Alloc<AnimSamplerData>(1);
            maskSamps[0] = new AnimSamplerData { Times = trTimes, Values = maskJ2Rot, KeyframeCount = 2, ComponentsPerValue = 4, Interpolation = AnimInterpolation.Linear };
            var maskChans = alloc.Persistent.Alloc<AnimChannelData>(1);
            maskChans[0] = new AnimChannelData { SamplerIndex = 0, TargetJointIndex = 2, Path = AnimTargetPath.Rotation };
            var clipMaskRotOnly = AnimationSystem.RegisterClip(ref anims, new AnimClipData
            {
                Name = StringId.Hash("mask_j2_rot_only"), Duration = 1f, Samplers = maskSamps, SamplerCount = 1, Channels = maskChans, ChannelCount = 1,
            });

            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipBaseTR, loop: true, crossfadeDuration: 0f), "F1: SetTrack 0 T+R");
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 1, clipMaskRotOnly, loop: true, crossfadeDuration: 0f, weight: 1f), "F1: SetTrack 1 R-only");
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 1, maskLo: 1UL << 2);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            m2 = anims.FrameJointPalette[pPtr->FrameJointOffset + 2];
            Assert.Near(3f, m2.C3.X, 1e-4f, "F1: Masked R-only Track 1 keeps Track 0 Translation X (3, not bind 0)");
            Assert.Near(4f, m2.C3.Y, 1e-4f, "F1: Masked R-only Track 1 keeps Track 0 Translation Y (4, not bind 2)");
            Assert.Near(0f, m2.C0.X, 1e-3f, "F1: Masked R-only Track 1 applies +90 deg Z Rotation C0.X");
            Assert.Near(1f, m2.C0.Y, 1e-3f, "F1: Masked R-only Track 1 applies +90 deg Z Rotation C0.Y");
            AnimationSystem.ClearTrack(ref anims, pH, track: 1, fadeOutDuration: 0f);

            // 4c. Regression (F2 & F5): Track 0 smooth fade-out clears mask, additive & clocks; zero-weight ClearTrack deactivates immediately
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipWalk, loop: true, crossfadeDuration: 0f, weight: 1f, additive: true), "F2: SetTrack 0 additive");
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 0, maskLo: 1UL << 2, maskHi: 1UL << 3);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.25f, ref alloc.Frame, ref alloc.Scratch);
            Assert.True(pPtr->Time > 0.1f, "F2: Track 0 clock advanced before fade-out");
            AnimationSystem.ClearTrack(ref anims, pH, track: 0, fadeOutDuration: 0.1f);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.15f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Equal((byte)0, pPtr->Track0Active, "F2: Track0Active cleared on fade-out completion");
            Assert.Equal((byte)0, pPtr->Track0UseJointMask, "F2: Track0UseJointMask cleared on fade-out completion");
            Assert.Equal(0UL, pPtr->Track0JointMaskLo, "F2: Track0JointMaskLo cleared on fade-out completion");
            Assert.Equal(0UL, pPtr->Track0JointMaskHi, "F2: Track0JointMaskHi cleared on fade-out completion");
            Assert.Equal((byte)0, pPtr->Track0Additive, "F2: Track0Additive cleared on fade-out completion");
            Assert.Near(0f, pPtr->Time, 1e-5f, "F2: Track 0 Time reset to 0 on fade-out completion");
            Assert.Near(0f, pPtr->NextTime, 1e-5f, "F2: Track 0 NextTime reset to 0 on fade-out completion");

            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 1, clipAttack, loop: true, crossfadeDuration: 0f, weight: 1f), "F5: SetTrack 1");
            AnimationSystem.SetTrackWeight(ref anims, pH, track: 1, 0f);
            AnimationSystem.ClearTrack(ref anims, pH, track: 1, fadeOutDuration: 0.25f);
            Assert.Equal((byte)0, pPtr->ExtraTracks[0].Active, "F5: Zero-weight Track 1 deactivates immediately on ClearTrack(fadeOutDuration > 0)");
            Assert.Near(0f, pPtr->ExtraTracks[0].Weight, 1e-5f, "F5: Zero-weight Track 1 Weight == 0");

            // 4d. Regression (F3): BlendTwoClips clears pre-existing masks on both Track 0 and Track 1 (even when Track 1 already plays clipB)
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipIdleTime, loop: true, crossfadeDuration: 0f), "F3: SetTrack 0 clipIdleTime");
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 0, maskLo: 1UL << 2);
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 1, clipRunTime, loop: true, crossfadeDuration: 0f, weight: 1f), "F3: SetTrack 1 clipRunTime");
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 1, maskLo: 1UL << 2);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);

            Assert.True(AnimationSystem.BlendTwoClips(ref anims, pH, clipIdleTime, clipRunTime, 0.5f), "F3: BlendTwoClips with pre-masked tracks");
            Assert.Equal((byte)0, pPtr->Track0UseJointMask, "F3: BlendTwoClips cleared Track0UseJointMask");
            Assert.Equal(0UL, pPtr->Track0JointMaskLo, "F3: BlendTwoClips cleared Track0JointMaskLo");
            Assert.Equal((byte)0, pPtr->ExtraTracks[0].UseJointMask, "F3: BlendTwoClips cleared Track 1 UseJointMask");
            Assert.Equal(0UL, pPtr->ExtraTracks[0].JointMaskLo, "F3: BlendTwoClips cleared Track 1 JointMaskLo");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(7.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "F3: Unmasked j0 blends Lerp(2, 12, 0.5) == 7.0");
            AnimationSystem.ClearTrack(ref anims, pH, track: 1, fadeOutDuration: 0f);

            // 4e. Regression (F4): Track 0 re-crossfade back to clipA mid-transition blends smoothly instead of snapping + dead NextClip cleanup
            var clipReA = MakeLinearXClip(ref alloc.Persistent, ref anims, "recf_a", 1.0f, 2f, 2f);
            var clipReB = MakeLinearXClip(ref alloc.Persistent, ref anims, "recf_b", 1.0f, 10f, 10f);
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipReA, loop: true, crossfadeDuration: 0f), "F4: Start clipReA");
            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipReB, loop: true, crossfadeDuration: 0.4f), "F4: Crossfade clipReA -> clipReB");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.3f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(8.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "F4: 75% into clipReA->clipReB == 8.0");

            Assert.True(AnimationSystem.SetTrack(ref anims, pH, track: 0, clipReA, loop: true, crossfadeDuration: 0.2f), "F4: Re-crossfade back to clipReA");
            Assert.Equal(clipReB, pPtr->Clip, "F4: Promoted clipReB to source Clip");
            Assert.Equal(clipReA, pPtr->NextClip, "F4: Queued clipReA as NextClip");
            Assert.Near(0.2f, pPtr->CrossfadeDuration, 1e-5f, "F4: CrossfadeDuration == 0.2f");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Near(6.0f, anims.FrameJointPalette[pPtr->FrameJointOffset].C3.X, 1e-3f, "F4: Mid-re-crossfade Lerp(10, 2, 0.5) == 6.0 (no snap!)");
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.15f, ref alloc.Frame, ref alloc.Scratch);
            Assert.Equal(clipReA, pPtr->Clip, "F4: Completed re-crossfade to clipReA");
            Assert.True(!pPtr->NextClip.IsValid, "F4: NextClip cleared after re-crossfade");

            var deadBundle = BundleId.Create(77, 1);
            var deadClip = AnimationSystem.RegisterClip(ref anims, new AnimClipData
            {
                Name = StringId.Hash("dead_next"), OwnerBundle = deadBundle, Duration = 1f,
            });
            AnimationSystem.Crossfade(ref anims, pH, deadClip, 0.5f);
            AnimationSystem.UnregisterByBundle(ref anims, deadBundle);
            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 0.05f, ref alloc.Frame, ref alloc.Scratch);
            Assert.True(!pPtr->NextClip.IsValid, "F4: Dead unloaded NextClip cleared in StepAll");
            Assert.Near(0f, pPtr->CrossfadeDuration, 1e-5f, "F4: CrossfadeDuration cleared when NextClip unloaded");

            // 5. 60-tick hot-loop 0 GC check with active multi-track crossfade + mask + additive
            AnimationSystem.SetTrack(ref anims, pH, track: 0, clipWalk, loop: true, crossfadeDuration: 0f);
            AnimationSystem.Crossfade(ref anims, pH, clipRunTime, duration: 5.0f, nextLoop: true);
            AnimationSystem.SetTrack(ref anims, pH, track: 1, clipAttack, loop: true, crossfadeDuration: 0f, weight: 0.7f);
            AnimationSystem.SetTrackJointMask(ref anims, pH, track: 1, maskLo: 1UL << 2);
            AnimationSystem.SetTrack(ref anims, pH, track: 2, clipAdditive, loop: true, crossfadeDuration: 0f, weight: 0.4f, additive: true);
            AnimationSystem.ClearTrackJointMask(ref anims, pH, track: 2);

            alloc.BeginFrame();
            AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                alloc.BeginFrame();
                AnimationSystem.StepAll(ref anims, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "60 ticks of multi-track 3D skeletal blending allocate 0 GC bytes");
        }
        finally
        {
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after 3D multi-track blending tests");
    }

    private static void TestAtlasSpriteFlipbooks()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 128 * 1024, 256 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);

        try
        {
            var region = new SpriteRegionData
            {
                Name = StringId.Hash("fx/coin_2x2"),
                AtlasTexture = TextureHandle.Create(1, 1),
                UvRect = new Vec4(0.25f, 0.50f, 0.50f, 0.25f),
                PixelSize = new Vec2(128f, 128f),
                Pivot = new Vec2(0.5f, 0.5f),
                Valid = 1,
            };

            SpriteAnimClipHandle ppClip = AnimationSystem.RegisterSpriteClipFromRegion(
                ref world.Animations, StringId.Hash("coin_pp"), in region,
                columns: 2, rows: 2, fps: 4f, loopMode: SpriteAnimLoopMode.PingPong);
            SpriteAnimClipHandle ppDup = AnimationSystem.RegisterSpriteClipFromRegion(
                ref world.Animations, StringId.Hash("coin_pp"), in region,
                columns: 2, rows: 2, fps: 4f, loopMode: SpriteAnimLoopMode.PingPong);
            Assert.Equal(ppClip, ppDup, "RegisterSpriteClipFromRegion deduplicates identical clip");

            var altRegion = region;
            altRegion.UvRect = new Vec4(0f, 0f, 0.5f, 0.5f);
            SpriteAnimClipHandle diffUvClip = AnimationSystem.RegisterSpriteClipFromRegion(
                ref world.Animations, StringId.Hash("coin_pp"), in altRegion,
                columns: 2, rows: 2, fps: 4f, loopMode: SpriteAnimLoopMode.PingPong);
            Assert.True(diffUvClip != ppClip, "Distinct BaseUvRect gets distinct SpriteAnimClipHandle");

            // Regression (F6): same-name sprite clips with different Fps get distinct handles
            SpriteAnimClipHandle diffFpsClip = AnimationSystem.RegisterSpriteClipFromRegion(
                ref world.Animations, StringId.Hash("coin_pp"), in region,
                columns: 2, rows: 2, fps: 12f, loopMode: SpriteAnimLoopMode.PingPong);
            Assert.True(diffFpsClip != ppClip, "F6: Distinct Fps gets distinct SpriteAnimClipHandle");

            SpriteAnimClipHandle holdClip = AnimationSystem.RegisterSpriteClipFromRegion(
                ref world.Animations, StringId.Hash("coin_hold"), in region,
                columns: 2, rows: 2, fps: 4f, startFrame: 0, frameCount: 4, loopMode: SpriteAnimLoopMode.Hold);

            EntityId ent = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, ent, Vec3.Zero);
            SpriteHandle spriteH = RenderComponentSystem.AddSprite(
                ref world.RenderComponents,
                RenderComponentSystem.SpriteFromRegion(ent, in region, Vec2.One));

            SpriteAnimPlayerHandle spH = AnimationSystem.AddSpritePlayer(
                ref world.Animations, ent, ppClip, targetSprite: spriteH);

            // Regression (F6): AddSpritePlayer immediately initializes CurrentFrame & CurrentUvRect inside BaseUvRect before first StepAll
            SpriteAnimPlayerData* spInit = AnimationSystem.TryGetSpritePlayer(in world.Animations, spH);
            Assert.Equal(0, spInit->CurrentFrame, "F6: AddSpritePlayer initializes CurrentFrame == startFrame (0)");
            Assert.Near(0.25f, spInit->CurrentUvRect.X, 1e-4f, "F6: Initial frame-0 u0 inside BaseUvRect before StepAll");
            Assert.Near(0.50f, spInit->CurrentUvRect.Y, 1e-4f, "F6: Initial frame-0 v0 inside BaseUvRect before StepAll");
            Assert.Near(0.25f, spInit->CurrentUvRect.Z, 1e-4f, "F6: Initial cell uW before StepAll");
            Assert.Near(0.125f, spInit->CurrentUvRect.W, 1e-4f, "F6: Initial cell vH before StepAll");

            SpriteAnimClipHandle start2Clip = AnimationSystem.RegisterSpriteClipFromRegion(
                ref world.Animations, StringId.Hash("coin_start2"), in region,
                columns: 2, rows: 2, fps: 4f, startFrame: 2, frameCount: 2, loopMode: SpriteAnimLoopMode.Loop);
            SpriteAnimPlayerHandle spStart2 = AnimationSystem.AddSpritePlayer(ref world.Animations, ent, start2Clip);
            SpriteAnimPlayerData* spStart2Ptr = AnimationSystem.TryGetSpritePlayer(in world.Animations, spStart2);
            Assert.Equal(2, spStart2Ptr->CurrentFrame, "F6: AddSpritePlayer initializes non-zero startFrame == 2");
            Assert.Near(0.25f, spStart2Ptr->CurrentUvRect.X, 1e-4f, "F6: startFrame=2 initial u0 == 0.25");
            Assert.Near(0.625f, spStart2Ptr->CurrentUvRect.Y, 1e-4f, "F6: startFrame=2 initial v0 == 0.625");
            AnimationSystem.RemoveSpritePlayer(ref world.Animations, spStart2);

            int[] expectedFrames = { 0, 1, 2, 3, 2, 1, 0 };
            float[] expectedU = { 0.25f, 0.50f, 0.25f, 0.50f, 0.25f, 0.50f, 0.25f };
            float[] expectedV = { 0.50f, 0.50f, 0.625f, 0.625f, 0.625f, 0.50f, 0.50f };

            for (int step = 0; step < expectedFrames.Length; step++)
            {
                alloc.BeginFrame();
                EngineWorldSystem.StepCoreSystems(ref world, ref alloc, step == 0 ? 0f : 0.25f);
                SpriteAnimPlayerData* sp = AnimationSystem.TryGetSpritePlayer(in world.Animations, spH);
                SpriteData* target = RenderComponentSystem.TryGetSprite(in world.RenderComponents, spriteH);
                Assert.Equal(expectedFrames[step], sp->CurrentFrame, $"PingPong step {step} frame == {expectedFrames[step]}");
                Assert.Near(expectedU[step], sp->CurrentUvRect.X, 1e-4f, $"PingPong step {step} u0 inside BaseUvRect");
                Assert.Near(expectedV[step], sp->CurrentUvRect.Y, 1e-4f, $"PingPong step {step} v0 inside BaseUvRect");
                Assert.Near(0.25f, sp->CurrentUvRect.Z, 1e-4f, "Atlas sub-cell uW == 0.25");
                Assert.Near(0.125f, sp->CurrentUvRect.W, 1e-4f, "Atlas sub-cell vH == 0.125");
                Assert.True(target->UvRect.Equals(sp->CurrentUvRect), "EngineWorldSystem.StepCoreSystems auto-updated bound SpriteData.UvRect");
            }

            // SetSpriteClip + Hold mode clamping + BindSpritePlayer + removed SpriteHandle safety
            AnimationSystem.SetSpriteClip(ref world.Animations, spH, holdClip, resetTime: true);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 5.0f);
            Assert.Equal(3, AnimationSystem.TryGetSpritePlayer(in world.Animations, spH)->CurrentFrame, "Hold mode clamped at frame 3");
            Assert.Near(0.50f, RenderComponentSystem.TryGetSprite(in world.RenderComponents, spriteH)->UvRect.X, 1e-4f, "Bound sprite updated to Hold last frame U");

            // Zero-GC 60-tick check via EngineWorldSystem.StepCoreSystems
            AnimationSystem.SetSpriteClip(ref world.Animations, spH, ppClip, resetTime: true);
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f);
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                alloc.BeginFrame();
                EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "60 ticks of bound atlas sprite flipbook StepCoreSystems allocate 0 GC bytes");

            // Removing bound sprite does not crash or corrupt
            Assert.True(RenderComponentSystem.RemoveSprite(ref world.RenderComponents, spriteH), "Remove bound sprite");
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 0.25f);
            AnimationSystem.BindSpritePlayer(ref world.Animations, spH, SpriteHandle.Invalid);
        }
        finally
        {
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after atlas sprite flipbook tests");
    }

    private static void TestParticleAtlasFlipbooks()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 256 * 1024, 64 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 64, 512);

        try
        {
            var region = new SpriteRegionData
            {
                Name = StringId.Hash("fx/smoke_sheet"),
                AtlasTexture = TextureHandle.Create(1, 1),
                UvRect = new Vec4(0.5f, 0.25f, 0.5f, 0.5f),
                PixelSize = new Vec2(128f, 128f),
                Valid = 1,
            };

            // 1. OverLifetime with FlipbookFrameCount=3 on 2x2 grid (excludes empty cell 3) + Blend=1
            var cfgOver = ParticleSystem.DefaultEmitter(Vec3.Zero, seed: 1001);
            cfgOver.EmitRate = 0f;
            cfgOver.SpeedMin = 0f; cfgOver.SpeedMax = 0f;
            cfgOver.Gravity = Vec3.Zero; cfgOver.Turbulence = 0f;
            cfgOver.LifetimeMin = 2.0f; cfgOver.LifetimeMax = 2.0f;
            ParticleSystem.SetFlipbookRegion(ref cfgOver, in region, columns: 2, rows: 2, frameCount: 3, mode: ParticleFlipbookMode.OverLifetime, blendFrames: true);
            Assert.Equal(region.AtlasTexture, cfgOver.Texture, "SetFlipbookRegion wired AtlasTexture");
            Assert.True(cfgOver.BaseUvRect.Equals(region.UvRect), "SetFlipbookRegion wired BaseUvRect");

            var emOver = ParticleSystem.CreateEmitter(ref world.Particles, in cfgOver);
            ParticleSystem.Burst(ref world.Particles, emOver, 1);

            // age=0.5 / life=2.0 -> norm=0.25 -> scaled = 0.25 * (3-1) = 0.5 -> f0=0, f1=1, blend=0.5!
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 0.5f);
            Assert.Equal((byte)0, world.Particles.FrameIndex[0], "OverLifetime FrameIndex == 0 at scaled=0.5");
            Assert.Equal((byte)1, world.Particles.NextFrameIndex[0], "OverLifetime NextFrameIndex == 1 at scaled=0.5");
            Assert.Near(0.5f, world.Particles.FrameBlend[0], 1e-4f, "OverLifetime FrameBlend == 0.5");

            // Extract RenderPacket and verify atlas sub-rect UV slicing + UvRectNext + RotExtra.Z
            CameraSystem.CreatePerspective(ref world.Cameras, new Vec3(0f, 0f, 5f), Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, 1f / 60f, 640, 360);
            var shaders = new RenderShaderDefaults { Particle = ShaderHandle.Create(1, 1) };
            alloc.BeginFrame();
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket pkt);
            Assert.Equal(1, pkt.Particles.Count, "1 flipbook particle extracted");
            ParticleGpuInstance gp = pkt.Particles[0];
            Assert.Near(0.50f, gp.UvRect.X, 1e-4f, "Particle frame 0 u0 == 0.50");
            Assert.Near(0.25f, gp.UvRect.Y, 1e-4f, "Particle frame 0 v0 == 0.25");
            Assert.Near(0.25f, gp.UvRect.Z, 1e-4f, "Particle cell uW == 0.25");
            Assert.Near(0.25f, gp.UvRect.W, 1e-4f, "Particle cell vH == 0.25");
            Assert.Near(0.75f, gp.UvRectNext.X, 1e-4f, "Particle next frame 1 u0 == 0.75");
            Assert.Near(0.25f, gp.UvRectNext.Y, 1e-4f, "Particle next frame 1 v0 == 0.25");
            Assert.Near(0.50f, gp.RotExtra.Z, 1e-4f, "Particle RotExtra.Z carries FrameBlend == 0.5");

            // Near end of life (age=1.9): frame must stay <= 2 (cell 3 strictly excluded!)
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 1.4f);
            Assert.True(world.Particles.FrameIndex[0] <= 2 && world.Particles.NextFrameIndex[0] <= 2,
                $"FlipbookFrameCount=3 excludes empty cell 3 (got {world.Particles.FrameIndex[0]}->{world.Particles.NextFrameIndex[0]})");
            ParticleSystem.DestroyEmitter(ref world.Particles, emOver);

            // 2. Loop (seam wrap blend 3->0), Hold (clamp at 3), PingPong (reverse blend 3->2)
            var cfgLoop = cfgOver;
            ParticleSystem.SetFlipbookRegion(ref cfgLoop, in region, 2, 2, 4, ParticleFlipbookMode.Loop, fps: 4f, blendFrames: true);
            var emLoop = ParticleSystem.CreateEmitter(ref world.Particles, in cfgLoop);
            ParticleSystem.Burst(ref world.Particles, emLoop, 1);
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 0.875f); // raw = 3.5 -> f0=3, f1=0, blend=0.5
            Assert.Equal((byte)3, world.Particles.FrameIndex[0], "Loop f0 == 3 at raw=3.5");
            Assert.Equal((byte)0, world.Particles.NextFrameIndex[0], "Loop f1 wraps cleanly to 0 across seam");
            Assert.Near(0.5f, world.Particles.FrameBlend[0], 1e-4f, "Loop seam FrameBlend == 0.5");
            ParticleSystem.DestroyEmitter(ref world.Particles, emLoop);

            var cfgHold = cfgOver;
            ParticleSystem.SetFlipbookRegion(ref cfgHold, in region, 2, 2, 4, ParticleFlipbookMode.Hold, fps: 4f, blendFrames: true);
            var emHold = ParticleSystem.CreateEmitter(ref world.Particles, in cfgHold);
            ParticleSystem.Burst(ref world.Particles, emHold, 1);
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 1.5f); // raw = 6.0 >= 3 -> clamp
            Assert.Equal((byte)3, world.Particles.FrameIndex[0], "Hold clamped f0 == 3");
            Assert.Equal((byte)3, world.Particles.NextFrameIndex[0], "Hold clamped f1 == 3");
            Assert.Near(0f, world.Particles.FrameBlend[0], 1e-4f, "Hold clamped FrameBlend == 0");

            // Regression (F8): directly mutated non-finite Lifetime with fallback FPS stays finite
            ParticleSystem.TryGetEmitter(in world.Particles, emHold)->FlipbookFps = 0f;
            world.Particles.Lifetime[0] = float.NaN;
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 0.05f);
            Assert.True(float.IsFinite(world.Particles.Size[0]) && float.IsFinite(world.Particles.FrameBlend[0]), "F8: Non-finite Lifetime guarded by safeLife");
            ParticleSystem.DestroyEmitter(ref world.Particles, emHold);

            var cfgPp = cfgOver;
            ParticleSystem.SetFlipbookRegion(ref cfgPp, in region, 2, 2, 4, ParticleFlipbookMode.PingPong, fps: 4f, blendFrames: true);
            var emPp = ParticleSystem.CreateEmitter(ref world.Particles, in cfgPp);
            ParticleSystem.Burst(ref world.Particles, emPp, 1);
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 0.875f); // raw = 3.5 -> idx0=3(f=3), idx1=4(f=2), blend=0.5
            Assert.Equal((byte)3, world.Particles.FrameIndex[0], "PingPong f0 == 3 at raw=3.5");
            Assert.Equal((byte)2, world.Particles.NextFrameIndex[0], "PingPong f1 reverses cleanly to 2");
            Assert.Near(0.5f, world.Particles.FrameBlend[0], 1e-4f, "PingPong reverse FrameBlend == 0.5");
            ParticleSystem.DestroyEmitter(ref world.Particles, emPp);

            // 3. RandomStatic + FlipbookRandomStart determinism & SwapRemoveParticle SoA preservation
            var pA = ParticleTable.Create(ref alloc.Persistent, 128, 8);
            var pB = ParticleTable.Create(ref alloc.Persistent, 128, 8);
            var cfgRand = cfgOver;
            cfgRand.LifetimeMin = 0.5f; cfgRand.LifetimeMax = 1.5f;
            ParticleSystem.SetFlipbookRegion(ref cfgRand, in region, 4, 4, 16, ParticleFlipbookMode.RandomStatic, randomStart: true);
            var hA = ParticleSystem.CreateEmitter(ref pA, in cfgRand);
            var hB = ParticleSystem.CreateEmitter(ref pB, in cfgRand);
            ParticleSystem.Burst(ref pA, hA, 32);
            ParticleSystem.Burst(ref pB, hB, 32);

            ParticleSystem.Step(ref pA, in world.Transforms, 0.1f);
            ParticleSystem.Step(ref pB, in world.Transforms, 0.1f);
            Assert.Equal(32, pA.ParticleCount, "32 RandomStatic particles spawned");
            byte firstFrame = pA.FrameIndex[0];
            bool anyDifferent = false;
            for (int k = 0; k < 32; k++)
            {
                Assert.Equal(pA.StartFrameOffset[k], pB.StartFrameOffset[k], "Deterministic RandomStatic StartFrameOffset");
                Assert.Equal(pA.FrameIndex[k], pB.FrameIndex[k], "Deterministic RandomStatic FrameIndex");
                Assert.Equal((byte)pA.StartFrameOffset[k], pA.FrameIndex[k], "RandomStatic FrameIndex stays fixed at StartFrameOffset");
                Assert.Near(0f, pA.FrameBlend[k], 1e-5f, "RandomStatic FrameBlend == 0");
                if (pA.FrameIndex[k] != firstFrame) anyDifferent = true;
            }
            Assert.True(anyDifferent, "RandomStatic distributed across multiple frames");

            // Expire short-lived particles (triggers SwapRemoveParticle) and verify surviving SoA parity
            ParticleSystem.Step(ref pA, in world.Transforms, 0.8f);
            ParticleSystem.Step(ref pB, in world.Transforms, 0.8f);
            Assert.True(pA.ParticleCount > 0 && pA.ParticleCount < 32, "Some particles expired triggering SwapRemoveParticle");
            Assert.Equal(pA.ParticleCount, pB.ParticleCount, "Deterministic survivor count after SwapRemove");
            for (int k = 0; k < pA.ParticleCount; k++)
            {
                Assert.Equal((byte)pA.StartFrameOffset[k], pA.FrameIndex[k], "SwapRemoveParticle preserved StartFrameOffset & FrameIndex");
                Assert.Equal(pA.FrameIndex[k], pB.FrameIndex[k], "Bit-identical survivor FrameIndex");
            }

            // 4. 60-tick hot-loop 0 GC check (Step + ExtractRenderPacket)
            var cfgGc = cfgOver;
            cfgGc.EmitRate = 40f;
            ParticleSystem.SetFlipbookRegion(ref cfgGc, in region, 4, 4, 12, ParticleFlipbookMode.Loop, fps: 12f, blendFrames: true, randomStart: true);
            ParticleSystem.CreateEmitter(ref world.Particles, in cfgGc);
            alloc.BeginFrame();
            ParticleSystem.Step(ref world.Particles, in world.Transforms, 1f / 60f);
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out _);

            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                alloc.BeginFrame();
                ParticleSystem.Step(ref world.Particles, in world.Transforms, 1f / 60f);
                BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out _);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "60 ticks of atlas-flipbook ParticleSystem.Step + ExtractRenderPacket allocate 0 GC bytes");
        }
        finally
        {
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after particle atlas flipbook tests");
    }
}

using System;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
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
        Assert.Run("ParticleSystem_SoADeterministicZeroGc", &TestParticleSystem);
        Assert.Run("ParticleSystem_ParallelParityShapesOverflowHoldPosAndMaxEmitters", &TestParticleParallelAndEdgeCases);
        Assert.Run("ParticleSystem_ManualLocalMoveBadDtOverflowAndHighAtlasClamp", &TestParticleAuditRegressions);
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
        var alloc = EngineAllocators.Create(2 * 1024 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
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
}

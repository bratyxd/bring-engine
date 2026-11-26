using System;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.Tween;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class TweenAnimParticleTests
{
    private static uint s_completedUserData;
    private static TweenTable* s_reentrantTweens;
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
        Assert.Run("AnimationSystem_SkeletalCrossfadeAndSpriteFrames", &TestAnimationSystem);
        Assert.Run("AnimationSystem_CubicSplineCrossfadeNonTopoJointsAndHandles", &TestAnimationEdgeCases);
        Assert.Run("ParticleSystem_SoADeterministicZeroGc", &TestParticleSystem);
        Assert.Run("ParticleSystem_ParallelParityShapesOverflowHoldPosAndMaxEmitters", &TestParticleParallelAndEdgeCases);
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
        int sClip = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
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
        int firstIdx = -1;
        for (int i = 0; i < 300; i++)
        {
            int idx = AnimationSystem.RegisterSpriteClip(ref anims, new SpriteAnimClipData
            {
                Name = starName,
                OwnerScene = scB,
                StartFrame = 0, FrameCount = 4, Columns = 4, Rows = 2, Fps = 8f, Loop = 1,
            });
            if (i == 0) firstIdx = idx;
            else Assert.Equal(firstIdx, idx, "RegisterSpriteClip deduplicated by Name");
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
        ParticleSystem.CreateEmitter(ref smallPool, in fastCfg);
        ParticleSystem.Step(ref smallPool, in transforms, 100f);
        Assert.Equal(16, smallPool.ParticleCount, "EmitAccumulator overflow clamped cleanly to capacity");

        // 3. Local-space (WorldSpace = 0) FollowEntity + hold last position on entity removal + DestroyEmitter
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
        var emH = ParticleSystem.CreateEmitter(ref smallPool, in localCfg, scA);
        ParticleSystem.Burst(ref smallPool, emH, 1);
        ParticleSystem.Step(ref smallPool, in transforms, 0.01f);
        int localIdx = smallPool.ParticleCount - 1;
        float x0 = smallPool.PosX[localIdx];

        // Move target by +10 in X -> local-space particle shifts by +10
        TransformSystem.SetLocalPosition(ref transforms, target, new Vec3(15, 0, 0));
        TransformSystem.PropagateHierarchy(ref transforms);
        ParticleSystem.Step(ref smallPool, in transforms, 0.01f);
        Assert.Near(x0 + 10f, smallPool.PosX[localIdx], 0.1f, "Local-space particle followed entity movement");

        // Remove target -> emitter holds last position (15, 0, 0) instead of snapping to Vec3.Zero
        TransformSystem.Remove(ref transforms, target);
        ParticleSystem.Step(ref smallPool, in transforms, 0.01f);
        Assert.Near(15f, ParticleSystem.TryGetEmitter(in smallPool, emH)->Position.X, 1e-4f, "Emitter holds last position on target removal");

        // DestroyEmitter invalidates handle & removes particles
        Assert.True(ParticleSystem.DestroyEmitter(ref smallPool, emH), "DestroyEmitter succeeded");
        Assert.True(ParticleSystem.TryGetEmitter(in smallPool, emH) == null, "Stale EmitterHandle rejected");
        ParticleSystem.DestroyByScene(ref smallPool, scA);

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
}

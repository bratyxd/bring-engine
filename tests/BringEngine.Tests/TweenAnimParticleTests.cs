using System;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.Tween;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class TweenAnimParticleTests
{
    private static uint s_completedUserData;

    public static void RunAll()
    {
        Console.WriteLine("=== Phase 4: Universal Tweens, Skeletal/Sprite Anim & SoA Particles ===");
        Assert.Run("TweenSystem_PointersComponentsSequencesAndLoops", &TestTweenSystem);
        Assert.Run("AnimationSystem_SkeletalCrossfadeAndSpriteFrames", &TestAnimationSystem);
        Assert.Run("ParticleSystem_SoADeterministicZeroGc", &TestParticleSystem);
    }

    private static void OnTweenDone(uint userData) => s_completedUserData = userData;

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
        int pIdx = AnimationSystem.AddPlayer(ref anims, entity, skel, clip);

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
        int spIdx = AnimationSystem.AddSpritePlayer(ref anims, entity, sClip);

        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.5f, ref alloc.Frame, ref alloc.Scratch);

        Assert.Equal(2, anims.FrameJointPalette.Length, "2 joint matrices generated");
        Mat4 joint1Mat = anims.FrameJointPalette[anims.Players[pIdx].FrameJointOffset + 1];
        Assert.Near(3.0f, joint1Mat.C3.Y, 1e-3f, "Joint 1 interpolated to Y=3 at t=0.5");

        // At t=0.5s, 8fps => frame 4 % 4 = 0; step +0.125s => frame 1 (u0 = 0.25)
        alloc.BeginFrame();
        AnimationSystem.StepAll(ref anims, 0.125f, ref alloc.Frame, ref alloc.Scratch);
        Assert.Equal(1, anims.SpritePlayers[spIdx].CurrentFrame, "Sprite anim frame 1");
        Assert.Near(0.25f, anims.SpritePlayers[spIdx].CurrentUvRect.X, 1e-4f, "Sprite UV u0=0.25");

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
}

using System;
using System.IO;
using System.Text;
using BringEngine.Assets;
using BringEngine.Assets.Bundles;
using BringEngine.Assets.Compression;
using BringEngine.Assets.Packing;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using BringEngine.Rendering.Platform;
using BringEngine.Rendering.Shaders;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.Rig2D;
using BringEngine.Simulation.World;
using StbImageWriteSharp;

namespace BringEngine.Tests;

public static unsafe class Rig2DTests
{
    public static void RunAll(bool headlessOnly = false)
    {
        Console.WriteLine("=== Phase 16: Rig2D Skeletal Animation, Deformable Meshes, IK/Spring & Batched Rendering ===");
        Assert.Run("Rig2D_BoneHierarchy_AndAllFourInheritModes", &TestBoneHierarchyAndInheritModes);
        Assert.Run("Rig2D_IkConstraints_1BoneAnd2Bone_BendSoftnessStretchCompress", &TestIkConstraints);
        Assert.Run("Rig2D_TransformConstraints_WorldLocalAndRelative", &TestTransformConstraints);
        Assert.Run("Rig2D_SpringPhysics_DeterministicBitExactAndClamped", &TestSpringPhysicsDeterminism);
        Assert.Run("Rig2D_WeightedMeshSkinning_FfdDeform_AndSequenceSlots", &TestMeshSkinningFfdAndSequence);
        Assert.Run("Rig2D_Skins_Overrides_DrawOrder_Tracks_Crossfade_Additive_Curves_Events_Sockets", &TestPlaybackSkinsCurvesEventsAndSockets);
        Assert.Run("Rig2D_BpackRoundTrip_BundleUnloadRollback_PoolReuse_ZeroGc_ZeroLeak", &TestBpackBundleZeroGcAndLeaks);
        Assert.Run("Rig2D_CorruptBrigAndCycles_RejectedSafely", &TestCorruptBrigAndCycles);
        if (!headlessOnly)
            Assert.Run("Gpu_Rig2D_MultipleRigsCompressedBc3Atlas_SingleDrawCallAndPngCapture", &TestGpuBatchedRig2DRender);
    }

    private static void TestBoneHierarchyAndInheritModes()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
        var bundleArena = LinearArena.Create(256 * 1024);
        var bundleId = BundleId.Create(1, 1);

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
            const string json = """
            {
              "name": "inherit_test",
              "pixelsPerUnit": 100,
              "bones": [
                { "name": "root", "x": 10, "y": 20, "rotation": 90, "scaleX": 2, "scaleY": 3, "length": 10 },
                { "name": "normal", "parent": "root", "x": 10, "y": 0, "rotation": 0, "inherit": "normal" },
                { "name": "only_trans", "parent": "root", "x": 10, "y": 0, "rotation": 0, "inherit": "onlyTranslation" },
                { "name": "no_rot", "parent": "root", "x": 10, "y": 0, "rotation": 0, "inherit": "noRotation" },
                { "name": "no_scale", "parent": "root", "x": 10, "y": 0, "rotation": 0, "inherit": "noScale" }
              ],
              "slots": [],
              "skins": { "default": {} }
            }
            """;

            byte[] brig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(json));
            Rig2DAssetHandle assetH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, bundleId);
            EntityId e = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            Rig2DInstanceHandle instH = Rig2DSystem.AddInstance(ref world.Rigs, e, assetH);

            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);

            Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, assetH);
            Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, instH);
            Assert.True(asset != null && inst != null, "Asset and instance valid");

            Rig2DAffine2D* bw = world.Rigs.BoneWorld + inst->BoneSliceStart;
            int iNorm = Rig2DSystem.FindBoneIndex(asset, "normal");
            int iTrans = Rig2DSystem.FindBoneIndex(asset, "only_trans");
            int iNoRot = Rig2DSystem.FindBoneIndex(asset, "no_rot");
            int iNoScl = Rig2DSystem.FindBoneIndex(asset, "no_scale");

            // Parent root at (10,20) rotated +90 deg with scaleX=2 maps local (10,0) -> (10, 20 + 20) = (10, 40)
            Assert.Near(10f, bw[iNorm].Tx, 1e-2f, "Normal Tx");
            Assert.Near(40f, bw[iNorm].Ty, 1e-2f, "Normal Ty");
            Assert.Near(DetMath.HalfPi, bw[iNorm].WorldRotationRad, 1e-2f, "Normal inherits +90 deg");
            Assert.Near(2f, bw[iNorm].WorldScaleX, 1e-2f, "Normal inherits scaleX=2");

            Assert.Near(10f, bw[iTrans].Tx, 1e-2f, "OnlyTranslation Tx");
            Assert.Near(40f, bw[iTrans].Ty, 1e-2f, "OnlyTranslation Ty");
            Assert.Near(0f, bw[iTrans].WorldRotationRad, 1e-2f, "OnlyTranslation ignores parent rotation");
            Assert.Near(1f, bw[iTrans].WorldScaleX, 1e-2f, "OnlyTranslation ignores parent scale");

            Assert.Near(0f, bw[iNoRot].WorldRotationRad, 1e-2f, "NoRotation ignores parent rotation");
            Assert.Near(2f, bw[iNoRot].WorldScaleX, 1e-2f, "NoRotation keeps parent scaleX=2");
            Assert.Near(3f, bw[iNoRot].WorldScaleY, 1e-2f, "NoRotation keeps parent scaleY=3");

            Assert.Near(DetMath.HalfPi, bw[iNoScl].WorldRotationRad, 1e-2f, "NoScale keeps parent rotation");
            Assert.Near(1f, bw[iNoScl].WorldScaleX, 1e-2f, "NoScale ignores parent scaleX");
            Assert.Near(1f, bw[iNoScl].WorldScaleY, 1e-2f, "NoScale ignores parent scaleY");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after bone hierarchy tests");
    }

    private static void TestIkConstraints()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
        var bundleArena = LinearArena.Create(256 * 1024);
        var bundleId = BundleId.Create(2, 1);

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
            const string json = """
            {
              "name": "ik_test",
              "bones": [
                { "name": "root", "x": 0, "y": 0 },
                { "name": "aim", "parent": "root", "x": 0, "y": 0, "length": 50 },
                { "name": "aim_target", "parent": "root", "x": 0, "y": 100 },
                { "name": "upper_pos", "parent": "root", "x": 0, "y": 0, "length": 100 },
                { "name": "lower_pos", "parent": "upper_pos", "x": 100, "y": 0, "length": 100 },
                { "name": "upper_neg", "parent": "root", "x": 0, "y": 0, "length": 100 },
                { "name": "lower_neg", "parent": "upper_neg", "x": 100, "y": 0, "length": 100 },
                { "name": "ik_target", "parent": "root", "x": 141.42135, "y": 0 },
                { "name": "stretch_u", "parent": "root", "x": 0, "y": 0, "length": 50 },
                { "name": "stretch_l", "parent": "stretch_u", "x": 50, "y": 0, "length": 50 },
                { "name": "stretch_target", "parent": "root", "x": 150, "y": 0 },
                { "name": "soft_u", "parent": "root", "x": 0, "y": 0, "length": 100 },
                { "name": "soft_l", "parent": "soft_u", "x": 100, "y": 0, "length": 100 },
                { "name": "soft_target", "parent": "root", "x": 200, "y": 0 }
              ],
              "slots": [],
              "skins": { "default": {} },
              "constraints": {
                "ik": [
                  { "name": "aim_1bone", "order": 0, "bones": ["aim"], "target": "aim_target", "mix": 1.0, "stretch": true },
                  { "name": "leg_pos", "order": 1, "bones": ["upper_pos", "lower_pos"], "target": "ik_target", "mix": 1.0, "bendDirection": 1 },
                  { "name": "leg_neg", "order": 2, "bones": ["upper_neg", "lower_neg"], "target": "ik_target", "mix": 1.0, "bendDirection": -1 },
                  { "name": "leg_stretch", "order": 3, "bones": ["stretch_u", "stretch_l"], "target": "stretch_target", "mix": 1.0, "stretch": true },
                  { "name": "leg_soft", "order": 4, "bones": ["soft_u", "soft_l"], "target": "soft_target", "mix": 1.0, "softness": 20.0, "bendDirection": 1 }
                ]
              }
            }
            """;

            byte[] brig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(json));
            Rig2DAssetHandle assetH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, bundleId);
            EntityId e = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            Rig2DInstanceHandle instH = Rig2DSystem.AddInstance(ref world.Rigs, e, assetH);

            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);

            Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, assetH);
            Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, instH);
            Rig2DAffine2D* bw = world.Rigs.BoneWorld + inst->BoneSliceStart;

            // 1-bone IK aim + stretch
            int bAim = Rig2DSystem.FindBoneIndex(asset, "aim");
            Assert.Near(DetMath.HalfPi, bw[bAim].WorldRotationRad, 1e-2f, "1-bone IK aims straight up (+90 deg)");
            Assert.Near(2f, bw[bAim].WorldScaleX, 1e-2f, "1-bone IK stretches 50->100 (scaleX=2)");

            // 2-bone IK reach + opposite bend directions
            int bLp = Rig2DSystem.FindBoneIndex(asset, "lower_pos");
            int bLn = Rig2DSystem.FindBoneIndex(asset, "lower_neg");
            Vec2 endPos = Rig2DAffine2D.TransformPoint(in bw[bLp], new Vec2(100f, 0f));
            Vec2 endNeg = Rig2DAffine2D.TransformPoint(in bw[bLn], new Vec2(100f, 0f));
            Assert.Near(141.42135f, endPos.X, 0.25f, "2-bone IK (+bend) hits target X");
            Assert.Near(0f, endPos.Y, 0.25f, "2-bone IK (+bend) hits target Y");
            Assert.Near(141.42135f, endNeg.X, 0.25f, "2-bone IK (-bend) hits target X");
            Assert.Near(0f, endNeg.Y, 0.25f, "2-bone IK (-bend) hits target Y");
            Assert.True(bw[bLp].Ty < -50f, $"2-bone IK bendDirection=+1 knee bends -Y (got {bw[bLp].Ty:F2})");
            Assert.True(bw[bLn].Ty > 50f, $"2-bone IK bendDirection=-1 knee bends +Y (got {bw[bLn].Ty:F2})");

            // 2-bone stretch beyond reach (50+50 -> 150)
            int bSl = Rig2DSystem.FindBoneIndex(asset, "stretch_l");
            Vec2 stretchEnd = Rig2DAffine2D.TransformPoint(in bw[bSl], new Vec2(50f, 0f));
            Assert.Near(150f, stretchEnd.X, 0.5f, "2-bone IK stretch reaches 150px");

            // Softness prevents harsh full-extension snap at d=200
            int bSoftL = Rig2DSystem.FindBoneIndex(asset, "soft_l");
            Vec2 softEnd = Rig2DAffine2D.TransformPoint(in bw[bSoftL], new Vec2(100f, 0f));
            Assert.True(softEnd.X < 198f && softEnd.X > 185f, $"Softness gently eases extension below 200 (got {softEnd.X:F2})");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after IK tests");
    }

    private static void TestTransformConstraints()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
        var bundleArena = LinearArena.Create(256 * 1024);
        var bundleId = BundleId.Create(3, 1);

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
            const string json = """
            {
              "name": "transform_c_test",
              "bones": [
                { "name": "root", "x": 0, "y": 0 },
                { "name": "driver", "parent": "root", "x": 80, "y": 40, "rotation": 60, "scaleX": 2.0 },
                { "name": "follower_world", "parent": "root", "x": 0, "y": 0, "rotation": 0 },
                { "name": "follower_rel", "parent": "root", "x": 10, "y": 10, "rotation": 15 }
              ],
              "slots": [],
              "skins": { "default": {} },
              "constraints": {
                "transform": [
                  { "name": "tc_world", "order": 0, "bone": "follower_world", "target": "driver", "mixRotate": 0.5, "mixX": 1.0, "mixY": 0.5, "mixScaleX": 1.0, "offsetX": 5 },
                  { "name": "tc_rel", "order": 1, "bone": "follower_rel", "target": "driver", "mixRotate": 0.5, "mixX": 0.25, "local": true, "relative": true }
                ]
              }
            }
            """;

            byte[] brig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(json));
            Rig2DAssetHandle assetH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, bundleId);
            EntityId e = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            Rig2DInstanceHandle instH = Rig2DSystem.AddInstance(ref world.Rigs, e, assetH);

            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 1f / 60f, ref alloc.Frame, ref alloc.Scratch);

            Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, assetH);
            Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, instH);
            Rig2DAffine2D* bw = world.Rigs.BoneWorld + inst->BoneSliceStart;

            int fw = Rig2DSystem.FindBoneIndex(asset, "follower_world");
            int fr = Rig2DSystem.FindBoneIndex(asset, "follower_rel");

            Assert.Near(85f, bw[fw].Tx, 0.1f, "World transform constraint Tx = 80 + 5");
            Assert.Near(20f, bw[fw].Ty, 0.1f, "World transform constraint Ty = lerp(0, 40, 0.5)");
            Assert.Near(30f * DetMath.Deg2Rad, bw[fw].WorldRotationRad, 1e-2f, "World transform constraint rot = 30 deg");
            Assert.Near(2f, bw[fw].WorldScaleX, 1e-2f, "World transform constraint scaleX = 2");

            Assert.Near(30f, bw[fr].Tx, 0.1f, "Relative local constraint Tx = 10 + 80*0.25");
            Assert.Near(45f * DetMath.Deg2Rad, bw[fr].WorldRotationRad, 1e-2f, "Relative local constraint rot = 15 + 60*0.5");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after transform constraint tests");
    }

    private static void TestSpringPhysicsDeterminism()
    {
        NativeMemoryTracker.ResetStatsForTest();
        const string json = """
        {
          "name": "spring_test",
          "bones": [
            { "name": "root", "x": 0, "y": 0 },
            { "name": "cape", "parent": "root", "x": 0, "y": 100, "rotation": -90, "length": 80 }
          ],
          "slots": [],
          "skins": { "default": {} },
          "constraints": {
            "spring": [
              { "name": "cape_sp", "order": 0, "bone": "cape", "inertia": 0.9, "strength": 55, "damping": 5, "mass": 1.0, "gravity": 150, "wind": 45, "maxAngle": 30, "mix": 1.0, "rotateScale": 1.0 }
            ]
          }
        }
        """;

        byte[] brig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(json));

        Rig2DAffine2D RunSim()
        {
            var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
            var bundleArena = LinearArena.Create(256 * 1024);
            try
            {
                var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
                Rig2DAssetHandle aH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, BundleId.Create(1, 1));
                EntityId e = EntitySystem.Spawn(ref world.Entities);
                TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
                Rig2DInstanceHandle iH = Rig2DSystem.AddInstance(ref world.Rigs, e, aH);

                for (int t = 0; t < 120; t++)
                {
                    // Move entity rapidly to induce inertia sway, then stop
                    float px = t < 40 ? t * 0.08f : 40 * 0.08f;
                    TransformSystem.SetLocalPosition(ref world.Transforms, e, new Vec3(px, 0f, 0f));
                    alloc.BeginFrame();
                    EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f);
                }

                Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, iH);
                Rig2DSpringRuntimeState st = world.Rigs.SpringStates[inst->SpringSliceStart];
                Assert.True(DetMath.Abs(st.AngleOffsetDeg) <= 30.001f, $"Spring clamped to maxAngle=30 (got {st.AngleOffsetDeg:F2})");
                Assert.True(DetMath.Abs(st.AngleOffsetDeg) > 0.1f, $"Spring responded dynamically to inertia+wind (got {st.AngleOffsetDeg:F2})");
                return world.Rigs.BoneWorld[inst->BoneSliceStart + 1];
            }
            finally
            {
                bundleArena.Dispose();
                alloc.Dispose();
            }
        }

        Rig2DAffine2D r1 = RunSim();
        Rig2DAffine2D r2 = RunSim();
        Assert.Equal(r1, r2, "Spring physics produces bit-exact identical Rig2DAffine2D across runs");
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after spring tests");
    }

    private static void TestMeshSkinningFfdAndSequence()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
        var bundleArena = LinearArena.Create(256 * 1024);
        var bundleId = BundleId.Create(4, 1);

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
            const string json = """
            {
              "name": "mesh_seq_test",
              "bones": [
                { "name": "b0", "x": 0, "y": 0, "length": 50 },
                { "name": "b1", "parent": "b0", "x": 50, "y": 0, "length": 50 }
              ],
              "slots": [
                { "name": "cloth", "bone": "b0", "attachment": "cloth_m" },
                { "name": "fx_hold", "bone": "b0", "attachment": "seq_hold" },
                { "name": "fx_loop", "bone": "b0", "attachment": "seq_loop" },
                { "name": "fx_pp", "bone": "b0", "attachment": "seq_pp" }
              ],
              "skins": {
                "default": {
                  "cloth": {
                    "cloth_m": {
                      "type": "mesh",
                      "image": "parts/cloth",
                      "vertices": [[0, 0], [50, 0], [100, 0]],
                      "uvs": [[0, 0], [0.5, 0], [1, 0]],
                      "triangles": [0, 1, 2],
                      "weights": [[["b0", 1.0]], [["b0", 0.5], ["b1", 0.5]], [["b1", 1.0]]]
                    }
                  },
                  "fx_hold": { "seq_hold": { "type": "sequence", "frames": ["f0", "f1", "f2"], "fps": 10, "loop": "hold" } },
                  "fx_loop": { "seq_loop": { "type": "sequence", "frames": ["f0", "f1", "f2"], "fps": 10, "loop": "loop" } },
                  "fx_pp":   { "seq_pp":   { "type": "sequence", "frames": ["f0", "f1", "f2"], "fps": 10, "loop": "pingpong" } }
                }
              },
              "animations": {
                "bend_deform": {
                  "duration": 1.0,
                  "bones": {
                    "b1": { "rotate": [{ "time": 0, "value": 0 }, { "time": 1.0, "value": 90 }] }
                  },
                  "deform": {
                    "default": {
                      "cloth": {
                        "cloth_m": [
                          { "time": 0.0, "vertices": [[0, 0], [0, 0], [0, 0]] },
                          { "time": 1.0, "vertices": [[0, 10], [0, 20], [0, 30]] }
                        ]
                      }
                    }
                  }
                }
              }
            }
            """;

            byte[] brig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(json));
            Rig2DAssetHandle aH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, bundleId);
            EntityId e = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e, Vec3.Zero);
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            Rig2DInstanceHandle iH = Rig2DSystem.AddInstance(ref world.Rigs, e, aH);
            Rig2DSystem.SetAnimation(ref world.Rigs, iH, "bend_deform", track: 0, loop: false, mixDuration: 0f);

            // Step to t = 0.5s (5 steps of 0.1s -> sequence frame index 5!)
            for (int k = 0; k < 5; k++)
            {
                alloc.BeginFrame();
                Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0.1f, ref alloc.Frame, ref alloc.Scratch);
            }

            Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, aH);
            Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, iH);
            Rig2DRuntimeSlotState* states = world.Rigs.SlotStates + inst->SlotSliceStart;

            // Verify FFD delta interpolated at t=0.5s -> (0,5), (0,10), (0,15)
            Assert.Equal(3, states[0].DeformVertexCount, "FFD deform allocated 3 vertices in FrameArena");
            Vec2* def = world.Rigs.FrameDeformDeltas.Ptr + states[0].DeformVertexOffset;
            Assert.Near(5f, def[0].Y, 0.1f, "FFD v0 delta Y == 5");
            Assert.Near(10f, def[1].Y, 0.1f, "FFD v1 delta Y == 10");
            Assert.Near(15f, def[2].Y, 0.1f, "FFD v2 delta Y == 15");

            // Verify weighted skinning on v2 (100% b1 rotated 45 deg + deform (0,15) in bind space)
            Rig2DAffine2D* bw = world.Rigs.BoneWorld + inst->BoneSliceStart;
            Vec2 skinnedV2 = Rig2DSystem.SkinMeshVertex(asset, bw, 0, in asset->MeshVertices[2], def[2]);
            Assert.True(skinnedV2.Y > 30f, $"Weighted mesh vertex rotated upward with b1 (got Y={skinnedV2.Y:F2})");

            // Verify Sequence modes (4 increments of 0.1s after PosedOnce=1 -> seqTime=0.4s -> raw=4 at 10fps)
            // Hold(4 of 3) = 2; Loop(4 % 3) = 1; PingPong(4 in [0,1,2,1,0]) = 0!
            Assert.Equal(2, states[1].SequenceFrame, "Sequence Hold clamps at last frame 2");
            Assert.Equal(1, states[2].SequenceFrame, "Sequence Loop wraps 4 % 3 == 1");
            Assert.Equal(0, states[3].SequenceFrame, "Sequence PingPong returns to frame 0 at step 4");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after mesh/FFD/sequence tests");
    }

    private static void TestPlaybackSkinsCurvesEventsAndSockets()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
        var bundleArena = LinearArena.Create(256 * 1024);
        var bundleId = BundleId.Create(5, 1);

        try
        {
            var world = EngineWorld.Create(ref alloc.Persistent, 64, 32);
            const string json = """
            {
              "name": "hero",
              "pixelsPerUnit": 100,
              "bones": [
                { "name": "root", "x": 0, "y": 0 },
                { "name": "arm", "parent": "root", "x": 0, "y": 50, "length": 60 },
                { "name": "hand", "parent": "arm", "x": 60, "y": 0, "length": 20 },
                { "name": "aim_tgt", "parent": "root", "x": 100, "y": 50 }
              ],
              "slots": [
                { "name": "body", "bone": "root", "attachment": "body_img" },
                { "name": "weapon", "bone": "hand", "attachment": "sword" }
              ],
              "skins": {
                "default": {
                  "body":   { "body_img": { "type": "region", "image": "parts/body", "width": 64, "height": 64 } },
                  "weapon": { "sword":    { "type": "region", "image": "parts/sword", "width": 32, "height": 96 } }
                },
                "halberd_skin": {
                  "weapon": { "sword":    { "type": "region", "image": "parts/halberd", "width": 40, "height": 120 } }
                }
              },
              "constraints": {
                "ik": [
                  { "name": "arm_aim", "order": 0, "bones": ["arm"], "target": "aim_tgt", "mix": 0.0 }
                ]
              },
              "events": {
                "hit": { "int": 42, "float": 1.5, "string": "slash" }
              },
              "animations": {
                "clip_a": {
                  "duration": 1.0,
                  "bones": {
                    "arm": {
                      "rotate": [
                        { "time": 0.0, "value": 0, "curve": "step" },
                        { "time": 0.5, "value": 60, "curve": [0.25, 0.1, 0.25, 1.0] },
                        { "time": 1.0, "value": 120 }
                      ]
                    }
                  },
                  "drawOrder": [
                    { "time": 0.0, "order": ["body", "weapon"] },
                    { "time": 0.5, "order": ["weapon", "body"] }
                  ],
                  "events": [
                    { "time": 0.25, "name": "hit" },
                    { "time": 0.95, "name": "hit", "int": 99 }
                  ]
                },
                "clip_b": {
                  "duration": 1.0,
                  "bones": {
                    "arm": { "rotate": [{ "time": 0.0, "value": 0 }, { "time": 1.0, "value": 0 }] }
                  }
                },
                "add_bob": {
                  "duration": 1.0,
                  "bones": {
                    "arm": { "rotate": [{ "time": 0.0, "value": 15 }, { "time": 1.0, "value": 15 }] }
                  }
                }
              }
            }
            """;

            byte[] brig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(json));
            Rig2DAssetHandle aH = Rig2DLoader.LoadFromMemory(brig, ref bundleArena, ref world.Rigs, bundleId);
            EntityId e = EntitySystem.Spawn(ref world.Entities);
            TransformSystem.Add(ref world.Transforms, e, new Vec3(2f, 1f, 0f));
            TransformSystem.PropagateHierarchy(ref world.Transforms);
            Rig2DInstanceHandle iH = Rig2DSystem.AddInstance(ref world.Rigs, e, aH);

            Rig2DAssetData* asset = Rig2DSystem.TryGetAsset(in world.Rigs, aH);
            Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, iH);

            // 1. Skin swap preserves inherited "body" from default skin while replacing "weapon"
            int defWeaponAtt = world.Rigs.SlotStates[inst->SlotSliceStart + 1].ActiveAttachmentIndex;
            Assert.True(Rig2DSystem.SetSkin(ref world.Rigs, iH, "halberd_skin"), "SetSkin halberd_skin succeeded");
            int altWeaponAtt = world.Rigs.SlotStates[inst->SlotSliceStart + 1].ActiveAttachmentIndex;
            Assert.True(altWeaponAtt != defWeaponAtt && altWeaponAtt >= 0, "Alternate skin swapped weapon attachment");
            Assert.True(world.Rigs.SlotStates[inst->SlotSliceStart + 0].ActiveAttachmentIndex >= 0, "Inherited default skin body slot preserved");

            // 2. Manual slot hide override & clear
            Rig2DSystem.SetSlotAttachment(ref world.Rigs, iH, "weapon", "");
            Assert.Equal(-1, world.Rigs.SlotStates[inst->SlotSliceStart + 1].ActiveAttachmentIndex, "Empty attachment hides slot");
            Rig2DSystem.ClearSlotAttachmentOverride(ref world.Rigs, iH, "weapon");

            // 3. Step curve + Bezier curve + DrawOrder + Events + Loop wrap
            Rig2DSystem.SetAnimation(ref world.Rigs, iH, "clip_a", track: 0, loop: true, mixDuration: 0f);
            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0.3f, ref alloc.Frame, ref alloc.Scratch);
            var ev1 = Rig2DSystem.GetFiredEvents(in world.Rigs);
            Assert.Equal(1, ev1.Length, "Event at t=0.25 fired when stepping 0->0.3");
            Assert.Equal(42, ev1[0].IntVal, "Event inherited default int=42");
            Assert.Near(0f, world.Rigs.BoneWorld[inst->BoneSliceStart + 1].WorldRotationRad, 1e-2f, "Step curve held 0 deg at t=0.3");
            Assert.Equal(0, world.Rigs.DrawOrder[inst->SlotSliceStart + 0], "DrawOrder [body, weapon] at t=0.3");

            // Step to t=0.75 (Bezier curve between 60 and 120 + DrawOrder swap)
            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0.45f, ref alloc.Frame, ref alloc.Scratch);
            float rotDeg75 = world.Rigs.BoneWorld[inst->BoneSliceStart + 1].WorldRotationRad * DetMath.Rad2Deg;
            Assert.True(rotDeg75 > 95f && rotDeg75 < 115f, $"CubicBezier ease-out evaluated ahead of linear 90 deg (got {rotDeg75:F2})");
            Assert.Equal(1, world.Rigs.DrawOrder[inst->SlotSliceStart + 0], "DrawOrder swapped [weapon, body] at t=0.75");

            // Loop wrap 0.75 -> 1.30 (wraps to 0.30, firing BOTH t=0.95 AND t=0.25!)
            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0.55f, ref alloc.Frame, ref alloc.Scratch);
            var evWrap = Rig2DSystem.GetFiredEvents(in world.Rigs);
            Assert.Equal(2, evWrap.Length, "Loop wrap fired both tail event (0.95) and head event (0.25)");
            Assert.Equal(99, evWrap[0].IntVal, "Tail event override int=99");
            Assert.Equal(42, evWrap[1].IntVal, "Wrapped head event int=42");

            // 4. Additive Track 1 + Crossfade on Track 0
            Rig2DSystem.SetAnimation(ref world.Rigs, iH, "clip_b", track: 0, loop: true, mixDuration: 0.2f);
            Rig2DSystem.SetAnimation(ref world.Rigs, iH, "add_bob", track: 1, loop: true, mixDuration: 0f, alpha: 1f, additive: true);
            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0.25f, ref alloc.Frame, ref alloc.Scratch);
            float rotWithAdd = world.Rigs.BoneWorld[inst->BoneSliceStart + 1].WorldRotationRad * DetMath.Rad2Deg;
            Assert.Near(15f, rotWithAdd, 0.5f, "Crossfade finished to clip_b (0 deg) + additive track (+15 deg) == 15 deg");

            // 5. World Bone Socket query & World IK Target override
            Assert.True(Rig2DSystem.TryGetBoneWorldPose(in world.Rigs, iH, "hand", in world.Transforms, out Vec3 handPos, out _, out _), "TryGetBoneWorldPose succeeded");
            Assert.True(handPos.X > 2.4f && handPos.Y > 1.5f, $"Hand socket mapped to entity world pos + rotated offset (got {handPos})");

            Assert.True(Rig2DSystem.SetIkTargetWorld(ref world.Rigs, iH, "aim_tgt", in world.Transforms, new Vec3(2f, 3f, 0f)), "SetIkTargetWorld succeeded");
            alloc.BeginFrame();
            Rig2DSystem.StepAll(ref world.Rigs, in world.Transforms, 0f, ref alloc.Frame, ref alloc.Scratch);
            Assert.True(Rig2DSystem.TryGetBoneWorldPose(in world.Rigs, iH, "aim_tgt", in world.Transforms, out Vec3 tgtPos, out _, out _), "Target bone socket query");
            Assert.Near(2f, tgtPos.X, 0.02f, "World IK target X matched");
            Assert.Near(3f, tgtPos.Y, 0.02f, "World IK target Y matched");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after playback/skins/events/sockets tests");
    }

    private static void TestBpackBundleZeroGcAndLeaks()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tmpRoot = Path.Combine(Path.GetTempPath(), $"bring_rig2d_pack_{Guid.NewGuid():N}");
        string rawDir = Path.Combine(tmpRoot, "raw");
        string partsDir = Path.Combine(rawDir, "parts");
        string distDir = Path.Combine(tmpRoot, "dist");
        Directory.CreateDirectory(partsDir);
        Directory.CreateDirectory(distDir);

        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 1 * 1024 * 1024, 2 * 1024 * 1024, 256 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        var bundles = BundleManager.Create(ref alloc.Persistent, 8);

        try
        {
            // Write raw part PNGs + declarative .rig2d.json
            File.WriteAllBytes(Path.Combine(partsDir, "torso.png"), MakeSolidPng(64, 64, 200, 60, 70, 255));
            File.WriteAllBytes(Path.Combine(partsDir, "cape.png"), MakeSolidPng(64, 64, 160, 20, 40, 220));
            File.WriteAllText(Path.Combine(rawDir, "knight.rig2d.json"), BuildTestKnightJson(), new UTF8Encoding(false));

            string packPath = Path.Combine(distDir, "knight.bpack");
            AssetPackBuildSummary sum = AssetPackBuilder.BuildPackFromDirectory(rawDir, packPath, PackCompressionMode.Brotli);
            Assert.Equal(1, sum.Rig2DCount, "AssetPackBuilder compiled 1 Rig2D entry");
            Assert.Equal(1, sum.AtlasPageCount, "Part PNGs packed into 1 compressed atlas page");
            Assert.Equal(2, sum.AtlasedSpriteCount, "2 part sprites packed into atlas");
            Assert.True(AssetPackBuilder.VerifyNoLooseTextures(distDir, out _), "Zero loose textures in dist/");

            // Load via BundleSystem.RegisterPack + Acquire
            BundleId bId = BundleSystem.RegisterPack(ref bundles, "knight_bundle", 2 * 1024 * 1024, packPath);
            BundleSystem.Acquire(ref bundles, bId, ref world, ref alloc.Scratch);

            Rig2DAssetHandle assetH = Rig2DSystem.FindAsset(in world.Rigs, "knight");
            Assert.True(assetH.IsValid, "Rig2D asset loaded automatically from .bpack");
            Assert.True(AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("parts/torso")) != null, "Atlas region 'parts/torso' registered");
            Assert.True(AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("parts/cape")) != null, "Atlas region 'parts/cape' registered");

            // Simulate GPU slot assignment for headless ExtractRenderPacket test
            SpriteRegionData* reg = AssetSystem.TryGetSpriteRegion(in world.Assets, StringId.Hash("parts/torso"));
            TextureAssetData* atlasTex = AssetSystem.TryGetTexture(in world.Assets, reg->AtlasTexture);
            atlasTex->GpuSlot = 0;
            atlasTex->GpuSlotGen = 1u;

            // Spawn 4 rigged instances + camera
            CameraHandle camH = CameraSystem.CreateOrthographicUI(ref world.Cameras, 640f, 360f);
            _ = camH;
            var instHandles = new Rig2DInstanceHandle[4];
            var entities = new EntityId[4];
            for (int i = 0; i < 4; i++)
            {
                entities[i] = EntitySystem.Spawn(ref world.Entities, SceneId.Create(1, 1));
                TransformSystem.Add(ref world.Transforms, entities[i], new Vec3(120f + i * 110f, 120f, 0f));
                instHandles[i] = Rig2DSystem.AddInstance(ref world.Rigs, entities[i], assetH, SceneId.Create(1, 1), layerMask: 1u << 31, depthTest: false);
                Rig2DSystem.SetAnimation(ref world.Rigs, instHandles[i], "idle", track: 0, loop: true);
            }

            var shaders = new RenderShaderDefaults { Rig2D = ShaderHandle.Create(1, 1) };

            // Warmup JIT
            alloc.BeginFrame();
            EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
            BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out RenderPacket warmPkt);
            Assert.Equal(1, warmPkt.Rig2DBatches.Count, "All 4 rigs & 8 slots sharing compressed atlas coalesce into 1 Rig2DBatchCmd");
            Assert.Equal(4, warmPkt.ExtractedStats.Rig2DInstances, "ExtractedStats reports 4 Rig2DInstances");

            // Strict 0-GC check over 120 frames of StepCoreSystems + ExtractRenderPacket!
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 120; f++)
            {
                alloc.BeginFrame();
                EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
                BatchRenderer.ExtractRenderPacket(in shaders, in world, SceneLighting.Default, ref alloc.Frame, ref alloc.Scratch, out _);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, gcAfter - gcBefore, "120 ticks of Rig2DSystem.StepAll + BatchRenderer.ExtractRenderPacket allocate 0 GC bytes");

            // Verify coalescing pool reuse via DestroyEntity & DestroySceneObjects
            EngineWorldSystem.DestroyEntity(ref world, entities[1]);
            Assert.Equal(3, world.Rigs.InstanceCount, "DestroyEntity removed Rig2D instance");
            EngineWorldSystem.DestroySceneObjects(ref world, SceneId.Create(1, 1), ref alloc.Scratch);
            Assert.Equal(0, world.Rigs.InstanceCount, "DestroySceneObjects removed remaining Rig2D instances");
            Assert.Equal(0, world.Rigs.BonePool.UsedCount, "BonePool completely freed");
            Assert.Equal(1, world.Rigs.BonePool.FreeRangeCount, "BonePool coalesced back to 1 contiguous range");
            Assert.Equal(0, world.Rigs.SlotPool.UsedCount, "SlotPool completely freed");
            Assert.Equal(1, world.Rigs.SlotPool.FreeRangeCount, "SlotPool coalesced back to 1 contiguous range");

            // Bundle release unregisters Rig2D asset cleanly
            BundleSystem.Release(ref bundles, bId, ref world);
            Assert.Equal(0, world.Rigs.LiveAssetCount, "BundleSystem.Release unregistered Rig2D asset");
            Assert.True(Rig2DSystem.TryGetAsset(in world.Rigs, assetH) == null, "Stale Rig2DAssetHandle invalidated after unload");
            Assert.True(!Rig2DSystem.FindAsset(in world.Rigs, "knight").IsValid, "AssetByName cleared after unload");

            // Bundle acquire rollback test (corrupt pack triggers rollback cleanly)
            string badPack = Path.Combine(distDir, "bad.bpack");
            File.WriteAllBytes(badPack, new byte[] { 1, 2, 3, 4 });
            BundleId badB = BundleSystem.RegisterPack(ref bundles, "bad_bundle", 256 * 1024, badPack);
            bool threw = false;
            try { BundleSystem.Acquire(ref bundles, badB, ref world, ref alloc.Scratch); }
            catch (InvalidDataException) { threw = true; }
            Assert.True(threw, "Corrupt .bpack threw InvalidDataException and rolled back cleanly");
            Assert.Equal(0, world.Rigs.LiveAssetCount, "Rollback left 0 leaked Rig2D assets");
        }
        finally
        {
            BundleSystem.UnloadAll(ref bundles, ref world);
            alloc.Dispose();
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after .bpack/bundle/0-GC tests");
    }

    private static void TestCorruptBrigAndCycles()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(4 * 1024 * 1024, 256 * 1024, 512 * 1024, 64 * 1024);
        var bundleArena = LinearArena.Create(256 * 1024);

        try
        {
            var rigs = Rig2DTable.Create(ref alloc.Persistent, 8, 8);
            var bId = BundleId.Create(1, 1);

            // Cycle in JSON rejected by Rig2DCompiler
            const string cycleJson = """{ "name": "cyc", "bones": [ { "name": "a", "parent": "b" }, { "name": "b", "parent": "a" } ] }""";
            bool threwCycle = false;
            try { Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(cycleJson)); }
            catch (InvalidDataException) { threwCycle = true; }
            Assert.True(threwCycle, "Bone cycle rejected by Rig2DCompiler");

            // Bad magic & truncated BRIG rejected with bundleArena rollback
            nuint offBefore = bundleArena.Offset;
            bool threwTrunc = false;
            try { Rig2DLoader.LoadFromMemory(new byte[] { 0x42, 0x52, 0x49, 0x47 }, ref bundleArena, ref rigs, bId); }
            catch (InvalidDataException) { threwTrunc = true; }
            Assert.True(threwTrunc, "Truncated BRIG rejected");
            Assert.Equal(offBefore, bundleArena.Offset, "Truncated BRIG rolled back bundleArena");

            byte[] goodBrig = Rig2DCompiler.CompileFromJsonBytes(Encoding.UTF8.GetBytes(BuildTestKnightJson()));
            byte[] badVer = (byte[])goodBrig.Clone();
            BitConverter.TryWriteBytes(badVer.AsSpan(4, 2), (ushort)99);
            bool threwVer = false;
            try { Rig2DLoader.LoadFromMemory(badVer, ref bundleArena, ref rigs, bId); }
            catch (InvalidDataException) { threwVer = true; }
            Assert.True(threwVer, "Unsupported BRIG version rejected");
        }
        finally
        {
            bundleArena.Dispose();
            alloc.Dispose();
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after corrupt BRIG tests");
    }

    private static void TestGpuBatchedRig2DRender()
    {
        NativeMemoryTracker.ResetStatsForTest();
        string tmpRoot = Path.Combine(Path.GetTempPath(), $"bring_rig2d_gpu_{Guid.NewGuid():N}");
        string rawDir = Path.Combine(tmpRoot, "raw");
        string partsDir = Path.Combine(rawDir, "parts");
        string distDir = Path.Combine(tmpRoot, "dist");
        Directory.CreateDirectory(partsDir);
        Directory.CreateDirectory(distDir);

        var alloc = EngineAllocators.Create(8 * 1024 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024, 512 * 1024);
        var world = EngineWorld.Create(ref alloc.Persistent, 256, 64);
        var bundles = BundleManager.Create(ref alloc.Persistent, 8);

        var window = PlatformSystem.InitWindow("BringEngine Rig2D Batched GPU Test", 640, 360, hidden: true);
        GpuContext* gpu = alloc.Persistent.Alloc<GpuContext>(1, 64);
        GpuSystem.Init(gpu, window.Window, ref alloc.Persistent, ref world.Assets, debugMode: true);

        try
        {
            ShaderSystem.CompileBuiltinShaders(ref *gpu);

            // Build a real BC3-compressed .bpack containing our rig + part textures
            File.WriteAllBytes(Path.Combine(partsDir, "torso.png"), MakeSolidPng(64, 64, 235, 80, 95, 255));
            File.WriteAllBytes(Path.Combine(partsDir, "cape.png"), MakeSolidPng(64, 64, 90, 210, 255, 220));
            File.WriteAllText(Path.Combine(rawDir, "knight.rig2d.json"), BuildTestKnightJson(), new UTF8Encoding(false));

            string packPath = Path.Combine(distDir, "knight_gpu.bpack");
            AssetPackBuilder.BuildPackFromDirectory(rawDir, packPath, PackCompressionMode.Brotli);

            BundleId bId = BundleSystem.RegisterPack(ref bundles, "knight_gpu", 2 * 1024 * 1024, packPath);
            BundleSystem.Acquire(ref bundles, bId, ref world, ref alloc.Scratch);

            Rig2DAssetHandle assetH = Rig2DSystem.FindAsset(in world.Rigs, "knight");
            Assert.True(assetH.IsValid, "GPU test Rig2D asset loaded from .bpack");

            CameraHandle camH = CameraSystem.CreateOrthographicUI(ref world.Cameras, 640f, 360f);
            CameraData* cam = CameraSystem.TryGetPtr(in world.Cameras, camH);
            cam->ClearFlags = CameraClearFlags.SolidColor;
            cam->ClearColor = new ColorF(0.06f, 0.08f, 0.14f, 1f);

            // Spawn 3 animated rigs sharing the SINGLE BC3 atlas -> must coalesce into 1 GPU draw call!
            for (int i = 0; i < 3; i++)
            {
                EntityId e = EntitySystem.Spawn(ref world.Entities);
                TransformSystem.Add(ref world.Transforms, e, new Vec3(140f + i * 180f, 140f, 0f));
                Rig2DInstanceHandle rh = Rig2DSystem.AddInstance(ref world.Rigs, e, assetH, layerMask: 1u << 31, depthTest: false);
                Rig2DInstanceData* inst = Rig2DSystem.TryGetInstance(in world.Rigs, rh);
                inst->Scale = new Vec2(100f, 100f); // UI camera uses pixel units (1 world unit = 1 pixel)
                if (i == 1) Rig2DSystem.SetHitFlash(ref world.Rigs, rh, 0.45f);
                if (i == 2) Rig2DSystem.SetFlip(ref world.Rigs, rh, flipX: true, flipY: false);
                Rig2DSystem.SetAnimation(ref world.Rigs, rh, "idle", track: 0, loop: true);
            }

            // Advance 20 frames so animations & springs pose dynamically
            for (int f = 0; f < 20; f++)
            {
                alloc.BeginFrame();
                EngineWorldSystem.StepCoreSystems(ref world, ref alloc, 1f / 60f, 640, 360);
            }

            BatchRenderer.UploadPendingAssets(ref *gpu, ref world);
            GpuSystem.EnsureCaptureTarget(ref *gpu, 640, 360);
            BatchRenderer.RenderFrame(ref *gpu, ref world, ref alloc, SceneLighting.Default, captureToOffscreen: true);

            RenderStats st = gpu->LastFrameStats;
            Assert.Equal(3, st.Rig2DInstances, "3 Rig2D instances rendered");
            Assert.Equal(1, st.DrawCalls, $"All 3 Rig2D characters (regions + weighted meshes) sharing BC3 atlas rendered in 1 draw call (got {st.DrawCalls})");
            Assert.Equal(1, st.SetPassCalls, $"1 SetPassCall for batched Rig2D atlas (got {st.SetPassCalls})");
            Assert.True(st.Triangles >= 12, $"Rendered Rig2D triangles >= 12 (got {st.Triangles})");

            // Read back pixels and verify non-blank rendered character pixels!
            byte[] rgba = new byte[640 * 360 * 4];
            Assert.True(GpuSystem.ReadCaptureTextureRgba(ref *gpu, rgba), "ReadCaptureTextureRgba succeeded");
            int litPixels = 0;
            for (int i = 0; i < 640 * 360; i++)
            {
                if (rgba[i * 4 + 0] > 80 || rgba[i * 4 + 1] > 100)
                    litPixels++;
            }
            Assert.True(litPixels > 1000, $"Captured Rig2D frame has substantial visible character pixels (got {litPixels})");

            string pngPath = Path.Combine(AppContext.BaseDirectory, "verify_rig2d_test.png");
            GpuSystem.SaveCaptureTextureToPng(ref *gpu, pngPath);
            Assert.True(File.Exists(pngPath) && new FileInfo(pngPath).Length > 1024, "Saved verify_rig2d_test.png");
        }
        finally
        {
            BundleSystem.UnloadAll(ref bundles, ref world);
            GpuSystem.Dispose(ref *gpu, ref world.Assets);
            PlatformSystem.Shutdown(ref window);
            alloc.Dispose();
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero native leaks after GPU batched Rig2D render test");
    }

    private static string BuildTestKnightJson() => """
    {
      "name": "knight",
      "pixelsPerUnit": 100,
      "origin": [0, 0],
      "size": [200, 240],
      "bones": [
        { "name": "root", "x": 0, "y": 0 },
        { "name": "torso", "parent": "root", "x": 0, "y": 40, "length": 80 },
        { "name": "cape_0", "parent": "torso", "x": -10, "y": 50, "rotation": -110, "length": 45 },
        { "name": "cape_1", "parent": "cape_0", "x": 45, "y": 0, "rotation": 10, "length": 45 }
      ],
      "slots": [
        { "name": "cape", "bone": "cape_0", "attachment": "cape_mesh" },
        { "name": "torso", "bone": "torso", "attachment": "torso_img" }
      ],
      "skins": {
        "default": {
          "cape": {
            "cape_mesh": {
              "type": "mesh",
              "image": "parts/cape",
              "vertices": [[-45, 70], [5, 70], [10, 10], [-50, 10]],
              "uvs": [[0, 0], [1, 0], [1, 1], [0, 1]],
              "triangles": [0, 1, 2, 0, 2, 3],
              "weights": [
                [["cape_0", 0.9], ["cape_1", 0.1]],
                [["cape_0", 0.8], ["cape_1", 0.2]],
                [["cape_0", 0.2], ["cape_1", 0.8]],
                [["cape_0", 0.2], ["cape_1", 0.8]]
              ]
            }
          },
          "torso": {
            "torso_img": { "type": "region", "image": "parts/torso", "x": 0, "y": 40, "width": 72, "height": 90 }
          }
        }
      },
      "constraints": {
        "spring": [
          { "name": "cape_sp", "order": 0, "bone": "cape_1", "inertia": 0.6, "strength": 60, "damping": 7, "mass": 1, "gravity": 100, "wind": 18, "maxAngle": 30, "mix": 1, "rotateScale": 1 }
        ]
      },
      "animations": {
        "idle": {
          "duration": 1.0,
          "bones": {
            "torso": {
              "rotate": [
                { "time": 0.0, "value": -4, "curve": "easeInOutSine" },
                { "time": 0.5, "value": 4, "curve": "easeInOutSine" },
                { "time": 1.0, "value": -4 }
              ]
            }
          }
        }
      }
    }
    """;

    private static byte[] MakeSolidPng(int w, int h, byte r, byte g, byte b, byte a)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            bool border = x < 4 || y < 4 || x >= w - 4 || y >= h - 4;
            rgba[o + 0] = border ? (byte)255 : r;
            rgba[o + 1] = border ? (byte)245 : g;
            rgba[o + 2] = border ? (byte)200 : b;
            rgba[o + 3] = a;
        }
        using var ms = new MemoryStream();
        var writer = new ImageWriter();
        writer.WritePng(rgba, w, h, ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }
}

using System;
using System.Runtime.InteropServices;
using BringEngine.Assets;
using BringEngine.Core.Handles;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.Tween;

namespace BringEngine.Simulation.World;

/// <summary>
/// Top-level unmanaged engine world aggregating all simulation & asset data tables.
/// Works 100% headlessly (zero SDL/GPU dependencies).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct EngineWorld
{
    public EntityTable Entities;
    public TransformTable Transforms;
    public CameraTable Cameras;
    public TweenTable Tweens;
    public AnimationTable Animations;
    public ParticleTable Particles;
    public RenderComponentTables RenderComponents;
    public AssetRegistry Assets;
    public InputState Input;
    public ulong TickCount;
    public float TotalTime;

    public static EngineWorld Create(
        ref LinearArena persistentArena,
        int maxEntities = 16384,
        int maxParticles = 16384)
    {
        return new EngineWorld
        {
            Entities = EntityTable.Create(ref persistentArena, maxEntities),
            Transforms = TransformTable.Create(ref persistentArena, maxEntities, maxEntities),
            Cameras = CameraTable.Create(ref persistentArena, 32),
            Tweens = TweenTable.Create(ref persistentArena, 4096),
            Animations = AnimationTable.Create(ref persistentArena, 256, 512, 1024, 256, 2048),
            Particles = ParticleTable.Create(ref persistentArena, maxParticles, 256),
            RenderComponents = RenderComponentTables.Create(ref persistentArena, 8192, 4096, 1024),
            Assets = AssetRegistry.Create(ref persistentArena, 1024, 512, 1024, 64, 256),
            Input = default,
            TickCount = 0,
            TotalTime = 0f,
        };
    }
}

public static unsafe class EngineWorldSystem
{
    public static void DestroyEntity(ref EngineWorld world, EntityId entity)
    {
        if (!EntitySystem.IsAlive(in world.Entities, entity)) return;
        TransformSystem.Remove(ref world.Transforms, entity);
        AnimationSystem.RemoveByEntity(ref world.Animations, entity);
        RenderComponentSystem.RemoveByEntity(ref world.RenderComponents, entity);
        EntitySystem.Destroy(ref world.Entities, entity);
    }

    public static void DestroySceneObjects(ref EngineWorld world, SceneId scene, ref LinearArena tempArena)
    {
        TweenSystem.KillByScene(ref world.Tweens, scene);
        CameraSystem.DestroyByScene(ref world.Cameras, scene);
        ParticleSystem.DestroyByScene(ref world.Particles, scene);
        AnimationSystem.RemoveByScene(ref world.Animations, scene);
        RenderComponentSystem.RemoveByScene(ref world.RenderComponents, scene);

        ArenaMarker mark = tempArena.SaveMarker();
        var entities = EntitySystem.CollectByScene(in world.Entities, scene, ref tempArena);
        for (int i = 0; i < entities.Length; i++)
        {
            TransformSystem.Remove(ref world.Transforms, entities[i]);
            EntitySystem.Destroy(ref world.Entities, entities[i]);
        }
        tempArena.Rollback(mark);
    }

    /// <summary>
    /// Advances core engine simulation systems (Tweens -> Transforms -> Animations -> Particles -> Cameras -> Text clocks).
    /// Completely headless and zero-GC. Pass an optional JobScheduler* to accelerate large transform hierarchies
    /// and particle pools automatically above their parallel thresholds (small counts stay single-threaded inline).
    /// </summary>
    public static void StepCoreSystems(
        ref EngineWorld world,
        ref EngineAllocators alloc,
        float dt,
        float viewportWidth = 1280f,
        float viewportHeight = 720f,
        JobScheduler* jobs = null)
    {
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, dt);
        TransformSystem.PropagateHierarchy(ref world.Transforms, jobs);
        AnimationSystem.StepAll(ref world.Animations, dt, ref alloc.Frame, ref alloc.Scratch);
        ParticleSystem.Step(ref world.Particles, in world.Transforms, dt, jobs);
        CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, dt, viewportWidth, viewportHeight);
        RenderComponentSystem.StepTextAnimClocks(ref world.RenderComponents, dt);
        world.TickCount++;
        world.TotalTime += dt;
    }
}

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

        // Iterative zero-GC DFS over transform hierarchy so child/descendant entities (e.g. glTF sub-nodes) are destroyed too
        const int StackCap = 256;
        EntityId* stack = stackalloc EntityId[StackCap];
        int sp = 0;
        stack[sp++] = entity;

        while (sp > 0)
        {
            EntityId cur = stack[--sp];
            if (!EntitySystem.IsAlive(in world.Entities, cur)) continue;

            // Collect direct children before TransformSystem.Remove clears their Parent links
            for (int i = world.Transforms.Count - 1; i >= 0; i--)
            {
                if (world.Transforms.Parent[i] == cur)
                {
                    EntityId child = world.Transforms.DenseEntity[i];
                    if (sp < StackCap)
                    {
                        stack[sp++] = child;
                    }
                    else
                    {
                        DestroyEntity(ref world, child);
                        if (i >= world.Transforms.Count)
                            i = world.Transforms.Count;
                    }
                }
            }

            TransformSystem.Remove(ref world.Transforms, cur);
            AnimationSystem.RemoveByEntity(ref world.Animations, cur);
            RenderComponentSystem.RemoveByEntity(ref world.RenderComponents, cur);
            TweenSystem.KillByEntity(ref world.Tweens, cur);
            EntitySystem.Destroy(ref world.Entities, cur);
        }
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
            EntityId e = entities[i];
            TransformSystem.Remove(ref world.Transforms, e);
            AnimationSystem.RemoveByEntity(ref world.Animations, e);
            RenderComponentSystem.RemoveByEntity(ref world.RenderComponents, e);
            TweenSystem.KillByEntity(ref world.Tweens, e);
            EntitySystem.Destroy(ref world.Entities, e);
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
        TweenSystem.StepAll(ref world.Tweens, ref world.Transforms, ref world.Cameras, ref world.RenderComponents, dt);
        TransformSystem.PropagateHierarchy(ref world.Transforms, jobs);
        AnimationSystem.StepAll(ref world.Animations, dt, ref alloc.Frame, ref alloc.Scratch);
        ParticleSystem.Step(ref world.Particles, in world.Transforms, dt, jobs);
        CameraSystem.UpdateAll(ref world.Cameras, in world.Transforms, dt, viewportWidth, viewportHeight);
        RenderComponentSystem.StepTextAnimClocks(ref world.RenderComponents, dt);
        world.TickCount++;
        world.TotalTime += dt;
    }
}

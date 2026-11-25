# Agent Guide 03 — Data-Driven ECS World, Transforms & Input

## Entity Lifecycle (`EntityTable` + `EntitySystem`)
Entities are lightweight 32-bit generational handles (`EntityId`), tagged with their owning `SceneId`.
```csharp
EntityId player = EntitySystem.Spawn(ref world.Entities, sceneId);
bool alive = EntitySystem.IsAlive(in world.Entities, player);
EntitySystem.Destroy(ref world.Entities, player);
```
- Dense iteration over all alive entities: `for (int i = 0; i < entities.AliveCount; i++) { EntityId e = entities.DenseAlive[i]; ... }`
- Scene cleanup: `EntitySystem.CollectByScene(in entities, sceneId, ref frameArena)` returns all entities owned by a scene with zero GC allocations.

## SoA Transform Hierarchy (`TransformTable` + `TransformSystem`)
Transforms are stored as 64-byte aligned Structure-of-Arrays (`LocalPosition`, `LocalRotation`, `LocalScale`, `Parent`, `WorldMatrix`, `WorldPosition`).
```csharp
TransformSystem.Add(ref world.Transforms, parent, new Vec3(0, 1, 0), Quat.Identity, Vec3.One);
TransformSystem.Add(ref world.Transforms, child, new Vec3(2, 0, 0), Quat.Identity, Vec3.One, parent);

// Once per fixed step / frame before rendering:
TransformSystem.PropagateHierarchy(ref world.Transforms);
Vec3 childWorldPos = TransformSystem.GetWorldPosition(in world.Transforms, child);
```
`PropagateHierarchy` runs linearly by hierarchy depth (0, 1, 2, ...) with zero recursion and zero allocations.

## Adding Custom Game Component Tables
Follow the exact same pattern in your game project:
1. Create a `NativeSparseSet<MyComponent>` (or SoA struct) allocated from `GameAllocators.Persistent` or `GameSceneArena`.
2. Iterate `0..set.Count` densely in `public static unsafe class MyGameSystem`.
3. Remove components cleanly when destroying an entity or unloading a scene.

## Platform-Agnostic `InputState`
`InputState` is a plain unmanaged bitset struct. In windowed mode, `PlatformSystem.PollEvents` fills it from SDL3. In headless mode / tests, set keys directly (`input.BeginFrame(); input.SetKey(KeyCode.W, true);`) for deterministic replay.

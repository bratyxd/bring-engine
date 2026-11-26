using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Core.Threading;
using BringEngine.Simulation.World;

namespace BringEngine.Simulation.Particles;

public enum ParticleShape : byte
{
    Point = 0,
    Sphere = 1,
    Box = 2,
    Cone = 3,
    CircleXZ = 4,
}

public enum ParticleBlendMode : byte
{
    Alpha = 0,
    Additive = 1,
}

[StructLayout(LayoutKind.Sequential)]
public struct ParticleEmitterData
{
    public EmitterHandle Handle;
    public EntityId FollowEntity;
    public SceneId OwnerScene;
    public TextureHandle Texture;
    public ShaderHandle CustomShader;

    public ParticleShape Shape;
    public ParticleBlendMode BlendMode;
    public byte Enabled;
    public byte WorldSpace; // 1 = world space, 0 = local
    public byte StretchedBillboard;
    public byte AtlasColumns;
    public byte AtlasRows;

    public Vec3 Position;
    public Vec3 ShapeExtents;
    public Vec3 BaseDirection;
    public float SpreadAngleRad;

    public float EmitRate;        // Particles per second
    public float EmitAccumulator;
    public int PendingBurst;

    public float LifetimeMin;
    public float LifetimeMax;
    public float SpeedMin;
    public float SpeedMax;
    public float StartSizeMin;
    public float StartSizeMax;
    public float EndSizeMin;
    public float EndSizeMax;
    public float AngularVelMin;
    public float AngularVelMax;

    public Vec3 Gravity;
    public float Drag;
    public float Turbulence;

    public ColorF StartColor;
    public ColorF EndColor;
    public uint LayerMask;
    public DetRng Rng;
}

/// <summary>
/// Structure-of-Arrays (SoA) native particle pool + emitter registry.
/// Maximizes L1/L2 cache utilization when simulating thousands of particles.
/// Zero managed allocations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ParticleTable
{
    // SoA Particle Pool
    public float* PosX;
    public float* PosY;
    public float* PosZ;
    public float* VelX;
    public float* VelY;
    public float* VelZ;
    public float* Size;
    public float* StartSize;
    public float* EndSize;
    public float* Rotation;
    public float* AngularVel;
    public float* Age;
    public float* Lifetime;
    public ColorF* Color;
    public ColorF* StartColor;
    public ColorF* EndColor;
    public ushort* EmitterSlot;
    public byte* FrameIndex;
    public int ParticleCount;
    public int ParticleCapacity;

    // Emitters
    public ParticleEmitterData* Emitters;
    public uint* EmitterGens;
    public byte* EmitterAlive;
    public int EmitterCapacity;
    public int ActiveEmitters;

    public static ParticleTable Create(ref LinearArena arena, int maxParticles = 16384, int maxEmitters = 256)
    {
        if (maxEmitters <= 0 || maxEmitters > 65535)
            throw new ArgumentOutOfRangeException(nameof(maxEmitters), maxEmitters, "maxEmitters must be in [1, 65535].");
        uint* gens = arena.AllocUninitialized<uint>(maxEmitters, 16);
        for (int i = 0; i < maxEmitters; i++) gens[i] = 1;

        return new ParticleTable
        {
            PosX = arena.AllocUninitialized<float>(maxParticles, 64),
            PosY = arena.AllocUninitialized<float>(maxParticles, 64),
            PosZ = arena.AllocUninitialized<float>(maxParticles, 64),
            VelX = arena.AllocUninitialized<float>(maxParticles, 64),
            VelY = arena.AllocUninitialized<float>(maxParticles, 64),
            VelZ = arena.AllocUninitialized<float>(maxParticles, 64),
            Size = arena.AllocUninitialized<float>(maxParticles, 64),
            StartSize = arena.AllocUninitialized<float>(maxParticles, 64),
            EndSize = arena.AllocUninitialized<float>(maxParticles, 64),
            Rotation = arena.AllocUninitialized<float>(maxParticles, 64),
            AngularVel = arena.AllocUninitialized<float>(maxParticles, 64),
            Age = arena.AllocUninitialized<float>(maxParticles, 64),
            Lifetime = arena.AllocUninitialized<float>(maxParticles, 64),
            Color = arena.AllocUninitialized<ColorF>(maxParticles, 64),
            StartColor = arena.AllocUninitialized<ColorF>(maxParticles, 64),
            EndColor = arena.AllocUninitialized<ColorF>(maxParticles, 64),
            EmitterSlot = arena.AllocUninitialized<ushort>(maxParticles, 64),
            FrameIndex = arena.AllocUninitialized<byte>(maxParticles, 64),
            ParticleCount = 0,
            ParticleCapacity = maxParticles,

            Emitters = arena.Alloc<ParticleEmitterData>(maxEmitters, 64),
            EmitterGens = gens,
            EmitterAlive = arena.Alloc<byte>(maxEmitters, 16),
            EmitterCapacity = maxEmitters,
            ActiveEmitters = 0,
        };
    }
}

public static unsafe class ParticleSystem
{
    public static ParticleEmitterData DefaultEmitter(Vec3 position, ulong seed = 1337) => new ParticleEmitterData
    {
        Shape = ParticleShape.Cone,
        BlendMode = ParticleBlendMode.Additive,
        Enabled = 1,
        WorldSpace = 1,
        AtlasColumns = 1,
        AtlasRows = 1,
        Position = position,
        ShapeExtents = new Vec3(0.3f, 0.3f, 0.3f),
        BaseDirection = Vec3.Up,
        SpreadAngleRad = 0.5f,
        EmitRate = 30f,
        LifetimeMin = 0.4f,
        LifetimeMax = 1.0f,
        SpeedMin = 1.5f,
        SpeedMax = 4.0f,
        StartSizeMin = 0.18f,
        StartSizeMax = 0.35f,
        EndSizeMin = 0.02f,
        EndSizeMax = 0.06f,
        AngularVelMin = -2f,
        AngularVelMax = 2f,
        Gravity = new Vec3(0f, -3.5f, 0f),
        Drag = 0.4f,
        Turbulence = 0.25f,
        StartColor = ColorF.Gold,
        EndColor = new ColorF(1f, 0.2f, 0.05f, 0f),
        LayerMask = 1u,
        Rng = DetRng.Create(seed),
    };

    public static EmitterHandle CreateEmitter(ref ParticleTable table, in ParticleEmitterData config, SceneId scene = default)
    {
        for (int i = 0; i < table.EmitterCapacity; i++)
        {
            if (table.EmitterAlive[i] == 0)
            {
                var h = EmitterHandle.Create((uint)i, table.EmitterGens[i]);
                table.EmitterAlive[i] = 1;
                table.ActiveEmitters++;
                table.Emitters[i] = config;
                table.Emitters[i].Handle = h;
                table.Emitters[i].OwnerScene = scene;
                if (table.Emitters[i].AtlasColumns == 0) table.Emitters[i].AtlasColumns = 1;
                if (table.Emitters[i].AtlasRows == 0) table.Emitters[i].AtlasRows = 1;
                if ((table.Emitters[i].Rng.S0 | table.Emitters[i].Rng.S1 | table.Emitters[i].Rng.S2 | table.Emitters[i].Rng.S3) == 0)
                    table.Emitters[i].Rng = DetRng.Create((ulong)(i + 1) * 7919UL);
                return h;
            }
        }
        throw new OutOfMemoryException("ParticleTable emitters exhausted.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParticleEmitterData* TryGetEmitter(in ParticleTable table, EmitterHandle handle)
    {
        if (!handle.IsValid || handle.Index >= (uint)table.EmitterCapacity) return null;
        if (table.EmitterAlive[handle.Index] == 0 || table.EmitterGens[handle.Index] != handle.Generation) return null;
        return table.Emitters + handle.Index;
    }

    public static void Burst(ref ParticleTable table, EmitterHandle handle, int count)
    {
        ParticleEmitterData* em = TryGetEmitter(in table, handle);
        if (em != null && count > 0)
            em->PendingBurst += count;
    }

    public static bool DestroyEmitter(ref ParticleTable table, EmitterHandle handle)
    {
        ParticleEmitterData* em = TryGetEmitter(in table, handle);
        if (em == null) return false;
        uint slot = handle.Index;
        table.EmitterAlive[slot] = 0;
        uint nextGen = (table.EmitterGens[slot] + 1) & HandlePacking.GenerationMask;
        table.EmitterGens[slot] = nextGen == 0 ? 1u : nextGen;
        table.ActiveEmitters--;

        // Kill particles owned by this slot
        for (int i = table.ParticleCount - 1; i >= 0; i--)
        {
            if (table.EmitterSlot[i] == (ushort)slot)
                SwapRemoveParticle(ref table, i);
        }
        return true;
    }

    public static void DestroyByScene(ref ParticleTable table, SceneId scene)
    {
        for (int i = 0; i < table.EmitterCapacity; i++)
        {
            if (table.EmitterAlive[i] != 0 && table.Emitters[i].OwnerScene == scene)
                DestroyEmitter(ref table, table.Emitters[i].Handle);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapRemoveParticle(ref ParticleTable t, int idx)
    {
        int last = --t.ParticleCount;
        if (idx == last) return;
        t.PosX[idx] = t.PosX[last];
        t.PosY[idx] = t.PosY[last];
        t.PosZ[idx] = t.PosZ[last];
        t.VelX[idx] = t.VelX[last];
        t.VelY[idx] = t.VelY[last];
        t.VelZ[idx] = t.VelZ[last];
        t.Size[idx] = t.Size[last];
        t.StartSize[idx] = t.StartSize[last];
        t.EndSize[idx] = t.EndSize[last];
        t.Rotation[idx] = t.Rotation[last];
        t.AngularVel[idx] = t.AngularVel[last];
        t.Age[idx] = t.Age[last];
        t.Lifetime[idx] = t.Lifetime[last];
        t.Color[idx] = t.Color[last];
        t.StartColor[idx] = t.StartColor[last];
        t.EndColor[idx] = t.EndColor[last];
        t.EmitterSlot[idx] = t.EmitterSlot[last];
        t.FrameIndex[idx] = t.FrameIndex[last];
    }

    private static void SpawnParticle(ref ParticleTable t, ushort emitterSlot, ref ParticleEmitterData em)
    {
        if (t.ParticleCount >= t.ParticleCapacity) return;
        int idx = t.ParticleCount++;

        Vec3 offset = Vec3.Zero;
        switch (em.Shape)
        {
            case ParticleShape.Sphere:
                offset = em.Rng.NextUnitSphere() * (em.Rng.NextFloat01() * em.ShapeExtents.X);
                break;
            case ParticleShape.Box:
                offset = new Vec3(
                    em.Rng.NextRange(-em.ShapeExtents.X, em.ShapeExtents.X),
                    em.Rng.NextRange(-em.ShapeExtents.Y, em.ShapeExtents.Y),
                    em.Rng.NextRange(-em.ShapeExtents.Z, em.ShapeExtents.Z));
                break;
            case ParticleShape.CircleXZ:
                Vec2 c = em.Rng.NextUnitCircle() * em.ShapeExtents.X;
                offset = new Vec3(c.X, 0f, c.Y);
                break;
            case ParticleShape.Cone:
                Vec2 disk = em.Rng.NextUnitCircle() * (em.Rng.NextFloat01() * em.ShapeExtents.X * 0.25f);
                offset = new Vec3(disk.X, 0f, disk.Y);
                break;
        }

        Vec3 dir = em.BaseDirection.Normalized();
        if (dir.LengthSq() < 1e-6f) dir = Vec3.Up;
        if (em.SpreadAngleRad > 1e-4f)
        {
            Vec3 randVec = em.Rng.NextUnitSphere();
            dir = Vec3.Lerp(dir, randVec, DetMath.Clamp01(em.SpreadAngleRad / DetMath.Pi)).Normalized();
        }
        float speed = em.Rng.NextRange(em.SpeedMin, em.SpeedMax);
        Vec3 vel = dir * speed;

        Vec3 p = em.Position + offset;
        float startSz = em.Rng.NextRange(em.StartSizeMin, em.StartSizeMax);
        float endSz = em.Rng.NextRange(em.EndSizeMin, em.EndSizeMax);
        float life = DetMath.Max(0.02f, em.Rng.NextRange(em.LifetimeMin, em.LifetimeMax));

        t.PosX[idx] = p.X;
        t.PosY[idx] = p.Y;
        t.PosZ[idx] = p.Z;
        t.VelX[idx] = vel.X;
        t.VelY[idx] = vel.Y;
        t.VelZ[idx] = vel.Z;
        t.StartSize[idx] = startSz;
        t.EndSize[idx] = endSz;
        t.Size[idx] = startSz;
        t.Rotation[idx] = em.Rng.NextRange(0f, DetMath.TwoPi);
        t.AngularVel[idx] = em.Rng.NextRange(em.AngularVelMin, em.AngularVelMax);
        t.Age[idx] = 0f;
        t.Lifetime[idx] = life;
        t.StartColor[idx] = em.StartColor;
        t.EndColor[idx] = em.EndColor;
        t.Color[idx] = em.StartColor;
        t.EmitterSlot[idx] = emitterSlot;
        t.FrameIndex[idx] = 0;
    }

    /// <summary>
    /// Minimum live particle count before Step fans out integration across JobSystem workers.
    /// Below this threshold, single-threaded L1 linear iteration is faster and runs inline with zero sync cost.
    /// </summary>
    public const int ParallelThreshold = 2048;

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleIntegrateJobCtx
    {
        public ParticleTable* Table;
        public float Dt;
    }

    private static void IntegrateParticleRange(void* userData, int begin, int end, int workerIndex)
    {
        _ = workerIndex;
        ParticleIntegrateJobCtx* ctx = (ParticleIntegrateJobCtx*)userData;
        ref ParticleTable t = ref *ctx->Table;
        float dt = ctx->Dt;

        for (int i = begin; i < end; i++)
        {
            float age = t.Age[i];
            float life = t.Lifetime[i];
            ushort eslot = t.EmitterSlot[i];
            ref ParticleEmitterData em = ref t.Emitters[eslot];

            float dragFactor = DetMath.Max(0f, 1f - em.Drag * dt);
            float vx = (t.VelX[i] + em.Gravity.X * dt) * dragFactor;
            float vy = (t.VelY[i] + em.Gravity.Y * dt) * dragFactor;
            float vz = (t.VelZ[i] + em.Gravity.Z * dt) * dragFactor;

            if (em.Turbulence > 0f)
            {
                float phase = age * 5.7f + i * 0.37f;
                vx += DetMath.Sin(phase) * em.Turbulence * dt;
                vy += DetMath.Cos(phase * 1.3f) * em.Turbulence * dt;
                vz += DetMath.Sin(phase * 0.8f + 2.1f) * em.Turbulence * dt;
            }

            t.VelX[i] = vx;
            t.VelY[i] = vy;
            t.VelZ[i] = vz;

            t.PosX[i] += vx * dt;
            t.PosY[i] += vy * dt;
            t.PosZ[i] += vz * dt;
            t.Rotation[i] += t.AngularVel[i] * dt;

            float norm = age / life;
            t.Size[i] = DetMath.Lerp(t.StartSize[i], t.EndSize[i], norm);
            t.Color[i] = ColorF.Lerp(t.StartColor[i], t.EndColor[i], norm);

            int totalFrames = em.AtlasColumns * em.AtlasRows;
            if (totalFrames > 1)
            {
                int frame = (int)(norm * totalFrames);
                t.FrameIndex[i] = (byte)(frame >= totalFrames ? totalFrames - 1 : frame);
            }
        }
    }

    /// <summary>
    /// Deterministic SoA particle simulation step. Runs identically headlessly or with GPU view.
    /// Optionally fans out integration across JobSystem workers ONLY when ParticleCount >= ParallelThreshold;
    /// smaller counts stay single-threaded inline. Bit-exact identical output either way. Zero GC allocations.
    /// </summary>
    public static void Step(ref ParticleTable t, in TransformTable transforms, float dt, JobScheduler* jobs = null)
    {
        // 1. Emit from active emitters (sequential: preserves exact DetRng order)
        for (int s = 0; s < t.EmitterCapacity; s++)
        {
            if (t.EmitterAlive[s] == 0) continue;
            ref ParticleEmitterData em = ref t.Emitters[s];

            Vec3 prevPos = em.Position;
            if (em.FollowEntity.IsValid)
            {
                int td = TransformSystem.GetDenseIndex(in transforms, em.FollowEntity);
                if (td >= 0)
                    em.Position = transforms.WorldPosition[td];
            }

            // Local-space emitters (WorldSpace == 0) carry their active particles along with emitter movement
            if (em.WorldSpace == 0)
            {
                Vec3 delta = em.Position - prevPos;
                if (delta.LengthSq() > 0f)
                {
                    for (int pIdx = 0; pIdx < t.ParticleCount; pIdx++)
                    {
                        if (t.EmitterSlot[pIdx] == (ushort)s)
                        {
                            t.PosX[pIdx] += delta.X;
                            t.PosY[pIdx] += delta.Y;
                            t.PosZ[pIdx] += delta.Z;
                        }
                    }
                }
            }

            if (em.PendingBurst > 0)
            {
                int burst = em.PendingBurst;
                em.PendingBurst = 0;
                int avail = t.ParticleCapacity - t.ParticleCount;
                if (burst > avail) burst = avail;
                for (int b = 0; b < burst; b++)
                    SpawnParticle(ref t, (ushort)s, ref em);
            }

            if (em.Enabled != 0 && em.EmitRate > 0f)
            {
                em.EmitAccumulator += em.EmitRate * dt;
                if (em.EmitAccumulator >= 1f)
                {
                    int avail = t.ParticleCapacity - t.ParticleCount;
                    float whole = MathF.Floor(em.EmitAccumulator);
                    int spawnCount = whole > avail ? avail : (int)whole;
                    em.EmitAccumulator = whole > avail ? (em.EmitAccumulator - whole) : (em.EmitAccumulator - spawnCount);
                    for (int k = 0; k < spawnCount; k++)
                        SpawnParticle(ref t, (ushort)s, ref em);
                }
            }
        }

        // 2. Advance ages & swap-remove expired particles sequentially (preserves exact dense layout)
        int i = 0;
        while (i < t.ParticleCount)
        {
            float age = t.Age[i] + dt;
            if (age >= t.Lifetime[i])
            {
                SwapRemoveParticle(ref t, i);
                continue;
            }
            t.Age[i] = age;
            i++;
        }

        if (t.ParticleCount == 0) return;

        // 3. Integrate surviving particles (inline when < ParallelThreshold or jobs == null, parallel above)
        fixed (ParticleTable* tPtr = &t)
        {
            var ctx = new ParticleIntegrateJobCtx { Table = tPtr, Dt = dt };
            JobSystem.ParallelFor(
                jobs,
                t.ParticleCount,
                batchSize: 512,
                userData: &ctx,
                callback: &IntegrateParticleRange,
                minParallelCount: ParallelThreshold);
        }
    }
}

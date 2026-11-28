using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Assets;
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

public enum ParticleFlipbookMode : byte
{
    OverLifetime = 0,
    Loop = 1,
    Hold = 2,
    PingPong = 3,
    RandomStatic = 4,
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
    public Vec3 PrevPosition;
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

    // Atlas-aware flipbook parameters (all zero defaults preserve exact v0.11 OverLifetime behavior)
    public Vec4 BaseUvRect;            // (u0, v0, uWidth, vHeight); Z <= 0 or W <= 0 treated as (0, 0, 1, 1)
    public ParticleFlipbookMode FlipbookMode;
    public byte FlipbookRandomStart;   // 1 = random initial frame offset via em.Rng on spawn
    public byte FlipbookBlend;         // 1 = compute NextFrameIndex & FrameBlend for smooth GPU cross-frame blend
    public ushort FlipbookFrameCount;  // 0 = AtlasColumns * AtlasRows; > 0 clamped to <= AtlasColumns * AtlasRows
    public float FlipbookFps;          // Used by Loop/Hold/PingPong; <= 0 falls back to frameCount / lifetime
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
    public byte* NextFrameIndex;
    public float* FrameBlend;
    public ushort* StartFrameOffset;
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
        if (maxParticles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxParticles), maxParticles, "maxParticles must be >= 1.");
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
            NextFrameIndex = arena.AllocUninitialized<byte>(maxParticles, 64),
            FrameBlend = arena.AllocUninitialized<float>(maxParticles, 64),
            StartFrameOffset = arena.AllocUninitialized<ushort>(maxParticles, 64),
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

    /// <summary>
    /// Configures an emitter to slice a flipbook grid inside a packed atlas SpriteRegionData sub-rectangle.
    /// Zero GC allocations.
    /// </summary>
    public static void SetFlipbookRegion(
        ref ParticleEmitterData cfg,
        in SpriteRegionData region,
        byte columns,
        byte rows,
        ushort frameCount = 0,
        ParticleFlipbookMode mode = ParticleFlipbookMode.OverLifetime,
        float fps = 0f,
        bool blendFrames = false,
        bool randomStart = false)
    {
        cfg.Texture = region.AtlasTexture;
        cfg.BaseUvRect = region.UvRect;
        cfg.AtlasColumns = columns > 0 ? columns : (byte)1;
        cfg.AtlasRows = rows > 0 ? rows : (byte)1;
        cfg.FlipbookFrameCount = frameCount;
        cfg.FlipbookMode = mode;
        cfg.FlipbookFps = (float.IsFinite(fps) && fps > 0f) ? fps : 0f;
        cfg.FlipbookBlend = (byte)(blendFrames ? 1 : 0);
        cfg.FlipbookRandomStart = (byte)(randomStart ? 1 : 0);
    }

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
                table.Emitters[i].PrevPosition = config.Position;
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
        t.NextFrameIndex[idx] = t.NextFrameIndex[last];
        t.FrameBlend[idx] = t.FrameBlend[last];
        t.StartFrameOffset[idx] = t.StartFrameOffset[last];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveActiveFlipbookFrames(in ParticleEmitterData em)
    {
        int cols = em.AtlasColumns > 0 ? em.AtlasColumns : 1;
        int rows = em.AtlasRows > 0 ? em.AtlasRows : 1;
        int gridFrames = cols * rows;
        return (em.FlipbookFrameCount > 0 && em.FlipbookFrameCount < gridFrames)
            ? em.FlipbookFrameCount
            : gridFrames;
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

        int activeFrames = ResolveActiveFlipbookFrames(in em);
        int usableFrames = activeFrames > 256 ? 256 : activeFrames;
        ushort startOff = 0;
        if (usableFrames > 1 && (em.FlipbookMode == ParticleFlipbookMode.RandomStatic || em.FlipbookRandomStart != 0))
            startOff = (ushort)em.Rng.NextInt(0, usableFrames);
        byte startByte = (byte)startOff;
        t.FrameIndex[idx] = startByte;
        t.NextFrameIndex[idx] = startByte;
        t.FrameBlend[idx] = 0f;
        t.StartFrameOffset[idx] = startOff;
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

            float safeLife = (float.IsFinite(life) && life > 1e-6f) ? life : 1f;
            float norm = DetMath.Clamp01(age / safeLife);
            t.Size[i] = DetMath.Lerp(t.StartSize[i], t.EndSize[i], norm);
            t.Color[i] = ColorF.Lerp(t.StartColor[i], t.EndColor[i], norm);

            int activeFrames = ResolveActiveFlipbookFrames(in em);
            if (activeFrames > 1)
            {
                bool doBlend = em.FlipbookBlend != 0;
                int startOff = t.StartFrameOffset[i];
                int f0 = 0;
                int f1 = 0;
                float blend = 0f;

                switch (em.FlipbookMode)
                {
                    case ParticleFlipbookMode.RandomStatic:
                    {
                        int usable = activeFrames > 256 ? 256 : activeFrames;
                        f0 = startOff < usable ? startOff : (usable - 1);
                        f1 = f0;
                        blend = 0f;
                        break;
                    }
                    case ParticleFlipbookMode.Hold:
                    {
                        int usable = activeFrames > 256 ? 256 : activeFrames;
                        int maxF = usable - 1;
                        float fps = (float.IsFinite(em.FlipbookFps) && em.FlipbookFps > 0f)
                            ? em.FlipbookFps
                            : (usable / safeLife);
                        double rawD = (double)startOff + (double)age * (double)fps;
                        if (!double.IsFinite(rawD) || rawD <= 0.0)
                        {
                            f0 = 0;
                            f1 = 0;
                            blend = 0f;
                        }
                        else if (rawD >= (double)maxF)
                        {
                            f0 = maxF;
                            f1 = maxF;
                            blend = 0f;
                        }
                        else
                        {
                            double fl = Math.Floor(rawD);
                            f0 = (int)fl;
                            f1 = f0 + 1;
                            blend = doBlend ? (float)(rawD - fl) : 0f;
                        }
                        break;
                    }
                    case ParticleFlipbookMode.Loop:
                    {
                        int usable = activeFrames > 256 ? 256 : activeFrames;
                        float fps = (float.IsFinite(em.FlipbookFps) && em.FlipbookFps > 0f)
                            ? em.FlipbookFps
                            : (usable / safeLife);
                        double rawD = (double)startOff + (double)age * (double)fps;
                        if (!double.IsFinite(rawD)) rawD = 0.0;
                        double fl = Math.Floor(rawD);
                        double mod = fl % usable;
                        if (mod < 0.0) mod += usable;
                        f0 = (int)mod;
                        if (f0 < 0) f0 = 0;
                        else if (f0 >= usable) f0 = usable - 1;
                        f1 = (f0 + 1) % usable;
                        blend = doBlend ? (float)(rawD - fl) : 0f;
                        break;
                    }
                    case ParticleFlipbookMode.PingPong:
                    {
                        int usable = activeFrames > 256 ? 256 : activeFrames;
                        int period = 2 * (usable - 1);
                        float fps = (float.IsFinite(em.FlipbookFps) && em.FlipbookFps > 0f)
                            ? em.FlipbookFps
                            : (usable / safeLife);
                        double rawD = (double)startOff + (double)age * (double)fps;
                        if (!double.IsFinite(rawD)) rawD = 0.0;
                        double fl = Math.Floor(rawD);
                        double mod = fl % period;
                        if (mod < 0.0) mod += period;
                        int idx0 = (int)mod;
                        if (idx0 < 0) idx0 = 0;
                        else if (idx0 >= period) idx0 = period - 1;
                        int idx1 = (idx0 + 1) % period;
                        f0 = idx0 < usable ? idx0 : (period - idx0);
                        f1 = idx1 < usable ? idx1 : (period - idx1);
                        blend = doBlend ? (float)(rawD - fl) : 0f;
                        break;
                    }
                    default: // ParticleFlipbookMode.OverLifetime
                    {
                        if (!doBlend && em.FlipbookRandomStart == 0)
                        {
                            int maxFrame = activeFrames - 1;
                            if (maxFrame > 255) maxFrame = 255;
                            int frame = (int)(norm * activeFrames);
                            if (frame < 0) frame = 0;
                            else if (frame > maxFrame) frame = maxFrame;
                            f0 = frame;
                            f1 = frame;
                            blend = 0f;
                        }
                        else
                        {
                            int usable = activeFrames > 256 ? 256 : activeFrames;
                            int maxF = usable - 1;
                            if (em.FlipbookRandomStart != 0)
                            {
                                float scaled = startOff + norm * usable;
                                float fl = MathF.Floor(scaled);
                                int i0 = (int)fl;
                                f0 = ((i0 % usable) + usable) % usable;
                                f1 = (f0 + 1) % usable;
                                blend = doBlend ? (scaled - fl) : 0f;
                            }
                            else
                            {
                                float scaled = norm * maxF;
                                f0 = (int)scaled;
                                if (f0 < 0) f0 = 0;
                                else if (f0 > maxF) f0 = maxF;
                                f1 = f0 < maxF ? f0 + 1 : maxF;
                                blend = f0 < maxF ? (scaled - f0) : 0f;
                            }
                        }
                        break;
                    }
                }

                t.FrameIndex[i] = (byte)f0;
                t.NextFrameIndex[i] = (byte)f1;
                t.FrameBlend[i] = doBlend ? DetMath.Clamp01(blend) : 0f;
            }
            else
            {
                t.FrameIndex[i] = 0;
                t.NextFrameIndex[i] = 0;
                t.FrameBlend[i] = 0f;
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
        if (!float.IsFinite(dt) || dt < 0f) return;

        // 1. Emit from active emitters (sequential: preserves exact DetRng order)
        for (int s = 0; s < t.EmitterCapacity; s++)
        {
            if (t.EmitterAlive[s] == 0) continue;
            ref ParticleEmitterData em = ref t.Emitters[s];

            if (em.FollowEntity.IsValid)
            {
                int td = TransformSystem.GetDenseIndex(in transforms, em.FollowEntity);
                if (td >= 0)
                    em.Position = transforms.WorldPosition[td];
            }

            // Local-space emitters (WorldSpace == 0) carry their active particles along with emitter movement
            // (tracks both FollowEntity and manual em->Position updates via PrevPosition)
            if (em.WorldSpace == 0)
            {
                Vec3 delta = em.Position - em.PrevPosition;
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
            em.PrevPosition = em.Position;

            if (em.PendingBurst > 0)
            {
                int burst = em.PendingBurst;
                em.PendingBurst = 0;
                int avail = t.ParticleCapacity - t.ParticleCount;
                if (burst > avail) burst = avail;
                for (int b = 0; b < burst; b++)
                    SpawnParticle(ref t, (ushort)s, ref em);
            }

            if (em.Enabled != 0 && float.IsFinite(em.EmitRate) && em.EmitRate > 0f)
            {
                if (!float.IsFinite(em.EmitAccumulator) || em.EmitAccumulator < 0f)
                    em.EmitAccumulator = 0f;
                em.EmitAccumulator += em.EmitRate * dt;
                if (!float.IsFinite(em.EmitAccumulator))
                {
                    int avail = t.ParticleCapacity - t.ParticleCount;
                    em.EmitAccumulator = 0f;
                    for (int k = 0; k < avail; k++)
                        SpawnParticle(ref t, (ushort)s, ref em);
                }
                else if (em.EmitAccumulator >= 1f)
                {
                    int avail = t.ParticleCapacity - t.ParticleCount;
                    float whole = MathF.Floor(em.EmitAccumulator);
                    int spawnCount = whole > avail ? avail : (int)whole;
                    em.EmitAccumulator = whole > avail ? (em.EmitAccumulator - whole) : (em.EmitAccumulator - spawnCount);
                    if (!float.IsFinite(em.EmitAccumulator) || em.EmitAccumulator < 0f)
                        em.EmitAccumulator = 0f;
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

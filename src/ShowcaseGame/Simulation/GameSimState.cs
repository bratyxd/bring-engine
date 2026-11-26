using System.Runtime.InteropServices;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;

namespace ShowcaseGame.Simulation;

public enum SimEventKind : byte
{
    None = 0,
    EnemySpawned = 1,
    EnemyHit = 2,
    EnemyKilled = 3,
    CrystalCollected = 4,
    PlayerDashed = 5,
    PlayerDamaged = 6,
    WaveAdvanced = 7,
}

[StructLayout(LayoutKind.Sequential)]
public struct SimEvent
{
    public SimEventKind Kind;
    public int Value;
    public Vec3 Position;
}

/// <summary>
/// 100% pure deterministic gameplay simulation state (ZERO SDL, ZERO GPU, ZERO OOP).
/// Uses Fix32/FixVec3 + DetRng + SoA native arrays for L1 cache locality and bit-exact headless replay.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct GameSimState
{
    public const int MaxEnemies = 128;
    public const int MaxProjectiles = 256;
    public const int MaxPickups = 128;
    public const int EventRingCapacity = 256;
    public const ulong EventRingMask = EventRingCapacity - 1;

    // Arena bounds (Fix32)
    public Fix32 ArenaHalfSize;

    // Player deterministic state
    public FixVec3 PlayerPos;
    public FixVec3 PlayerVel;
    public Fix32 PlayerYaw;
    public Fix32 PlayerHp;
    public Fix32 PlayerMaxHp;
    public Fix32 FireCooldown;
    public Fix32 DashCooldown;
    public Fix32 DashTimer;
    public Fix32 InvulnTimer;

    public int Score;
    public int Combo;
    public Fix32 ComboTimer;
    public int Wave;
    public int KillsInWave;
    public Fix32 SpawnTimer;
    public ulong SimTick;
    public byte GameOver;

    // Enemy SoA tables (cache-linear, allocated in GameAllocators.Persistent)
    public FixVec3* EnemyPos;
    public FixVec3* EnemyVel;
    public Fix32* EnemyHp;
    public Fix32* EnemyMaxHp;
    public Fix32* EnemyHitFlash;
    public Fix32* EnemyAngle;
    public byte* EnemyKind;   // 0 = Chaser Crystal, 1 = Orbital Sentinel
    public byte* EnemyAlive;
    public int EnemyActiveCount;

    // Projectile SoA tables
    public FixVec3* ProjPos;
    public FixVec3* ProjVel;
    public Fix32* ProjLife;
    public byte* ProjFromPlayer;
    public byte* ProjAlive;
    public int ProjActiveCount;

    // Crystal Pickup SoA tables
    public FixVec3* PickupPos;
    public Fix32* PickupBobPhase;
    public int* PickupValue;
    public byte* PickupAlive;
    public int PickupActiveCount;

    // Monotonic fixed-size event ring buffer consumed by View layer via cursor (never mutates sim!)
    public SimEvent* Events;
    public ulong EventWriteSeq;

    public DetRng Rng;

    public static GameSimState Create(ref LinearArena persistentArena, ulong seed = 20260417UL)
    {
        var st = new GameSimState
        {
            ArenaHalfSize = Fix32.FromInt(12),
            EnemyPos = persistentArena.Alloc<FixVec3>(MaxEnemies, 64),
            EnemyVel = persistentArena.Alloc<FixVec3>(MaxEnemies, 64),
            EnemyHp = persistentArena.Alloc<Fix32>(MaxEnemies, 64),
            EnemyMaxHp = persistentArena.Alloc<Fix32>(MaxEnemies, 64),
            EnemyHitFlash = persistentArena.Alloc<Fix32>(MaxEnemies, 64),
            EnemyAngle = persistentArena.Alloc<Fix32>(MaxEnemies, 64),
            EnemyKind = persistentArena.Alloc<byte>(MaxEnemies, 64),
            EnemyAlive = persistentArena.Alloc<byte>(MaxEnemies, 64),

            ProjPos = persistentArena.Alloc<FixVec3>(MaxProjectiles, 64),
            ProjVel = persistentArena.Alloc<FixVec3>(MaxProjectiles, 64),
            ProjLife = persistentArena.Alloc<Fix32>(MaxProjectiles, 64),
            ProjFromPlayer = persistentArena.Alloc<byte>(MaxProjectiles, 64),
            ProjAlive = persistentArena.Alloc<byte>(MaxProjectiles, 64),

            PickupPos = persistentArena.Alloc<FixVec3>(MaxPickups, 64),
            PickupBobPhase = persistentArena.Alloc<Fix32>(MaxPickups, 64),
            PickupValue = persistentArena.Alloc<int>(MaxPickups, 64),
            PickupAlive = persistentArena.Alloc<byte>(MaxPickups, 64),

            Events = persistentArena.Alloc<SimEvent>(EventRingCapacity, 64),
            Rng = DetRng.Create(seed),
        };
        GameSimSystems.ResetMatch(ref st, seed);
        return st;
    }
}

using System.Runtime.CompilerServices;
using BringEngine.Core.Math;
using BringEngine.Simulation.Input;

namespace ShowcaseGame.Simulation;

/// <summary>
/// Pure deterministic gameplay systems operating strictly on GameSimState.
/// Zero SDL/GPU calls, zero managed heap allocations, 100% headless-testable.
/// </summary>
public static unsafe class GameSimSystems
{
    public static void ResetMatch(ref GameSimState s, ulong seed)
    {
        s.Rng = DetRng.Create(seed);
        s.PlayerPos = new FixVec3(Fix32.Zero, Fix32.Zero, Fix32.FromInt(2));
        s.PlayerVel = FixVec3.Zero;
        s.PlayerYaw = Fix32.Zero;
        s.PlayerMaxHp = Fix32.FromInt(100);
        s.PlayerHp = s.PlayerMaxHp;
        s.FireCooldown = Fix32.Zero;
        s.DashCooldown = Fix32.Zero;
        s.DashTimer = Fix32.Zero;
        s.InvulnTimer = Fix32.Zero;
        s.Score = 0;
        s.Combo = 1;
        s.ComboTimer = Fix32.Zero;
        s.Wave = 1;
        s.KillsInWave = 0;
        s.SpawnTimer = Fix32.FromFloat(0.25f);
        s.SimTick = 0;
        s.GameOver = 0;
        s.EventCount = 0;

        for (int i = 0; i < GameSimState.MaxEnemies; i++) s.EnemyAlive[i] = 0;
        for (int i = 0; i < GameSimState.MaxProjectiles; i++) s.ProjAlive[i] = 0;
        for (int i = 0; i < GameSimState.MaxPickups; i++) s.PickupAlive[i] = 0;
        s.EnemyActiveCount = 0;
        s.ProjActiveCount = 0;
        s.PickupActiveCount = 0;

        // Seed initial arena encounters & glowing crystal pickups for immediate action!
        SpawnEnemy(ref s, new FixVec3(Fix32.FromFloat(-5.5f), Fix32.Zero, Fix32.FromFloat(-4.5f)), kind: 0);
        SpawnEnemy(ref s, new FixVec3(Fix32.FromFloat(5.5f), Fix32.Zero, Fix32.FromFloat(-4.5f)), kind: 1);
        SpawnEnemy(ref s, new FixVec3(Fix32.FromFloat(0f), Fix32.Zero, Fix32.FromFloat(-7.0f)), kind: 0);
        SpawnEnemy(ref s, new FixVec3(Fix32.FromFloat(-6.5f), Fix32.Zero, Fix32.FromFloat(1.5f)), kind: 1);
        SpawnEnemy(ref s, new FixVec3(Fix32.FromFloat(6.5f), Fix32.Zero, Fix32.FromFloat(1.5f)), kind: 0);

        SpawnPickup(ref s, new FixVec3(Fix32.FromFloat(-3.2f), Fix32.Zero, Fix32.FromFloat(-1.5f)), 100);
        SpawnPickup(ref s, new FixVec3(Fix32.FromFloat(3.2f), Fix32.Zero, Fix32.FromFloat(-1.5f)), 100);
        SpawnPickup(ref s, new FixVec3(Fix32.FromFloat(0f), Fix32.Zero, Fix32.FromFloat(-3.5f)), 150);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitEvent(ref GameSimState s, SimEventKind kind, Vec3 pos, int value = 0)
    {
        if (s.EventCount < GameSimState.MaxEventsPerTick)
            s.Events[s.EventCount++] = new SimEvent { Kind = kind, Position = pos, Value = value };
    }

    public static void StepFixed(ref GameSimState s, in InputState input, Fix32 dt)
    {
        s.EventCount = 0;
        if (s.GameOver != 0) return;

        s.SimTick++;
        StepPlayer(ref s, in input, dt);
        StepSpawning(ref s, dt);
        StepEnemies(ref s, dt);
        StepProjectiles(ref s, dt);
        StepPickups(ref s, dt);

        if (s.ComboTimer > Fix32.Zero)
        {
            s.ComboTimer -= dt;
            if (s.ComboTimer <= Fix32.Zero)
            {
                s.ComboTimer = Fix32.Zero;
                s.Combo = 1;
            }
        }
    }

    private static void StepPlayer(ref GameSimState s, in InputState input, Fix32 dt)
    {
        int mx = (input.IsKeyDown(KeyCode.D) || input.IsKeyDown(KeyCode.Right) ? 1 : 0)
               - (input.IsKeyDown(KeyCode.A) || input.IsKeyDown(KeyCode.Left) ? 1 : 0);
        int mz = (input.IsKeyDown(KeyCode.S) || input.IsKeyDown(KeyCode.Down) ? 1 : 0)
               - (input.IsKeyDown(KeyCode.W) || input.IsKeyDown(KeyCode.Up) ? 1 : 0);

        FixVec3 moveDir = new FixVec3(Fix32.FromInt(mx), Fix32.Zero, Fix32.FromInt(mz)).Normalized();

        if (s.DashCooldown > Fix32.Zero) s.DashCooldown -= dt;
        if (s.DashTimer > Fix32.Zero) s.DashTimer -= dt;
        if (s.InvulnTimer > Fix32.Zero) s.InvulnTimer -= dt;
        if (s.FireCooldown > Fix32.Zero) s.FireCooldown -= dt;

        if (input.IsKeyPressed(KeyCode.Space) && s.DashCooldown <= Fix32.Zero)
        {
            FixVec3 dashDir = moveDir.LengthSq() > Fix32.Zero
                ? moveDir
                : new FixVec3(Fix32.Sin(s.PlayerYaw), Fix32.Zero, -Fix32.Cos(s.PlayerYaw));
            s.PlayerVel = dashDir * Fix32.FromInt(20);
            s.DashTimer = Fix32.FromFloat(0.16f);
            s.DashCooldown = Fix32.FromFloat(0.85f);
            s.InvulnTimer = Fix32.FromFloat(0.25f);
            EmitEvent(ref s, SimEventKind.PlayerDashed, s.PlayerPos.ToVec3());
        }

        if (s.DashTimer <= Fix32.Zero)
        {
            Fix32 moveSpeed = Fix32.FromFloat(7.5f);
            FixVec3 targetVel = moveDir * moveSpeed;
            Fix32 accel = Fix32.FromFloat(0.28f);
            s.PlayerVel = FixVec3.Lerp(s.PlayerVel, targetVel, accel);
        }

        s.PlayerPos = s.PlayerPos + s.PlayerVel * dt;
        Fix32 lim = s.ArenaHalfSize - Fix32.One;
        s.PlayerPos.X = Fix32.Clamp(s.PlayerPos.X, -lim, lim);
        s.PlayerPos.Z = Fix32.Clamp(s.PlayerPos.Z, -lim, lim);

        // Aim at nearest alive enemy (or movement direction)
        int nearest = FindNearestEnemy(in s, s.PlayerPos);
        FixVec3 aimDir = moveDir;
        if (nearest >= 0)
            aimDir = (s.EnemyPos[nearest] - s.PlayerPos).Normalized();

        if (aimDir.LengthSq() > Fix32.Zero)
            s.PlayerYaw = Fix32.Atan2(aimDir.X, -aimDir.Z);

        // Auto-fire or Space/Click/J fire towards nearest enemy
        bool wantsFire = input.IsMouseDown(0) || input.IsKeyDown(KeyCode.J) || nearest >= 0;
        if (wantsFire && s.FireCooldown <= Fix32.Zero && nearest >= 0)
        {
            s.FireCooldown = Fix32.FromFloat(0.20f);
            FixVec3 shotDir = (s.EnemyPos[nearest] - s.PlayerPos).Normalized();
            FixVec3 muzzle = s.PlayerPos + shotDir * Fix32.FromFloat(0.9f) + new FixVec3(Fix32.Zero, Fix32.One, Fix32.Zero);
            SpawnProjectile(ref s, muzzle, shotDir * Fix32.FromInt(22), fromPlayer: true);
        }
    }

    private static void StepSpawning(ref GameSimState s, Fix32 dt)
    {
        s.SpawnTimer -= dt;
        int desiredCap = 5 + s.Wave * 2;
        if (desiredCap > 24) desiredCap = 24;

        if (s.SpawnTimer <= Fix32.Zero && s.EnemyActiveCount < desiredCap)
        {
            s.SpawnTimer = Fix32.FromFloat(1.35f) - Fix32.FromFloat(0.08f) * s.Wave;
            if (s.SpawnTimer < Fix32.FromFloat(0.45f)) s.SpawnTimer = Fix32.FromFloat(0.45f);

            Fix32 angle = s.Rng.NextFix32Range(Fix32.Zero, Fix32.TwoPi);
            Fix32 radius = Fix32.FromFloat(9.5f);
            FixVec3 pos = new FixVec3(Fix32.Cos(angle) * radius, Fix32.Zero, Fix32.Sin(angle) * radius);
            byte kind = (byte)(s.Rng.NextInt(0, 3) == 0 ? 1 : 0);
            SpawnEnemy(ref s, pos, kind);
        }
    }

    private static void StepEnemies(ref GameSimState s, Fix32 dt)
    {
        Fix32 playerHitRadSq = Fix32.FromFloat(1.15f * 1.15f);

        for (int i = 0; i < GameSimState.MaxEnemies; i++)
        {
            if (s.EnemyAlive[i] == 0) continue;

            if (s.EnemyHitFlash[i] > Fix32.Zero)
            {
                s.EnemyHitFlash[i] -= dt * 5;
                if (s.EnemyHitFlash[i] < Fix32.Zero) s.EnemyHitFlash[i] = Fix32.Zero;
            }

            FixVec3 toPlayer = s.PlayerPos - s.EnemyPos[i];
            toPlayer.Y = Fix32.Zero;
            Fix32 distSq = toPlayer.LengthSq();
            FixVec3 dir = toPlayer.Normalized();

            Fix32 speed = s.EnemyKind[i] == 0
                ? Fix32.FromFloat(3.1f) + Fix32.FromFloat(0.25f) * s.Wave
                : Fix32.FromFloat(2.4f);

            if (s.EnemyKind[i] == 1 && distSq < Fix32.FromInt(25))
            {
                // Orbital strafe around player
                FixVec3 tangent = new FixVec3(-dir.Z, Fix32.Zero, dir.X);
                s.EnemyVel[i] = (tangent * Fix32.FromFloat(0.85f) + dir * Fix32.FromFloat(0.25f)) * speed;
            }
            else
            {
                s.EnemyVel[i] = dir * speed;
            }

            s.EnemyPos[i] = s.EnemyPos[i] + s.EnemyVel[i] * dt;
            s.EnemyAngle[i] = s.EnemyAngle[i] + dt * Fix32.FromInt(2);

            // Soft separation from arena walls
            Fix32 lim = s.ArenaHalfSize - Fix32.One;
            s.EnemyPos[i].X = Fix32.Clamp(s.EnemyPos[i].X, -lim, lim);
            s.EnemyPos[i].Z = Fix32.Clamp(s.EnemyPos[i].Z, -lim, lim);

            // Player contact damage
            if (distSq < playerHitRadSq && s.InvulnTimer <= Fix32.Zero)
            {
                s.PlayerHp -= Fix32.FromInt(12);
                s.InvulnTimer = Fix32.FromFloat(0.65f);
                s.Combo = 1;
                EmitEvent(ref s, SimEventKind.PlayerDamaged, s.PlayerPos.ToVec3(), 12);
                if (s.PlayerHp <= Fix32.Zero)
                {
                    s.PlayerHp = Fix32.Zero;
                    s.GameOver = 1;
                }
            }
        }
    }

    private static void StepProjectiles(ref GameSimState s, Fix32 dt)
    {
        Fix32 hitRadSq = Fix32.FromFloat(1.05f * 1.05f);

        for (int i = 0; i < GameSimState.MaxProjectiles; i++)
        {
            if (s.ProjAlive[i] == 0) continue;

            s.ProjLife[i] -= dt;
            if (s.ProjLife[i] <= Fix32.Zero)
            {
                s.ProjAlive[i] = 0;
                s.ProjActiveCount--;
                continue;
            }

            s.ProjPos[i] = s.ProjPos[i] + s.ProjVel[i] * dt;

            if (Fix32.Abs(s.ProjPos[i].X) > s.ArenaHalfSize || Fix32.Abs(s.ProjPos[i].Z) > s.ArenaHalfSize)
            {
                s.ProjAlive[i] = 0;
                s.ProjActiveCount--;
                continue;
            }

            if (s.ProjFromPlayer[i] != 0)
            {
                for (int e = 0; e < GameSimState.MaxEnemies; e++)
                {
                    if (s.EnemyAlive[e] == 0) continue;
                    Fix32 dx = s.ProjPos[i].X - s.EnemyPos[e].X;
                    Fix32 dz = s.ProjPos[i].Z - s.EnemyPos[e].Z;
                    if (dx * dx + dz * dz <= hitRadSq)
                    {
                        s.ProjAlive[i] = 0;
                        s.ProjActiveCount--;

                        int dmg = 34 + (s.Combo > 1 ? s.Combo * 3 : 0);
                        s.EnemyHp[e] -= Fix32.FromInt(dmg);
                        s.EnemyHitFlash[e] = Fix32.One;
                        Vec3 hitPos = s.EnemyPos[e].ToVec3() + new Vec3(0f, 1.5f, 0f);

                        if (s.EnemyHp[e] <= Fix32.Zero)
                        {
                            s.EnemyAlive[e] = 0;
                            s.EnemyActiveCount--;
                            s.Combo++;
                            s.ComboTimer = Fix32.FromFloat(3.5f);
                            int pts = 100 * s.Combo;
                            s.Score += pts;
                            s.KillsInWave++;

                            EmitEvent(ref s, SimEventKind.EnemyKilled, hitPos, pts);
                            SpawnPickup(ref s, s.EnemyPos[e], 50 * s.Combo);

                            if (s.KillsInWave >= 4 + s.Wave * 2)
                            {
                                s.Wave++;
                                s.KillsInWave = 0;
                                EmitEvent(ref s, SimEventKind.WaveAdvanced, s.PlayerPos.ToVec3() + new Vec3(0, 2.8f, 0), s.Wave);
                            }
                        }
                        else
                        {
                            EmitEvent(ref s, SimEventKind.EnemyHit, hitPos, dmg);
                        }
                        break;
                    }
                }
            }
        }
    }

    private static void StepPickups(ref GameSimState s, Fix32 dt)
    {
        Fix32 magnetRadSq = Fix32.FromInt(16); // radius 4
        Fix32 collectRadSq = Fix32.FromFloat(1.2f * 1.2f);

        for (int i = 0; i < GameSimState.MaxPickups; i++)
        {
            if (s.PickupAlive[i] == 0) continue;
            s.PickupBobPhase[i] += dt * Fix32.FromInt(3);

            FixVec3 toPlayer = s.PlayerPos - s.PickupPos[i];
            toPlayer.Y = Fix32.Zero;
            Fix32 distSq = toPlayer.LengthSq();

            if (distSq < collectRadSq)
            {
                s.Score += s.PickupValue[i];
                s.PlayerHp = Fix32.Min(s.PlayerMaxHp, s.PlayerHp + Fix32.FromInt(5));
                EmitEvent(ref s, SimEventKind.CrystalCollected, s.PickupPos[i].ToVec3() + new Vec3(0, 1f, 0), s.PickupValue[i]);
                s.PickupAlive[i] = 0;
                s.PickupActiveCount--;
                continue;
            }

            if (distSq < magnetRadSq)
            {
                FixVec3 dir = toPlayer.Normalized();
                s.PickupPos[i] = s.PickupPos[i] + dir * (Fix32.FromInt(9) * dt);
            }
        }
    }

    private static int FindNearestEnemy(in GameSimState s, FixVec3 pos)
    {
        int best = -1;
        Fix32 bestDistSq = Fix32.MaxValue;
        for (int i = 0; i < GameSimState.MaxEnemies; i++)
        {
            if (s.EnemyAlive[i] == 0) continue;
            Fix32 d = FixVec3.DistanceSq(pos, s.EnemyPos[i]);
            if (d < bestDistSq) { bestDistSq = d; best = i; }
        }
        return best;
    }

    private static void SpawnEnemy(ref GameSimState s, FixVec3 pos, byte kind)
    {
        for (int i = 0; i < GameSimState.MaxEnemies; i++)
        {
            if (s.EnemyAlive[i] != 0) continue;
            s.EnemyAlive[i] = 1;
            s.EnemyActiveCount++;
            s.EnemyPos[i] = pos;
            s.EnemyVel[i] = FixVec3.Zero;
            Fix32 hp = Fix32.FromInt(kind == 0 ? 70 + s.Wave * 15 : 100 + s.Wave * 20);
            s.EnemyMaxHp[i] = hp;
            s.EnemyHp[i] = hp;
            s.EnemyHitFlash[i] = Fix32.Zero;
            s.EnemyAngle[i] = Fix32.Zero;
            s.EnemyKind[i] = kind;
            EmitEvent(ref s, SimEventKind.EnemySpawned, pos.ToVec3());
            return;
        }
    }

    private static void SpawnProjectile(ref GameSimState s, FixVec3 pos, FixVec3 vel, bool fromPlayer)
    {
        for (int i = 0; i < GameSimState.MaxProjectiles; i++)
        {
            if (s.ProjAlive[i] != 0) continue;
            s.ProjAlive[i] = 1;
            s.ProjActiveCount++;
            s.ProjPos[i] = pos;
            s.ProjVel[i] = vel;
            s.ProjLife[i] = Fix32.FromFloat(1.25f);
            s.ProjFromPlayer[i] = (byte)(fromPlayer ? 1 : 0);
            return;
        }
    }

    private static void SpawnPickup(ref GameSimState s, FixVec3 pos, int value)
    {
        for (int i = 0; i < GameSimState.MaxPickups; i++)
        {
            if (s.PickupAlive[i] != 0) continue;
            s.PickupAlive[i] = 1;
            s.PickupActiveCount++;
            s.PickupPos[i] = pos;
            s.PickupBobPhase[i] = s.Rng.NextFix32Range(Fix32.Zero, Fix32.TwoPi);
            s.PickupValue[i] = value;
            return;
        }
    }

    /// <summary>
    /// Deterministic bit-exact FNV-1a state hash for headless determinism verification.
    /// </summary>
    public static ulong ComputeStateHash(in GameSimState s)
    {
        ulong h = 14695981039346656037UL;
        void Mix(long v) { h ^= (ulong)v; h *= 1099511628211UL; }

        Mix(s.PlayerPos.X.Raw);
        Mix(s.PlayerPos.Y.Raw);
        Mix(s.PlayerPos.Z.Raw);
        Mix(s.PlayerHp.Raw);
        Mix(s.Score);
        Mix(s.Combo);
        Mix(s.Wave);
        Mix(s.EnemyActiveCount);
        Mix(s.ProjActiveCount);
        Mix(s.PickupActiveCount);

        for (int i = 0; i < GameSimState.MaxEnemies; i++)
        {
            if (s.EnemyAlive[i] == 0) continue;
            Mix(s.EnemyPos[i].X.Raw);
            Mix(s.EnemyPos[i].Z.Raw);
            Mix(s.EnemyHp[i].Raw);
        }
        for (int i = 0; i < GameSimState.MaxProjectiles; i++)
        {
            if (s.ProjAlive[i] == 0) continue;
            Mix(s.ProjPos[i].X.Raw);
            Mix(s.ProjPos[i].Z.Raw);
        }
        return h;
    }
}

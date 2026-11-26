using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Rendering.Gpu;
using ShowcaseGame.Simulation;

namespace ShowcaseGame.View;

[StructLayout(LayoutKind.Sequential)]
public struct FloatingTextSlot
{
    public EntityId Entity;
    public TextHandle Text;
    public float LifeRemaining;
    public byte Alive;
}

/// <summary>
/// Unmanaged presentation-only view state (strictly separate from GameSimState!).
/// Tracks visual entity handles, cameras, custom shaders, emitters, and HUD handles.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct GameViewState
{
    public const int MaxFloatingTexts = 64;

    public GameSimState* Sim;
    public ShaderHandle HologramShader;
    public SceneLighting Lighting;

    // Active mode tracking (0 = Title, 1 = Arena)
    public byte Mode;
    public byte RequestStartArena;
    public byte RequestReturnTitle;

    // Title scene handles
    public CameraHandle TitleCamera;
    public EntityId TitleCoreEntity;
    public EntityId TitleRuneEntity;
    public SpriteHandle TitleRuneSprite;
    public SpriteAnimPlayerHandle TitleRuneAnim;
    public float TitleOrbitAngle;

    // Arena scene handles
    public SceneId ArenaSceneId;
    public SceneId HudSceneId;
    public CameraHandle ArenaCamera;
    public EntityId PlayerEntity;
    public EntityId CoreEntity;
    public EntityId FloorRuneEntity;
    public SpriteHandle FloorRuneSprite;
    public SpriteAnimPlayerHandle FloorRuneAnim;
    public SpriteAnimClipHandle PickupStarClip;
    public MaterialHandle CoreMaterial;

    public EmitterHandle PlayerTrailEmitter;
    public EmitterHandle AmbientEmbersEmitter;
    public EmitterHandle BurstHitEmitter;
    public EmitterHandle BurstKillEmitter;

    // Mapped visual handles parallel to Sim pools (allocated in GameAllocators.Persistent)
    public EntityId* EnemyEntities;
    public EntityId* EnemyHpBarEntities;
    public SpriteHandle* EnemyHpBars;
    public EntityId* ProjEntities;
    public SpriteHandle* ProjSprites;
    public EntityId* PickupEntities;
    public SpriteHandle* PickupSprites;
    public SpriteAnimPlayerHandle* PickupSpriteAnims;

    // Floating 3D animated SDF combat numbers
    public FloatingTextSlot* FloatingTexts;

    // Additive HUD scene handles (2D Orthographic camera)
    public CameraHandle HudCamera;
    public TextHandle HudScoreText;
    public TextHandle HudStatsText;
    public TextHandle HudControlsText;
    public SpriteHandle HudHpFillSprite;
    public EntityId HudHpFillEntity;
    public RenderStats CachedRenderStats;

    public static GameViewState Create(ref LinearArena gamePersistent, GameSimState* simPtr)
    {
        return new GameViewState
        {
            Sim = simPtr,
            Lighting = SceneLighting.Default,
            EnemyEntities = gamePersistent.Alloc<EntityId>(GameSimState.MaxEnemies, 64),
            EnemyHpBarEntities = gamePersistent.Alloc<EntityId>(GameSimState.MaxEnemies, 64),
            EnemyHpBars = gamePersistent.Alloc<SpriteHandle>(GameSimState.MaxEnemies, 64),
            ProjEntities = gamePersistent.Alloc<EntityId>(GameSimState.MaxProjectiles, 64),
            ProjSprites = gamePersistent.Alloc<SpriteHandle>(GameSimState.MaxProjectiles, 64),
            PickupEntities = gamePersistent.Alloc<EntityId>(GameSimState.MaxPickups, 64),
            PickupSprites = gamePersistent.Alloc<SpriteHandle>(GameSimState.MaxPickups, 64),
            PickupSpriteAnims = gamePersistent.Alloc<SpriteAnimPlayerHandle>(GameSimState.MaxPickups, 64),
            FloatingTexts = gamePersistent.Alloc<FloatingTextSlot>(MaxFloatingTexts, 64),
        };
    }
}

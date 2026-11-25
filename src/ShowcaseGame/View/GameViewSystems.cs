using System;
using BringEngine.Assets;
using BringEngine.Assets.Gltf;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Rendering;
using BringEngine.Simulation.Animation;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.Particles;
using BringEngine.Simulation.RenderData;
using BringEngine.Simulation.Scenes;
using BringEngine.Simulation.Tween;
using BringEngine.Simulation.World;
using ShowcaseGame.Simulation;

namespace ShowcaseGame.View;

/// <summary>
/// Pure Data + Systems presentation layer for ShowcaseGame ("Bring Arena").
/// Reads GameSimState + SimEventQueue to sync 3D glTF models, 2D sprites in 3D,
/// animated/tweened/shadered SDF text, SoA particles, custom hologram shader, and multi-cameras.
/// </summary>
public static unsafe class GameViewSystems
{
    // =========================================================================
    // TITLE SCENE (Single)
    // =========================================================================

    public static void OnTitleLoad(ref SceneContext ctx)
    {
        GameViewState* v = (GameViewState*)ctx.UserData;
        ref var w = ref *ctx.World;
        SceneId sc = ctx.Scene;
        v->Mode = 0;
        v->TitleOrbitAngle = 0f;

        // Floor
        var floorEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, floorEnt, Vec3.Zero);
        RenderComponentSystem.AddMeshRenderer(
            ref w.RenderComponents, floorEnt,
            AssetSystem.FindMesh(in w.Assets, StringId.Hash("title_floor_mesh")),
            AssetSystem.FindMaterial(in w.Assets, StringId.Hash("title_floor_mat")), sc);

        // Central glowing holographic energy core (uses custom hologram_energy.hlsl shader!)
        GltfHandle coreGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("title_core"));
        v->TitleCoreEntity = GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, coreGltf, new Vec3(0, 0.35f, 0), Quat.Identity, new Vec3(1.25f, 1.25f, 1.25f), sc);
        ApplyHologramShaderToGltf(ref w, coreGltf, v->HologramShader, sc);

        // Hero Mech & Sentinel Showcase ring
        GltfHandle heroGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("title_hero"));
        GltfHandle sentGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("title_sentinel"));
        GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, heroGltf, new Vec3(-3.0f, 0f, 1.8f), Quat.FromAxisAngle(Vec3.UnitY, 2.75f), new Vec3(1.2f, 1.2f, 1.2f), sc);
        GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, sentGltf, new Vec3(3.1f, 0.1f, 1.4f), Quat.FromAxisAngle(Vec3.UnitY, -0.5f), new Vec3(1.1f, 1.1f, 1.1f), sc);

        // Unity-style 2D animated rune sprite flat on the 3D floor under the core
        TextureHandle sheet = AssetSystem.FindTexture(in w.Assets, StringId.Hash("arcade_sheet"));
        v->TitleRuneEntity = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, v->TitleRuneEntity, new Vec3(0, 0.04f, 0), Quat.FromAxisAngle(Vec3.UnitX, -DetMath.HalfPi), Vec3.One);
        v->TitleRuneSprite = RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
        {
            Entity = v->TitleRuneEntity,
            OwnerScene = sc,
            Texture = sheet,
            Size = new Vec2(5.2f, 5.2f),
            Pivot = new Vec2(0.5f, 0.5f),
            UvRect = new Vec4(0f, 0.5f, 0.25f, 0.5f),
            Color = new ColorF(0.25f, 0.9f, 1f, 0.9f),
            LayerMask = 1u,
            Billboard = SpriteBillboardMode.None,
            BlendMode = SpriteBlendMode.Additive,
            DepthTest = 1,
            Visible = 1,
        });
        int runeClip = AnimationSystem.RegisterSpriteClip(ref w.Animations, new SpriteAnimClipData
        {
            Name = StringId.Hash("title_rune"),
            StartFrame = 4, FrameCount = 4, Columns = 4, Rows = 2, Fps = 6f, Loop = 1,
        });
        v->TitleRuneAnimIdx = AnimationSystem.AddSpritePlayer(ref w.Animations, v->TitleRuneEntity, runeClip, sc);

        // Giant 3D Animated & Shadered SDF Title Text!
        FontHandle font = AssetSystem.FindFont(in w.Assets, StringId.Hash("main_font"));
        var titleEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, titleEnt, new Vec3(0, 4.35f, 0));
        var titleTd = RenderComponentSystem.DefaultWorldText(titleEnt, font, "BRING ARENA", sc);
        titleTd.FontScale = 0.032f;
        titleTd.Color = ColorF.Cyan;
        titleTd.SecondaryColor = ColorF.Gold;
        titleTd.OutlineColor = new ColorF(0.05f, 0.04f, 0.14f, 1f);
        titleTd.OutlineWidth = 0.15f;
        titleTd.GlowColor = ColorF.Cyan;
        titleTd.GlowIntensity = 1.15f;
        titleTd.AnimFlags = TextAnimFlags.Wave | TextAnimFlags.Rainbow | TextAnimFlags.GlowPulse | TextAnimFlags.ShineSweep;
        titleTd.AnimAmplitude = 0.16f;
        RenderComponentSystem.AddText(ref w.RenderComponents, in titleTd);

        // Subtitle with Typewriter tween!
        var subEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, subEnt, new Vec3(0, 3.05f, 1.2f));
        var subTd = RenderComponentSystem.DefaultWorldText(subEnt, font, "PRESS ENTER OR SPACE TO PLAY", sc);
        subTd.FontScale = 0.015f;
        subTd.Color = ColorF.Gold;
        subTd.SecondaryColor = ColorF.White;
        subTd.OutlineWidth = 0.14f;
        subTd.GlowColor = ColorF.Gold;
        subTd.GlowIntensity = 0.7f;
        subTd.AnimFlags = TextAnimFlags.Bounce | TextAnimFlags.Typewriter | TextAnimFlags.GlowPulse;
        subTd.AnimAmplitude = 0.05f;
        var subH = RenderComponentSystem.AddText(ref w.RenderComponents, in subTd);
        TextData* subPtr = RenderComponentSystem.TryGetText(in w.RenderComponents, subH);
        TweenSystem.TweenFloatPtr(ref w.Tweens, &subPtr->TypewriterProgress, 0f, 1f, 0.9f, EaseType.OutQuad, scene: sc);

        // Ambient golden embers emitter
        TextureHandle glow = AssetSystem.FindTexture(in w.Assets, StringId.Hash("radial_glow"));
        var em = ParticleSystem.DefaultEmitter(new Vec3(0, 0.2f, 0), seed: 101);
        em.Texture = glow;
        em.Shape = ParticleShape.CircleXZ;
        em.ShapeExtents = new Vec3(5.5f, 0f, 5.5f);
        em.EmitRate = 45f;
        em.SpeedMin = 0.8f; em.SpeedMax = 2.2f;
        em.Gravity = new Vec3(0, 0.5f, 0);
        em.StartColor = ColorF.Cyan;
        em.EndColor = new ColorF(1f, 0.75f, 0.2f, 0f);
        em.LifetimeMin = 1.2f; em.LifetimeMax = 2.4f;
        var emH = ParticleSystem.CreateEmitter(ref w.Particles, in em, sc);
        ParticleSystem.Burst(ref w.Particles, emH, 50);

        // Orbit Camera
        v->TitleCamera = CameraSystem.CreatePerspective(ref w.Cameras, new Vec3(0, 4.2f, 10.5f), new Vec3(0, 1.9f, 0), 50f, 0.1f, 120f, sc);
        var camPtr = CameraSystem.TryGetPtr(in w.Cameras, v->TitleCamera);
        camPtr->ClearColor = new ColorF(0.04f, 0.05f, 0.10f, 1f);

        // Dynamic Title point lights
        v->Lighting = SceneLighting.Default;
        v->Lighting.SetPointLight(0, new Vec3(0, 2.2f, 0), 12f, ColorF.Cyan, 2.6f);
        v->Lighting.SetPointLight(1, new Vec3(-3.5f, 2.0f, 2.5f), 8f, ColorF.Blue, 1.6f);
        v->Lighting.SetPointLight(2, new Vec3(3.5f, 2.0f, 2.0f), 8f, ColorF.Magenta, 1.8f);
    }

    public static void OnTitleViewSync(ref SceneContext ctx)
    {
        GameViewState* v = (GameViewState*)ctx.UserData;
        ref var w = ref *ctx.World;

        v->TitleOrbitAngle += ctx.DeltaTime * 0.42f;
        CameraData* cam = CameraSystem.TryGetPtr(in w.Cameras, v->TitleCamera);
        if (cam != null)
        {
            cam->Position = new Vec3(DetMath.Sin(v->TitleOrbitAngle) * 10.2f, 4.1f + DetMath.Sin(v->TitleOrbitAngle * 2f) * 0.4f, DetMath.Cos(v->TitleOrbitAngle) * 10.2f);
            cam->LookTarget = new Vec3(0f, 2.0f, 0f);
        }

        // Spin energy core & update animated rune UV
        TransformSystem.SetLocalRotation(ref w.Transforms, v->TitleCoreEntity, Quat.FromAxisAngle(Vec3.UnitY, v->TitleOrbitAngle * 2.2f));
        SpriteData* runeSp = RenderComponentSystem.TryGetSprite(in w.RenderComponents, v->TitleRuneSprite);
        if (runeSp != null && (uint)v->TitleRuneAnimIdx < (uint)w.Animations.SpritePlayerCount)
            runeSp->UvRect = w.Animations.SpritePlayers[v->TitleRuneAnimIdx].CurrentUvRect;

        if (w.Input.IsKeyPressed(KeyCode.Return) || w.Input.IsKeyPressed(KeyCode.Space))
            v->RequestStartArena = 1;
    }

    // =========================================================================
    // ARENA GAMEPLAY SCENE (Single)
    // =========================================================================

    public static void OnArenaLoad(ref SceneContext ctx)
    {
        GameViewState* v = (GameViewState*)ctx.UserData;
        ref var w = ref *ctx.World;
        SceneId sc = ctx.Scene;
        v->Mode = 1;
        v->ArenaSceneId = sc;

        // Clear view handle tables
        for (int i = 0; i < GameSimState.MaxEnemies; i++) { v->EnemyEntities[i] = EntityId.Invalid; v->EnemyHpBarEntities[i] = EntityId.Invalid; v->EnemyHpBars[i] = SpriteHandle.Invalid; }
        for (int i = 0; i < GameSimState.MaxProjectiles; i++) { v->ProjEntities[i] = EntityId.Invalid; v->ProjSprites[i] = SpriteHandle.Invalid; }
        for (int i = 0; i < GameSimState.MaxPickups; i++) { v->PickupEntities[i] = EntityId.Invalid; v->PickupSprites[i] = SpriteHandle.Invalid; v->PickupSpriteAnims[i] = -1; }
        for (int i = 0; i < GameViewState.MaxFloatingTexts; i++) v->FloatingTexts[i] = default;

        // 1. Checkered PBR Arena Floor
        var floorEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, floorEnt, Vec3.Zero);
        RenderComponentSystem.AddMeshRenderer(
            ref w.RenderComponents, floorEnt,
            AssetSystem.FindMesh(in w.Assets, StringId.Hash("arena_floor_mesh")),
            AssetSystem.FindMaterial(in w.Assets, StringId.Hash("arena_floor_mat")), sc);

        // 2. 8 Perimeter Arena Pillars (ALL instanced into ONE draw call!)
        GltfHandle pillarGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("arena_pillar"));
        for (int i = 0; i < 8; i++)
        {
            float ang = i * (DetMath.TwoPi / 8f) + 0.39f;
            Vec3 p = new Vec3(DetMath.Cos(ang) * 11.4f, 2.1f, DetMath.Sin(ang) * 11.4f);
            GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, pillarGltf, p, Quat.FromAxisAngle(Vec3.UnitY, -ang), Vec3.One, sc);
        }

        // 3. Central Hologram Energy Core (Custom HLSL shader + TweenSystem animated CustomParams!)
        GltfHandle coreGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("arena_core"));
        v->CoreEntity = GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, coreGltf, new Vec3(0, 0.25f, -5.0f), Quat.Identity, new Vec3(0.95f, 0.95f, 0.95f), sc);
        v->CoreMaterial = ApplyHologramShaderToGltf(ref w, coreGltf, v->HologramShader, sc);

        // 4. Unity-style 2D animated floor rune sprite under the core
        TextureHandle sheet = AssetSystem.FindTexture(in w.Assets, StringId.Hash("arcade_sheet"));
        v->FloorRuneEntity = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, v->FloorRuneEntity, new Vec3(0, 0.04f, -5.0f), Quat.FromAxisAngle(Vec3.UnitX, -DetMath.HalfPi), Vec3.One);
        v->FloorRuneSprite = RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
        {
            Entity = v->FloorRuneEntity,
            OwnerScene = sc,
            Texture = sheet,
            Size = new Vec2(4.4f, 4.4f),
            Pivot = new Vec2(0.5f, 0.5f),
            UvRect = new Vec4(0f, 0.5f, 0.25f, 0.5f),
            Color = new ColorF(0.2f, 0.95f, 1f, 0.85f),
            LayerMask = 1u,
            Billboard = SpriteBillboardMode.None,
            BlendMode = SpriteBlendMode.Additive,
            DepthTest = 1,
            Visible = 1,
        });
        int runeClip = AnimationSystem.RegisterSpriteClip(ref w.Animations, new SpriteAnimClipData
        {
            Name = StringId.Hash("arena_rune"),
            StartFrame = 4, FrameCount = 4, Columns = 4, Rows = 2, Fps = 7f, Loop = 1,
        });
        v->FloorRuneAnimIdx = AnimationSystem.AddSpritePlayer(ref w.Animations, v->FloorRuneEntity, runeClip, sc);

        // 5. Player Skinned Animated Hero Mech (.glb)
        GltfHandle heroGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("arena_hero"));
        v->PlayerEntity = GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, heroGltf, v->Sim->PlayerPos.ToVec3(), Quat.Identity, Vec3.One, sc);

        // 6. Particle Emitters (Player trail, Ambient embers, Hit bursts, Kill explosions)
        TextureHandle glow = AssetSystem.FindTexture(in w.Assets, StringId.Hash("radial_glow"));

        var trailCfg = ParticleSystem.DefaultEmitter(Vec3.Zero, seed: 201);
        trailCfg.FollowEntity = v->PlayerEntity;
        trailCfg.Texture = glow;
        trailCfg.EmitRate = 38f;
        trailCfg.Shape = ParticleShape.Sphere;
        trailCfg.ShapeExtents = new Vec3(0.25f, 0.25f, 0.25f);
        trailCfg.Gravity = new Vec3(0, 1.2f, 0);
        trailCfg.SpeedMin = 0.3f; trailCfg.SpeedMax = 1.0f;
        trailCfg.StartColor = ColorF.Cyan;
        trailCfg.EndColor = new ColorF(0.1f, 0.3f, 1f, 0f);
        trailCfg.StartSizeMin = 0.22f; trailCfg.StartSizeMax = 0.35f;
        v->PlayerTrailEmitter = ParticleSystem.CreateEmitter(ref w.Particles, in trailCfg, sc);

        var ambCfg = ParticleSystem.DefaultEmitter(new Vec3(0, 0.2f, 0), seed: 202);
        ambCfg.Texture = glow;
        ambCfg.Shape = ParticleShape.CircleXZ;
        ambCfg.ShapeExtents = new Vec3(10.5f, 0, 10.5f);
        ambCfg.EmitRate = 25f;
        ambCfg.Gravity = new Vec3(0, 0.8f, 0);
        ambCfg.SpeedMin = 0.4f; ambCfg.SpeedMax = 1.2f;
        ambCfg.LifetimeMin = 1.4f; ambCfg.LifetimeMax = 2.8f;
        ambCfg.StartColor = new ColorF(0.4f, 0.9f, 1f, 0.85f);
        ambCfg.EndColor = new ColorF(1f, 0.8f, 0.3f, 0f);
        v->AmbientEmbersEmitter = ParticleSystem.CreateEmitter(ref w.Particles, in ambCfg, sc);

        var hitCfg = ParticleSystem.DefaultEmitter(Vec3.Zero, seed: 203);
        hitCfg.Texture = glow;
        hitCfg.EmitRate = 0f;
        hitCfg.Shape = ParticleShape.Sphere;
        hitCfg.ShapeExtents = new Vec3(0.2f, 0.2f, 0.2f);
        hitCfg.SpreadAngleRad = DetMath.Pi;
        hitCfg.SpeedMin = 3.5f; hitCfg.SpeedMax = 8.5f;
        hitCfg.StartColor = ColorF.Gold;
        hitCfg.EndColor = new ColorF(1f, 0.2f, 0.1f, 0f);
        v->BurstHitEmitter = ParticleSystem.CreateEmitter(ref w.Particles, in hitCfg, sc);

        var killCfg = hitCfg;
        killCfg.Rng = DetRng.Create(204);
        killCfg.SpeedMin = 4.5f; killCfg.SpeedMax = 11.5f;
        killCfg.StartSizeMin = 0.35f; killCfg.StartSizeMax = 0.65f;
        killCfg.StartColor = ColorF.Magenta;
        killCfg.EndColor = new ColorF(0.2f, 0.95f, 1f, 0f);
        v->BurstKillEmitter = ParticleSystem.CreateEmitter(ref w.Particles, in killCfg, sc);

        // 7. Main 3D Follow Camera with Smooth Damping & Trauma Shake
        v->ArenaCamera = CameraSystem.CreatePerspective(ref w.Cameras, new Vec3(0, 11.5f, 13.5f), Vec3.Zero, 52f, 0.1f, 150f, sc, depthOrder: 0);
        CameraData* cam = CameraSystem.TryGetPtr(in w.Cameras, v->ArenaCamera);
        cam->Mode = CameraMode.FollowTarget;
        cam->TargetEntity = v->PlayerEntity;
        cam->FollowOffset = new Vec3(0f, 10.5f, 12.0f);
        cam->LookOffset = new Vec3(0f, 0.8f, -1.5f);
        cam->FollowDamping = 6.5f;
        cam->ClearColor = new ColorF(0.04f, 0.06f, 0.11f, 1f);

        // 8. Dramatic Arena Point Lights
        v->Lighting = SceneLighting.Default;
        v->Lighting.SetPointLight(0, new Vec3(0, 2.5f, -5.0f), 14f, ColorF.Cyan, 3.0f);
        v->Lighting.SetPointLight(1, new Vec3(-7f, 2.5f, 4f), 10f, ColorF.Magenta, 1.5f);
        v->Lighting.SetPointLight(2, new Vec3(7f, 2.5f, 4f), 10f, ColorF.Gold, 1.5f);
    }

    public static void OnArenaViewSync(ref SceneContext ctx)
    {
        GameViewState* v = (GameViewState*)ctx.UserData;
        ref var w = ref *ctx.World;
        ref var sim = ref *v->Sim;
        SceneId sc = ctx.Scene;

        // 1. Sync Player transform + follow light
        Vec3 pPos = sim.PlayerPos.ToVec3();
        TransformSystem.SetLocalPosition(ref w.Transforms, v->PlayerEntity, pPos);
        TransformSystem.SetLocalRotation(ref w.Transforms, v->PlayerEntity, Quat.FromAxisAngle(Vec3.UnitY, -sim.PlayerYaw.ToFloat()));
        v->Lighting.SetPointLight(3, pPos + new Vec3(0, 2.2f, 0), 9f, ColorF.Cyan, 2.0f);

        // Spin central hologram core & rune sprite
        TransformSystem.SetLocalRotation(ref w.Transforms, v->CoreEntity, Quat.FromAxisAngle(Vec3.UnitY, w.TotalTime * 1.4f));
        SpriteData* runeSp = RenderComponentSystem.TryGetSprite(in w.RenderComponents, v->FloorRuneSprite);
        if (runeSp != null && (uint)v->FloorRuneAnimIdx < (uint)w.Animations.SpritePlayerCount)
            runeSp->UvRect = w.Animations.SpritePlayers[v->FloorRuneAnimIdx].CurrentUvRect;

        // 2. Sync Enemies (skinned glTF models + 3D billboarded 2D health bar sprites!)
        GltfHandle chaserGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("arena_chaser"));
        GltfHandle orbitalGltf = AssetSystem.FindGltf(in w.Assets, StringId.Hash("arena_orbital"));
        TextureHandle whiteTex = AssetSystem.FindTexture(in w.Assets, StringId.Hash("white"));

        for (int i = 0; i < GameSimState.MaxEnemies; i++)
        {
            if (sim.EnemyAlive[i] != 0)
            {
                Vec3 ePos = sim.EnemyPos[i].ToVec3();
                if (!v->EnemyEntities[i].IsValid)
                {
                    GltfHandle g = sim.EnemyKind[i] == 0 ? chaserGltf : orbitalGltf;
                    v->EnemyEntities[i] = GltfLoader.Instantiate(ref w, ref ctx.Allocators->Scratch, g, ePos, Quat.Identity, Vec3.One, sc);

                    // Pop-in scale tween via universal TweenSystem!
                    TweenSystem.TweenTransformScale(ref w.Tweens, v->EnemyEntities[i], new Vec3(0.1f, 0.1f, 0.1f), Vec3.One, 0.28f, EaseType.OutBack, scene: sc);

                    // 2D Health Bar Sprite floating in 3D space above enemy!
                    var hbEnt = EntitySystem.Spawn(ref w.Entities, sc);
                    TransformSystem.Add(ref w.Transforms, hbEnt, ePos + new Vec3(0, 2.35f, 0));
                    v->EnemyHpBarEntities[i] = hbEnt;
                    v->EnemyHpBars[i] = RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
                    {
                        Entity = hbEnt,
                        OwnerScene = sc,
                        Texture = whiteTex,
                        Size = new Vec2(1.25f, 0.16f),
                        Pivot = new Vec2(0.5f, 0.5f),
                        Color = ColorF.Green,
                        LayerMask = 1u,
                        Billboard = SpriteBillboardMode.CylindricalY,
                        BlendMode = SpriteBlendMode.AlphaBlend,
                        DepthTest = 1,
                        Visible = 1,
                    });
                }

                TransformSystem.SetLocalPosition(ref w.Transforms, v->EnemyEntities[i], ePos);
                TransformSystem.SetLocalRotation(ref w.Transforms, v->EnemyEntities[i], Quat.FromAxisAngle(Vec3.UnitY, sim.EnemyAngle[i].ToFloat()));

                // Update 3D health bar sprite position, width & color
                TransformSystem.SetLocalPosition(ref w.Transforms, v->EnemyHpBarEntities[i], ePos + new Vec3(0, 2.35f, 0));
                SpriteData* hb = RenderComponentSystem.TryGetSprite(in w.RenderComponents, v->EnemyHpBars[i]);
                if (hb != null)
                {
                    float frac = DetMath.Clamp01(sim.EnemyHp[i].ToFloat() / DetMath.Max(1f, sim.EnemyMaxHp[i].ToFloat()));
                    hb->Size = new Vec2(1.25f * frac, 0.16f);
                    hb->Color = ColorF.Lerp(ColorF.Red, ColorF.Green, frac);
                    hb->CustomParams.Y = sim.EnemyHitFlash[i].ToFloat(); // hit flash!
                }
            }
            else if (v->EnemyEntities[i].IsValid)
            {
                // Destroy visual entities cleanly
                EngineWorldSystem.DestroyEntity(ref w, v->EnemyEntities[i]);
                EngineWorldSystem.DestroyEntity(ref w, v->EnemyHpBarEntities[i]);
                v->EnemyEntities[i] = EntityId.Invalid;
                v->EnemyHpBarEntities[i] = EntityId.Invalid;
                v->EnemyHpBars[i] = SpriteHandle.Invalid;
            }
        }

        // 3. Sync Projectiles (Glowing 2D sprites in 3D space!)
        TextureHandle glowTex = AssetSystem.FindTexture(in w.Assets, StringId.Hash("radial_glow"));
        for (int i = 0; i < GameSimState.MaxProjectiles; i++)
        {
            if (sim.ProjAlive[i] != 0)
            {
                Vec3 pos = sim.ProjPos[i].ToVec3();
                if (!v->ProjEntities[i].IsValid)
                {
                    var pe = EntitySystem.Spawn(ref w.Entities, sc);
                    TransformSystem.Add(ref w.Transforms, pe, pos);
                    v->ProjEntities[i] = pe;
                    v->ProjSprites[i] = RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
                    {
                        Entity = pe,
                        OwnerScene = sc,
                        Texture = glowTex,
                        Size = new Vec2(0.72f, 0.72f),
                        Pivot = new Vec2(0.5f, 0.5f),
                        Color = new ColorF(0.3f, 0.95f, 1.0f, 1f),
                        LayerMask = 1u,
                        Billboard = SpriteBillboardMode.Spherical,
                        BlendMode = SpriteBlendMode.Additive,
                        DepthTest = 1,
                        Visible = 1,
                    });
                }
                TransformSystem.SetLocalPosition(ref w.Transforms, v->ProjEntities[i], pos);
            }
            else if (v->ProjEntities[i].IsValid)
            {
                EngineWorldSystem.DestroyEntity(ref w, v->ProjEntities[i]);
                v->ProjEntities[i] = EntityId.Invalid;
                v->ProjSprites[i] = SpriteHandle.Invalid;
            }
        }

        // 4. Sync Crystal Pickups (Animated 2D sprite-sheet stars in 3D world!)
        TextureHandle sheetTex = AssetSystem.FindTexture(in w.Assets, StringId.Hash("arcade_sheet"));
        for (int i = 0; i < GameSimState.MaxPickups; i++)
        {
            if (sim.PickupAlive[i] != 0)
            {
                Vec3 pos = sim.PickupPos[i].ToVec3();
                pos.Y = 0.85f + DetMath.Sin(sim.PickupBobPhase[i].ToFloat()) * 0.25f;

                if (!v->PickupEntities[i].IsValid)
                {
                    var ke = EntitySystem.Spawn(ref w.Entities, sc);
                    TransformSystem.Add(ref w.Transforms, ke, pos);
                    v->PickupEntities[i] = ke;
                    v->PickupSprites[i] = RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
                    {
                        Entity = ke,
                        OwnerScene = sc,
                        Texture = sheetTex,
                        Size = new Vec2(0.95f, 0.95f),
                        Pivot = new Vec2(0.5f, 0.5f),
                        UvRect = new Vec4(0, 0, 0.25f, 0.5f),
                        Color = ColorF.Gold,
                        LayerMask = 1u,
                        Billboard = SpriteBillboardMode.Spherical,
                        BlendMode = SpriteBlendMode.Additive,
                        DepthTest = 1,
                        Visible = 1,
                    });
                    int starClip = AnimationSystem.RegisterSpriteClip(ref w.Animations, new SpriteAnimClipData
                    {
                        Name = StringId.Hash("pickup_star"),
                        StartFrame = 0, FrameCount = 4, Columns = 4, Rows = 2, Fps = 8f, Loop = 1,
                    });
                    v->PickupSpriteAnims[i] = AnimationSystem.AddSpritePlayer(ref w.Animations, ke, starClip, sc);
                }
                TransformSystem.SetLocalPosition(ref w.Transforms, v->PickupEntities[i], pos);
                SpriteData* psp = RenderComponentSystem.TryGetSprite(in w.RenderComponents, v->PickupSprites[i]);
                if (psp != null && (uint)v->PickupSpriteAnims[i] < (uint)w.Animations.SpritePlayerCount)
                    psp->UvRect = w.Animations.SpritePlayers[v->PickupSpriteAnims[i]].CurrentUvRect;
            }
            else if (v->PickupEntities[i].IsValid)
            {
                EngineWorldSystem.DestroyEntity(ref w, v->PickupEntities[i]);
                v->PickupEntities[i] = EntityId.Invalid;
                v->PickupSprites[i] = SpriteHandle.Invalid;
                v->PickupSpriteAnims[i] = -1;
            }
        }

        // 5. Consume Simulation Events -> Particles + Trauma Shake + Floating Animated SDF Combat Text!
        for (int e = 0; e < sim.EventCount; e++)
        {
            SimEvent ev = sim.Events[e];
            switch (ev.Kind)
            {
                case SimEventKind.EnemyHit:
                    EmitBurstAt(ref w.Particles, v->BurstHitEmitter, ev.Position, 16);
                    SpawnFloatingText(v, ref w, sc, ev.Position, $"-{ev.Value}", ColorF.Gold, TextAnimFlags.Bounce | TextAnimFlags.ScalePop);
                    break;

                case SimEventKind.EnemyKilled:
                    EmitBurstAt(ref w.Particles, v->BurstKillEmitter, ev.Position, 45);
                    CameraSystem.AddTrauma(ref w.Cameras, v->ArenaCamera, 0.35f);
                    SpawnFloatingText(v, ref w, sc, ev.Position + new Vec3(0, 0.5f, 0), $"+{ev.Value}!", ColorF.Cyan, TextAnimFlags.Bounce | TextAnimFlags.Rainbow | TextAnimFlags.GlowPulse);
                    break;

                case SimEventKind.CrystalCollected:
                    EmitBurstAt(ref w.Particles, v->BurstHitEmitter, ev.Position, 20);
                    SpawnFloatingText(v, ref w, sc, ev.Position, $"+{ev.Value}", ColorF.Green, TextAnimFlags.Wave);
                    break;

                case SimEventKind.PlayerDashed:
                    EmitBurstAt(ref w.Particles, v->BurstKillEmitter, ev.Position + new Vec3(0, 0.8f, 0), 28);
                    CameraSystem.AddTrauma(ref w.Cameras, v->ArenaCamera, 0.2f);
                    var f1 = TweenSystem.TweenCameraFov(ref w.Tweens, v->ArenaCamera, 52f, 60f, 0.10f, EaseType.OutQuad, sc);
                    var f2 = TweenSystem.TweenCameraFov(ref w.Tweens, v->ArenaCamera, 60f, 52f, 0.25f, EaseType.OutCubic, sc);
                    TweenSystem.Chain(ref w.Tweens, f1, f2);
                    break;

                case SimEventKind.PlayerDamaged:
                    CameraSystem.AddTrauma(ref w.Cameras, v->ArenaCamera, 0.65f);
                    SpawnFloatingText(v, ref w, sc, ev.Position + new Vec3(0, 2f, 0), $"-{ev.Value} HP", ColorF.Red, TextAnimFlags.Shake);
                    break;

                case SimEventKind.WaveAdvanced:
                    SpawnFloatingText(v, ref w, sc, ev.Position, $"WAVE {ev.Value}!", ColorF.Gold, TextAnimFlags.Wave | TextAnimFlags.Rainbow | TextAnimFlags.GlowPulse);
                    break;
            }
        }

        // 6. Expire floating 3D SDF texts
        for (int i = 0; i < GameViewState.MaxFloatingTexts; i++)
        {
            if (v->FloatingTexts[i].Alive == 0) continue;
            v->FloatingTexts[i].LifeRemaining -= ctx.DeltaTime;
            if (v->FloatingTexts[i].LifeRemaining <= 0f)
            {
                EngineWorldSystem.DestroyEntity(ref w, v->FloatingTexts[i].Entity);
                v->FloatingTexts[i] = default;
            }
        }

        if (w.Input.IsKeyPressed(KeyCode.Num1))
            v->RequestReturnTitle = 1;
    }

    // =========================================================================
    // ADDITIVE HUD SCENE (2D Orthographic Overlay Camera)
    // =========================================================================

    public static void OnHudLoad(ref SceneContext ctx)
    {
        GameViewState* v = (GameViewState*)ctx.UserData;
        ref var w = ref *ctx.World;
        SceneId sc = ctx.Scene;
        v->HudSceneId = sc;

        // Secondary 2D Orthographic UI Overlay Camera (renders AFTER 3D world, DepthOrder = 100!)
        v->HudCamera = CameraSystem.CreateOrthographicUI(ref w.Cameras, 1280f, 720f, sc, depthOrder: 100, layerMask: 1u << 31);

        FontHandle font = AssetSystem.FindFont(in w.Assets, StringId.Hash("main_font"));
        TextureHandle white = AssetSystem.FindTexture(in w.Assets, StringId.Hash("white"));

        // Top-left Score / Wave / Combo SDF HUD Text
        var scoreEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, scoreEnt, new Vec3(24f, 696f, 0f));
        var scoreTd = RenderComponentSystem.DefaultHudText(scoreEnt, font, "SCORE: 0   WAVE: 1   COMBO x1", sc);
        scoreTd.FontScale = 0.62f;
        scoreTd.Color = ColorF.Gold;
        scoreTd.SecondaryColor = ColorF.White;
        scoreTd.OutlineWidth = 0.15f;
        scoreTd.GlowColor = ColorF.Gold;
        scoreTd.GlowIntensity = 0.55f;
        scoreTd.AnimFlags = TextAnimFlags.GlowPulse | TextAnimFlags.ShineSweep;
        v->HudScoreText = RenderComponentSystem.AddText(ref w.RenderComponents, in scoreTd);

        // Bottom-left Controls hint (Top-Right is occupied by Bring Engine's common ProfilerOverlay!)
        var ctrlEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, ctrlEnt, new Vec3(24f, 34f, 0f));
        var ctrlTd = RenderComponentSystem.DefaultHudText(ctrlEnt, font, "WASD Move | SPACE Dash | F1 Profiler | F2 VSync | F3 FPS Cap | 1 Title | ESC Quit", sc);
        ctrlTd.FontScale = 0.36f;
        ctrlTd.Color = new ColorF(0.75f, 0.85f, 0.98f, 0.92f);
        v->HudControlsText = RenderComponentSystem.AddText(ref w.RenderComponents, in ctrlTd);

        // 2D HUD HP Bar background & fill sprites
        var bgEnt = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, bgEnt, new Vec3(24f, 626f, -1f));
        RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
        {
            Entity = bgEnt, OwnerScene = sc, Texture = white,
            Size = new Vec2(248f, 22f), Pivot = Vec2.Zero,
            Color = new ColorF(0.08f, 0.10f, 0.18f, 0.85f),
            LayerMask = 1u << 31, SortingLayer = 0, OrderInLayer = 0, DepthTest = 0, Visible = 1,
        });

        v->HudHpFillEntity = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, v->HudHpFillEntity, new Vec3(28f, 630f, 0f));
        v->HudHpFillSprite = RenderComponentSystem.AddSprite(ref w.RenderComponents, new SpriteData
        {
            Entity = v->HudHpFillEntity, OwnerScene = sc, Texture = white,
            Size = new Vec2(240f, 14f), Pivot = Vec2.Zero,
            Color = ColorF.Cyan,
            LayerMask = 1u << 31, SortingLayer = 0, OrderInLayer = 1, DepthTest = 0, Visible = 1,
        });
    }

    public static void OnHudViewSync(ref SceneContext ctx)
    {
        GameViewState* v = (GameViewState*)ctx.UserData;
        ref var w = ref *ctx.World;
        ref var sim = ref *v->Sim;

        // Zero-GC UTF-8 HUD string formatting using stackalloc char buffer!
        char* buf = stackalloc char[128];
        Span<char> span = new Span<char>(buf, 128);

        TextData* scoreTxt = RenderComponentSystem.TryGetText(in w.RenderComponents, v->HudScoreText);
        if (scoreTxt != null && span.TryWrite($"SCORE: {sim.Score}   WAVE: {sim.Wave}   COMBO x{sim.Combo}", out int len1))
            scoreTxt->Content = FixedString256.From(span.Slice(0, len1));

        SpriteData* hpBar = RenderComponentSystem.TryGetSprite(in w.RenderComponents, v->HudHpFillSprite);
        if (hpBar != null)
        {
            float hpFrac = DetMath.Clamp01(sim.PlayerHp.ToFloat() / DetMath.Max(1f, sim.PlayerMaxHp.ToFloat()));
            hpBar->Size = new Vec2(240f * hpFrac, 14f);
            hpBar->Color = ColorF.Lerp(ColorF.Red, ColorF.Cyan, hpFrac);
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static MaterialHandle ApplyHologramShaderToGltf(ref EngineWorld w, GltfHandle gltf, ShaderHandle holoShader, SceneId sc)
    {
        GltfModelAssetData* model = AssetSystem.TryGetGltf(in w.Assets, gltf);
        if (model == null || model->PrimitiveCount <= 0) return MaterialHandle.Invalid;
        MaterialHandle mh = model->Primitives[0].Material;
        MaterialAssetData* mat = AssetSystem.TryGetMaterial(in w.Assets, mh);
        if (mat != null)
        {
            mat->CustomShader = holoShader;
            mat->AlphaMode = MaterialAlphaMode.Blend;
            mat->CustomParams = new Vec4(1.0f, 2.5f, 2.2f, 0f);
            // Tween the custom HLSL shader's pulse parameter continuously using universal TweenSystem!
            TweenSystem.TweenFloatPtr(
                ref w.Tweens, &mat->CustomParams.X, 0.7f, 1.55f, 1.1f,
                EaseType.InOutSine, loopMode: TweenLoopMode.PingPong, loops: -1, scene: sc);
        }
        return mh;
    }

    private static void EmitBurstAt(ref ParticleTable pt, EmitterHandle emitter, Vec3 pos, int count)
    {
        ParticleEmitterData* em = ParticleSystem.TryGetEmitter(in pt, emitter);
        if (em == null) return;
        em->Position = pos;
        em->PendingBurst += count;
    }

    private static void SpawnFloatingText(
        GameViewState* v, ref EngineWorld w, SceneId sc, Vec3 pos, string text, ColorF color, TextAnimFlags anim)
    {
        int slot = -1;
        for (int i = 0; i < GameViewState.MaxFloatingTexts; i++)
            if (v->FloatingTexts[i].Alive == 0) { slot = i; break; }
        if (slot < 0) return;

        FontHandle font = AssetSystem.FindFont(in w.Assets, StringId.Hash("main_font"));
        if (!font.IsValid) return;

        EntityId e = EntitySystem.Spawn(ref w.Entities, sc);
        TransformSystem.Add(ref w.Transforms, e, pos);

        var td = RenderComponentSystem.DefaultWorldText(e, font, text, sc);
        td.FontScale = 0.017f;
        td.Color = color;
        td.SecondaryColor = ColorF.White;
        td.OutlineWidth = 0.16f;
        td.GlowColor = color;
        td.GlowIntensity = 0.85f;
        td.AnimFlags = anim;
        td.AnimAmplitude = 0.14f;
        TextHandle th = RenderComponentSystem.AddText(ref w.RenderComponents, in td);

        // Animate floating upward + pop scale with universal TweenSystem!
        TweenSystem.TweenTransformPos(ref w.Tweens, e, pos, pos + new Vec3(0f, 1.85f, 0f), 0.95f, EaseType.OutCubic, scene: sc);
        TweenSystem.TweenTransformScale(ref w.Tweens, e, new Vec3(0.3f, 0.3f, 0.3f), new Vec3(1.15f, 1.15f, 1.15f), 0.25f, EaseType.OutBack, scene: sc);

        v->FloatingTexts[slot] = new FloatingTextSlot { Entity = e, Text = th, LifeRemaining = 0.95f, Alive = 1 };
    }
}

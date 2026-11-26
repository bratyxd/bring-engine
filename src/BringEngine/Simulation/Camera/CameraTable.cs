using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.World;

namespace BringEngine.Simulation.Camera;

public enum CameraProjection : byte
{
    Perspective = 0,
    Orthographic = 1,
}

public enum CameraClearFlags : byte
{
    SolidColor = 0,
    DepthOnly = 1,
    None = 2,
}

public enum CameraMode : byte
{
    Manual = 0,
    FollowTarget = 1,
    OrbitTarget = 2,
    LookAtTarget = 3,
}

[StructLayout(LayoutKind.Sequential)]
public struct CameraData
{
    public CameraHandle Handle;
    public EntityId Entity;
    public SceneId OwnerScene;

    public CameraProjection Projection;
    public CameraClearFlags ClearFlags;
    public CameraMode Mode;
    public byte Enabled;

    public float FovYDeg;
    public float OrthoHeight;
    public float NearPlane;
    public float FarPlane;

    /// <summary>Normalized viewport rect (x, y, width, height) in [0, 1].</summary>
    public Vec4 ViewportRect;
    public ColorF ClearColor;
    public uint LayerMask;
    public int DepthOrder;

    // Direct position/rotation (used when Entity is invalid or overwritten by Follow/Orbit modes)
    public Vec3 Position;
    public Vec3 LookTarget;
    public Vec3 Up;

    // Follow / Orbit controller parameters
    public EntityId TargetEntity;
    public Vec3 FollowOffset;
    public Vec3 LookOffset;
    public float FollowDamping;
    public float OrbitYaw;
    public float OrbitPitch;
    public float OrbitDistance;

    // Deterministic trauma screen shake
    public float Trauma;
    public float TraumaDecay;
    public float MaxShakePos;
    public float MaxShakeAngle;
    public float ShakeClock;

    // Computed render matrices & frustum (populated by CameraSystem.UpdateAll)
    public Vec3 ShakenPosition;
    public Vec3 Forward;
    public Vec3 Right;
    public Vec3 CameraUp;
    public Mat4 ViewMatrix;
    public Mat4 ProjMatrix;
    public Mat4 ViewProjMatrix;
    public Mat4 InvViewProjMatrix;
    public Frustum Frustum;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct CameraTable
{
    public CameraData* Slots;
    public uint* Generations;
    public byte* Alive;
    public int Capacity;
    public int ActiveCount;

    public static CameraTable Create(ref LinearArena arena, int maxCameras = 32)
    {
        const int maxCap = (int)(HandlePacking.IndexMask + 1u);
        if (maxCameras < 1 || maxCameras > maxCap)
            throw new ArgumentOutOfRangeException(nameof(maxCameras), maxCameras, $"Capacity must be in [1, {maxCap}].");

        CameraData* slots = arena.Alloc<CameraData>(maxCameras, 64);
        uint* gens = arena.AllocUninitialized<uint>(maxCameras, 16);
        byte* alive = arena.Alloc<byte>(maxCameras, 16);
        for (int i = 0; i < maxCameras; i++) gens[i] = 1;

        return new CameraTable
        {
            Slots = slots,
            Generations = gens,
            Alive = alive,
            Capacity = maxCameras,
            ActiveCount = 0,
        };
    }
}

public static unsafe class CameraSystem
{
    public static CameraHandle CreatePerspective(
        ref CameraTable table,
        Vec3 position,
        Vec3 lookTarget,
        float fovYDeg = 60f,
        float nearPlane = 0.1f,
        float farPlane = 500f,
        SceneId ownerScene = default,
        int depthOrder = 0,
        uint layerMask = 0x3FFF_FFFFu) // World layers 0..29 (excludes bit 30 ProfilerOverlay & bit 31 UI HUD)
    {
        int slot = FindFreeSlot(in table);
        if (slot < 0) throw new OutOfMemoryException("CameraTable exhausted.");

        CameraHandle handle = CameraHandle.Create((uint)slot, table.Generations[slot]);
        table.Alive[slot] = 1;
        table.ActiveCount++;

        table.Slots[slot] = new CameraData
        {
            Handle = handle,
            OwnerScene = ownerScene,
            Projection = CameraProjection.Perspective,
            ClearFlags = CameraClearFlags.SolidColor,
            Mode = CameraMode.Manual,
            Enabled = 1,
            FovYDeg = fovYDeg,
            OrthoHeight = 10f,
            NearPlane = nearPlane,
            FarPlane = farPlane,
            ViewportRect = new Vec4(0f, 0f, 1f, 1f),
            ClearColor = new ColorF(0.06f, 0.08f, 0.13f, 1f),
            LayerMask = layerMask,
            DepthOrder = depthOrder,
            Position = position,
            LookTarget = lookTarget,
            Up = Vec3.Up,
            FollowDamping = 8f,
            TraumaDecay = 1.6f,
            MaxShakePos = 0.65f,
            MaxShakeAngle = 0.04f,
            ViewMatrix = Mat4.Identity,
            ProjMatrix = Mat4.Identity,
            ViewProjMatrix = Mat4.Identity,
            InvViewProjMatrix = Mat4.Identity,
        };
        return handle;
    }

    public static CameraHandle CreateOrthographicUI(
        ref CameraTable table,
        float screenWidth,
        float screenHeight,
        SceneId ownerScene = default,
        int depthOrder = 100,
        uint layerMask = 1u << 31)
    {
        int slot = FindFreeSlot(in table);
        if (slot < 0) throw new OutOfMemoryException("CameraTable exhausted.");

        CameraHandle handle = CameraHandle.Create((uint)slot, table.Generations[slot]);
        table.Alive[slot] = 1;
        table.ActiveCount++;

        table.Slots[slot] = new CameraData
        {
            Handle = handle,
            OwnerScene = ownerScene,
            Projection = CameraProjection.Orthographic,
            ClearFlags = CameraClearFlags.None, // 2D UI overlays draw with DepthTest=0 and must not clear 3D depth mid-frame
            Mode = CameraMode.Manual,
            Enabled = 1,
            FovYDeg = 60f,
            OrthoHeight = screenHeight,
            NearPlane = -100f,
            FarPlane = 100f,
            ViewportRect = new Vec4(0f, 0f, 1f, 1f),
            ClearColor = ColorF.Transparent,
            LayerMask = layerMask,
            DepthOrder = depthOrder,
            Position = new Vec3(screenWidth * 0.5f, screenHeight * 0.5f, 10f),
            LookTarget = new Vec3(screenWidth * 0.5f, screenHeight * 0.5f, 0f),
            Up = Vec3.Up,
            ViewMatrix = Mat4.Identity,
            ProjMatrix = Mat4.Identity,
            ViewProjMatrix = Mat4.Identity,
            InvViewProjMatrix = Mat4.Identity,
        };
        return handle;
    }

    private static int FindFreeSlot(in CameraTable table)
    {
        for (int i = 0; i < table.Capacity; i++)
            if (table.Alive[i] == 0) return i;
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CameraData* TryGetPtr(in CameraTable table, CameraHandle handle)
    {
        if (!handle.IsValid) return null;
        uint slot = handle.Index;
        if (slot >= (uint)table.Capacity) return null;
        if (table.Alive[slot] == 0 || table.Generations[slot] != handle.Generation) return null;
        return table.Slots + slot;
    }

    public static bool Destroy(ref CameraTable table, CameraHandle handle)
    {
        CameraData* ptr = TryGetPtr(in table, handle);
        if (ptr == null) return false;
        uint slot = handle.Index;
        table.Alive[slot] = 0;
        uint nextGen = (table.Generations[slot] + 1) & HandlePacking.GenerationMask;
        table.Generations[slot] = nextGen == 0 ? 1u : nextGen;
        table.ActiveCount--;
        return true;
    }

    public static void DestroyByScene(ref CameraTable table, SceneId scene)
    {
        for (int i = 0; i < table.Capacity; i++)
        {
            if (table.Alive[i] != 0 && table.Slots[i].OwnerScene == scene)
                Destroy(ref table, table.Slots[i].Handle);
        }
    }

    public static void AddTrauma(ref CameraTable table, CameraHandle handle, float amount)
    {
        CameraData* cam = TryGetPtr(in table, handle);
        if (cam == null) return;
        cam->Trauma = DetMath.Clamp01(cam->Trauma + amount);
    }

    /// <summary>
    /// Updates all active camera controllers, deterministic screen shake, matrices, and frustum planes.
    /// </summary>
    public static void UpdateAll(ref CameraTable table, in TransformTable transforms, float dt, float viewportWidth, float viewportHeight)
    {
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        if (!float.IsFinite(viewportWidth) || viewportWidth < 1f) viewportWidth = 1f;
        if (!float.IsFinite(viewportHeight) || viewportHeight < 1f) viewportHeight = 1f;

        for (int i = 0; i < table.Capacity; i++)
        {
            if (table.Alive[i] == 0) continue;
            ref CameraData cam = ref table.Slots[i];
            if (cam.Enabled == 0) continue;

            // Update controller mode (hold last valid position/target if target entity was destroyed)
            if (cam.Mode == CameraMode.FollowTarget && cam.TargetEntity.IsValid)
            {
                int td = TransformSystem.GetDenseIndex(in transforms, cam.TargetEntity);
                if (td >= 0)
                {
                    Vec3 targetPos = transforms.WorldPosition[td];
                    Vec3 desiredPos = targetPos + cam.FollowOffset;
                    float t = cam.FollowDamping > 0f ? DetMath.Clamp01(1f - DetMath.Exp(-cam.FollowDamping * dt)) : 1f;
                    cam.Position = Vec3.Lerp(cam.Position, desiredPos, t);
                    cam.LookTarget = Vec3.Lerp(cam.LookTarget, targetPos + cam.LookOffset, t);
                }
            }
            else if (cam.Mode == CameraMode.OrbitTarget && cam.TargetEntity.IsValid)
            {
                int td = TransformSystem.GetDenseIndex(in transforms, cam.TargetEntity);
                if (td >= 0)
                {
                    Vec3 pivot = transforms.WorldPosition[td] + cam.LookOffset;
                    float pitch = float.IsFinite(cam.OrbitPitch)
                        ? DetMath.Clamp(cam.OrbitPitch, -DetMath.HalfPi + 0.001f, DetMath.HalfPi - 0.001f)
                        : 0f;
                    float yaw = float.IsFinite(cam.OrbitYaw) ? cam.OrbitYaw : 0f;
                    float dist = float.IsFinite(cam.OrbitDistance) ? cam.OrbitDistance : 1f;
                    float cp = DetMath.Cos(pitch);
                    Vec3 offset = new Vec3(
                        DetMath.Sin(yaw) * cp * dist,
                        DetMath.Sin(pitch) * dist,
                        DetMath.Cos(yaw) * cp * dist);
                    cam.Position = pivot + offset;
                    cam.LookTarget = pivot;
                }
            }
            else if (cam.Mode == CameraMode.LookAtTarget && cam.TargetEntity.IsValid)
            {
                int td = TransformSystem.GetDenseIndex(in transforms, cam.TargetEntity);
                if (td >= 0)
                    cam.LookTarget = transforms.WorldPosition[td] + cam.LookOffset;
            }
            else if (cam.Entity.IsValid)
            {
                int td = TransformSystem.GetDenseIndex(in transforms, cam.Entity);
                if (td >= 0)
                {
                    cam.Position = transforms.WorldPosition[td];
                    Vec3 fwd = TransformSystem.GetWorldForward(in transforms, cam.Entity);
                    cam.LookTarget = cam.Position + fwd;
                }
            }

            // Deterministic trauma screen shake (multi-frequency trigonometric noise)
            Vec3 shakeOffset = Vec3.Zero;
            float shakeRoll = 0f;
            if (cam.Trauma > 0f)
            {
                cam.ShakeClock += dt * 31.0f;
                float shake = cam.Trauma * cam.Trauma;
                float sx = DetMath.Sin(cam.ShakeClock * 1.13f) * DetMath.Cos(cam.ShakeClock * 2.37f);
                float sy = DetMath.Sin(cam.ShakeClock * 1.71f + 1.3f) * DetMath.Cos(cam.ShakeClock * 2.91f);
                float sz = DetMath.Sin(cam.ShakeClock * 1.43f + 2.7f);
                shakeOffset = new Vec3(sx, sy, sz * 0.4f) * (cam.MaxShakePos * shake);
                if (cam.MaxShakeAngle > 0f)
                    shakeRoll = DetMath.Sin(cam.ShakeClock * 1.93f + 0.7f) * cam.MaxShakeAngle * shake;
                cam.Trauma = DetMath.Max(0f, cam.Trauma - cam.TraumaDecay * dt);
            }

            cam.ShakenPosition = cam.Position + shakeOffset;
            float vpW = viewportWidth * cam.ViewportRect.Z;
            float vpH = viewportHeight * cam.ViewportRect.W;
            float aspect = (float.IsFinite(vpW) && float.IsFinite(vpH) && vpW > 0f && vpH > 0f) ? vpW / vpH : (16f / 9f);

            Vec3 shakenTarget = cam.Projection == CameraProjection.Perspective
                ? cam.LookTarget + shakeOffset * 0.35f
                : cam.LookTarget + shakeOffset;

            Vec3 effectiveUp = cam.Up.LengthSq() > 1e-6f ? cam.Up : Vec3.Up;
            if (shakeRoll != 0f)
            {
                Vec3 fwdAxis = (shakenTarget - cam.ShakenPosition).Normalized();
                if (fwdAxis.LengthSq() > 1e-6f)
                    effectiveUp = Quat.FromAxisAngle(fwdAxis, shakeRoll).Rotate(effectiveUp);
            }

            if (cam.Projection == CameraProjection.Perspective)
            {
                float fovDeg = float.IsFinite(cam.FovYDeg) ? DetMath.Clamp(cam.FovYDeg, 1f, 179f) : 60f;
                float near = (float.IsFinite(cam.NearPlane) && cam.NearPlane > 1e-4f) ? cam.NearPlane : 0.1f;
                float far = float.IsFinite(cam.FarPlane) ? cam.FarPlane : near + 100f;
                if (DetMath.Abs(far - near) <= 1e-4f || far <= near)
                    far = near + 1f;
                cam.ViewMatrix = Mat4.CreateLookAt(cam.ShakenPosition, shakenTarget, effectiveUp);
                cam.ProjMatrix = Mat4.CreatePerspective(fovDeg * DetMath.Deg2Rad, aspect, near, far);
            }
            else
            {
                // UI / 2D orthographic: origin top-left or centered via Position
                float orthoH = (float.IsFinite(cam.OrthoHeight) && cam.OrthoHeight > 1e-4f) ? cam.OrthoHeight : 1f;
                float near = float.IsFinite(cam.NearPlane) ? cam.NearPlane : -100f;
                float far = float.IsFinite(cam.FarPlane) ? cam.FarPlane : 100f;
                if (DetMath.Abs(far - near) <= 1e-4f)
                    far = near + 1f;
                float halfH = orthoH * 0.5f;
                float halfW = halfH * aspect;
                cam.ViewMatrix = Mat4.CreateLookAt(cam.ShakenPosition, shakenTarget, effectiveUp);
                cam.ProjMatrix = Mat4.CreateOrtho(-halfW, halfW, -halfH, halfH, near, far);
            }

            cam.ViewProjMatrix = Mat4.Multiply(in cam.ProjMatrix, in cam.ViewMatrix);
            Mat4.TryInvert(in cam.ViewProjMatrix, out cam.InvViewProjMatrix);
            cam.Frustum = Frustum.FromViewProjection(in cam.ViewProjMatrix);

            // Extract basis vectors from view matrix rows
            cam.Right = new Vec3(cam.ViewMatrix.C0.X, cam.ViewMatrix.C1.X, cam.ViewMatrix.C2.X).Normalized();
            cam.CameraUp = new Vec3(cam.ViewMatrix.C0.Y, cam.ViewMatrix.C1.Y, cam.ViewMatrix.C2.Y).Normalized();
            cam.Forward = new Vec3(-cam.ViewMatrix.C0.Z, -cam.ViewMatrix.C1.Z, -cam.ViewMatrix.C2.Z).Normalized();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeViewportPixels(
        in CameraData cam, float screenW, float screenH,
        out float vpX, out float vpY, out float vpW, out float vpH)
    {
        if (!float.IsFinite(screenW) || screenW < 1f) screenW = 1f;
        if (!float.IsFinite(screenH) || screenH < 1f) screenH = 1f;
        vpX = float.IsFinite(cam.ViewportRect.X) ? cam.ViewportRect.X * screenW : 0f;
        vpY = float.IsFinite(cam.ViewportRect.Y) ? cam.ViewportRect.Y * screenH : 0f;
        vpW = (float.IsFinite(cam.ViewportRect.Z) && cam.ViewportRect.Z > 0f) ? cam.ViewportRect.Z * screenW : screenW;
        vpH = (float.IsFinite(cam.ViewportRect.W) && cam.ViewportRect.W > 0f) ? cam.ViewportRect.W * screenH : screenH;
        if (vpW < 1f) vpW = 1f;
        if (vpH < 1f) vpH = 1f;
    }

    /// <summary>
    /// Unprojects screen pixel coordinates (0..screenW, 0..screenH top-left origin) into a 3D world ray,
    /// respecting the camera's normalized ViewportRect for split-screen and picture-in-picture cameras.
    /// </summary>
    public static Ray ScreenToWorldRay(in CameraData cam, float screenX, float screenY, float screenW, float screenH)
    {
        ComputeViewportPixels(in cam, screenW, screenH, out float vpX, out float vpY, out float vpW, out float vpH);
        float ndcX = ((screenX - vpX) / vpW) * 2f - 1f;
        float ndcY = 1f - ((screenY - vpY) / vpH) * 2f;

        Vec4 nearClip = new Vec4(ndcX, ndcY, 0f, 1f);
        Vec4 farClip = new Vec4(ndcX, ndcY, 1f, 1f);

        Vec4 nearW = Mat4.TransformVec4(in cam.InvViewProjMatrix, nearClip);
        Vec4 farW = Mat4.TransformVec4(in cam.InvViewProjMatrix, farClip);

        Vec3 p0 = new Vec3(nearW.X, nearW.Y, nearW.Z) / (DetMath.Abs(nearW.W) > 1e-6f ? nearW.W : 1f);
        Vec3 p1 = new Vec3(farW.X, farW.Y, farW.Z) / (DetMath.Abs(farW.W) > 1e-6f ? farW.W : 1f);
        return new Ray(p0, p1 - p0);
    }

    public static bool WorldToScreen(in CameraData cam, Vec3 worldPos, float screenW, float screenH, out Vec3 screenPos)
    {
        Vec4 clip = Mat4.TransformVec4(in cam.ViewProjMatrix, new Vec4(worldPos, 1f));
        if (clip.W <= 1e-6f)
        {
            screenPos = Vec3.Zero;
            return false;
        }
        ComputeViewportPixels(in cam, screenW, screenH, out float vpX, out float vpY, out float vpW, out float vpH);
        float invW = 1f / clip.W;
        float ndcX = clip.X * invW;
        float ndcY = clip.Y * invW;
        float ndcZ = clip.Z * invW;
        screenPos = new Vec3(vpX + (ndcX * 0.5f + 0.5f) * vpW, vpY + (0.5f - ndcY * 0.5f) * vpH, ndcZ);
        return ndcZ >= 0f && ndcZ <= 1f;
    }

    /// <summary>
    /// Returns active enabled cameras sorted by DepthOrder ascending using the Frame arena (zero GC).
    /// </summary>
    public static NativeSlice<CameraHandle> GetSortedActiveCameras(in CameraTable table, ref LinearArena frameArena)
    {
        int count = 0;
        for (int i = 0; i < table.Capacity; i++)
            if (table.Alive[i] != 0 && table.Slots[i].Enabled != 0) count++;

        if (count == 0) return default;
        var list = frameArena.AllocSlice<CameraHandle>(count, 16, zeroed: false);
        int idx = 0;
        for (int i = 0; i < table.Capacity; i++)
            if (table.Alive[i] != 0 && table.Slots[i].Enabled != 0) list[idx++] = table.Slots[i].Handle;

        // Insertion sort (tiny N <= 32)
        for (int i = 1; i < count; i++)
        {
            CameraHandle key = list[i];
            int keyOrder = table.Slots[key.Index].DepthOrder;
            int j = i - 1;
            while (j >= 0 && table.Slots[list[j].Index].DepthOrder > keyOrder)
            {
                list[j + 1] = list[j];
                j--;
            }
            list[j + 1] = key;
        }
        return list;
    }
}

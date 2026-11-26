using System;
using BringEngine.Core.Handles;
using BringEngine.Core.Math;
using BringEngine.Core.Memory;
using BringEngine.Simulation.Camera;
using BringEngine.Simulation.Input;
using BringEngine.Simulation.World;

namespace BringEngine.Tests;

public static unsafe class SimulationTests
{
    public static void RunAll()
    {
        Console.WriteLine("=== Phase 3: ECS World, Transform Hierarchy, Camera & Input ===");
        Assert.Run("EntityTable_SpawnDestroyGenerationAndSceneCollect", &TestEntities);
        Assert.Run("TransformTable_ParentChildHierarchyPropagation", &TestTransformHierarchy);
        Assert.Run("CameraSystem_FollowShakeFrustumAndRayPick", &TestCameraSystem);
        Assert.Run("InputState_BitsetEdgesAndWASD", &TestInputState);
        Assert.Run("InputState_ExtendedKeysMouseReleaseTextInputFocusAndZeroGc", &TestInputPlatformEssentials);
        Assert.Run("GamepadInput_ButtonsAxesRadialDeadzoneMultiSlotAndCombinedMoveZeroGc", &TestGamepadInput);
    }

    private static void TestEntities()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var table = EntityTable.Create(ref alloc.Persistent, 256);

        var sceneA = SceneId.Create(1, 1);
        var sceneB = SceneId.Create(2, 1);

        var e1 = EntitySystem.Spawn(ref table, sceneA);
        var e2 = EntitySystem.Spawn(ref table, sceneB);
        var e3 = EntitySystem.Spawn(ref table, sceneA);
        Assert.Equal(3, table.AliveCount, "3 alive");
        Assert.True(EntitySystem.IsAlive(in table, e1), "e1 alive");

        Assert.True(EntitySystem.Destroy(ref table, e2), "destroy e2");
        Assert.True(!EntitySystem.IsAlive(in table, e2), "e2 dead");
        Assert.Equal(2, table.AliveCount, "2 alive");

        // Reuse slot -> new generation invalidates old e2 handle
        var e4 = EntitySystem.Spawn(ref table, sceneA);
        Assert.Equal(e2.Index, e4.Index, "slot reused");
        Assert.True(e4.Generation != e2.Generation, "generation incremented");
        Assert.True(!EntitySystem.IsAlive(in table, e2), "stale handle rejected");
        Assert.True(EntitySystem.IsAlive(in table, e4), "new handle alive");

        alloc.BeginFrame();
        var sceneAEntities = EntitySystem.CollectByScene(in table, sceneA, ref alloc.Frame);
        Assert.Equal(3, sceneAEntities.Length, "3 entities in sceneA");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestTransformHierarchy()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 256);
        var transforms = TransformTable.Create(ref alloc.Persistent, 256, 256);

        var root = EntitySystem.Spawn(ref entities);
        var child = EntitySystem.Spawn(ref entities);
        var grandChild = EntitySystem.Spawn(ref entities);

        // Root at (10, 0, 0) rotated 90 deg around Y (+X becomes -Z)
        TransformSystem.Add(ref transforms, root, new Vec3(10, 0, 0), Quat.FromAxisAngle(Vec3.UnitY, DetMath.HalfPi), Vec3.One);
        TransformSystem.Add(ref transforms, child, new Vec3(5, 0, 0), Quat.Identity, new Vec3(2, 2, 2), root);
        TransformSystem.Add(ref transforms, grandChild, new Vec3(0, 3, 0), Quat.Identity, Vec3.One, child);

        TransformSystem.PropagateHierarchy(ref transforms);

        Vec3 childWorld = TransformSystem.GetWorldPosition(in transforms, child);
        Assert.Near(10f, childWorld.X, 1e-3f, "child world X");
        Assert.Near(0f, childWorld.Y, 1e-3f, "child world Y");
        Assert.Near(-5f, childWorld.Z, 1e-3f, "child world Z");

        Vec3 grandWorld = TransformSystem.GetWorldPosition(in transforms, grandChild);
        Assert.Near(10f, grandWorld.X, 1e-3f, "grandChild world X");
        Assert.Near(6f, grandWorld.Y, 1e-3f, "grandChild world Y (scaled by parent 2x)");
        Assert.Near(-5f, grandWorld.Z, 1e-3f, "grandChild world Z");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestCameraSystem()
    {
        NativeMemoryTracker.ResetStatsForTest();
        var alloc = EngineAllocators.Create(256 * 1024, 64 * 1024, 64 * 1024, 64 * 1024);
        var entities = EntityTable.Create(ref alloc.Persistent, 64);
        var transforms = TransformTable.Create(ref alloc.Persistent, 64, 64);
        var cameras = CameraTable.Create(ref alloc.Persistent, 16);

        var target = EntitySystem.Spawn(ref entities);
        TransformSystem.Add(ref transforms, target, new Vec3(0, 2, 0));
        TransformSystem.PropagateHierarchy(ref transforms);

        var mainCam = CameraSystem.CreatePerspective(ref cameras, new Vec3(0, 5, 10), Vec3.Zero, 60f, 0.1f, 200f, depthOrder: 0);
        var uiCam = CameraSystem.CreateOrthographicUI(ref cameras, 1280, 720, depthOrder: 100);

        var camPtr = CameraSystem.TryGetPtr(in cameras, mainCam);
        camPtr->Mode = CameraMode.FollowTarget;
        camPtr->TargetEntity = target;
        camPtr->FollowOffset = new Vec3(0, 4, 10);
        camPtr->LookOffset = Vec3.Zero;
        camPtr->FollowDamping = 0f; // instant snap

        CameraSystem.AddTrauma(ref cameras, mainCam, 0.5f);
        CameraSystem.UpdateAll(ref cameras, in transforms, 1f / 60f, 1280f, 720f);

        Assert.Near(6f, camPtr->Position.Y, 1e-3f, "Follow cam snapped Y");
        Assert.True(camPtr->Frustum.IntersectsSphere(new Vec3(0, 2, 0), 0.5f), "Target visible in frustum");

        // Screen center ray should point towards target
        Ray centerRay = CameraSystem.ScreenToWorldRay(in *camPtr, 640f, 360f, 1280f, 720f);
        Assert.True(Vec3.Dot(centerRay.Direction, camPtr->Forward) > 0.99f, "Center ray aligns with camera forward");

        // WorldToScreen of look target should be near screen center
        Assert.True(CameraSystem.WorldToScreen(in *camPtr, camPtr->LookTarget, 1280f, 720f, out Vec3 sp), "LookTarget projects onscreen");
        Assert.Near(640f, sp.X, 40f, "Projected X near center");
        Assert.Near(360f, sp.Y, 40f, "Projected Y near center");

        // Sorted camera order
        alloc.BeginFrame();
        var sorted = CameraSystem.GetSortedActiveCameras(in cameras, ref alloc.Frame);
        Assert.Equal(2, sorted.Length, "2 active cameras");
        Assert.Equal(mainCam, sorted[0], "Main camera renders first");
        Assert.Equal(uiCam, sorted[1], "UI camera renders second");

        alloc.Dispose();
        Assert.Equal(0L, NativeMemoryTracker.LiveBytes, "Zero leak");
    }

    private static void TestInputState()
    {
        InputState input = default;
        input.BeginFrame();
        input.SetKey(KeyCode.W, true);
        Assert.True(input.IsKeyDown(KeyCode.W), "W down");
        Assert.True(input.IsKeyPressed(KeyCode.W), "W just pressed");

        var axis = input.GetMoveAxisWASD();
        Assert.Near(0f, axis.X, 1e-4f, "WASD X");
        Assert.Near(1f, axis.Y, 1e-4f, "WASD Y");

        input.BeginFrame();
        Assert.True(input.IsKeyDown(KeyCode.W), "W still down");
        Assert.True(!input.IsKeyPressed(KeyCode.W), "W not newly pressed");

        input.SetKey(KeyCode.W, false);
        Assert.True(input.IsKeyReleased(KeyCode.W), "W released");
    }

    private static void TestInputPlatformEssentials()
    {
        InputState input = default;
        // Default state must be headless-friendly: focused, not minimized, mouse unlocked
        Assert.True(input.IsWindowFocused, "Default InputState is window-focused");
        Assert.True(!input.IsWindowMinimized, "Default InputState is not minimized");
        Assert.True(!input.IsMouseLocked, "Default InputState mouse is unlocked");

        // Extended KeyCode scancodes
        input.BeginFrame();
        input.SetKey(KeyCode.Num0, true);
        input.SetKey(KeyCode.Num9, true);
        input.SetKey(KeyCode.LeftAlt, true);
        input.SetKey(KeyCode.RightShift, true);
        input.SetKey(KeyCode.Delete, true);
        input.SetKey(KeyCode.Home, true);
        input.SetKey(KeyCode.PageDown, true);
        input.SetKey(KeyCode.Grave, true);
        input.SetKey(KeyCode.KpEnter, true);
        Assert.True(input.IsKeyPressed(KeyCode.Num0) && input.IsKeyPressed(KeyCode.Num9), "Num0/Num9 pressed");
        Assert.True(input.IsKeyPressed(KeyCode.LeftAlt) && input.IsKeyPressed(KeyCode.RightShift), "Modifier keys pressed");
        Assert.True(input.IsKeyPressed(KeyCode.Delete) && input.IsKeyPressed(KeyCode.Home) && input.IsKeyPressed(KeyCode.PageDown), "Nav keys pressed");
        Assert.True(input.IsKeyPressed(KeyCode.Grave) && input.IsKeyPressed(KeyCode.KpEnter), "Punctuation & keypad keys pressed");

        // Mouse button pressed / down / released edge transitions
        input.MouseButtonsDown |= 1u << 0; // Left button down
        Assert.True(input.IsMousePressed(0) && input.IsMouseDown(0) && !input.IsMouseReleased(0), "LMB pressed edge");
        input.BeginFrame();
        Assert.True(!input.IsMousePressed(0) && input.IsMouseDown(0) && !input.IsMouseReleased(0), "LMB held");
        input.MouseButtonsDown &= ~(1u << 0);
        Assert.True(!input.IsMouseDown(0) && input.IsMouseReleased(0), "LMB released edge");

        // Window focus & minimize edges
        input.BeginFrame();
        input.SetWindowFocused(false);
        input.SetWindowMinimized(true);
        input.SetMouseLocked(true);
        Assert.True(!input.IsWindowFocused && input.WasWindowFocusLost, "Focus lost edge detected");
        Assert.True(input.IsWindowMinimized && input.IsMouseLocked, "Minimized & mouse lock flags tracked");
        input.BeginFrame();
        Assert.True(!input.WasWindowFocusLost && !input.IsWindowFocused, "Focus lost edge clears next frame");
        input.SetWindowFocused(true);
        Assert.True(input.IsWindowFocused && input.WasWindowFocusGained, "Focus gained edge detected");

        // UTF-8 text input buffer (ASCII + multi-byte UTF-8 + frame clear + capacity clamp)
        input.BeginFrame();
        Assert.True(!input.HasTextInput, "Text input cleared on BeginFrame");
        input.AppendTextInput("Bring-");
        input.AppendTextInputUtf8("123"u8);
        Assert.True(input.HasTextInput && input.TextInputLength == 9, "Appended 9 UTF-8 text bytes");

        Span<char> chars = stackalloc char[32];
        int charCount = input.CopyTextInputChars(chars);
        Assert.True(chars.Slice(0, charCount).SequenceEqual("Bring-123"), "Decoded text input matches");

        // Overflow clamp safety (> MaxTextInputBytes must not throw or corrupt)
        for (int i = 0; i < 20; i++) input.AppendTextInput("ABCDEFGH");
        Assert.Equal((byte)InputState.MaxTextInputBytes, input.TextInputLength, "Text input clamped cleanly to MaxTextInputBytes");

        // Zero-GC hot loop across 500 frames
        Span<byte> utf8Out = stackalloc byte[InputState.MaxTextInputBytes];
        Span<char> charOut = stackalloc char[InputState.MaxTextInputBytes];
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        int totalChars = 0;
        for (int i = 0; i < 500; i++)
        {
            input.BeginFrame();
            input.AppendTextInput("ab");
            int b = input.CopyTextInputUtf8(utf8Out);
            int c = input.CopyTextInputChars(charOut);
            bool rel = input.IsMouseReleased(0);
            totalChars += b + c + (rel ? 1 : 0);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "Text input + mouse release + focus hot loop allocates 0 GC bytes");
        Assert.True(totalChars > 0, "Text input loop read characters");
    }

    private static void TestGamepadInput()
    {
        InputState input = default;
        Assert.Equal((byte)0, input.ConnectedGamepadCount, "Initially 0 gamepads");
        Assert.True(!input.IsGamepadConnected(0), "Slot 0 disconnected");

        // Connect two gamepads (slot 0 & slot 2)
        input.SetGamepadConnected(0, true, instanceId: 101u);
        input.SetGamepadConnected(2, true, instanceId: 202u);
        Assert.Equal((byte)2, input.ConnectedGamepadCount, "2 connected gamepads");
        Assert.True(input.IsGamepadConnected(0) && input.IsGamepadConnected(2), "Slots 0 and 2 connected");

        // Frame 1: press South (A) + Steam Deck LeftPaddle1 + trigger + sticks
        input.BeginFrame();
        input.SetGamepadButton(0, GamepadButton.South, true);
        input.SetGamepadButton(0, GamepadButton.LeftPaddle1, true);
        input.SetGamepadButton(2, GamepadButton.Start, true);
        input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.08f);  // inside 0.15 radial deadzone
        input.SetGamepadAxis(0, GamepadAxis.LeftY, 0.06f);  // magnitude 0.10 <= 0.15 -> Zero!
        input.SetGamepadAxis(0, GamepadAxis.RightX, 1.0f);
        input.SetGamepadAxis(0, GamepadAxis.RightY, 1.0f);  // diagonal clamped to length 1.0
        input.SetGamepadAxis(0, GamepadAxis.LeftTrigger, 0.75f);
        input.SetGamepadAxis(0, GamepadAxis.RightTrigger, 1.5f); // clamped to 1.0

        Assert.True(input.IsGamepadButtonDown(GamepadButton.South, 0), "South down");
        Assert.True(input.IsGamepadButtonPressed(GamepadButton.South, 0), "South newly pressed");
        Assert.True(input.IsGamepadButtonPressed(GamepadButton.LeftPaddle1, 0), "Steam Deck LeftPaddle1 pressed");
        Assert.True(input.IsAnyGamepadButtonPressed(GamepadButton.Start), "Any-controller Start pressed on slot 2");

        Vec2 deadStick = input.GetGamepadLeftStick(0, deadzone: 0.15f);
        Assert.Near(0f, deadStick.X, 1e-5f, "Inside radial deadzone X == 0");
        Assert.Near(0f, deadStick.Y, 1e-5f, "Inside radial deadzone Y == 0");

        Vec2 clampedRight = input.GetGamepadRightStick(0, deadzone: 0.15f);
        float rightLen = DetMath.Sqrt(clampedRight.X * clampedRight.X + clampedRight.Y * clampedRight.Y);
        Assert.Near(1f, rightLen, 1e-4f, "Diagonal stick clamped to unit length");
        Assert.Near(0.75f, input.GetGamepadLeftTrigger(0), 1e-4f, "Left trigger 0.75");
        Assert.Near(1.0f, input.GetGamepadRightTrigger(0), 1e-4f, "Right trigger clamped to 1.0");

        // Halfway past deadzone (0.15 + 0.5 * 0.85 = 0.575) -> exact 0.50 scaled output
        input.SetGamepadAxis(0, GamepadAxis.LeftX, 0.575f);
        input.SetGamepadAxis(0, GamepadAxis.LeftY, 0f);
        Vec2 halfStick = input.GetGamepadLeftStick(0, deadzone: 0.15f);
        Assert.Near(0.5f, halfStick.X, 1e-4f, "Scaled radial deadzone midpoint == 0.5");

        // Unified GetMoveAxis combines keyboard + left stick + D-pad, clamped <= 1
        input.SetKey(KeyCode.W, true);
        Vec2 combined = input.GetMoveAxis(gamepadSlot: 0, deadzone: 0.15f);
        float combLen = DetMath.Sqrt(combined.X * combined.X + combined.Y * combined.Y);
        Assert.True(combined.X > 0.3f && combined.Y > 0.7f, "Combined keyboard W + stick right produces diagonal");
        Assert.True(combLen <= 1.0001f, $"Combined move vector clamped to unit circle (got {combLen:F4})");

        // Frame 2: hold South, release LeftPaddle1
        input.BeginFrame();
        input.SetGamepadButton(0, GamepadButton.LeftPaddle1, false);
        Assert.True(input.IsGamepadButtonDown(GamepadButton.South, 0), "South still down");
        Assert.True(!input.IsGamepadButtonPressed(GamepadButton.South, 0), "South not newly pressed");
        Assert.True(input.IsGamepadButtonReleased(GamepadButton.LeftPaddle1, 0), "LeftPaddle1 released");

        // Disconnect clears slot cleanly
        input.SetGamepadConnected(0, false);
        Assert.Equal((byte)1, input.ConnectedGamepadCount, "1 connected gamepad after disconnect");
        Assert.True(!input.IsGamepadButtonDown(GamepadButton.South, 0), "Disconnected pad reports false");

        // Zero-GC hot loop verification across 1000 frames
        input.SetGamepadConnected(0, true, 101u);
        long gcBefore = GC.GetAllocatedBytesForCurrentThread();
        float acc = 0f;
        for (int i = 0; i < 1000; i++)
        {
            input.BeginFrame();
            input.SetGamepadAxis(0, GamepadAxis.LeftX, (i & 1) == 0 ? 0.6f : -0.6f);
            input.SetGamepadButton(0, GamepadButton.South, (i & 3) == 0);
            Vec2 m = input.GetMoveAxis(0);
            acc += m.X + (input.IsGamepadButtonPressed(GamepadButton.South, 0) ? 1f : 0f);
        }
        long gcAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, gcAfter - gcBefore, "Gamepad input hot loop allocates 0 GC bytes");
        Assert.True(acc > 0f, "Gamepad loop produced non-zero inputs");
    }
}

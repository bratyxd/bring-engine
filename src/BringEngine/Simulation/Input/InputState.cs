using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BringEngine.Core.Math;

namespace BringEngine.Simulation.Input;

/// <summary>
/// Platform-agnostic SDL3 scancode constants so simulation code stays 100% decoupled from SDL3.
/// Matches SDL_Scancode (USB HID) numeric values.
/// </summary>
public enum KeyCode : int
{
    Unknown = 0,
    A = 4, B = 5, C = 6, D = 7, E = 8, F = 9, G = 10, H = 11,
    I = 12, J = 13, K = 14, L = 15, M = 16, N = 17, O = 18, P = 19,
    Q = 20, R = 21, S = 22, T = 23, U = 24, V = 25, W = 26, X = 27,
    Y = 28, Z = 29,
    Num1 = 30, Num2 = 31, Num3 = 32, Num4 = 33, Num5 = 34,
    Num6 = 35, Num7 = 36, Num8 = 37, Num9 = 38, Num0 = 39,
    Return = 40, Escape = 41, Backspace = 42, Tab = 43, Space = 44,
    Minus = 45, Equals = 46, LeftBracket = 47, RightBracket = 48, Backslash = 49,
    Semicolon = 51, Apostrophe = 52, Grave = 53, Comma = 54, Period = 55, Slash = 56, CapsLock = 57,
    F1 = 58, F2 = 59, F3 = 60, F4 = 61, F5 = 62, F6 = 63,
    F7 = 64, F8 = 65, F9 = 66, F10 = 67, F11 = 68, F12 = 69,
    PrintScreen = 70, ScrollLock = 71, Pause = 72,
    Insert = 73, Home = 74, PageUp = 75, Delete = 76, End = 77, PageDown = 78,
    Right = 79, Left = 80, Down = 81, Up = 82,
    KpDivide = 84, KpMultiply = 85, KpMinus = 86, KpPlus = 87, KpEnter = 88,
    Kp1 = 89, Kp2 = 90, Kp3 = 91, Kp4 = 92, Kp5 = 93,
    Kp6 = 94, Kp7 = 95, Kp8 = 96, Kp9 = 97, Kp0 = 98, KpPeriod = 99,
    LeftCtrl = 224, LeftShift = 225, LeftAlt = 226, LeftSuper = 227,
    RightCtrl = 228, RightShift = 229, RightAlt = 230, RightSuper = 231,
}

/// <summary>
/// Platform-agnostic standard gamepad buttons matching SDL3's SDL_GamepadButton layout
/// (Xbox / PlayStation / Nintendo / Steam Deck including back grip paddles and touchpad click).
/// </summary>
public enum GamepadButton : byte
{
    South = 0,          // Xbox A / PS Cross / Nintendo B / Steam Deck A
    East = 1,           // Xbox B / PS Circle / Nintendo A / Steam Deck B
    West = 2,           // Xbox X / PS Square / Nintendo Y / Steam Deck X
    North = 3,          // Xbox Y / PS Triangle / Nintendo X / Steam Deck Y
    Back = 4,           // View / Share / Select
    Guide = 5,          // Xbox / PS / Steam button
    Start = 6,          // Menu / Options / Start
    LeftStick = 7,      // L3
    RightStick = 8,     // R3
    LeftShoulder = 9,   // LB / L1
    RightShoulder = 10, // RB / R1
    DPadUp = 11,
    DPadDown = 12,
    DPadLeft = 13,
    DPadRight = 14,
    Misc1 = 15,         // Share / Capture / Mic
    RightPaddle1 = 16,  // Steam Deck R4 / Xbox Elite P1
    LeftPaddle1 = 17,   // Steam Deck L4 / Xbox Elite P3
    RightPaddle2 = 18,  // Steam Deck R5 / Xbox Elite P2
    LeftPaddle2 = 19,   // Steam Deck L5 / Xbox Elite P4
    Touchpad = 20,      // DualSense / DualShock / Steam Deck touchpad click
    Count = 21,
}

/// <summary>
/// Platform-agnostic standard gamepad analog axes.
/// Stick Y axes follow Bring Engine's +Y = Up convention (SDL3 raw negative-up is inverted automatically by PlatformSystem).
/// </summary>
public enum GamepadAxis : byte
{
    LeftX = 0,         // [-1, 1], +X = Right
    LeftY = 1,         // [-1, 1], +Y = Up
    RightX = 2,        // [-1, 1], +X = Right
    RightY = 3,        // [-1, 1], +Y = Up
    LeftTrigger = 4,   // [0, 1]
    RightTrigger = 5,  // [0, 1]
    Count = 6,
}

/// <summary>
/// Per-controller unmanaged state snapshot (zero pointers, zero GC, deterministic across headless tests & 2-thread pipeline).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GamepadState
{
    public uint InstanceId;
    public uint ButtonsDown;
    public uint ButtonsPrev;
    public float LeftX;
    public float LeftY;
    public float RightX;
    public float RightY;
    public float LeftTrigger;
    public float RightTrigger;
    public byte Connected;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginFrame()
    {
        ButtonsPrev = ButtonsDown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        InstanceId = 0;
        ButtonsDown = 0;
        ButtonsPrev = 0;
        LeftX = 0f;
        LeftY = 0f;
        RightX = 0f;
        RightY = 0f;
        LeftTrigger = 0f;
        RightTrigger = 0f;
        Connected = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetButton(GamepadButton button, bool down)
    {
        byte idx = (byte)button;
        if (idx >= (byte)GamepadButton.Count) return;
        uint bit = 1u << idx;
        if (down) ButtonsDown |= bit;
        else ButtonsDown &= ~bit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAxis(GamepadAxis axis, float value)
    {
        switch (axis)
        {
            case GamepadAxis.LeftX: LeftX = DetMath.Clamp(value, -1f, 1f); break;
            case GamepadAxis.LeftY: LeftY = DetMath.Clamp(value, -1f, 1f); break;
            case GamepadAxis.RightX: RightX = DetMath.Clamp(value, -1f, 1f); break;
            case GamepadAxis.RightY: RightY = DetMath.Clamp(value, -1f, 1f); break;
            case GamepadAxis.LeftTrigger: LeftTrigger = DetMath.Clamp01(value); break;
            case GamepadAxis.RightTrigger: RightTrigger = DetMath.Clamp01(value); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsButtonDown(GamepadButton button)
    {
        byte idx = (byte)button;
        return Connected != 0 && idx < (byte)GamepadButton.Count && (ButtonsDown & (1u << idx)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsButtonPressed(GamepadButton button)
    {
        byte idx = (byte)button;
        if (Connected == 0 || idx >= (byte)GamepadButton.Count) return false;
        uint bit = 1u << idx;
        return (ButtonsDown & bit) != 0 && (ButtonsPrev & bit) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsButtonReleased(GamepadButton button)
    {
        byte idx = (byte)button;
        if (Connected == 0 || idx >= (byte)GamepadButton.Count) return false;
        uint bit = 1u << idx;
        return (ButtonsDown & bit) == 0 && (ButtonsPrev & bit) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vec2 GetLeftStick(float deadzone = 0.15f)
        => Connected != 0 ? ApplyRadialDeadzone(LeftX, LeftY, deadzone) : Vec2.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vec2 GetRightStick(float deadzone = 0.15f)
        => Connected != 0 ? ApplyRadialDeadzone(RightX, RightY, deadzone) : Vec2.Zero;

    public readonly Vec2 GetDPadAxis()
    {
        if (Connected == 0) return Vec2.Zero;
        float x = (IsButtonDown(GamepadButton.DPadRight) ? 1f : 0f) - (IsButtonDown(GamepadButton.DPadLeft) ? 1f : 0f);
        float y = (IsButtonDown(GamepadButton.DPadUp) ? 1f : 0f) - (IsButtonDown(GamepadButton.DPadDown) ? 1f : 0f);
        float len = DetMath.Sqrt(x * x + y * y);
        return len > 1e-4f ? new Vec2(x / len, y / len) : Vec2.Zero;
    }

    /// <summary>
    /// Applies a smooth scaled radial deadzone and clamps magnitude to 1.0 (ideal for twin-stick & Steam Deck controls).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 ApplyRadialDeadzone(float x, float y, float deadzone)
    {
        float dz = DetMath.Clamp(deadzone, 0f, 0.95f);
        float mag = DetMath.Sqrt(x * x + y * y);
        if (mag <= dz || mag <= 1e-5f) return Vec2.Zero;
        float scaled = (mag - dz) / (1f - dz);
        if (scaled > 1f) scaled = 1f;
        float factor = scaled / mag;
        return new Vec2(x * factor, y * factor);
    }
}

/// <summary>
/// Unmanaged input snapshot populated by SDL3 platform (keyboard, mouse, up to 4 gamepads)
/// OR fed synthetically in headless tests and deterministic playtests.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct InputState
{
    public const int MaxGamepads = 4;

    public fixed ulong KeysDown[8];     // 512 scancodes bitset
    public fixed ulong KeysPrev[8];
    public float MouseX;
    public float MouseY;
    public float MouseDeltaX;
    public float MouseDeltaY;
    public float MouseWheelY;
    public uint MouseButtonsDown;
    public uint MouseButtonsPrev;

    // Inline fixed 4-controller state (copies cleanly by value across Logic/Render threads with 0 pointers)
    public GamepadState Gamepad0;
    public GamepadState Gamepad1;
    public GamepadState Gamepad2;
    public GamepadState Gamepad3;
    public byte ConnectedGamepadCount;
    public byte QuitRequested;

    // Zero-GC per-frame UTF-8 text input buffer (populated when PlatformSystem.StartTextInput is active)
    public const int MaxTextInputBytes = 64;
    public fixed byte TextInputUtf8[MaxTextInputBytes];
    public byte TextInputLength;

    // Window focus, minimize & cursor lock state (0-initialized defaults = focused, visible, unlocked — headless-test friendly)
    public byte WindowUnfocused;
    public byte WindowUnfocusedPrev;
    public byte WindowMinimized;
    public byte MouseLocked;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginFrame()
    {
        for (int i = 0; i < 8; i++)
            KeysPrev[i] = KeysDown[i];
        MouseButtonsPrev = MouseButtonsDown;
        MouseDeltaX = 0f;
        MouseDeltaY = 0f;
        MouseWheelY = 0f;
        TextInputLength = 0;
        WindowUnfocusedPrev = WindowUnfocused;
        Gamepad0.BeginFrame();
        Gamepad1.BeginFrame();
        Gamepad2.BeginFrame();
        Gamepad3.BeginFrame();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKey(int scancode, bool down)
    {
        if ((uint)scancode >= 512u) return;
        int word = scancode >> 6;
        ulong bit = 1UL << (scancode & 63);
        if (down) KeysDown[word] |= bit;
        else KeysDown[word] &= ~bit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetKey(KeyCode key, bool down) => SetKey((int)key, down);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsKeyDown(KeyCode key)
    {
        int s = (int)key;
        if ((uint)s >= 512u) return false;
        return (KeysDown[s >> 6] & (1UL << (s & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsKeyPressed(KeyCode key)
    {
        int s = (int)key;
        if ((uint)s >= 512u) return false;
        ulong bit = 1UL << (s & 63);
        int w = s >> 6;
        return (KeysDown[w] & bit) != 0 && (KeysPrev[w] & bit) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsKeyReleased(KeyCode key)
    {
        int s = (int)key;
        if ((uint)s >= 512u) return false;
        ulong bit = 1UL << (s & 63);
        int w = s >> 6;
        return (KeysDown[w] & bit) == 0 && (KeysPrev[w] & bit) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsMouseDown(int buttonIndex) => (MouseButtonsDown & (1u << buttonIndex)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsMousePressed(int buttonIndex)
    {
        uint bit = 1u << buttonIndex;
        return (MouseButtonsDown & bit) != 0 && (MouseButtonsPrev & bit) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsMouseReleased(int buttonIndex)
    {
        uint bit = 1u << buttonIndex;
        return (MouseButtonsDown & bit) == 0 && (MouseButtonsPrev & bit) != 0;
    }

    public readonly bool IsWindowFocused => WindowUnfocused == 0;
    public readonly bool WasWindowFocusLost => WindowUnfocused != 0 && WindowUnfocusedPrev == 0;
    public readonly bool WasWindowFocusGained => WindowUnfocused == 0 && WindowUnfocusedPrev != 0;
    public readonly bool IsWindowMinimized => WindowMinimized != 0;
    public readonly bool IsMouseLocked => MouseLocked != 0;
    public readonly bool HasTextInput => TextInputLength > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWindowFocused(bool focused) => WindowUnfocused = (byte)(focused ? 0 : 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWindowMinimized(bool minimized) => WindowMinimized = (byte)(minimized ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMouseLocked(bool locked) => MouseLocked = (byte)(locked ? 1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearTextInput() => TextInputLength = 0;

    /// <summary>
    /// Appends a null-terminated UTF-8 string into this frame's fixed text input buffer (0 GC).
    /// </summary>
    public void AppendTextInputUtf8(byte* utf8NullTerminated)
    {
        if (utf8NullTerminated == null) return;
        int len = TextInputLength;
        for (int i = 0; utf8NullTerminated[i] != 0 && len < MaxTextInputBytes; i++)
            TextInputUtf8[len++] = utf8NullTerminated[i];
        TextInputLength = (byte)len;
    }

    /// <summary>
    /// Appends raw UTF-8 bytes into this frame's fixed text input buffer (0 GC; useful in headless tests).
    /// </summary>
    public void AppendTextInputUtf8(ReadOnlySpan<byte> utf8Bytes)
    {
        int len = TextInputLength;
        int avail = MaxTextInputBytes - len;
        if (avail <= 0 || utf8Bytes.IsEmpty) return;
        int copy = utf8Bytes.Length < avail ? utf8Bytes.Length : avail;
        for (int i = 0; i < copy; i++)
            TextInputUtf8[len + i] = utf8Bytes[i];
        TextInputLength = (byte)(len + copy);
    }

    /// <summary>
    /// Encodes UTF-16 text into this frame's fixed UTF-8 text input buffer (0 GC; useful in headless tests).
    /// </summary>
    public void AppendTextInput(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        int len = TextInputLength;
        int avail = MaxTextInputBytes - len;
        if (avail <= 0) return;

        fixed (byte* dstBase = TextInputUtf8)
        {
            Span<byte> dst = new Span<byte>(dstBase + len, avail);
            if (Encoding.UTF8.TryGetBytes(text, dst, out int written))
            {
                TextInputLength = (byte)(len + written);
                return;
            }

            // Partial fit fallback (append char-by-char until buffer full, zero GC)
            Span<byte> tmp = stackalloc byte[4];
            for (int i = 0; i < text.Length; i++)
            {
                ReadOnlySpan<char> slice = text.Slice(i, 1);
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    slice = text.Slice(i, 2);
                    i++;
                }
                int b = Encoding.UTF8.GetBytes(slice, tmp);
                if (len + b > MaxTextInputBytes) break;
                for (int k = 0; k < b; k++) dstBase[len++] = tmp[k];
            }
            TextInputLength = (byte)len;
        }
    }

    /// <summary>
    /// Copies this frame's UTF-8 text input bytes into caller's span with 0 GC allocations.
    /// </summary>
    public readonly int CopyTextInputUtf8(Span<byte> destination)
    {
        int len = TextInputLength;
        if (len == 0 || destination.IsEmpty) return 0;
        int copy = len < destination.Length ? len : destination.Length;
        fixed (byte* src = TextInputUtf8)
        {
            for (int i = 0; i < copy; i++)
                destination[i] = src[i];
        }
        return copy;
    }

    /// <summary>
    /// Decodes this frame's UTF-8 text input into caller's char span with 0 GC allocations.
    /// </summary>
    public readonly int CopyTextInputChars(Span<char> destination)
    {
        int len = TextInputLength;
        if (len == 0 || destination.IsEmpty) return 0;
        fixed (byte* src = TextInputUtf8)
        fixed (char* dst = destination)
        {
            return Encoding.UTF8.GetChars(src, len, dst, destination.Length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly GamepadState GetGamepad(int slot = 0) => slot switch
    {
        0 => Gamepad0,
        1 => Gamepad1,
        2 => Gamepad2,
        3 => Gamepad3,
        _ => default,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecountConnectedGamepads()
    {
        ConnectedGamepadCount = (byte)(Gamepad0.Connected + Gamepad1.Connected + Gamepad2.Connected + Gamepad3.Connected);
    }

    public void SetGamepadConnected(int slot, bool connected, uint instanceId = 0)
    {
        switch (slot)
        {
            case 0: ApplyConnected(ref Gamepad0, connected, instanceId); break;
            case 1: ApplyConnected(ref Gamepad1, connected, instanceId); break;
            case 2: ApplyConnected(ref Gamepad2, connected, instanceId); break;
            case 3: ApplyConnected(ref Gamepad3, connected, instanceId); break;
            default: return;
        }
        RecountConnectedGamepads();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyConnected(ref GamepadState pad, bool connected, uint instanceId)
    {
        pad.Clear();
        if (connected)
        {
            pad.Connected = 1;
            pad.InstanceId = instanceId;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGamepadButton(int slot, GamepadButton button, bool down)
    {
        bool autoConnected;
        switch (slot)
        {
            case 0: autoConnected = ApplyButton(ref Gamepad0, button, down); break;
            case 1: autoConnected = ApplyButton(ref Gamepad1, button, down); break;
            case 2: autoConnected = ApplyButton(ref Gamepad2, button, down); break;
            case 3: autoConnected = ApplyButton(ref Gamepad3, button, down); break;
            default: return;
        }
        if (autoConnected) RecountConnectedGamepads();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyButton(ref GamepadState pad, GamepadButton button, bool down)
    {
        bool newlyConnected = pad.Connected == 0;
        if (newlyConnected) pad.Connected = 1;
        pad.SetButton(button, down);
        return newlyConnected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGamepadAxis(int slot, GamepadAxis axis, float value)
    {
        bool autoConnected;
        switch (slot)
        {
            case 0: autoConnected = ApplyAxis(ref Gamepad0, axis, value); break;
            case 1: autoConnected = ApplyAxis(ref Gamepad1, axis, value); break;
            case 2: autoConnected = ApplyAxis(ref Gamepad2, axis, value); break;
            case 3: autoConnected = ApplyAxis(ref Gamepad3, axis, value); break;
            default: return;
        }
        if (autoConnected) RecountConnectedGamepads();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyAxis(ref GamepadState pad, GamepadAxis axis, float value)
    {
        bool newlyConnected = pad.Connected == 0;
        if (newlyConnected) pad.Connected = 1;
        pad.SetAxis(axis, value);
        return newlyConnected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyGamepadsFrom(in InputState source)
    {
        CopyGamepadDownState(ref Gamepad0, in source.Gamepad0);
        CopyGamepadDownState(ref Gamepad1, in source.Gamepad1);
        CopyGamepadDownState(ref Gamepad2, in source.Gamepad2);
        CopyGamepadDownState(ref Gamepad3, in source.Gamepad3);
        ConnectedGamepadCount = source.ConnectedGamepadCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyGamepadDownState(ref GamepadState dst, in GamepadState src)
    {
        dst.Connected = src.Connected;
        dst.InstanceId = src.InstanceId;
        dst.ButtonsDown = src.ButtonsDown;
        dst.LeftX = src.LeftX;
        dst.LeftY = src.LeftY;
        dst.RightX = src.RightX;
        dst.RightY = src.RightY;
        dst.LeftTrigger = src.LeftTrigger;
        dst.RightTrigger = src.RightTrigger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsGamepadConnected(int slot = 0) => GetGamepad(slot).Connected != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsGamepadButtonDown(GamepadButton button, int slot = 0) => GetGamepad(slot).IsButtonDown(button);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsGamepadButtonPressed(GamepadButton button, int slot = 0) => GetGamepad(slot).IsButtonPressed(button);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsGamepadButtonReleased(GamepadButton button, int slot = 0) => GetGamepad(slot).IsButtonReleased(button);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsAnyGamepadButtonPressed(GamepadButton button)
        => Gamepad0.IsButtonPressed(button) || Gamepad1.IsButtonPressed(button)
        || Gamepad2.IsButtonPressed(button) || Gamepad3.IsButtonPressed(button);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vec2 GetGamepadLeftStick(int slot = 0, float deadzone = 0.15f) => GetGamepad(slot).GetLeftStick(deadzone);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vec2 GetGamepadRightStick(int slot = 0, float deadzone = 0.15f) => GetGamepad(slot).GetRightStick(deadzone);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float GetGamepadLeftTrigger(int slot = 0)
    {
        GamepadState g = GetGamepad(slot);
        return g.Connected != 0 ? g.LeftTrigger : 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float GetGamepadRightTrigger(int slot = 0)
    {
        GamepadState g = GetGamepad(slot);
        return g.Connected != 0 ? g.RightTrigger : 0f;
    }

    public readonly Vec2 GetMoveAxisWASD()
    {
        float x = (IsKeyDown(KeyCode.D) || IsKeyDown(KeyCode.Right) ? 1f : 0f)
                - (IsKeyDown(KeyCode.A) || IsKeyDown(KeyCode.Left) ? 1f : 0f);
        float y = (IsKeyDown(KeyCode.W) || IsKeyDown(KeyCode.Up) ? 1f : 0f)
                - (IsKeyDown(KeyCode.S) || IsKeyDown(KeyCode.Down) ? 1f : 0f);
        float len = DetMath.Sqrt(x * x + y * y);
        return len > 1e-4f ? new Vec2(x / len, y / len) : Vec2.Zero;
    }

    /// <summary>
    /// Unified movement axis combining Keyboard (WASD / Arrow keys) + Gamepad Left Stick + Gamepad D-Pad,
    /// clamped to unit circle (length <= 1.0) so desktop and Steam Deck / controller controls work seamlessly together.
    /// </summary>
    public readonly Vec2 GetMoveAxis(int gamepadSlot = 0, float deadzone = 0.15f)
    {
        Vec2 kb = GetMoveAxisWASD();
        GamepadState pad = GetGamepad(gamepadSlot);
        Vec2 stick = pad.GetLeftStick(deadzone);
        Vec2 dpad = pad.GetDPadAxis();

        float x = kb.X + stick.X + dpad.X;
        float y = kb.Y + stick.Y + dpad.Y;
        float lenSq = x * x + y * y;
        if (lenSq > 1f)
        {
            float inv = 1f / DetMath.Sqrt(lenSq);
            return new Vec2(x * inv, y * inv);
        }
        return new Vec2(x, y);
    }
}

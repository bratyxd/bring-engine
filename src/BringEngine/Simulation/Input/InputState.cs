using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Math;

namespace BringEngine.Simulation.Input;

/// <summary>
/// Platform-agnostic SDL3 scancode constants so simulation code stays 100% decoupled from SDL3.
/// Matches SDL_Scancode numeric values.
/// </summary>
public enum KeyCode : int
{
    Unknown = 0,
    A = 4, B = 5, C = 6, D = 7, E = 8, F = 9, G = 10, H = 11,
    I = 12, J = 13, K = 14, L = 15, M = 16, N = 17, O = 18, P = 19,
    Q = 20, R = 21, S = 22, T = 23, U = 24, V = 25, W = 26, X = 27,
    Y = 28, Z = 29,
    Num1 = 30, Num2 = 31, Num3 = 32, Num4 = 33, Num5 = 34,
    Return = 40, Escape = 41, Backspace = 42, Tab = 43, Space = 44,
    Right = 79, Left = 80, Down = 81, Up = 82,
    LeftShift = 225, LeftCtrl = 224,
}

/// <summary>
/// Unmanaged input snapshot populated by SDL3 platform OR fed synthetically in headless tests/replays.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct InputState
{
    public fixed ulong KeysDown[8];     // 512 scancodes bitset
    public fixed ulong KeysPrev[8];
    public float MouseX;
    public float MouseY;
    public float MouseDeltaX;
    public float MouseDeltaY;
    public float MouseWheelY;
    public uint MouseButtonsDown;
    public uint MouseButtonsPrev;
    public byte QuitRequested;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginFrame()
    {
        for (int i = 0; i < 8; i++)
            KeysPrev[i] = KeysDown[i];
        MouseButtonsPrev = MouseButtonsDown;
        MouseDeltaX = 0f;
        MouseDeltaY = 0f;
        MouseWheelY = 0f;
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

    public readonly Vec2 GetMoveAxisWASD()
    {
        float x = (IsKeyDown(KeyCode.D) || IsKeyDown(KeyCode.Right) ? 1f : 0f)
                - (IsKeyDown(KeyCode.A) || IsKeyDown(KeyCode.Left) ? 1f : 0f);
        float y = (IsKeyDown(KeyCode.W) || IsKeyDown(KeyCode.Up) ? 1f : 0f)
                - (IsKeyDown(KeyCode.S) || IsKeyDown(KeyCode.Down) ? 1f : 0f);
        float len = DetMath.Sqrt(x * x + y * y);
        return len > 1e-4f ? new Vec2(x / len, y / len) : Vec2.Zero;
    }
}

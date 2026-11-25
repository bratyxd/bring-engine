using System;
using System.Runtime.InteropServices;
using BringEngine.Core.Math;
using BringEngine.Simulation.Input;
using SDL3;

namespace BringEngine.Rendering.Platform;

public enum WindowMode : byte
{
    /// <summary>Default for all Bring Engine games: borderless fullscreen window at native desktop resolution.</summary>
    BorderlessFullscreen = 0,
    Windowed = 1,
}

/// <summary>
/// Unmanaged SDL3 window, gamepad handles & timing state. Only initialized in non-headless mode.
/// MUST be owned and polled on the Main Thread (SDL3 platform requirement).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PlatformWindow
{
    public nint Window;
    public int Width;
    public int Height;
    public int PixelWidth;
    public int PixelHeight;
    public int WindowedWidth;
    public int WindowedHeight;
    public ulong LastTicksNs;
    public fixed long GamepadHandles[InputState.MaxGamepads];
    public fixed uint GamepadIds[InputState.MaxGamepads];
    public WindowMode Mode;
    public byte Initialized;
    public byte GamepadSubsystemActive;
    public byte Closed;
    public byte Resized;
}

public static unsafe class PlatformSystem
{
    /// <summary>
    /// Creates an SDL3 window (with Gamepad subsystem initialized for Xbox / PlayStation / Steam Deck / Steam Input).
    /// All Bring Engine games default to BorderlessFullscreen (desktop borderless full window).
    /// Hidden windows (used by --verify-render and GPU unit tests) stay at exact (width, height) in Windowed mode.
    /// </summary>
    public static PlatformWindow InitWindow(
        string title,
        int width = 1280,
        int height = 720,
        bool hidden = false,
        WindowMode mode = WindowMode.BorderlessFullscreen)
    {
        byte gamepadReady = 1;
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Events | SDL.InitFlags.Gamepad))
        {
            // Graceful fallback for restricted headless containers without gamepad support
            gamepadReady = 0;
            if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Events))
                throw new InvalidOperationException($"SDL_Init failed: {SDL.GetError()}");
        }

        WindowMode effectiveMode = hidden ? WindowMode.Windowed : mode;

        SDL.WindowFlags flags = SDL.WindowFlags.HighPixelDensity;
        if (hidden)
        {
            flags |= SDL.WindowFlags.Hidden;
        }
        else if (effectiveMode == WindowMode.BorderlessFullscreen)
        {
            // SDL3 native borderless desktop fullscreen: Fullscreen with NULL fullscreen mode + Borderless
            flags |= SDL.WindowFlags.Fullscreen | SDL.WindowFlags.Borderless;
        }
        else
        {
            flags |= SDL.WindowFlags.Resizable;
        }

        nint win = SDL.CreateWindow(title, width, height, flags);
        if (win == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

        if (!hidden && effectiveMode == WindowMode.BorderlessFullscreen)
        {
            SDL.SetWindowFullscreenMode(win, nint.Zero); // NULL = borderless desktop fullscreen
            SDL.SetWindowFullscreen(win, true);
        }

        SDL.GetWindowSizeInPixels(win, out int pw, out int ph);
        if (pw <= 0) pw = width;
        if (ph <= 0) ph = height;

        return new PlatformWindow
        {
            Window = win,
            Width = pw,
            Height = ph,
            PixelWidth = pw,
            PixelHeight = ph,
            WindowedWidth = width,
            WindowedHeight = height,
            LastTicksNs = SDL.GetTicksNS(),
            Mode = effectiveMode,
            Initialized = 1,
            GamepadSubsystemActive = gamepadReady,
        };
    }

    /// <summary>
    /// Switches between Borderless Full Window (default) and Resizable Windowed mode at runtime (e.g., F11).
    /// </summary>
    public static void SetWindowMode(ref PlatformWindow win, WindowMode mode)
    {
        if (win.Window == nint.Zero) return;
        if (mode == WindowMode.BorderlessFullscreen)
        {
            SDL.SetWindowFullscreenMode(win.Window, nint.Zero);
            SDL.SetWindowFullscreen(win.Window, true);
        }
        else
        {
            SDL.SetWindowFullscreen(win.Window, false);
            SDL.SetWindowBordered(win.Window, true);
            SDL.SetWindowResizable(win.Window, true);
            if (win.WindowedWidth > 0 && win.WindowedHeight > 0)
                SDL.SetWindowSize(win.Window, win.WindowedWidth, win.WindowedHeight);
        }

        win.Mode = mode;
        SyncPixelSize(ref win);
        win.Resized = 1;
    }

    public static void ToggleBorderlessFullscreen(ref PlatformWindow win)
        => SetWindowMode(ref win, win.Mode == WindowMode.BorderlessFullscreen ? WindowMode.Windowed : WindowMode.BorderlessFullscreen);

    public static void SyncPixelSize(ref PlatformWindow win)
    {
        if (win.Window == nint.Zero) return;
        SDL.GetWindowSizeInPixels(win.Window, out int pw, out int ph);
        if (pw > 0 && ph > 0 && (pw != win.PixelWidth || ph != win.PixelHeight))
        {
            win.PixelWidth = pw;
            win.PixelHeight = ph;
            win.Width = pw;
            win.Height = ph;
            win.Resized = 1;
        }
    }

    public static float PollDeltaSeconds(ref PlatformWindow win)
    {
        ulong now = SDL.GetTicksNS();
        ulong deltaNs = now >= win.LastTicksNs ? (now - win.LastTicksNs) : 0;
        win.LastTicksNs = now;
        float dt = (float)(deltaNs * 1e-9);
        return dt > 0.1f ? 0.1f : (dt < 0f ? 0f : dt);
    }

    public static void PollEvents(ref PlatformWindow win, ref InputState input)
    {
        input.BeginFrame();
        win.Resized = 0;

        while (SDL.PollEvent(out SDL.Event ev))
        {
            SDL.EventType type = (SDL.EventType)ev.Type;
            switch (type)
            {
                case SDL.EventType.Quit:
                case SDL.EventType.WindowCloseRequested:
                    win.Closed = 1;
                    input.QuitRequested = 1;
                    break;

                case SDL.EventType.WindowResized:
                case SDL.EventType.WindowPixelSizeChanged:
                    SDL.GetWindowSizeInPixels(win.Window, out int pw, out int ph);
                    if (pw > 0 && ph > 0)
                    {
                        win.PixelWidth = pw;
                        win.PixelHeight = ph;
                        win.Width = pw;
                        win.Height = ph;
                        win.Resized = 1;
                    }
                    break;

                case SDL.EventType.KeyDown:
                    input.SetKey((int)ev.Key.Scancode, true);
                    break;

                case SDL.EventType.KeyUp:
                    input.SetKey((int)ev.Key.Scancode, false);
                    break;

                case SDL.EventType.MouseMotion:
                    input.MouseX = ev.Motion.X;
                    input.MouseY = ev.Motion.Y;
                    input.MouseDeltaX += ev.Motion.XRel;
                    input.MouseDeltaY += ev.Motion.YRel;
                    break;

                case SDL.EventType.MouseButtonDown:
                    if (ev.Button.Button >= 1 && ev.Button.Button <= 8)
                        input.MouseButtonsDown |= 1u << (ev.Button.Button - 1);
                    break;

                case SDL.EventType.MouseButtonUp:
                    if (ev.Button.Button >= 1 && ev.Button.Button <= 8)
                        input.MouseButtonsDown &= ~(1u << (ev.Button.Button - 1));
                    break;

                case SDL.EventType.MouseWheel:
                    input.MouseWheelY += ev.Wheel.Y;
                    break;

                case SDL.EventType.GamepadAdded:
                    HandleGamepadAdded(ref win, ref input, ev.GDevice.Which);
                    break;

                case SDL.EventType.GamepadRemoved:
                    HandleGamepadRemoved(ref win, ref input, ev.GDevice.Which);
                    break;

                case SDL.EventType.GamepadButtonDown:
                case SDL.EventType.GamepadButtonUp:
                {
                    int slot = FindGamepadSlot(in win, ev.GButton.Which);
                    if (slot >= 0 && TryMapGamepadButton((SDL.GamepadButton)ev.GButton.Button, out GamepadButton mapped))
                        input.SetGamepadButton(slot, mapped, type == SDL.EventType.GamepadButtonDown);
                    break;
                }

                case SDL.EventType.GamepadAxisMotion:
                {
                    int slot = FindGamepadSlot(in win, ev.GAxis.Which);
                    if (slot >= 0 && TryMapGamepadAxis((SDL.GamepadAxis)ev.GAxis.Axis, ev.GAxis.Value, out GamepadAxis axis, out float norm))
                        input.SetGamepadAxis(slot, axis, norm);
                    break;
                }
            }
        }

        SyncPixelSize(ref win);
    }

    /// <summary>
    /// Triggers dual-motor controller rumble (Main Thread / View only).
    /// lowFreq01 and highFreq01 are in [0, 1]; durationMs = 0 stops active rumble.
    /// </summary>
    public static bool RumbleGamepad(ref PlatformWindow win, int slot, float lowFreq01, float highFreq01, uint durationMs)
    {
        if ((uint)slot >= InputState.MaxGamepads) return false;
        nint handle = (nint)win.GamepadHandles[slot];
        if (handle == nint.Zero) return false;
        ushort low = (ushort)(DetMath.Clamp01(lowFreq01) * 65535f);
        ushort high = (ushort)(DetMath.Clamp01(highFreq01) * 65535f);
        return SDL.RumbleGamepad(handle, low, high, durationMs);
    }

    /// <summary>
    /// Triggers impulse trigger rumble on supported controllers (Main Thread / View only).
    /// </summary>
    public static bool RumbleGamepadTriggers(ref PlatformWindow win, int slot, float left01, float right01, uint durationMs)
    {
        if ((uint)slot >= InputState.MaxGamepads) return false;
        nint handle = (nint)win.GamepadHandles[slot];
        if (handle == nint.Zero) return false;
        ushort left = (ushort)(DetMath.Clamp01(left01) * 65535f);
        ushort right = (ushort)(DetMath.Clamp01(right01) * 65535f);
        return SDL.RumbleGamepadTriggers(handle, left, right, durationMs);
    }

    public static string? GetGamepadName(in PlatformWindow win, int slot)
    {
        if ((uint)slot >= InputState.MaxGamepads) return null;
        nint handle = (nint)win.GamepadHandles[slot];
        return handle != nint.Zero ? SDL.GetGamepadName(handle) : null;
    }

    private static void HandleGamepadAdded(ref PlatformWindow win, ref InputState input, uint instanceId)
    {
        if (instanceId == 0 || FindGamepadSlot(in win, instanceId) >= 0) return;
        int freeSlot = -1;
        for (int i = 0; i < InputState.MaxGamepads; i++)
        {
            if (win.GamepadHandles[i] == 0) { freeSlot = i; break; }
        }
        if (freeSlot < 0) return;

        nint pad = SDL.OpenGamepad(instanceId);
        if (pad == nint.Zero) return;

        win.GamepadHandles[freeSlot] = pad;
        win.GamepadIds[freeSlot] = instanceId;
        input.SetGamepadConnected(freeSlot, true, instanceId);
    }

    private static void HandleGamepadRemoved(ref PlatformWindow win, ref InputState input, uint instanceId)
    {
        int slot = FindGamepadSlot(in win, instanceId);
        if (slot < 0) return;

        nint pad = (nint)win.GamepadHandles[slot];
        if (pad != nint.Zero) SDL.CloseGamepad(pad);
        win.GamepadHandles[slot] = 0;
        win.GamepadIds[slot] = 0;
        input.SetGamepadConnected(slot, false, 0);
    }

    private static int FindGamepadSlot(in PlatformWindow win, uint instanceId)
    {
        for (int i = 0; i < InputState.MaxGamepads; i++)
        {
            if (win.GamepadHandles[i] != 0 && win.GamepadIds[i] == instanceId)
                return i;
        }
        return -1;
    }

    private static bool TryMapGamepadButton(SDL.GamepadButton sdlBtn, out GamepadButton mapped)
    {
        switch (sdlBtn)
        {
            case SDL.GamepadButton.South: mapped = GamepadButton.South; return true;
            case SDL.GamepadButton.East: mapped = GamepadButton.East; return true;
            case SDL.GamepadButton.West: mapped = GamepadButton.West; return true;
            case SDL.GamepadButton.North: mapped = GamepadButton.North; return true;
            case SDL.GamepadButton.Back: mapped = GamepadButton.Back; return true;
            case SDL.GamepadButton.Guide: mapped = GamepadButton.Guide; return true;
            case SDL.GamepadButton.Start: mapped = GamepadButton.Start; return true;
            case SDL.GamepadButton.LeftStick: mapped = GamepadButton.LeftStick; return true;
            case SDL.GamepadButton.RightStick: mapped = GamepadButton.RightStick; return true;
            case SDL.GamepadButton.LeftShoulder: mapped = GamepadButton.LeftShoulder; return true;
            case SDL.GamepadButton.RightShoulder: mapped = GamepadButton.RightShoulder; return true;
            case SDL.GamepadButton.DPadUp: mapped = GamepadButton.DPadUp; return true;
            case SDL.GamepadButton.DPadDown: mapped = GamepadButton.DPadDown; return true;
            case SDL.GamepadButton.DPadLeft: mapped = GamepadButton.DPadLeft; return true;
            case SDL.GamepadButton.DPadRight: mapped = GamepadButton.DPadRight; return true;
            case SDL.GamepadButton.Misc1: mapped = GamepadButton.Misc1; return true;
            case SDL.GamepadButton.RightPaddle1: mapped = GamepadButton.RightPaddle1; return true;
            case SDL.GamepadButton.LeftPaddle1: mapped = GamepadButton.LeftPaddle1; return true;
            case SDL.GamepadButton.RightPaddle2: mapped = GamepadButton.RightPaddle2; return true;
            case SDL.GamepadButton.LeftPaddle2: mapped = GamepadButton.LeftPaddle2; return true;
            case SDL.GamepadButton.Touchpad: mapped = GamepadButton.Touchpad; return true;
            default: mapped = default; return false;
        }
    }

    private static bool TryMapGamepadAxis(SDL.GamepadAxis sdlAxis, short rawValue, out GamepadAxis axis, out float normalized)
    {
        float signed = rawValue < 0 ? (rawValue / 32768f) : (rawValue / 32767f);
        switch (sdlAxis)
        {
            case SDL.GamepadAxis.LeftX:
                axis = GamepadAxis.LeftX;
                normalized = signed;
                return true;
            case SDL.GamepadAxis.LeftY:
                axis = GamepadAxis.LeftY;
                normalized = -signed; // Flip SDL negative-up to Bring Engine +Y = Up
                return true;
            case SDL.GamepadAxis.RightX:
                axis = GamepadAxis.RightX;
                normalized = signed;
                return true;
            case SDL.GamepadAxis.RightY:
                axis = GamepadAxis.RightY;
                normalized = -signed; // Flip SDL negative-up to Bring Engine +Y = Up
                return true;
            case SDL.GamepadAxis.LeftTrigger:
                axis = GamepadAxis.LeftTrigger;
                normalized = DetMath.Clamp01(rawValue / 32767f);
                return true;
            case SDL.GamepadAxis.RightTrigger:
                axis = GamepadAxis.RightTrigger;
                normalized = DetMath.Clamp01(rawValue / 32767f);
                return true;
            default:
                axis = default;
                normalized = 0f;
                return false;
        }
    }

    public static void Shutdown(ref PlatformWindow win)
    {
        for (int i = 0; i < InputState.MaxGamepads; i++)
        {
            nint pad = (nint)win.GamepadHandles[i];
            if (pad != nint.Zero)
            {
                SDL.CloseGamepad(pad);
                win.GamepadHandles[i] = 0;
                win.GamepadIds[i] = 0;
            }
        }
        if (win.Window != nint.Zero)
        {
            SDL.DestroyWindow(win.Window);
            win.Window = nint.Zero;
        }
        if (win.Initialized != 0)
        {
            SDL.Quit();
            win.Initialized = 0;
            win.GamepadSubsystemActive = 0;
        }
    }
}

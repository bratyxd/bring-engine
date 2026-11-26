using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Logging;
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
    public int LogicalWidth;
    public int LogicalHeight;
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
    public byte Focused;
    public byte Minimized;
    public byte MouseLocked;
    public byte CursorHidden;
    public byte TextInputActive;
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
        WindowMode mode = WindowMode.BorderlessFullscreen,
        LogState* log = null)
    {
        byte gamepadReady = 1;
        SDL.InitFlags initFlags = SDL.InitFlags.Video | SDL.InitFlags.Events | SDL.InitFlags.Gamepad;
        if (!SDL.InitSubSystem(initFlags))
        {
            // Graceful fallback for restricted headless containers without gamepad support
            gamepadReady = 0;
            initFlags = SDL.InitFlags.Video | SDL.InitFlags.Events;
            if (log != null)
                LogSystem.Warn(log, $"SDL_InitSubSystem with Gamepad failed ({SDL.GetError()}); retrying without Gamepad subsystem");
            if (!SDL.InitSubSystem(initFlags))
            {
                string err = SDL.GetError();
                if (log != null)
                    LogSystem.Error(log, $"SDL_InitSubSystem failed: {err}");
                throw new InvalidOperationException($"SDL_InitSubSystem failed: {err}");
            }
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
        {
            string err = SDL.GetError();
            if (log != null)
                LogSystem.Error(log, $"SDL_CreateWindow('{title}', {width}x{height}) failed: {err}");
            SDL.QuitSubSystem(initFlags);
            throw new InvalidOperationException($"SDL_CreateWindow failed: {err}");
        }

        if (!hidden && effectiveMode == WindowMode.BorderlessFullscreen)
        {
            SDL.SetWindowFullscreenMode(win, nint.Zero); // NULL = borderless desktop fullscreen
            SDL.SetWindowFullscreen(win, true);
        }

        SDL.GetWindowSize(win, out int lw, out int lh);
        if (lw <= 0) lw = width;
        if (lh <= 0) lh = height;

        SDL.GetWindowSizeInPixels(win, out int pw, out int ph);
        if (pw <= 0) pw = lw;
        if (ph <= 0) ph = lh;

        if (log != null)
        {
            LogSystem.Info(log, $"Platform window initialized: '{title}' mode={(hidden ? "HiddenWindowed" : effectiveMode.ToString())} pixels={pw}x{ph} logical={lw}x{lh} windowed={width}x{height} gamepad={(gamepadReady != 0 ? "ON" : "OFF")}");
        }

        return new PlatformWindow
        {
            Window = win,
            Width = pw,
            Height = ph,
            LogicalWidth = lw,
            LogicalHeight = lh,
            PixelWidth = pw,
            PixelHeight = ph,
            WindowedWidth = width,
            WindowedHeight = height,
            LastTicksNs = SDL.GetTicksNS(),
            Mode = effectiveMode,
            Initialized = 1,
            GamepadSubsystemActive = gamepadReady,
            Focused = 1,
            Minimized = 0,
            MouseLocked = 0,
            CursorHidden = 0,
            TextInputActive = 0,
        };
    }

    /// <summary>
    /// Locks or unlocks the mouse cursor to the window in relative-motion mode (FPS / 3D orbit cameras).
    /// While locked, cursor is hidden and MouseDeltaX / MouseDeltaY report raw relative deltas.
    /// Main Thread ONLY.
    /// </summary>
    public static bool SetMouseLock(ref PlatformWindow win, bool locked)
    {
        if (win.Window == nint.Zero) return false;
        bool ok = SDL.SetWindowRelativeMouseMode(win.Window, locked);
        if (ok)
            win.MouseLocked = (byte)(locked ? 1 : 0);
        return ok;
    }

    /// <summary>
    /// Shows or hides the OS mouse cursor (for custom software cursors or clean fullscreen gameplay).
    /// Main Thread ONLY.
    /// </summary>
    public static bool SetCursorVisible(ref PlatformWindow win, bool visible)
    {
        bool ok = visible ? SDL.ShowCursor() : SDL.HideCursor();
        if (ok)
            win.CursorHidden = (byte)(visible ? 0 : 1);
        return ok;
    }

    /// <summary>
    /// Shows or hides the OS mouse cursor directly (Main Thread ONLY).
    /// </summary>
    public static bool SetCursorVisible(bool visible) => visible ? SDL.ShowCursor() : SDL.HideCursor();

    /// <summary>
    /// Enables OS/IME Unicode text input events for the window (populates InputState.TextInputUtf8 each frame).
    /// Call when a text field / chat box / lobby code input gains focus. Main Thread ONLY.
    /// </summary>
    public static bool StartTextInput(ref PlatformWindow win)
    {
        if (win.Window == nint.Zero) return false;
        bool ok = SDL.StartTextInput(win.Window);
        if (ok) win.TextInputActive = 1;
        return ok;
    }

    /// <summary>
    /// Disables OS/IME Unicode text input events for the window when leaving a text field.
    /// Main Thread ONLY.
    /// </summary>
    public static bool StopTextInput(ref PlatformWindow win)
    {
        if (win.Window == nint.Zero) return false;
        bool ok = SDL.StopTextInput(win.Window);
        win.TextInputActive = 0;
        return ok;
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

    /// <summary>
    /// Converts SDL3 logical window coordinates into backing-pixel coordinates matching PixelWidth/PixelHeight
    /// (1x on standard displays, 1.5x/2x on Windows HiDPI and macOS Retina).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ScaleLogicalToPixel(
        float lx, float ly,
        int logicalW, int logicalH,
        int pixelW, int pixelH,
        out float px, out float py)
    {
        float sx = (logicalW > 0 && pixelW > 0) ? ((float)pixelW / logicalW) : 1f;
        float sy = (logicalH > 0 && pixelH > 0) ? ((float)pixelH / logicalH) : 1f;
        px = lx * sx;
        py = ly * sy;
    }

    public static void SyncPixelSize(ref PlatformWindow win)
    {
        if (win.Window == nint.Zero) return;
        SDL.GetWindowSize(win.Window, out int lw, out int lh);
        if (lw > 0 && lh > 0)
        {
            win.LogicalWidth = lw;
            win.LogicalHeight = lh;
        }
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
        input.SetWindowFocused(win.Focused != 0);
        input.SetWindowMinimized(win.Minimized != 0);
        input.SetMouseLocked(win.MouseLocked != 0);

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

                case SDL.EventType.WindowFocusGained:
                    win.Focused = 1;
                    input.SetWindowFocused(true);
                    if (win.Window != nint.Zero)
                    {
                        if (win.MouseLocked != 0)
                        {
                            if (!SDL.SetWindowRelativeMouseMode(win.Window, true))
                                win.MouseLocked = (byte)(SDL.GetWindowRelativeMouseMode(win.Window) ? 1 : 0);
                        }
                        else
                        {
                            win.MouseLocked = (byte)(SDL.GetWindowRelativeMouseMode(win.Window) ? 1 : 0);
                        }
                        input.SetMouseLocked(win.MouseLocked != 0);
                    }
                    break;

                case SDL.EventType.WindowFocusLost:
                    win.Focused = 0;
                    InputSystem.ApplyFocusLost(ref input);
                    break;

                case SDL.EventType.WindowMinimized:
                    win.Minimized = 1;
                    input.SetWindowMinimized(true);
                    break;

                case SDL.EventType.WindowRestored:
                case SDL.EventType.WindowMaximized:
                    win.Minimized = 0;
                    input.SetWindowMinimized(false);
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
                    SDL.GetWindowSize(win.Window, out int ww, out int wh);
                    if (ww > 0 && wh > 0)
                    {
                        win.LogicalWidth = ww;
                        win.LogicalHeight = wh;
                        if (win.Mode == WindowMode.Windowed)
                        {
                            win.WindowedWidth = ww;
                            win.WindowedHeight = wh;
                        }
                    }
                    break;

                case SDL.EventType.KeyDown:
                    input.SetKey((int)ev.Key.Scancode, true);
                    break;

                case SDL.EventType.KeyUp:
                    input.SetKey((int)ev.Key.Scancode, false);
                    break;

                case SDL.EventType.TextInput:
                    if (ev.Text.Text != nint.Zero)
                        input.AppendTextInputUtf8((byte*)ev.Text.Text);
                    break;

                case SDL.EventType.MouseMotion:
                    ScaleLogicalToPixel(ev.Motion.X, ev.Motion.Y, win.LogicalWidth, win.LogicalHeight, win.PixelWidth, win.PixelHeight, out float mx, out float my);
                    input.MouseX = mx;
                    input.MouseY = my;
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
            SDL.InitFlags quitFlags = SDL.InitFlags.Video | SDL.InitFlags.Events;
            if (win.GamepadSubsystemActive != 0)
                quitFlags |= SDL.InitFlags.Gamepad;
            SDL.QuitSubSystem(quitFlags);
            win.Initialized = 0;
            win.GamepadSubsystemActive = 0;
        }
    }
}

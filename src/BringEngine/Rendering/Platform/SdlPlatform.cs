using System;
using System.Runtime.InteropServices;
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
/// Unmanaged SDL3 window & timing state. Only initialized in non-headless mode.
/// MUST be owned and polled on the Main Thread (SDL3 platform requirement).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlatformWindow
{
    public nint Window;
    public int Width;
    public int Height;
    public int PixelWidth;
    public int PixelHeight;
    public int WindowedWidth;
    public int WindowedHeight;
    public ulong LastTicksNs;
    public WindowMode Mode;
    public byte Initialized;
    public byte Closed;
    public byte Resized;
}

public static unsafe class PlatformSystem
{
    /// <summary>
    /// Creates an SDL3 window. All Bring Engine games default to BorderlessFullscreen (desktop borderless full window).
    /// Hidden windows (used by --verify-render and GPU unit tests) stay at exact (width, height) in Windowed mode.
    /// </summary>
    public static PlatformWindow InitWindow(
        string title,
        int width = 1280,
        int height = 720,
        bool hidden = false,
        WindowMode mode = WindowMode.BorderlessFullscreen)
    {
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Events))
            throw new InvalidOperationException($"SDL_Init failed: {SDL.GetError()}");

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
            }
        }

        SyncPixelSize(ref win);
    }

    public static void Shutdown(ref PlatformWindow win)
    {
        if (win.Window != nint.Zero)
        {
            SDL.DestroyWindow(win.Window);
            win.Window = nint.Zero;
        }
        if (win.Initialized != 0)
        {
            SDL.Quit();
            win.Initialized = 0;
        }
    }
}

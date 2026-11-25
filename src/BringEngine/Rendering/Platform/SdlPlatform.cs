using System;
using System.Runtime.InteropServices;
using BringEngine.Simulation.Input;
using SDL3;

namespace BringEngine.Rendering.Platform;

/// <summary>
/// Unmanaged SDL3 window & timing state. Only initialized in non-headless mode.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlatformWindow
{
    public nint Window;
    public int Width;
    public int Height;
    public int PixelWidth;
    public int PixelHeight;
    public ulong LastTicksNs;
    public byte Initialized;
    public byte Closed;
    public byte Resized;
}

public static unsafe class PlatformSystem
{
    public static PlatformWindow InitWindow(string title, int width, int height, bool hidden = false)
    {
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Events))
            throw new InvalidOperationException($"SDL_Init failed: {SDL.GetError()}");

        SDL.WindowFlags flags = SDL.WindowFlags.Resizable | SDL.WindowFlags.HighPixelDensity;
        if (hidden) flags |= SDL.WindowFlags.Hidden;

        nint win = SDL.CreateWindow(title, width, height, flags);
        if (win == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

        SDL.GetWindowSizeInPixels(win, out int pw, out int ph);
        if (pw <= 0) pw = width;
        if (ph <= 0) ph = height;

        return new PlatformWindow
        {
            Window = win,
            Width = width,
            Height = height,
            PixelWidth = pw,
            PixelHeight = ph,
            LastTicksNs = SDL.GetTicksNS(),
            Initialized = 1,
        };
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

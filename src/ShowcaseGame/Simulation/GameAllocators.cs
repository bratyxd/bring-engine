using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Memory;

namespace ShowcaseGame.Simulation;

/// <summary>
/// Explicit game-owned native allocators created at startup.
/// - Persistent: lifetime game tables & config.
/// - Level: reset in O(1) when restarting or changing levels.
/// - Frame: reset in O(1) every simulation/render frame.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GameAllocators
{
    public LinearArena Persistent;
    public LinearArena Level;
    public LinearArena Frame;

    public static GameAllocators Create()
    {
        return new GameAllocators
        {
            Persistent = LinearArena.Create(2 * 1024 * 1024, StringId.Hash("GamePersistent").Value),
            Level = LinearArena.Create(2 * 1024 * 1024, StringId.Hash("GameLevel").Value),
            Frame = LinearArena.Create(512 * 1024, StringId.Hash("GameFrame").Value),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginFrame() => Frame.Reset();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetLevel() => Level.Reset();

    public void Dispose()
    {
        Frame.Dispose();
        Level.Dispose();
        Persistent.Dispose();
    }
}

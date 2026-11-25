using System;
using BringEngine;

namespace BringEngine.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - NativeAOT Test Suite");
        CoreTests.RunAll();
        SimulationTests.RunAll();
        TweenAnimParticleTests.RunAll();
        AssetBundleSceneGltfTests.RunAll();
        RenderTests.RunAll();
        ProfilingAndPacingTests.RunAll();

        Console.WriteLine($"\nSummary: {Assert.Passed} passed, {Assert.Failed} failed.");
        return Assert.Failed == 0 ? 0 : 1;
    }
}

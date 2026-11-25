using System;
using BringEngine;

namespace BringEngine.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        bool headlessOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--headless-only") headlessOnly = true;
        }

        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - NativeAOT Test Suite{(headlessOnly ? " (Headless Only)" : "")}");
        CoreTests.RunAll();
        JobSystemTests.RunAll();
        SimulationTests.RunAll();
        TweenAnimParticleTests.RunAll();
        AssetBundleSceneGltfTests.RunAll();
        AssetPackingAndNineSliceTests.RunAll(headlessOnly);
        RenderTests.RunAll(headlessOnly);
        ProfilingAndPacingTests.RunAll(headlessOnly);

        Console.WriteLine($"\nSummary: {Assert.Passed} passed, {Assert.Failed} failed.");
        return Assert.Failed == 0 ? 0 : 1;
    }
}

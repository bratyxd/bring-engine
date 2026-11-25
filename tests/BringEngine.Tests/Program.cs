using System;
using BringEngine;

namespace BringEngine.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine($"{EngineInfo.Name} v{EngineInfo.Version} - AOT Test Runner bootstrap");
        return 0;
    }
}

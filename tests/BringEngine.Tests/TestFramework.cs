using System;

namespace BringEngine.Tests;

public static class Assert
{
    public static int Passed;
    public static int Failed;

    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"ASSERT FAILED: {message}");
    }

    public static void Equal<T>(T expected, T actual, string message = "") where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new Exception($"ASSERT EQUAL FAILED ({message}): expected={expected}, actual={actual}");
    }

    public static void Near(float expected, float actual, float eps = 1e-3f, string message = "")
    {
        if (MathF.Abs(expected - actual) > eps)
            throw new Exception($"ASSERT NEAR FAILED ({message}): expected={expected}, actual={actual}, eps={eps}");
    }

    public static unsafe void Run(string name, delegate*<void> test)
    {
        try
        {
            test();
            Passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
        }
    }
}

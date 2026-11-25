using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BringEngine.Core.Math;

namespace BringEngine.Simulation.Tween;

public enum EaseType : byte
{
    Linear = 0,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InQuint, OutQuint, InOutQuint,
    InSine, OutSine, InOutSine,
    InExpo, OutExpo, InOutExpo,
    InCirc, OutCirc, InOutCirc,
    InBack, OutBack, InOutBack,
    InElastic, OutElastic, InOutElastic,
    InBounce, OutBounce, InOutBounce,
    SmoothStep,
}

/// <summary>
/// 4-point cubic Bezier curve for custom animation/tween timing curves.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CubicCurve
{
    public float P0, P1, P2, P3;

    public static CubicCurve Smooth => new CubicCurve { P0 = 0f, P1 = 0f, P2 = 1f, P3 = 1f };
    public static CubicCurve Overshoot => new CubicCurve { P0 = 0f, P1 = 1.35f, P2 = 0.95f, P3 = 1f };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Evaluate(float t)
    {
        float u = 1f - t;
        float tt = t * t;
        float uu = u * u;
        return uu * u * P0 + 3f * uu * t * P1 + 3f * u * tt * P2 + tt * t * P3;
    }
}

/// <summary>
/// Deterministic easing function evaluator using DetMath (zero platform libm drift).
/// </summary>
public static class Easing
{
    public static float Evaluate(EaseType ease, float t)
    {
        t = DetMath.Clamp01(t);
        return ease switch
        {
            EaseType.Linear => t,
            EaseType.InQuad => t * t,
            EaseType.OutQuad => 1f - (1f - t) * (1f - t),
            EaseType.InOutQuad => t < 0.5f ? 2f * t * t : 1f - (-2f * t + 2f) * (-2f * t + 2f) * 0.5f,

            EaseType.InCubic => t * t * t,
            EaseType.OutCubic => 1f - (1f - t) * (1f - t) * (1f - t),
            EaseType.InOutCubic => t < 0.5f ? 4f * t * t * t : 1f - DetMath.Pow(-2f * t + 2f, 3f) * 0.5f,

            EaseType.InQuart => t * t * t * t,
            EaseType.OutQuart => 1f - DetMath.Pow(1f - t, 4f),
            EaseType.InOutQuart => t < 0.5f ? 8f * t * t * t * t : 1f - DetMath.Pow(-2f * t + 2f, 4f) * 0.5f,

            EaseType.InQuint => t * t * t * t * t,
            EaseType.OutQuint => 1f - DetMath.Pow(1f - t, 5f),
            EaseType.InOutQuint => t < 0.5f ? 16f * t * t * t * t * t : 1f - DetMath.Pow(-2f * t + 2f, 5f) * 0.5f,

            EaseType.InSine => 1f - DetMath.Cos(t * DetMath.HalfPi),
            EaseType.OutSine => DetMath.Sin(t * DetMath.HalfPi),
            EaseType.InOutSine => -(DetMath.Cos(DetMath.Pi * t) - 1f) * 0.5f,

            EaseType.InExpo => t == 0f ? 0f : DetMath.Pow(2f, 10f * t - 10f),
            EaseType.OutExpo => t == 1f ? 1f : 1f - DetMath.Pow(2f, -10f * t),
            EaseType.InOutExpo => t == 0f ? 0f : t == 1f ? 1f :
                t < 0.5f ? DetMath.Pow(2f, 20f * t - 10f) * 0.5f : (2f - DetMath.Pow(2f, -20f * t + 10f)) * 0.5f,

            EaseType.InCirc => 1f - DetMath.Sqrt(1f - t * t),
            EaseType.OutCirc => DetMath.Sqrt(1f - (t - 1f) * (t - 1f)),
            EaseType.InOutCirc => t < 0.5f
                ? (1f - DetMath.Sqrt(1f - 4f * t * t)) * 0.5f
                : (DetMath.Sqrt(1f - (-2f * t + 2f) * (-2f * t + 2f)) + 1f) * 0.5f,

            EaseType.InBack => InBack(t),
            EaseType.OutBack => OutBack(t),
            EaseType.InOutBack => InOutBack(t),

            EaseType.InElastic => InElastic(t),
            EaseType.OutElastic => OutElastic(t),
            EaseType.InOutElastic => InOutElastic(t),

            EaseType.InBounce => 1f - OutBounce(1f - t),
            EaseType.OutBounce => OutBounce(t),
            EaseType.InOutBounce => t < 0.5f ? (1f - OutBounce(1f - 2f * t)) * 0.5f : (1f + OutBounce(2f * t - 1f)) * 0.5f,

            EaseType.SmoothStep => t * t * (3f - 2f * t),
            _ => t,
        };
    }

    private static float InBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return c3 * t * t * t - c1 * t * t;
    }

    private static float OutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    private static float InOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c2 = c1 * 1.525f;
        return t < 0.5f
            ? (4f * t * t * ((c2 + 1f) * 2f * t - c2)) * 0.5f
            : ((2f * t - 2f) * (2f * t - 2f) * ((c2 + 1f) * (t * 2f - 2f) + c2) + 2f) * 0.5f;
    }

    private static float InElastic(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        const float c4 = DetMath.TwoPi / 3f;
        return -DetMath.Pow(2f, 10f * t - 10f) * DetMath.Sin((t * 10f - 10.75f) * c4);
    }

    private static float OutElastic(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        const float c4 = DetMath.TwoPi / 3f;
        return DetMath.Pow(2f, -10f * t) * DetMath.Sin((t * 10f - 0.75f) * c4) + 1f;
    }

    private static float InOutElastic(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        const float c5 = DetMath.TwoPi / 4.5f;
        return t < 0.5f
            ? -(DetMath.Pow(2f, 20f * t - 10f) * DetMath.Sin((20f * t - 11.125f) * c5)) * 0.5f
            : (DetMath.Pow(2f, -20f * t + 10f) * DetMath.Sin((20f * t - 11.125f) * c5)) * 0.5f + 1f;
    }

    private static float OutBounce(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1;
        return n1 * t * t + 0.984375f;
    }
}

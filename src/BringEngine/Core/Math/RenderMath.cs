using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BringEngine.Core.Math;

/// <summary>
/// Deterministic float math helpers (software trig + interpolation) so simulation and view
/// never diverge across platforms.
/// </summary>
public static class DetMath
{
    public const float Pi = 3.14159265358979323846f;
    public const float TwoPi = 6.28318530717958647692f;
    public const float HalfPi = 1.57079632679489661923f;
    public const float Deg2Rad = Pi / 180.0f;
    public const float Rad2Deg = 180.0f / Pi;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float v, float min, float max)
        => !(v >= min) ? min : (v > max ? max : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp01(float v)
        => !(v > 0f) ? 0f : (v > 1f ? 1f : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float diff = edge1 - edge0;
        if (Abs(diff) < 1e-12f || float.IsNaN(diff))
            return x >= edge1 ? 1f : 0f;
        float t = Clamp01((x - edge0) / diff);
        return t * t * (3f - 2f * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Abs(float v) => v < 0f ? -v : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float a, float b) => a < b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float a, float b) => a > b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float v) => !(v > 0f) ? 0f : MathF.Sqrt(v);

    /// <summary>
    /// Deterministic minimax polynomial Sin (max error ~1e-6), independent of platform libm.
    /// </summary>
    public static float Sin(float x)
    {
        if (!float.IsFinite(x)) return 0f;
        return SinDouble(x);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cos(float x)
    {
        if (!float.IsFinite(x)) return 0f;
        return SinDouble((double)x + 0.5 * System.Math.PI);
    }

    private static float SinDouble(double xd)
    {
        const double TwoPiD = 2.0 * System.Math.PI;
        double r;
        if (System.Math.Abs(xd) <= 1e15)
        {
            double k = System.Math.Floor((xd + System.Math.PI) / TwoPiD);
            r = xd - k * TwoPiD;
        }
        else
        {
            r = xd % TwoPiD;
        }
        if (r > System.Math.PI) r -= TwoPiD;
        else if (r < -System.Math.PI) r += TwoPiD;

        float x = (float)r;
        if (x > HalfPi) x = Pi - x;
        else if (x < -HalfPi) x = -Pi - x;

        float x2 = x * x;
        return x * (1.0f + x2 * (-0.16666667f + x2 * (0.0083333310f + x2 * (-0.0001984090f + x2 * 0.0000027526f))));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Tan(float x)
    {
        float c = Cos(x);
        return Abs(c) < 1e-6f ? (Sin(x) >= 0f ? 1e6f : -1e6f) : Sin(x) / c;
    }

    public static float Atan2(float y, float x)
    {
        if (float.IsNaN(y) || float.IsNaN(x))
            return float.NaN;
        if (float.IsInfinity(y) && float.IsInfinity(x))
        {
            float infAngle = x > 0f ? (Pi * 0.25f) : (Pi * 0.75f);
            return y < 0f ? -infAngle : infAngle;
        }
        if (x == 0f)
        {
            if (y > 0f) return HalfPi;
            if (y < 0f) return -HalfPi;
            return 0f;
        }
        float ay = Abs(y), ax = Abs(x);
        bool swap = ay > ax;
        float z = swap ? ax / ay : ay / ax;
        float z2 = z * z;
        float a = z * (0.9998660f + z2 * (-0.3302995f + z2 * (0.1801410f + z2 * (-0.0851330f + z2 * 0.0208351f))));
        if (swap) a = HalfPi - a;
        if (x < 0f) a = Pi - a;
        if (y < 0f) a = -a;
        return a;
    }

    /// <summary>
    /// Deterministic minimax polynomial Acos (zero libm transcendental calls).
    /// </summary>
    public static float Acos(float x)
    {
        if (float.IsNaN(x)) return HalfPi;
        bool negate = x < 0f;
        float ax = negate ? -x : x;
        if (ax >= 1f) return negate ? Pi : 0f;

        double d = ax;
        double p = ((((((-0.0012624911 * d + 0.0066700901) * d - 0.0170881256) * d + 0.0308918810) * d
            - 0.0501743046) * d + 0.0889789874) * d - 0.2145988016) * d + 1.5707963050;
        float res = (float)(System.Math.Sqrt(1.0 - d) * p);
        return negate ? Pi - res : res;
    }

    /// <summary>Deterministic natural exponential (zero libm transcendental calls).</summary>
    public static float Exp(float x) => (float)ExpD(x);

    /// <summary>Deterministic base-2 exponential (zero libm transcendental calls).</summary>
    public static float Exp2(float x)
    {
        const double Ln2 = 0.69314718055994530942;
        return (float)ExpD((double)x * Ln2);
    }

    /// <summary>Deterministic natural logarithm (zero libm transcendental calls).</summary>
    public static float Log(float x) => (float)LogD(x);

    /// <summary>Deterministic power function b^e (zero libm transcendental calls).</summary>
    public static float Pow(float b, float e)
    {
        if (e == 0f || b == 1f) return 1f;
        if (float.IsNaN(b) || float.IsNaN(e)) return float.NaN;
        if (b == 0f) return e > 0f ? 0f : float.PositiveInfinity;

        if (b < 0f)
        {
            if (float.IsInfinity(e))
            {
                if (b == -1f) return 1f;
                return (Abs(b) > 1f) == (e > 0f) ? float.PositiveInfinity : 0f;
            }
            if (e != MathF.Truncate(e)) return float.NaN;
            double ae = System.Math.Abs((double)e);
            bool odd = ae <= 16777216.0 && (((long)ae & 1L) != 0);
            float mag = (float)ExpD((double)e * LogD(-(double)b));
            return odd ? -mag : mag;
        }

        return (float)ExpD((double)e * LogD((double)b));
    }

    private static double ExpD(double xd)
    {
        if (double.IsNaN(xd)) return double.NaN;
        if (xd >= 709.782712893384) return double.PositiveInfinity;
        if (xd <= -745.1332191019412) return 0.0;

        const double Log2E = 1.44269504088896340736;
        const double Ln2Hi = 0.69314718036912381649;
        const double Ln2Lo = 1.9082149292705877e-10;

        double kReal = System.Math.Round(xd * Log2E);
        int k = (int)kReal;
        double u = (xd - kReal * Ln2Hi) - kReal * Ln2Lo;

        double p = 1.0 + u * (1.0 + u * (0.5 + u * (1.0 / 6.0 + u * (1.0 / 24.0 + u * (1.0 / 120.0
            + u * (1.0 / 720.0 + u * (1.0 / 5040.0 + u * (1.0 / 40320.0 + u * (1.0 / 362880.0)))))))));

        if (k >= -1022)
        {
            double scale = BitConverter.Int64BitsToDouble((long)(k + 1023) << 52);
            return p * scale;
        }
        else
        {
            double scale1 = BitConverter.Int64BitsToDouble(1L << 52);
            double scale2 = BitConverter.Int64BitsToDouble((long)(k + 2045) << 52);
            return (p * scale1) * scale2;
        }
    }

    private static double LogD(double xd)
    {
        if (double.IsNaN(xd) || xd < 0.0) return double.NaN;
        if (xd == 0.0) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(xd)) return double.PositiveInfinity;

        long bits = BitConverter.DoubleToInt64Bits(xd);
        int exp = (int)(bits >> 52) - 1023;
        if (exp == -1023)
        {
            xd *= 18014398509481984.0;
            bits = BitConverter.DoubleToInt64Bits(xd);
            exp = (int)(bits >> 52) - 1023 - 54;
        }

        long mantBits = (bits & 0x000F_FFFF_FFFF_FFFFL) | (1023L << 52);
        double m = BitConverter.Int64BitsToDouble(mantBits);
        const double Sqrt2 = 1.4142135623730950488;
        if (m > Sqrt2)
        {
            m *= 0.5;
            exp++;
        }

        double t = (m - 1.0) / (m + 1.0);
        double t2 = t * t;
        double poly = 1.0 + t2 * (1.0 / 3.0 + t2 * (1.0 / 5.0 + t2 * (1.0 / 7.0 + t2 * (1.0 / 9.0 + t2 * (1.0 / 11.0 + t2 * (1.0 / 13.0))))));
        double lnM = 2.0 * t * poly;

        const double Ln2 = 0.69314718055994530942;
        return exp * Ln2 + lnM;
    }
}

/// <summary>
/// Deterministic Xoshiro128++ PRNG stored strictly as value data (never global shared state).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DetRng
{
    public uint S0, S1, S2, S3;

    public static DetRng Create(ulong seed)
    {
        ulong z = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        uint s0 = (uint)SplitMix(ref z);
        uint s1 = (uint)SplitMix(ref z);
        uint s2 = (uint)SplitMix(ref z);
        uint s3 = (uint)SplitMix(ref z);
        if ((s0 | s1 | s2 | s3) == 0) s0 = 1;
        return new DetRng { S0 = s0, S1 = s1, S2 = s2, S3 = s3 };
    }

    private static ulong SplitMix(ref ulong state)
    {
        ulong z = (state += 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Rotl(uint x, int k) => (x << k) | (x >> (32 - k));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextUInt()
    {
        uint result = Rotl(S0 + S3, 7) + S0;
        uint t = S1 << 9;
        S2 ^= S0;
        S3 ^= S1;
        S1 ^= S2;
        S0 ^= S3;
        S2 ^= t;
        S3 = Rotl(S3, 11);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        uint range = (uint)((long)maxExclusive - minInclusive);
        if (range <= 1) return minInclusive;
        return (int)(minInclusive + (long)(NextUInt() % range));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat01() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextRange(float min, float max) => min + (max - min) * NextFloat01();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Fix32 NextFix32Range(Fix32 min, Fix32 max)
    {
        long span = (long)max.Raw - min.Raw;
        if (span <= 0) return min;
        long offset = (long)((ulong)NextUInt() % (ulong)(span + 1));
        return new Fix32((int)(min.Raw + offset));
    }

    public Vec3 NextUnitSphere()
    {
        float z = NextRange(-1f, 1f);
        float a = NextRange(0f, DetMath.TwoPi);
        float r = DetMath.Sqrt(DetMath.Max(0f, 1f - z * z));
        return new Vec3(r * DetMath.Cos(a), z, r * DetMath.Sin(a));
    }

    public Vec2 NextUnitCircle()
    {
        float a = NextRange(0f, DetMath.TwoPi);
        return new Vec2(DetMath.Cos(a), DetMath.Sin(a));
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Vec2 : IEquatable<Vec2>
{
    public float X, Y;
    public static readonly Vec2 Zero = new Vec2(0f, 0f);
    public static readonly Vec2 One = new Vec2(1f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec2(float x, float y) { X = x; Y = y; }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
    public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Y * s);
    public static Vec2 operator *(Vec2 a, Vec2 b) => new Vec2(a.X * b.X, a.Y * b.Y);
    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    public readonly float Length() => DetMath.Sqrt(X * X + Y * Y);
    public readonly bool Equals(Vec2 other) => X == other.X && Y == other.Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vec3 : IEquatable<Vec3>
{
    public float X, Y, Z;
    public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
    public static readonly Vec3 One = new Vec3(1f, 1f, 1f);
    public static readonly Vec3 UnitX = new Vec3(1f, 0f, 0f);
    public static readonly Vec3 UnitY = new Vec3(0f, 1f, 0f);
    public static readonly Vec3 UnitZ = new Vec3(0f, 0f, 1f);
    public static readonly Vec3 Forward = new Vec3(0f, 0f, -1f);
    public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
    public static readonly Vec3 Right = new Vec3(1f, 0f, 0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(Vec3 a, Vec3 b) => new Vec3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float LengthSq() => X * X + Y * Y + Z * Z;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Length() => DetMath.Sqrt(X * X + Y * Y + Z * Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(Vec3 a, Vec3 b) => (a - b).Length();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSq(Vec3 a, Vec3 b) => (a - b).LengthSq();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vec3 Normalized()
    {
        float lenSq = X * X + Y * Y + Z * Z;
        if (lenSq < 1e-12f) return Zero;
        float inv = 1.0f / DetMath.Sqrt(lenSq);
        return new Vec3(X * inv, Y * inv, Z * inv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new Vec3(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t);

    public static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(DetMath.Min(a.X, b.X), DetMath.Min(a.Y, b.Y), DetMath.Min(a.Z, b.Z));
    public static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(DetMath.Max(a.X, b.X), DetMath.Max(a.Y, b.Y), DetMath.Max(a.Z, b.Z));

    public readonly bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}

[StructLayout(LayoutKind.Sequential)]
public struct Vec4 : IEquatable<Vec4>
{
    public float X, Y, Z, W;
    public static readonly Vec4 Zero = default;
    public static readonly Vec4 One = new Vec4(1f, 1f, 1f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec4(Vec3 v, float w) { X = v.X; Y = v.Y; Z = v.Z; W = w; }

    public static Vec4 Lerp(Vec4 a, Vec4 b, float t) => new Vec4(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
    public readonly bool Equals(Vec4 other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
}

[StructLayout(LayoutKind.Sequential)]
public struct ColorF : IEquatable<ColorF>
{
    public float R, G, B, A;

    public static readonly ColorF White = new ColorF(1f, 1f, 1f, 1f);
    public static readonly ColorF Black = new ColorF(0f, 0f, 0f, 1f);
    public static readonly ColorF Transparent = new ColorF(0f, 0f, 0f, 0f);
    public static readonly ColorF Red = new ColorF(1f, 0.2f, 0.2f, 1f);
    public static readonly ColorF Green = new ColorF(0.2f, 1f, 0.35f, 1f);
    public static readonly ColorF Blue = new ColorF(0.2f, 0.55f, 1f, 1f);
    public static readonly ColorF Cyan = new ColorF(0.15f, 0.95f, 1f, 1f);
    public static readonly ColorF Gold = new ColorF(1f, 0.82f, 0.2f, 1f);
    public static readonly ColorF Magenta = new ColorF(1f, 0.2f, 0.85f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ColorF(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ColorF Lerp(ColorF a, ColorF b, float t) => new ColorF(
        a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    public readonly Color32 ToColor32() => Color32.FromFloat(R, G, B, A);
    public readonly Vec4 ToVec4() => new Vec4(R, G, B, A);
    public readonly bool Equals(ColorF other) => R == other.R && G == other.G && B == other.B && A == other.A;
}

[StructLayout(LayoutKind.Sequential)]
public struct Color32 : IEquatable<Color32>
{
    public byte R, G, B, A;

    public static readonly Color32 White = new Color32(255, 255, 255, 255);
    public static readonly Color32 Black = new Color32(0, 0, 0, 255);
    public static readonly Color32 Transparent = new Color32(0, 0, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Color32(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

    public static Color32 FromFloat(float r, float g, float b, float a = 1f) => new Color32(
        (byte)(DetMath.Clamp01(r) * 255f + 0.5f),
        (byte)(DetMath.Clamp01(g) * 255f + 0.5f),
        (byte)(DetMath.Clamp01(b) * 255f + 0.5f),
        (byte)(DetMath.Clamp01(a) * 255f + 0.5f));

    public readonly ColorF ToColorF() => new ColorF(R / 255f, G / 255f, B / 255f, A / 255f);
    public readonly bool Equals(Color32 other) => R == other.R && G == other.G && B == other.B && A == other.A;
}

[StructLayout(LayoutKind.Sequential)]
public struct Quat : IEquatable<Quat>
{
    public float X, Y, Z, W;
    public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static Quat FromAxisAngle(Vec3 axis, float angleRad)
    {
        Vec3 n = axis.Normalized();
        if (n.LengthSq() < 1e-8f) return Identity;
        float half = angleRad * 0.5f;
        float s = DetMath.Sin(half);
        float c = DetMath.Cos(half);
        return new Quat(n.X * s, n.Y * s, n.Z * s, c);
    }

    public static Quat FromEulerYXZ(float pitchX, float yawY, float rollZ)
    {
        Quat qy = FromAxisAngle(Vec3.UnitY, yawY);
        Quat qx = FromAxisAngle(Vec3.UnitX, pitchX);
        Quat qz = FromAxisAngle(Vec3.UnitZ, rollZ);
        return Multiply(Multiply(qy, qx), qz).Normalized();
    }

    public static Quat LookRotation(Vec3 forward, Vec3 up)
    {
        Vec3 f = forward.Normalized();
        if (f.LengthSq() < 1e-8f) return Identity;
        Vec3 r = Vec3.Cross(f, up).Normalized();
        if (r.LengthSq() < 1e-8f) r = Vec3.Cross(f, Vec3.UnitX).Normalized();
        if (r.LengthSq() < 1e-8f) r = Vec3.Cross(f, Vec3.UnitZ).Normalized();
        Vec3 u = Vec3.Cross(r, f);

        // Build rotation matrix with columns (r, u, -f) and extract quaternion
        float m00 = r.X, m01 = u.X, m02 = -f.X;
        float m10 = r.Y, m11 = u.Y, m12 = -f.Y;
        float m20 = r.Z, m21 = u.Z, m22 = -f.Z;

        float trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            float s = DetMath.Sqrt(trace + 1f) * 2f;
            return new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s).Normalized();
        }
        if (m00 > m11 && m00 > m22)
        {
            float s = DetMath.Sqrt(1f + m00 - m11 - m22) * 2f;
            return new Quat(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s).Normalized();
        }
        if (m11 > m22)
        {
            float s = DetMath.Sqrt(1f + m11 - m00 - m22) * 2f;
            return new Quat((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s).Normalized();
        }
        else
        {
            float s = DetMath.Sqrt(1f + m22 - m00 - m11) * 2f;
            return new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s).Normalized();
        }
    }

    public static Quat Multiply(Quat a, Quat b) => new Quat(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public static Quat operator *(Quat a, Quat b) => Multiply(a, b);

    public readonly Vec3 Rotate(Vec3 v)
    {
        Vec3 qv = new Vec3(X, Y, Z);
        Vec3 t = Vec3.Cross(qv, v) * 2f;
        return v + t * W + Vec3.Cross(qv, t);
    }

    public readonly Quat Normalized()
    {
        float lenSq = X * X + Y * Y + Z * Z + W * W;
        if (lenSq < 1e-12f) return Identity;
        float inv = 1f / DetMath.Sqrt(lenSq);
        return new Quat(X * inv, Y * inv, Z * inv, W * inv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Quat Conjugate() => new Quat(-X, -Y, -Z, W);

    public static Quat Slerp(Quat a, Quat b, float t)
    {
        float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0f)
        {
            dot = -dot;
            b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
        }
        if (dot > 0.9995f)
            return new Quat(DetMath.Lerp(a.X, b.X, t), DetMath.Lerp(a.Y, b.Y, t), DetMath.Lerp(a.Z, b.Z, t), DetMath.Lerp(a.W, b.W, t)).Normalized();

        float theta0 = DetMath.Acos(DetMath.Clamp(dot, -1f, 1f));
        float theta = theta0 * t;
        float sinTheta = DetMath.Sin(theta);
        float sinTheta0 = DetMath.Sin(theta0);
        float s0 = DetMath.Cos(theta) - dot * sinTheta / sinTheta0;
        float s1 = sinTheta / sinTheta0;
        return new Quat(s0 * a.X + s1 * b.X, s0 * a.Y + s1 * b.Y, s0 * a.Z + s1 * b.Z, s0 * a.W + s1 * b.W).Normalized();
    }

    public readonly bool Equals(Quat other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
}

/// <summary>
/// Column-major 4x4 matrix (64 bytes) matching HLSL/Vulkan/D3D12/SDL3_gpu conventions.
/// Right-handed Y-up coordinate system with [0, 1] clip-space depth.
/// Columns are C0 (M0..M3), C1 (M4..M7), C2 (M8..M11), C3 (M12..M15).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Mat4
{
    public Vec4 C0;
    public Vec4 C1;
    public Vec4 C2;
    public Vec4 C3;

    public static readonly Mat4 Identity = new Mat4
    {
        C0 = new Vec4(1, 0, 0, 0),
        C1 = new Vec4(0, 1, 0, 0),
        C2 = new Vec4(0, 0, 1, 0),
        C3 = new Vec4(0, 0, 0, 1),
    };

    public static Mat4 CreateTRS(Vec3 pos, Quat rot, Vec3 scale)
    {
        float x = rot.X, y = rot.Y, z = rot.Z, w = rot.W;
        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;

        return new Mat4
        {
            C0 = new Vec4((1f - 2f * (yy + zz)) * scale.X, (2f * (xy + wz)) * scale.X, (2f * (xz - wy)) * scale.X, 0f),
            C1 = new Vec4((2f * (xy - wz)) * scale.Y, (1f - 2f * (xx + zz)) * scale.Y, (2f * (yz + wx)) * scale.Y, 0f),
            C2 = new Vec4((2f * (xz + wy)) * scale.Z, (2f * (yz - wx)) * scale.Z, (1f - 2f * (xx + yy)) * scale.Z, 0f),
            C3 = new Vec4(pos.X, pos.Y, pos.Z, 1f),
        };
    }

    public static Mat4 CreateTranslation(Vec3 pos) => new Mat4
    {
        C0 = new Vec4(1, 0, 0, 0),
        C1 = new Vec4(0, 1, 0, 0),
        C2 = new Vec4(0, 0, 1, 0),
        C3 = new Vec4(pos.X, pos.Y, pos.Z, 1),
    };

    /// <summary>
    /// Right-handed LookAt view matrix.
    /// </summary>
    public static Mat4 CreateLookAt(Vec3 eye, Vec3 target, Vec3 up)
    {
        Vec3 f = (target - eye).Normalized();
        if (f.LengthSq() < 1e-8f) f = Vec3.Forward;
        Vec3 r = Vec3.Cross(f, up).Normalized();
        if (r.LengthSq() < 1e-8f) r = Vec3.Cross(f, Vec3.UnitX).Normalized();
        if (r.LengthSq() < 1e-8f) r = Vec3.Cross(f, Vec3.UnitZ).Normalized();
        Vec3 u = Vec3.Cross(r, f);

        return new Mat4
        {
            C0 = new Vec4(r.X, u.X, -f.X, 0f),
            C1 = new Vec4(r.Y, u.Y, -f.Y, 0f),
            C2 = new Vec4(r.Z, u.Z, -f.Z, 0f),
            C3 = new Vec4(-Vec3.Dot(r, eye), -Vec3.Dot(u, eye), Vec3.Dot(f, eye), 1f),
        };
    }

    /// <summary>
    /// Right-handed Perspective projection mapping view-space [-near, -far] to clip Z [0, 1] (SDL3_gpu / D3D12 / Vulkan / Metal).
    /// </summary>
    public static Mat4 CreatePerspective(float fovYRad, float aspect, float nearPlane, float farPlane)
    {
        float f = 1.0f / DetMath.Tan(fovYRad * 0.5f);
        float nf = 1.0f / (nearPlane - farPlane);
        return new Mat4
        {
            C0 = new Vec4(f / aspect, 0f, 0f, 0f),
            C1 = new Vec4(0f, f, 0f, 0f),
            C2 = new Vec4(0f, 0f, farPlane * nf, -1f),
            C3 = new Vec4(0f, 0f, farPlane * nearPlane * nf, 0f),
        };
    }

    /// <summary>
    /// Right-handed Orthographic projection mapping Z to [0, 1].
    /// </summary>
    public static Mat4 CreateOrtho(float left, float right, float bottom, float top, float nearPlane, float farPlane)
    {
        float rl = 1.0f / (right - left);
        float tb = 1.0f / (top - bottom);
        float nf = 1.0f / (nearPlane - farPlane);
        return new Mat4
        {
            C0 = new Vec4(2f * rl, 0f, 0f, 0f),
            C1 = new Vec4(0f, 2f * tb, 0f, 0f),
            C2 = new Vec4(0f, 0f, nf, 0f),
            C3 = new Vec4(-(right + left) * rl, -(top + bottom) * tb, nearPlane * nf, 1f),
        };
    }

    public static Mat4 Multiply(in Mat4 a, in Mat4 b)
    {
        return new Mat4
        {
            C0 = TransformVec4(in a, b.C0),
            C1 = TransformVec4(in a, b.C1),
            C2 = TransformVec4(in a, b.C2),
            C3 = TransformVec4(in a, b.C3),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec4 TransformVec4(in Mat4 m, Vec4 v) => new Vec4(
        m.C0.X * v.X + m.C1.X * v.Y + m.C2.X * v.Z + m.C3.X * v.W,
        m.C0.Y * v.X + m.C1.Y * v.Y + m.C2.Y * v.Z + m.C3.Y * v.W,
        m.C0.Z * v.X + m.C1.Z * v.Y + m.C2.Z * v.Z + m.C3.Z * v.W,
        m.C0.W * v.X + m.C1.W * v.Y + m.C2.W * v.Z + m.C3.W * v.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 TransformPoint(in Mat4 m, Vec3 p) => new Vec3(
        m.C0.X * p.X + m.C1.X * p.Y + m.C2.X * p.Z + m.C3.X,
        m.C0.Y * p.X + m.C1.Y * p.Y + m.C2.Y * p.Z + m.C3.Y,
        m.C0.Z * p.X + m.C1.Z * p.Y + m.C2.Z * p.Z + m.C3.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 TransformVector(in Mat4 m, Vec3 v) => new Vec3(
        m.C0.X * v.X + m.C1.X * v.Y + m.C2.X * v.Z,
        m.C0.Y * v.X + m.C1.Y * v.Y + m.C2.Y * v.Z,
        m.C0.Z * v.X + m.C1.Z * v.Y + m.C2.Z * v.Z);

    public static Mat4 Transpose(in Mat4 m) => new Mat4
    {
        C0 = new Vec4(m.C0.X, m.C1.X, m.C2.X, m.C3.X),
        C1 = new Vec4(m.C0.Y, m.C1.Y, m.C2.Y, m.C3.Y),
        C2 = new Vec4(m.C0.Z, m.C1.Z, m.C2.Z, m.C3.Z),
        C3 = new Vec4(m.C0.W, m.C1.W, m.C2.W, m.C3.W),
    };

    public static bool TryInvert(in Mat4 m, out Mat4 inv)
    {
        // Column-major elements: a_col_row
        float a00 = m.C0.X, a01 = m.C0.Y, a02 = m.C0.Z, a03 = m.C0.W;
        float a10 = m.C1.X, a11 = m.C1.Y, a12 = m.C1.Z, a13 = m.C1.W;
        float a20 = m.C2.X, a21 = m.C2.Y, a22 = m.C2.Z, a23 = m.C2.W;
        float a30 = m.C3.X, a31 = m.C3.Y, a32 = m.C3.Z, a33 = m.C3.W;

        float b00 = a00 * a11 - a01 * a10;
        float b01 = a00 * a12 - a02 * a10;
        float b02 = a00 * a13 - a03 * a10;
        float b03 = a01 * a12 - a02 * a11;
        float b04 = a01 * a13 - a03 * a11;
        float b05 = a02 * a13 - a03 * a12;
        float b06 = a20 * a31 - a21 * a30;
        float b07 = a20 * a32 - a22 * a30;
        float b08 = a20 * a33 - a23 * a30;
        float b09 = a21 * a32 - a22 * a31;
        float b10 = a21 * a33 - a23 * a31;
        float b11 = a22 * a33 - a23 * a32;

        float det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
        if (!(DetMath.Abs(det) >= 1e-12f))
        {
            inv = Identity;
            return false;
        }
        float invDet = 1f / det;

        inv = new Mat4
        {
            C0 = new Vec4(
                (a11 * b11 - a12 * b10 + a13 * b09) * invDet,
                (a02 * b10 - a01 * b11 - a03 * b09) * invDet,
                (a31 * b05 - a32 * b04 + a33 * b03) * invDet,
                (a22 * b04 - a21 * b05 - a23 * b03) * invDet),
            C1 = new Vec4(
                (a12 * b08 - a10 * b11 - a13 * b07) * invDet,
                (a00 * b11 - a02 * b08 + a03 * b07) * invDet,
                (a32 * b02 - a30 * b05 - a33 * b01) * invDet,
                (a20 * b05 - a22 * b02 + a23 * b01) * invDet),
            C2 = new Vec4(
                (a10 * b10 - a11 * b08 + a13 * b06) * invDet,
                (a01 * b08 - a00 * b10 - a03 * b06) * invDet,
                (a30 * b04 - a31 * b02 + a33 * b00) * invDet,
                (a21 * b02 - a20 * b04 - a23 * b00) * invDet),
            C3 = new Vec4(
                (a11 * b07 - a10 * b09 - a12 * b06) * invDet,
                (a00 * b09 - a01 * b07 + a02 * b06) * invDet,
                (a31 * b01 - a30 * b03 - a32 * b00) * invDet,
                (a20 * b03 - a21 * b01 + a22 * b00) * invDet),
        };
        return true;
    }

    public static Mat4 operator *(in Mat4 a, in Mat4 b) => Multiply(in a, in b);
}

[StructLayout(LayoutKind.Sequential)]
public struct Bounds3
{
    public Vec3 Min;
    public Vec3 Max;

    public static readonly Bounds3 Empty = new Bounds3
    {
        Min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue),
        Max = new Vec3(float.MinValue, float.MinValue, float.MinValue),
    };

    public readonly bool IsEmpty => !(Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z);

    public readonly Vec3 Center => (Min + Max) * 0.5f;
    public readonly Vec3 Extents => (Max - Min) * 0.5f;

    public static Bounds3 FromCenterExtents(Vec3 center, Vec3 extents) => new Bounds3 { Min = center - extents, Max = center + extents };

    public void Encapsulate(Vec3 p)
    {
        Min = Vec3.Min(Min, p);
        Max = Vec3.Max(Max, p);
    }

    public readonly Bounds3 Transform(in Mat4 m)
    {
        if (IsEmpty) return Empty;
        Vec3 c = Center;
        Vec3 e = Extents;
        Vec3 wc = Mat4.TransformPoint(in m, c);
        Vec3 we = new Vec3(
            DetMath.Abs(m.C0.X) * e.X + DetMath.Abs(m.C1.X) * e.Y + DetMath.Abs(m.C2.X) * e.Z,
            DetMath.Abs(m.C0.Y) * e.X + DetMath.Abs(m.C1.Y) * e.Y + DetMath.Abs(m.C2.Y) * e.Z,
            DetMath.Abs(m.C0.Z) * e.X + DetMath.Abs(m.C1.Z) * e.Y + DetMath.Abs(m.C2.Z) * e.Z);
        return FromCenterExtents(wc, we);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Plane
{
    public Vec3 Normal;
    public float D;

    public static Plane FromEquation(float a, float b, float c, float d)
    {
        float len = DetMath.Sqrt(a * a + b * b + c * c);
        if (len < 1e-8f) return new Plane { Normal = Vec3.UnitY, D = 0f };
        float inv = 1f / len;
        return new Plane { Normal = new Vec3(a * inv, b * inv, c * inv), D = d * inv };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float DistanceToPoint(Vec3 p) => Vec3.Dot(Normal, p) + D;
}

/// <summary>
/// 6-plane camera frustum extracted from a [0, 1] Z clip-space ViewProjection matrix.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Frustum
{
    public Plane Left, Right, Bottom, Top, Near, Far;

    public static Frustum FromViewProjection(in Mat4 vp)
    {
        // Row extraction from column-major Mat4
        float r0x = vp.C0.X, r0y = vp.C1.X, r0z = vp.C2.X, r0w = vp.C3.X;
        float r1x = vp.C0.Y, r1y = vp.C1.Y, r1z = vp.C2.Y, r1w = vp.C3.Y;
        float r2x = vp.C0.Z, r2y = vp.C1.Z, r2z = vp.C2.Z, r2w = vp.C3.Z;
        float r3x = vp.C0.W, r3y = vp.C1.W, r3z = vp.C2.W, r3w = vp.C3.W;

        return new Frustum
        {
            Left = Plane.FromEquation(r3x + r0x, r3y + r0y, r3z + r0z, r3w + r0w),
            Right = Plane.FromEquation(r3x - r0x, r3y - r0y, r3z - r0z, r3w - r0w),
            Bottom = Plane.FromEquation(r3x + r1x, r3y + r1y, r3z + r1z, r3w + r1w),
            Top = Plane.FromEquation(r3x - r1x, r3y - r1y, r3z - r1z, r3w - r1w),
            Near = Plane.FromEquation(r2x, r2y, r2z, r2w), // [0, 1] Z depth near plane
            Far = Plane.FromEquation(r3x - r2x, r3y - r2y, r3z - r2z, r3w - r2w),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOutside(in Plane p, Vec3 c, Vec3 e)
    {
        float r = e.X * DetMath.Abs(p.Normal.X) + e.Y * DetMath.Abs(p.Normal.Y) + e.Z * DetMath.Abs(p.Normal.Z);
        return p.DistanceToPoint(c) + r < 0f;
    }

    public readonly bool IntersectsBounds(in Bounds3 bounds)
    {
        if (bounds.IsEmpty) return false;
        Vec3 c = bounds.Center;
        Vec3 e = bounds.Extents;
        if (IsOutside(in Left, c, e)) return false;
        if (IsOutside(in Right, c, e)) return false;
        if (IsOutside(in Bottom, c, e)) return false;
        if (IsOutside(in Top, c, e)) return false;
        if (IsOutside(in Near, c, e)) return false;
        if (IsOutside(in Far, c, e)) return false;
        return true;
    }

    public readonly bool IntersectsSphere(Vec3 center, float radius)
    {
        if (Left.DistanceToPoint(center) < -radius) return false;
        if (Right.DistanceToPoint(center) < -radius) return false;
        if (Bottom.DistanceToPoint(center) < -radius) return false;
        if (Top.DistanceToPoint(center) < -radius) return false;
        if (Near.DistanceToPoint(center) < -radius) return false;
        if (Far.DistanceToPoint(center) < -radius) return false;
        return true;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct Ray
{
    public Vec3 Origin;
    public Vec3 Direction;

    public Ray(Vec3 origin, Vec3 direction)
    {
        Origin = origin;
        Direction = direction.Normalized();
    }

    public readonly bool IntersectsPlane(in Plane plane, out float t, out Vec3 hitPoint)
    {
        float denom = Vec3.Dot(plane.Normal, Direction);
        if (DetMath.Abs(denom) < 1e-6f)
        {
            t = 0f;
            hitPoint = Vec3.Zero;
            return false;
        }
        t = -(Vec3.Dot(plane.Normal, Origin) + plane.D) / denom;
        hitPoint = Origin + Direction * t;
        return t >= 0f;
    }
}

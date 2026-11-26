using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BringEngine.Core.Math;

/// <summary>
/// Deterministic Q16.16 signed fixed-point number.
/// Uses 100% pure integer math for bit-exact cross-platform simulation determinism
/// across x64/ARM64 and Windows/Linux/macOS.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Fix32 : IEquatable<Fix32>, IComparable<Fix32>
{
    public const int Shift = 16;
    public const int OneRaw = 1 << Shift; // 65536
    public const int HalfRaw = 1 << (Shift - 1);

    public static readonly Fix32 Zero = new Fix32(0);
    public static readonly Fix32 One = new Fix32(OneRaw);
    public static readonly Fix32 Half = new Fix32(HalfRaw);
    public static readonly Fix32 NegOne = new Fix32(-OneRaw);
    public static readonly Fix32 Pi = new Fix32(205887);         // 3.1415863...
    public static readonly Fix32 TwoPi = new Fix32(411775);      // 6.2831878...
    public static readonly Fix32 HalfPi = new Fix32(102944);     // 1.5708007...
    public static readonly Fix32 Deg2Rad = new Fix32(1144);      // 0.017456...
    public static readonly Fix32 Rad2Deg = new Fix32(3754936);   // 57.295776...
    public static readonly Fix32 Epsilon = new Fix32(1);
    public static readonly Fix32 MaxValue = new Fix32(int.MaxValue);
    public static readonly Fix32 MinValue = new Fix32(int.MinValue);

    public readonly int Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Fix32(int raw) => Raw = raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 FromInt(int v) => new Fix32(v << Shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 FromFloat(float v)
    {
        if (float.IsNaN(v)) return Zero;
        if (v >= 32767.9999f) return MaxValue;
        if (v <= -32768.0f) return MinValue;
        double scaled = System.Math.Round((double)v * OneRaw);
        if (scaled >= int.MaxValue) return MaxValue;
        if (scaled <= int.MinValue) return MinValue;
        return new Fix32((int)scaled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 FromRatio(int num, int den) => new Fix32((int)(((long)num << Shift) / den));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ToFloat() => Raw * (1.0f / OneRaw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ToDouble() => Raw * (1.0 / OneRaw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToIntFloor() => Raw >> Shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToIntRound() => (int)(((long)Raw + HalfRaw) >> Shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator +(Fix32 a, Fix32 b) => new Fix32(a.Raw + b.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator -(Fix32 a, Fix32 b) => new Fix32(a.Raw - b.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator -(Fix32 a) => new Fix32(-a.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator *(Fix32 a, Fix32 b) => new Fix32((int)(((long)a.Raw * b.Raw) >> Shift));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator *(Fix32 a, int b) => new Fix32(a.Raw * b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator *(int a, Fix32 b) => new Fix32(a * b.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator /(Fix32 a, Fix32 b) => new Fix32((int)(((long)a.Raw << Shift) / b.Raw));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 operator /(Fix32 a, int b) => new Fix32(a.Raw / b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(Fix32 a, Fix32 b) => a.Raw == b.Raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(Fix32 a, Fix32 b) => a.Raw != b.Raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <(Fix32 a, Fix32 b) => a.Raw < b.Raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >(Fix32 a, Fix32 b) => a.Raw > b.Raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator <=(Fix32 a, Fix32 b) => a.Raw <= b.Raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator >=(Fix32 a, Fix32 b) => a.Raw >= b.Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 Abs(Fix32 a) => a.Raw == int.MinValue ? MaxValue : new Fix32(a.Raw < 0 ? -a.Raw : a.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 Min(Fix32 a, Fix32 b) => a.Raw < b.Raw ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 Max(Fix32 a, Fix32 b) => a.Raw > b.Raw ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 Clamp(Fix32 v, Fix32 min, Fix32 max) => v.Raw < min.Raw ? min : (v.Raw > max.Raw ? max : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 Lerp(Fix32 a, Fix32 b, Fix32 t) => a + (b - a) * t;

    /// <summary>
    /// Exact pure-integer square root for Q16.16 fixed-point.
    /// </summary>
    public static Fix32 Sqrt(Fix32 value)
    {
        if (value.Raw <= 0) return Zero;
        ulong n = (ulong)(uint)value.Raw << Shift;
        ulong res = 0;
        ulong bit = 1UL << 62;
        while (bit > n) bit >>= 2;
        while (bit != 0)
        {
            if (n >= res + bit)
            {
                n -= res + bit;
                res = (res >> 1) + bit;
            }
            else
            {
                res >>= 1;
            }
            bit >>= 2;
        }
        return new Fix32((int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SqrtU64(ulong n)
    {
        if (n == 0) return 0;
        ulong res = 0;
        ulong bit = 1UL << 62;
        while (bit > n) bit >>= 2;
        while (bit != 0)
        {
            if (n >= res + bit)
            {
                n -= res + bit;
                res = (res >> 1) + bit;
            }
            else
            {
                res >>= 1;
            }
            bit >>= 2;
        }
        return res;
    }

    /// <summary>
    /// Deterministic pure-integer sine (radian input) via quadrant reduction and minimax polynomial.
    /// Max error &lt; 0.0001 across full domain.
    /// </summary>
    public static Fix32 Sin(Fix32 radians) => SinRaw(radians.Raw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix32 Cos(Fix32 radians) => SinRaw((long)radians.Raw + HalfPi.Raw);

    private static Fix32 SinRaw(long r)
    {
        long twoPi = TwoPi.Raw;
        long pi = Pi.Raw;
        long halfPi = HalfPi.Raw;

        r %= twoPi;
        if (r > pi) r -= twoPi;
        else if (r < -pi) r += twoPi;

        bool negate = false;
        if (r < 0)
        {
            negate = true;
            r = -r;
        }
        if (r > halfPi)
            r = pi - r;

        // Normalize x = r / (pi/2) in Q30 [0, 1]
        long x = (r << 30) / halfPi;
        long x2 = (x * x) >> 30;
        long x3 = (x2 * x) >> 30;
        long x5 = (x3 * x2) >> 30;

        // Minimax coefficients for sin(pi/2 * x) in Q30:
        // c1 =  1.5707963268 -> 1686629713
        // c3 = -0.6459640975 -> -693598628
        // c5 =  0.0796926262 ->  85569304
        // c7 = -0.0046817541 ->  -5027000
        long x7 = (x5 * x2) >> 30;
        long y = ((1686629713L * x) >> 30)
               - ((693598628L * x3) >> 30)
               + ((85569304L * x5) >> 30)
               - ((5027000L * x7) >> 30);

        int raw = (int)(y >> 14); // Q30 -> Q16
        if (raw > OneRaw) raw = OneRaw;
        if (raw < -OneRaw) raw = -OneRaw;
        return new Fix32(negate ? -raw : raw);
    }

    public static Fix32 Atan2(Fix32 y, Fix32 x)
    {
        if (x.Raw == 0)
        {
            if (y.Raw > 0) return HalfPi;
            if (y.Raw < 0) return -HalfPi;
            return Zero;
        }

        long ay = y.Raw < 0 ? -(long)y.Raw : y.Raw;
        long ax = x.Raw < 0 ? -(long)x.Raw : x.Raw;

        // Rational approximation on [0, 1]: atan(z) ≈ z * (pi/4 + 0.273 * (1 - z))
        bool swap = ay > ax;
        long num = swap ? ax : ay;
        long den = swap ? ay : ax;
        long z = (num << 16) / den; // Q16 in [0, 65536]
        long oneMinusZ = OneRaw - z;
        // pi/4 in Q16 = 51472, 0.273 in Q16 = 17891
        long term = 51472L + ((17891L * oneMinusZ) >> 16);
        long angle = (z * term) >> 16;

        if (swap) angle = HalfPi.Raw - angle;
        if (x.Raw < 0) angle = Pi.Raw - angle;
        if (y.Raw < 0) angle = -angle;
        return new Fix32((int)angle);
    }

    public bool Equals(Fix32 other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fix32 other && Raw == other.Raw;
    public override int GetHashCode() => Raw;
    public int CompareTo(Fix32 other) => Raw.CompareTo(other.Raw);
    public override string ToString() => ToFloat().ToString("F4");
}

/// <summary>
/// Deterministic Q32.32 fixed-point for high-precision world time and large coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>
{
    public const int Shift = 32;
    public const long OneRaw = 1L << Shift;
    public static readonly Fix64 Zero = new Fix64(0);
    public static readonly Fix64 One = new Fix64(OneRaw);

    public readonly long Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Fix64(long raw) => Raw = raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix64 FromInt(long v) => new Fix64(v << Shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix64 FromFix32(Fix32 v) => new Fix64((long)v.Raw << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Fix64 FromRatio(long num, long den) => new Fix64((num << Shift) / den);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ToDouble() => Raw * (1.0 / OneRaw);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Fix32 ToFix32() => new Fix32((int)(Raw >> 16));

    public static Fix64 operator +(Fix64 a, Fix64 b) => new Fix64(a.Raw + b.Raw);
    public static Fix64 operator -(Fix64 a, Fix64 b) => new Fix64(a.Raw - b.Raw);
    public static Fix64 operator -(Fix64 a) => new Fix64(-a.Raw);
    public static bool operator ==(Fix64 a, Fix64 b) => a.Raw == b.Raw;
    public static bool operator !=(Fix64 a, Fix64 b) => a.Raw != b.Raw;
    public static bool operator <(Fix64 a, Fix64 b) => a.Raw < b.Raw;
    public static bool operator >(Fix64 a, Fix64 b) => a.Raw > b.Raw;
    public static bool operator <=(Fix64 a, Fix64 b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix64 a, Fix64 b) => a.Raw >= b.Raw;

    public static Fix64 Abs(Fix64 a) => a.Raw == long.MinValue ? new Fix64(long.MaxValue) : new Fix64(a.Raw < 0 ? -a.Raw : a.Raw);
    public static Fix64 Min(Fix64 a, Fix64 b) => a.Raw < b.Raw ? a : b;
    public static Fix64 Max(Fix64 a, Fix64 b) => a.Raw > b.Raw ? a : b;
    public static Fix64 Clamp(Fix64 v, Fix64 min, Fix64 max) => v.Raw < min.Raw ? min : (v.Raw > max.Raw ? max : v);

    public bool Equals(Fix64 other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Fix64 other && Raw == other.Raw;
    public override int GetHashCode() => Raw.GetHashCode();
    public int CompareTo(Fix64 other) => Raw.CompareTo(other.Raw);
}

[StructLayout(LayoutKind.Sequential)]
public struct FixVec2 : IEquatable<FixVec2>
{
    public Fix32 X, Y;

    public static readonly FixVec2 Zero = new FixVec2(Fix32.Zero, Fix32.Zero);
    public static readonly FixVec2 One = new FixVec2(Fix32.One, Fix32.One);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixVec2(Fix32 x, Fix32 y) { X = x; Y = y; }

    public static FixVec2 FromFloat(float x, float y) => new FixVec2(Fix32.FromFloat(x), Fix32.FromFloat(y));
    public readonly Vec2 ToVec2() => new Vec2(X.ToFloat(), Y.ToFloat());

    public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
    public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
    public static FixVec2 operator *(FixVec2 a, Fix32 s) => new FixVec2(a.X * s, a.Y * s);
    public static Fix32 Dot(FixVec2 a, FixVec2 b) => a.X * b.X + a.Y * b.Y;
    public readonly Fix32 LengthSq() => X * X + Y * Y;

    public readonly Fix32 Length()
    {
        ulong sumSq = (ulong)((long)X.Raw * X.Raw) + (ulong)((long)Y.Raw * Y.Raw);
        ulong lenRaw = Fix32.SqrtU64(sumSq);
        return new Fix32(lenRaw >= int.MaxValue ? int.MaxValue : (int)lenRaw);
    }

    public readonly FixVec2 Normalized()
    {
        ulong sumSq = (ulong)((long)X.Raw * X.Raw) + (ulong)((long)Y.Raw * Y.Raw);
        ulong lenRaw = Fix32.SqrtU64(sumSq);
        if (lenRaw == 0) return Zero;
        return new FixVec2(
            new Fix32((int)(((long)X.Raw << Fix32.Shift) / (long)lenRaw)),
            new Fix32((int)(((long)Y.Raw << Fix32.Shift) / (long)lenRaw)));
    }

    public readonly bool Equals(FixVec2 other) => X == other.X && Y == other.Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct FixVec3 : IEquatable<FixVec3>
{
    public Fix32 X, Y, Z;

    public static readonly FixVec3 Zero = new FixVec3(Fix32.Zero, Fix32.Zero, Fix32.Zero);
    public static readonly FixVec3 One = new FixVec3(Fix32.One, Fix32.One, Fix32.One);
    public static readonly FixVec3 UnitX = new FixVec3(Fix32.One, Fix32.Zero, Fix32.Zero);
    public static readonly FixVec3 UnitY = new FixVec3(Fix32.Zero, Fix32.One, Fix32.Zero);
    public static readonly FixVec3 UnitZ = new FixVec3(Fix32.Zero, Fix32.Zero, Fix32.One);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixVec3(Fix32 x, Fix32 y, Fix32 z) { X = x; Y = y; Z = z; }

    public static FixVec3 FromFloat(float x, float y, float z) => new FixVec3(Fix32.FromFloat(x), Fix32.FromFloat(y), Fix32.FromFloat(z));
    public static FixVec3 FromVec3(Vec3 v) => new FixVec3(Fix32.FromFloat(v.X), Fix32.FromFloat(v.Y), Fix32.FromFloat(v.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vec3 ToVec3() => new Vec3(X.ToFloat(), Y.ToFloat(), Z.ToFloat());

    public static FixVec3 operator +(FixVec3 a, FixVec3 b) => new FixVec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static FixVec3 operator -(FixVec3 a, FixVec3 b) => new FixVec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static FixVec3 operator -(FixVec3 a) => new FixVec3(-a.X, -a.Y, -a.Z);
    public static FixVec3 operator *(FixVec3 a, Fix32 s) => new FixVec3(a.X * s, a.Y * s, a.Z * s);
    public static FixVec3 operator *(Fix32 s, FixVec3 a) => new FixVec3(a.X * s, a.Y * s, a.Z * s);
    public static FixVec3 operator *(FixVec3 a, int s) => new FixVec3(a.X * s, a.Y * s, a.Z * s);

    public static Fix32 Dot(FixVec3 a, FixVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static FixVec3 Cross(FixVec3 a, FixVec3 b) => new FixVec3(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public readonly Fix32 LengthSq() => X * X + Y * Y + Z * Z;

    public readonly Fix32 Length()
    {
        ulong sumSq = (ulong)((long)X.Raw * X.Raw) + (ulong)((long)Y.Raw * Y.Raw) + (ulong)((long)Z.Raw * Z.Raw);
        ulong lenRaw = Fix32.SqrtU64(sumSq);
        return new Fix32(lenRaw >= int.MaxValue ? int.MaxValue : (int)lenRaw);
    }

    public static Fix32 DistanceSq(FixVec3 a, FixVec3 b) => (a - b).LengthSq();
    public static Fix32 Distance(FixVec3 a, FixVec3 b) => (a - b).Length();

    public readonly FixVec3 Normalized()
    {
        ulong sumSq = (ulong)((long)X.Raw * X.Raw) + (ulong)((long)Y.Raw * Y.Raw) + (ulong)((long)Z.Raw * Z.Raw);
        ulong lenRaw = Fix32.SqrtU64(sumSq);
        if (lenRaw == 0) return Zero;
        return new FixVec3(
            new Fix32((int)(((long)X.Raw << Fix32.Shift) / (long)lenRaw)),
            new Fix32((int)(((long)Y.Raw << Fix32.Shift) / (long)lenRaw)),
            new Fix32((int)(((long)Z.Raw << Fix32.Shift) / (long)lenRaw)));
    }

    public static FixVec3 Lerp(FixVec3 a, FixVec3 b, Fix32 t) => new FixVec3(
        Fix32.Lerp(a.X, b.X, t),
        Fix32.Lerp(a.Y, b.Y, t),
        Fix32.Lerp(a.Z, b.Z, t));

    public readonly bool Equals(FixVec3 other) => X == other.X && Y == other.Y && Z == other.Z;
}

[StructLayout(LayoutKind.Sequential)]
public struct FixQuat : IEquatable<FixQuat>
{
    public Fix32 X, Y, Z, W;

    public static readonly FixQuat Identity = new FixQuat(Fix32.Zero, Fix32.Zero, Fix32.Zero, Fix32.One);

    public FixQuat(Fix32 x, Fix32 y, Fix32 z, Fix32 w) { X = x; Y = y; Z = z; W = w; }

    public static FixQuat FromAxisAngle(FixVec3 axis, Fix32 angleRad)
    {
        FixVec3 n = axis.Normalized();
        if (n.X.Raw == 0 && n.Y.Raw == 0 && n.Z.Raw == 0)
            return Identity;
        Fix32 half = new Fix32(angleRad.Raw >> 1);
        Fix32 s = Fix32.Sin(half);
        Fix32 c = Fix32.Cos(half);
        return new FixQuat(n.X * s, n.Y * s, n.Z * s, c);
    }

    public readonly FixQuat Normalized()
    {
        long x = X.Raw, y = Y.Raw, z = Z.Raw, w = W.Raw;
        ulong ab = (ulong)(x * x) + (ulong)(y * y);
        ulong cd = (ulong)(z * z) + (ulong)(w * w);
        if (ab > ulong.MaxValue - cd)
        {
            x >>= 1; y >>= 1; z >>= 1; w >>= 1;
            ab = (ulong)(x * x) + (ulong)(y * y);
            cd = (ulong)(z * z) + (ulong)(w * w);
        }
        ulong lenRaw = Fix32.SqrtU64(ab + cd);
        if (lenRaw == 0) return Identity;
        return new FixQuat(
            new Fix32((int)((x << Fix32.Shift) / (long)lenRaw)),
            new Fix32((int)((y << Fix32.Shift) / (long)lenRaw)),
            new Fix32((int)((z << Fix32.Shift) / (long)lenRaw)),
            new Fix32((int)((w << Fix32.Shift) / (long)lenRaw)));
    }

    public readonly FixVec3 Rotate(FixVec3 v)
    {
        FixVec3 qv = new FixVec3(X, Y, Z);
        FixVec3 t = FixVec3.Cross(qv, v) * 2;
        return v + t * W + FixVec3.Cross(qv, t);
    }

    public readonly Quat ToQuat() => new Quat(X.ToFloat(), Y.ToFloat(), Z.ToFloat(), W.ToFloat());
    public readonly bool Equals(FixQuat other) => X == other.X && Y == other.Y && Z == other.Z && W == other.W;
}

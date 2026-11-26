using System;
using System.IO;
using BringEngine.Core.Memory;

namespace BringEngine.Core.Storage;

/// <summary>
/// Cross-platform OS-standard writable application data directory resolver (headless-safe, zero SDL3 dependency).
/// Uses a unified per-game parent folder across all desktop targets so logs, saves, and configs live side by side:
/// - Windows: %LOCALAPPDATA%\&lt;AppName&gt;\{Logs, Saves}
/// - macOS:   ~/Library/Application Support/&lt;AppName&gt;/{Logs, Saves}
/// - Linux:   $XDG_DATA_HOME/&lt;AppName&gt;/{logs, saves} (fallback ~/.local/share/&lt;AppName&gt;/{logs, saves})
/// </summary>
public static class PlatformPaths
{
    /// <summary>
    /// Maximum UTF-8 byte length for a sanitized filename or directory segment.
    /// Leaves 31 bytes of headroom below Linux's 255-byte NAME_MAX for suffixes such as
    /// <c>.bak</c> (+4B), <c>.trim.tmp</c> (+9B), and <c>.&lt;int64_seq&gt;.tmp</c> (max +24B),
    /// while staying within <see cref="FixedString256.MaxBytes"/> (252B) for async/sync parity.
    /// </summary>
    internal const int MaxSegmentUtf8Bytes = 224;

    public static string ResolveAppDataDirectory(ReadOnlySpan<char> appName)
    {
        string safeApp = SanitizeSegment(appName, "BringEngine");

        if (OperatingSystem.IsWindows())
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, safeApp);
        }

        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                home = Path.GetTempPath();
            return Path.Combine(home, "Library", "Application Support", safeApp);
        }

        // Linux / Steam Deck (SteamOS) / Unix (XDG Base Directory Spec)
        string? xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgData))
            return Path.Combine(xdgData, safeApp);

        string unixHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(unixHome))
            unixHome = Path.GetTempPath();
        return Path.Combine(unixHome, ".local", "share", safeApp);
    }

    public static string ResolveLogsDirectory(ReadOnlySpan<char> appName)
    {
        string parent = ResolveAppDataDirectory(appName);
        string sub = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "Logs" : "logs";
        return Path.Combine(parent, sub);
    }

    public static string ResolveSavesDirectory(ReadOnlySpan<char> appName)
    {
        string parent = ResolveAppDataDirectory(appName);
        string sub = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "Saves" : "saves";
        return Path.Combine(parent, sub);
    }

    public static string SanitizeSegment(ReadOnlySpan<char> input, string fallback)
    {
        Span<char> buf = stackalloc char[128];
        int len = SanitizeSegmentToSpan(input, fallback.AsSpan(), buf);
        return new string(buf.Slice(0, len));
    }

    public static string SanitizeFileName(ReadOnlySpan<char> input, string fallback)
    {
        if (input.IsEmpty) return fallback;
        ReadOnlySpan<char> name = Path.GetFileName(input);
        Span<char> buf = stackalloc char[128];
        int len = SanitizeSegmentToSpan(name, fallback.AsSpan(), buf);
        return new string(buf.Slice(0, len));
    }

    /// <summary>
    /// Zero-GC filename sanitization into a caller-provided buffer (at least 128 chars recommended).
    /// Produces the exact same output as SanitizeFileName(input, fallback).
    /// </summary>
    internal static int SanitizeFileNameToSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> fallback, Span<char> dst)
    {
        ReadOnlySpan<char> name = input.IsEmpty ? ReadOnlySpan<char>.Empty : Path.GetFileName(input);
        return SanitizeSegmentToSpan(name, fallback, dst);
    }

    private static bool IsWindowsReservedDeviceName(ReadOnlySpan<char> stem)
    {
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                   stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }
        if (stem.Length == 4)
        {
            char last = stem[3];
            if (last >= '1' && last <= '9')
            {
                ReadOnlySpan<char> prefix = stem.Slice(0, 3);
                return prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                       prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static int SanitizeSegmentToSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> fallback, Span<char> dst)
    {
        if (!input.IsEmpty && dst.Length > 0)
        {
            int cap = dst.Length < 128 ? dst.Length : 128;
            Span<char> tmp = stackalloc char[128];
            int len = 0;
            for (int i = 0; i < input.Length && len < cap; i++)
            {
                char c = input[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ')
                    tmp[len++] = c;
            }
            ReadOnlySpan<char> trimmed = tmp.Slice(0, len).Trim(" \t\r\n.".AsSpan());
            if (!trimmed.IsEmpty && !trimmed.IsWhiteSpace())
            {
                Span<char> work = stackalloc char[128];
                int outLen = 0;

                int dotIdx = trimmed.IndexOf('.');
                ReadOnlySpan<char> stem = (dotIdx >= 0 ? trimmed.Slice(0, dotIdx) : trimmed).TrimEnd();
                if (IsWindowsReservedDeviceName(stem))
                {
                    if (cap > 1)
                    {
                        work[outLen++] = '_';
                        int copyLen = trimmed.Length < (cap - 1) ? trimmed.Length : (cap - 1);
                        trimmed.Slice(0, copyLen).CopyTo(work.Slice(1));
                        outLen += copyLen;
                    }
                }
                else
                {
                    int copyLen = trimmed.Length < cap ? trimmed.Length : cap;
                    trimmed.Slice(0, copyLen).CopyTo(work);
                    outLen = copyLen;
                }

                // Cap UTF-8 encoded length to <= MaxSegmentUtf8Bytes (224B) at whole rune boundaries
                // so filenames leave headroom below Linux 255B NAME_MAX for .bak/.<seq>.tmp suffixes
                // AND async FixedString256.From and sync SanitizeFileName stay 100% byte-identical.
                int utf8Bytes = 0;
                int runeSafeLen = 0;
                while (runeSafeLen < outLen)
                {
                    char ch = work[runeSafeLen];
                    int charBytes = ch <= 0x7F ? 1 : (ch <= 0x7FF ? 2 : 3);
                    if (utf8Bytes + charBytes > MaxSegmentUtf8Bytes)
                        break;
                    utf8Bytes += charBytes;
                    runeSafeLen++;
                }

                ReadOnlySpan<char> finalSpan = work.Slice(0, runeSafeLen).Trim(" \t\r\n.".AsSpan());
                if (!finalSpan.IsEmpty && !finalSpan.IsWhiteSpace())
                {
                    finalSpan.CopyTo(dst);
                    return finalSpan.Length;
                }
            }
        }
        int fbLen = fallback.Length < dst.Length ? fallback.Length : dst.Length;
        fallback.Slice(0, fbLen).CopyTo(dst);
        return fbLen;
    }
}

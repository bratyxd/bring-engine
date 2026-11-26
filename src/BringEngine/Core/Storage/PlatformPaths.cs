using System;
using System.IO;

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

    private static int SanitizeSegmentToSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> fallback, Span<char> dst)
    {
        if (!input.IsEmpty && dst.Length > 0)
        {
            int cap = dst.Length < 128 ? dst.Length : 128;
            int len = 0;
            for (int i = 0; i < input.Length && len < cap; i++)
            {
                char c = input[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ')
                    dst[len++] = c;
            }
            ReadOnlySpan<char> trimmed = dst.Slice(0, len).Trim().Trim('.');
            if (!trimmed.IsEmpty && !trimmed.IsWhiteSpace())
            {
                trimmed.CopyTo(dst);
                return trimmed.Length;
            }
        }
        int fbLen = fallback.Length < dst.Length ? fallback.Length : dst.Length;
        fallback.Slice(0, fbLen).CopyTo(dst);
        return fbLen;
    }
}

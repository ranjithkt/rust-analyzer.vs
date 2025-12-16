using System;
using System.Text.RegularExpressions;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Maps Windows-local paths (C:\...) to WSL mirror paths under a WSL-native directory.
/// Mirror layout:
///   &lt;MirrorWindowsRootLinux&gt;/&lt;drive&gt;/&lt;path...&gt;
/// Example:
///   C:\Repos\proj\src\main.rs -> /home/u/.cache/rust-analyzer.vs/mirrors/&lt;id&gt;/win/c/Repos/proj/src/main.rs
/// </summary>
public static class WslMirrorPathMapper
{
    private static readonly Regex WindowsDrivePath = new(@"^(?<drive>[a-zA-Z]):[\\/](?<rest>.*)$", RegexOptions.Compiled);
    private const string WinSentinel = "/win/";

    public static bool TryWindowsToMirrorLinuxPath(string windowsPath, WslMirrorConfig cfg, out string mirrorLinuxPath)
    {
        mirrorLinuxPath = null;

        if (cfg == null || string.IsNullOrWhiteSpace(cfg.MirrorWindowsRootLinux))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(windowsPath))
        {
            return false;
        }

        // Already a Linux path.
        if (windowsPath.StartsWith("/", StringComparison.Ordinal))
        {
            mirrorLinuxPath = windowsPath;
            return true;
        }

        // If this is a WSL UNC path, keep it as-is (caller should convert via WslInfo instead).
        if (WslInfo.IsWslPath(windowsPath))
        {
            mirrorLinuxPath = windowsPath;
            return false;
        }

        var m = WindowsDrivePath.Match(windowsPath.Trim());
        if (!m.Success)
        {
            return false;
        }

        var drive = char.ToLowerInvariant(m.Groups["drive"].Value[0]);
        var rest = m.Groups["rest"].Value.Replace('\\', '/');

        mirrorLinuxPath = $"{cfg.MirrorWindowsRootLinux}/{drive}/{rest}";
        return true;
    }

    public static bool TryMirrorLinuxToWindowsPath(string linuxPath, WslMirrorConfig cfg, out string windowsPath)
    {
        windowsPath = null;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.MirrorWindowsRootLinux))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        var trimmed = linuxPath.Trim();
        if (!trimmed.StartsWith(cfg.MirrorWindowsRootLinux, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = trimmed.Substring(cfg.MirrorWindowsRootLinux.Length).TrimStart('/');
        if (suffix.Length < 2)
        {
            return false;
        }

        // Expect: "c/Repos/proj/..."
        var drive = suffix[0];
        if (!char.IsLetter(drive) || suffix[1] != '/')
        {
            return false;
        }

        var rest = suffix.Substring(2).Replace('/', '\\');
        windowsPath = char.ToUpperInvariant(drive) + ":\\" + rest;
        return true;
    }

    /// <summary>
    /// Heuristic mapping that does not require a config instance: looks for a "/win/&lt;drive&gt;/..." segment.
    /// This is used for fast path mapping in build/test output when only the Linux path is available.
    /// </summary>
    public static bool TryMirrorLinuxToWindowsPathBySentinel(string linuxPath, out string windowsPath)
    {
        windowsPath = null;
        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        var normalized = linuxPath.Trim().Replace('\\', '/');
        var idx = normalized.IndexOf(WinSentinel, StringComparison.Ordinal);
        if (idx < 0)
        {
            return false;
        }

        var suffix = normalized.Substring(idx + WinSentinel.Length);
        if (suffix.Length < 2)
        {
            return false;
        }

        var drive = suffix[0];
        if (!char.IsLetter(drive) || suffix[1] != '/')
        {
            return false;
        }

        var rest = suffix.Substring(2).Replace('/', '\\');
        windowsPath = char.ToUpperInvariant(drive) + ":\\" + rest;
        return true;
    }

    /// <summary>
    /// Converts an argument that may contain a Windows path into a mirror Linux path.
    /// Handles raw paths and patterns like "--foo=C:\path".
    /// </summary>
    public static string ConvertArgumentWindowsPathsToMirror(string arg, WslMirrorConfig cfg)
    {
        if (string.IsNullOrEmpty(arg) || cfg == null)
        {
            return arg;
        }

        // Fast-path: raw "C:\..." token.
        if (TryWindowsToMirrorLinuxPath(arg, cfg, out var linux) && linux != arg)
        {
            return linux;
        }

        // Handle "--foo=C:\..." and similar.
        var idx = arg.IndexOf(":\\", StringComparison.Ordinal);
        if (idx > 0)
        {
            var driveChar = arg[idx - 1];
            if (char.IsLetter(driveChar))
            {
                var start = idx - 1;
                var prefix = arg.Substring(0, start);
                var pathPart = arg.Substring(start);
                if (TryWindowsToMirrorLinuxPath(pathPart, cfg, out var linuxPath))
                {
                    return prefix + linuxPath;
                }
            }
        }

        return arg;
    }
}

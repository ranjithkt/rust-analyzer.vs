using System;
using System.IO;
using System.Text.RegularExpressions;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Maps Windows-local paths (C:\...) to WSL mirror paths under a WSL-native directory.
/// Mirror layout:
///   &lt;MirrorWindowsRootLinux&gt;/&lt;drive&gt;/&lt;path...&gt;
/// Example:
///   C:\Repos\proj\src\main.rs -> /home/u/.cache/rust-analyzer.vs/mirrors/&lt;id&gt;/win/c/Repos/proj/src/main.rs.
/// </summary>
public static class WslMirrorPathMapper
{
    private const string WinSentinel = "/win/";
    private static readonly Regex WindowsDrivePath = new(@"^(?<drive>[a-zA-Z]):[\\/](?<rest>.*)$", RegexOptions.Compiled);

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

        // Support Windows extended-length paths (\\?\C:\...) by stripping the prefix for drive mapping.
        // Also normalize \\?\UNC\server\share\... -> \\server\share\...
        windowsPath = windowsPath.Trim();
        if (windowsPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            windowsPath = @"\\" + windowsPath.Substring(@"\\?\UNC\".Length);
        }
        else if (windowsPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) && windowsPath.Length > 4)
        {
            windowsPath = windowsPath.Substring(4);
        }

        // This mapper is Windows -> mirror Linux. If the input is already Linux, treat as not-a-Windows-path.
        if (windowsPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        // If this is a WSL UNC path, keep it as-is (caller should convert via WslInfo instead).
        if (WslInfo.IsWslPath(windowsPath))
        {
            return false;
        }

        var normalizedWindowsPath = windowsPath;
        try
        {
            // Normalize separators and ".." segments so mirror paths are stable even if callers vary.
            normalizedWindowsPath = Path.GetFullPath(normalizedWindowsPath);
        }
        catch
        {
            // Best-effort only.
        }

        var m = WindowsDrivePath.Match(normalizedWindowsPath);
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

        // Tighten the heuristic: only accept paths that look like our default mirror layout:
        //   .../mirrors/<workspaceId>/win/<drive>/...
        // This avoids false positives for arbitrary "/home/me/win/c/..." folders.
        const string mirrorsSeg = "/mirrors/";
        var idxMirrors = normalized.LastIndexOf(mirrorsSeg, idx, StringComparison.Ordinal);
        if (idxMirrors < 0)
        {
            return false;
        }

        var idStart = idxMirrors + mirrorsSeg.Length;
        var idEnd = normalized.IndexOf('/', idStart);
        if (idEnd != idx)
        {
            return false;
        }

        // Expect a 32-hex workspace id segment.
        if (idEnd - idStart != 32)
        {
            return false;
        }

        for (var i = idStart; i < idEnd; i++)
        {
            var c = normalized[i];
            var isHex = (c >= '0' && c <= '9') ||
                        (c >= 'a' && c <= 'f') ||
                        (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
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

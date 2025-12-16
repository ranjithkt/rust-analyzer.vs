using System;
using System.Text.RegularExpressions;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Maps Windows-local paths (e.g. C:\foo\bar) to WSL paths under /mnt (e.g. /mnt/c/foo/bar),
/// and vice versa. This supports "Mode 2": Windows workspace + WSL execution.
/// </summary>
public static class WslPathMapper
{
    private static readonly Regex WindowsDrivePath = new(@"^(?<drive>[a-zA-Z]):[\\/](?<rest>.*)$", RegexOptions.Compiled);

    // Support "/mnt/c", "/mnt/c/", and "/mnt/c/foo/bar"
    private static readonly Regex WslMntPath = new(@"^/mnt/(?<drive>[a-zA-Z])(?:/(?<rest>.*))?$", RegexOptions.Compiled);

    public static bool TryWindowsToWslPath(string windowsPath, out string linuxPath)
    {
        linuxPath = null;
        if (string.IsNullOrWhiteSpace(windowsPath))
        {
            return false;
        }

        windowsPath = windowsPath.Trim();

        // Already looks like a Linux path.
        if (windowsPath.StartsWith("/", StringComparison.Ordinal))
        {
            linuxPath = windowsPath;
            return true;
        }

        // Support Windows extended-length paths (\\?\C:\...) by stripping the prefix for drive mapping.
        // Also normalize \\?\UNC\server\share\... -> \\server\share\...
        if (windowsPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            windowsPath = @"\\" + windowsPath.Substring(@"\\?\UNC\".Length);
        }
        else if (windowsPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) && windowsPath.Length > 4)
        {
            windowsPath = windowsPath.Substring(4);
        }

        // UNC WSL path: delegate to WslInfo for Mode 1.
        if (WslInfo.TryParse(windowsPath, out var wslInfo))
        {
            linuxPath = wslInfo.ToLinuxPath(windowsPath);
            return true;
        }

        var m = WindowsDrivePath.Match(windowsPath);
        if (!m.Success)
        {
            return false;
        }

        var drive = char.ToLowerInvariant(m.Groups["drive"].Value[0]);
        var rest = m.Groups["rest"].Value.Replace('\\', '/');
        linuxPath = $"/mnt/{drive}/{rest}";
        return true;
    }

    public static bool TryWslToWindowsPath(string linuxPath, out string windowsPath)
    {
        windowsPath = null;
        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        // If already a Windows rooted path, return it.
        if (linuxPath.Length >= 3 && char.IsLetter(linuxPath[0]) && linuxPath[1] == ':' && (linuxPath[2] == '\\' || linuxPath[2] == '/'))
        {
            windowsPath = linuxPath.Replace('/', '\\');
            return true;
        }

        var trimmed = linuxPath.Trim();

        var m = WslMntPath.Match(trimmed);
        if (!m.Success)
        {
            return false;
        }

        var drive = char.ToUpperInvariant(m.Groups["drive"].Value[0]);
        var restGroup = m.Groups["rest"];
        var rest = restGroup.Success ? restGroup.Value.Replace('/', '\\') : string.Empty;
        windowsPath = string.IsNullOrEmpty(rest) ? $"{drive}:\\" : $"{drive}:\\{rest}";
        return true;
    }

    /// <summary>
    /// Converts an argument that may contain a Windows path into a WSL /mnt path.
    /// Handles raw paths and patterns like "--foo=C:\path" or "-C=C:\path".
    /// </summary>
    public static string ConvertArgumentWindowsPathsToWsl(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return arg;
        }

        // Fast-path: raw "C:\..." token.
        if (TryWindowsToWslPath(arg, out var linux))
        {
            // Only rewrite if it was actually a Windows-rooted path.
            if (linux != arg)
            {
                return linux;
            }
        }

        // Handle "--foo=C:\..." and similar.
        var idx = arg.IndexOf(":\\", StringComparison.Ordinal);
        if (idx > 0)
        {
            var driveChar = arg[idx - 1];
            if (char.IsLetter(driveChar))
            {
                // Find the start of the path token (the drive letter).
                var start = idx - 1;
                var prefix = arg.Substring(0, start);
                var pathPart = arg.Substring(start);
                if (TryWindowsToWslPath(pathPart, out var linuxPath))
                {
                    return prefix + linuxPath;
                }
            }
        }

        return arg;
    }
}


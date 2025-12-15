using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Utility class for WSL workspace detection and path conversion.
/// WSL workspaces are identified by their UNC path prefix:
/// \\wsl.localhost\{DistroName}\... or \\wsl$\{DistroName}\....
/// </summary>
[DebuggerDisplay("{DistroName} ({HostPrefix})")]
public sealed class WslInfo
{
    /// <summary>
    /// Regex pattern to match WSL UNC paths and extract distro name and relative path.
    /// Matches: \\wsl.localhost\{distro}\{path} or \\wsl$\{distro}\{path}.
    /// </summary>
    private static readonly Regex WslUncPattern = new(
        @"^(?<prefix>\\\\wsl(?:\.localhost|\$))\\(?<distro>[^\\]+)(?:\\(?<path>.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private WslInfo(string hostPrefix, string distroName, string uncDistroRoot)
    {
        HostPrefix = hostPrefix;
        DistroName = distroName;
        UncDistroRoot = uncDistroRoot;
    }

    /// <summary>
    /// Gets the WSL host prefix (either "\\wsl.localhost" or "\\wsl$").
    /// </summary>
    public string HostPrefix { get; }

    /// <summary>
    /// Gets the name of the WSL distribution (e.g., "Ubuntu-22.04", "Ubuntu").
    /// </summary>
    public string DistroName { get; }

    /// <summary>
    /// Gets the full UNC root path for this distro (e.g., "\\wsl.localhost\Ubuntu-22.04\").
    /// Always ends with a backslash.
    /// </summary>
    public string UncDistroRoot { get; }

    /// <summary>
    /// Attempts to parse a WSL UNC path and extract the distro information.
    /// </summary>
    /// <param name="path">The path to check (can be any Windows path or WSL UNC path).</param>
    /// <param name="wslInfo">The parsed WslInfo if the path is a WSL UNC path, null otherwise.</param>
    /// <returns>True if the path is a WSL UNC path and was successfully parsed.</returns>
    public static bool TryParse(string path, out WslInfo wslInfo)
    {
        wslInfo = null;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var match = WslUncPattern.Match(path);
        if (!match.Success)
        {
            return false;
        }

        var hostPrefix = match.Groups["prefix"].Value;
        var distroName = match.Groups["distro"].Value;
        var uncDistroRoot = $"{hostPrefix}\\{distroName}\\";

        wslInfo = new WslInfo(hostPrefix, distroName, uncDistroRoot);
        return true;
    }

    /// <summary>
    /// Attempts to parse a PathEx as a WSL UNC path.
    /// </summary>
    public static bool TryParse(PathEx path, out WslInfo wslInfo)
    {
        return TryParse((string)path, out wslInfo);
    }

    /// <summary>
    /// Checks if a path is a WSL UNC path without extracting the full WslInfo.
    /// </summary>
    public static bool IsWslPath(string path)
    {
        return TryParse(path, out _);
    }

    /// <summary>
    /// Checks if a PathEx is a WSL UNC path without extracting the full WslInfo.
    /// </summary>
    public static bool IsWslPath(PathEx path)
    {
        return IsWslPath((string)path);
    }

    /// <summary>
    /// Converts a Windows UNC path to a Linux path.
    /// The path must be under this WslInfo's UncDistroRoot.
    /// </summary>
    /// <param name="uncPath">The Windows UNC path (e.g., "\\wsl.localhost\Ubuntu\home\user\project\src\main.rs").</param>
    /// <returns>The equivalent Linux path (e.g., "/home/user/project/src/main.rs").</returns>
    /// <exception cref="ArgumentException">If the path is not under this distro's UNC root.</exception>
    public string ToLinuxPath(string uncPath)
    {
        if (string.IsNullOrEmpty(uncPath))
        {
            throw new ArgumentNullException(nameof(uncPath));
        }

        if (!uncPath.StartsWith(UncDistroRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Path '{uncPath}' is not under WSL root '{UncDistroRoot}'.", nameof(uncPath));
        }

        // Get the portion after the distro root
        var relativePart = uncPath.Substring(UncDistroRoot.Length);

        // Replace backslashes with forward slashes
        var linuxPath = "/" + relativePart.Replace('\\', '/');

        // Normalize any double slashes
        while (linuxPath.Contains("//"))
        {
            linuxPath = linuxPath.Replace("//", "/");
        }

        return linuxPath;
    }

    /// <summary>
    /// Converts a Windows UNC PathEx to a Linux path.
    /// </summary>
    public string ToLinuxPath(PathEx uncPath)
    {
        return ToLinuxPath((string)uncPath);
    }

    /// <summary>
    /// Converts a Linux absolute path to a Windows UNC path.
    /// </summary>
    /// <param name="linuxPath">The Linux absolute path (e.g., "/home/user/project/src/main.rs").</param>
    /// <returns>The equivalent Windows UNC path (e.g., "\\wsl.localhost\Ubuntu\home\user\project\src\main.rs").</returns>
    /// <exception cref="ArgumentException">If the path is not an absolute Linux path.</exception>
    public string ToUncPath(string linuxPath)
    {
        if (string.IsNullOrEmpty(linuxPath))
        {
            throw new ArgumentNullException(nameof(linuxPath));
        }

        if (!linuxPath.StartsWith("/"))
        {
            throw new ArgumentException($"Linux path '{linuxPath}' must be absolute (start with '/').", nameof(linuxPath));
        }

        // Remove leading slash and replace forward slashes with backslashes
        var relativePart = linuxPath.Substring(1).Replace('/', '\\');

        // Combine with UNC root (which already ends with \)
        return UncDistroRoot + relativePart;
    }

    /// <summary>
    /// Converts a Linux absolute path to a Windows UNC PathEx.
    /// </summary>
    public PathEx ToUncPathEx(string linuxPath)
    {
        return (PathEx)ToUncPath(linuxPath);
    }

    /// <summary>
    /// Checks if the given path appears to be a Linux absolute path (starts with /).
    /// </summary>
    public static bool IsLinuxAbsolutePath(string path)
    {
        return !string.IsNullOrEmpty(path) && path.StartsWith("/");
    }

    /// <summary>
    /// Converts a path that might be Linux absolute to a UNC path if this is a WSL workspace,
    /// otherwise returns the original path as-is.
    /// Useful for processing cargo output that may contain Linux paths.
    /// </summary>
    /// <param name="path">The path from cargo output (could be Linux absolute or relative).</param>
    /// <param name="workspaceRoot">The workspace root path (used to determine if WSL and for relative path resolution).</param>
    /// <returns>The resolved path as a Windows path (UNC for WSL, regular for Windows).</returns>
    public static string ResolvePathForWorkspace(string path, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Try to get WSL info from workspace root
        if (TryParse(workspaceRoot, out var wslInfo))
        {
            // This is a WSL workspace
            if (IsLinuxAbsolutePath(path))
            {
                // Absolute Linux path - convert to UNC
                return wslInfo.ToUncPath(path);
            }
            else
            {
                // Relative path - it's already in Windows format from cargo metadata rewriting
                // or needs to be combined with workspace root
                return path;
            }
        }
        else
        {
            // Not a WSL workspace - return as-is
            return path;
        }
    }

    /// <summary>
    /// Gets the path to wsl.exe. Prefers the System32 location for reliability.
    /// </summary>
    public static string GetWslExePath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var wslPath = System.IO.Path.Combine(systemRoot, "System32", "wsl.exe");

        if (System.IO.File.Exists(wslPath))
        {
            return wslPath;
        }

        // Fall back to PATH lookup
        return "wsl.exe".FindInPath() ?? wslPath;
    }

    /// <summary>
    /// Checks if wsl.exe exists and is accessible.
    /// </summary>
    public static bool IsWslAvailable()
    {
        var wslPath = GetWslExePath();
        return System.IO.File.Exists(wslPath);
    }
}

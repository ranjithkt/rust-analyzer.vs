using System;
using System.Runtime.CompilerServices;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Path mapper for WSL paths. Converts between Windows UNC paths (\\wsl$\...) and Linux paths.
/// Optimized for minimal allocations on hot paths.
/// </summary>
public sealed class WslPathMapper : IPathMapper
{
    private readonly string _distroName;
    private readonly string _uncPrefix;      // \\wsl$\Distro (legacy)
    private readonly string _uncAltPrefix;   // \\wsl.localhost\Distro (modern)
    private readonly string _preferredPrefix; // The prefix to use for MapToLocal
    private readonly int _uncPrefixLength;
    private readonly int _uncAltPrefixLength;

    /// <summary>
    /// Creates a new WSL path mapper for the specified distro.
    /// Uses \\wsl.localhost\ prefix by default (modern Windows 10/11 format).
    /// </summary>
    /// <param name="distroName">The WSL distribution name (e.g., "Ubuntu").</param>
    public WslPathMapper(string distroName)
        : this(distroName, useLocalhostPrefix: true)
    {
    }

    /// <summary>
    /// Creates a new WSL path mapper for the specified distro with explicit prefix selection.
    /// </summary>
    /// <param name="distroName">The WSL distribution name (e.g., "Ubuntu").</param>
    /// <param name="useLocalhostPrefix">True to use \\wsl.localhost\, false for \\wsl$\.</param>
    public WslPathMapper(string distroName, bool useLocalhostPrefix)
    {
        _distroName = distroName ?? throw new ArgumentNullException(nameof(distroName));
        _uncPrefix = $@"\\wsl$\{distroName}";
        _uncAltPrefix = $@"\\wsl.localhost\{distroName}";
        _uncPrefixLength = _uncPrefix.Length;
        _uncAltPrefixLength = _uncAltPrefix.Length;

        // Use the prefix that matches how modern Windows accesses WSL
        _preferredPrefix = useLocalhostPrefix ? _uncAltPrefix : _uncPrefix;
    }

    /// <summary>
    /// Creates a WslPathMapper that will use the same UNC prefix format as the given reference path.
    /// </summary>
    /// <param name="distroName">The WSL distribution name.</param>
    /// <param name="referencePath">A path to detect the preferred prefix format from.</param>
    /// <returns>A WslPathMapper configured to match the reference path's format.</returns>
    public static WslPathMapper CreateMatchingFormat(string distroName, string referencePath)
    {
        // Detect which format the reference path uses
        bool useLocalhostPrefix = referencePath?.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase) ?? true;
        return new WslPathMapper(distroName, useLocalhostPrefix);
    }

    /// <summary>
    /// Gets the WSL distribution name.
    /// </summary>
    public string DistroName => _distroName;

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Wsl;

    /// <inheritdoc/>
    public RemotePath MapToRemote(PathEx vsPath)
    {
        string path = vsPath;
        if (string.IsNullOrEmpty(path))
        {
            return new RemotePath("/", TargetKind.Wsl);
        }

        // Check for \\wsl$\Distro prefix
        if (path.StartsWith(_uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var linuxPath = ConvertUncSuffixToLinux(path, _uncPrefixLength);
            return new RemotePath(linuxPath, TargetKind.Wsl);
        }

        // Check for \\wsl.localhost\Distro prefix
        if (path.StartsWith(_uncAltPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var linuxPath = ConvertUncSuffixToLinux(path, _uncAltPrefixLength);
            return new RemotePath(linuxPath, TargetKind.Wsl);
        }

        throw new ArgumentException(
            $"Path '{path}' is not a WSL path for distro '{_distroName}'. " +
            $"Expected path starting with '{_uncPrefix}' or '{_uncAltPrefix}'.",
            nameof(vsPath));
    }

    /// <inheritdoc/>
    public PathEx MapToLocal(RemotePath remotePath)
    {
        if (remotePath.Kind != TargetKind.Wsl)
        {
            throw new ArgumentException($"Expected WSL path, got {remotePath.Kind}", nameof(remotePath));
        }

        string linuxPath = remotePath;

        // Handle empty path
        if (string.IsNullOrEmpty(linuxPath))
        {
            return (PathEx)_preferredPrefix;
        }

        // Ensure path starts with /
        if (!linuxPath.StartsWith("/", StringComparison.Ordinal))
        {
            linuxPath = "/" + linuxPath;
        }

        // Convert / to \ and prepend UNC prefix (use preferred prefix to match VS format)
        var prefixLength = _preferredPrefix.Length;
        var buffer = new char[prefixLength + linuxPath.Length];

        _preferredPrefix.AsSpan().CopyTo(buffer);

        var destSpan = buffer.AsSpan(prefixLength);
        for (int i = 0; i < linuxPath.Length; i++)
        {
            destSpan[i] = linuxPath[i] == '/' ? '\\' : linuxPath[i];
        }

        return (PathEx)new string(buffer);
    }

    /// <inheritdoc/>
    public Uri MapUriToRemote(Uri vsUri)
    {
        if (vsUri == null || vsUri.Scheme != "file")
        {
            return vsUri;
        }

        // Get the local path from the URI
        string localPath;
        try
        {
            localPath = vsUri.LocalPath;
        }
        catch
        {
            // If we can't parse it, return as-is
            return vsUri;
        }

        // Check if this is a WSL path
        if (!IsWslPath(localPath))
        {
            return vsUri;
        }

        // Map to remote and create new file URI
        try
        {
            var remotePath = MapToRemote((PathEx)localPath);
            return new Uri("file://" + (string)remotePath);
        }
        catch
        {
            return vsUri;
        }
    }

    /// <inheritdoc/>
    public Uri MapUriToLocal(Uri remoteUri)
    {
        if (remoteUri == null || remoteUri.Scheme != "file")
        {
            return remoteUri;
        }

        // Get the path from the URI - for Linux paths this is the AbsolutePath
        string linuxPath;
        try
        {
            linuxPath = remoteUri.AbsolutePath;

            // Handle URL encoding
            linuxPath = Uri.UnescapeDataString(linuxPath);
        }
        catch
        {
            return remoteUri;
        }

        // Only process Linux absolute paths
        if (string.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith("/", StringComparison.Ordinal))
        {
            return remoteUri;
        }

        // Map to local UNC path and create new file URI
        try
        {
            var localPath = MapToLocal(new RemotePath(linuxPath, TargetKind.Wsl));

            // For UNC paths like \\wsl$\Ubuntu\..., create a file URI using UriBuilder
            // Note: The $ in wsl$ causes issues with standard URI parsing, so we use UriBuilder
            // with an empty host and put the full path (including wsl$) in the Path property.
            // This produces URIs like file:///wsl$/Ubuntu/home/user/...
            // whose LocalPath is /wsl$/Ubuntu/home/user/...
            string uncPath = (string)localPath;
            if (uncPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // Convert \\wsl$\Ubuntu\home\user to /wsl$/Ubuntu/home/user
                string uriPath = "/" + uncPath.Substring(2).Replace('\\', '/');
                var builder = new UriBuilder("file", string.Empty) { Path = uriPath };
                return builder.Uri;
            }

            return new Uri("file:///" + uncPath.Replace('\\', '/'));
        }
        catch
        {
            return remoteUri;
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPathForTarget(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return IsWslPath(path) || path.StartsWith("/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if the path is a WSL UNC path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsWslPath(string path)
    {
        return path.StartsWith(_uncPrefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(_uncAltPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts the suffix of a UNC path to a Linux path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ConvertUncSuffixToLinux(string path, int prefixLength)
    {
        if (path.Length <= prefixLength)
        {
            return "/";
        }

        var suffix = path.Substring(prefixLength);

        // Fast path: if no backslashes, just return with leading /
        if (suffix.IndexOf('\\') < 0)
        {
            return suffix.StartsWith("/", StringComparison.Ordinal) ? suffix : "/" + suffix;
        }

        // Replace backslashes with forward slashes
        return suffix.Replace('\\', '/');
    }

    /// <summary>
    /// Tries to parse a distro name from a WSL UNC path.
    /// </summary>
    /// <param name="path">The UNC path to parse.</param>
    /// <param name="distroName">The extracted distro name, if successful.</param>
    /// <returns>True if the path is a valid WSL UNC path.</returns>
    public static bool TryGetDistroName(string path, out string distroName)
    {
        distroName = null;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Check for \\wsl$\ prefix
        const string wslPrefix = @"\\wsl$\";
        const string wslAltPrefix = @"\\wsl.localhost\";

        int prefixLength;
        if (path.StartsWith(wslPrefix, StringComparison.OrdinalIgnoreCase))
        {
            prefixLength = wslPrefix.Length;
        }
        else if (path.StartsWith(wslAltPrefix, StringComparison.OrdinalIgnoreCase))
        {
            prefixLength = wslAltPrefix.Length;
        }
        else
        {
            return false;
        }

        // Find the distro name (ends at next \ or end of string)
        var remaining = path.Substring(prefixLength);
        var separatorIndex = remaining.IndexOf('\\');

        distroName = separatorIndex >= 0
            ? remaining.Substring(0, separatorIndex)
            : remaining;

        return !string.IsNullOrEmpty(distroName);
    }
}


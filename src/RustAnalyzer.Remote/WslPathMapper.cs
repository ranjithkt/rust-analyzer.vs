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
    private readonly string _uncPrefix;      // \\wsl$\Distro
    private readonly string _uncAltPrefix;   // \\wsl.localhost\Distro
    private readonly int _uncPrefixLength;
    private readonly int _uncAltPrefixLength;

    /// <summary>
    /// Creates a new WSL path mapper for the specified distro.
    /// </summary>
    /// <param name="distroName">The WSL distribution name (e.g., "Ubuntu").</param>
    public WslPathMapper(string distroName)
    {
        _distroName = distroName ?? throw new ArgumentNullException(nameof(distroName));
        _uncPrefix = $@"\\wsl$\{distroName}";
        _uncAltPrefix = $@"\\wsl.localhost\{distroName}";
        _uncPrefixLength = _uncPrefix.Length;
        _uncAltPrefixLength = _uncAltPrefix.Length;
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
            return (PathEx)_uncPrefix;
        }

        // Ensure path starts with /
        if (!linuxPath.StartsWith("/", StringComparison.Ordinal))
        {
            linuxPath = "/" + linuxPath;
        }

        // Convert / to \ and prepend UNC prefix
        // Optimize: use char[] buffer for single allocation
        var buffer = new char[_uncPrefixLength + linuxPath.Length];

        _uncPrefix.AsSpan().CopyTo(buffer);

        var destSpan = buffer.AsSpan(_uncPrefixLength);
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
            return new Uri("file:///" + ((string)localPath).Replace('\\', '/'));
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


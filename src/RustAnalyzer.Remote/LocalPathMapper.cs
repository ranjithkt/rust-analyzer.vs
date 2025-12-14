using System;
using System.Runtime.CompilerServices;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Path mapper for local Windows paths. Performs no transformation.
/// </summary>
public sealed class LocalPathMapper : IPathMapper
{
    /// <summary>
    /// Singleton instance for the local path mapper.
    /// </summary>
    public static readonly LocalPathMapper Instance = new LocalPathMapper();

    private LocalPathMapper()
    {
    }

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Local;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RemotePath MapToRemote(PathEx vsPath)
    {
        return new RemotePath(vsPath, TargetKind.Local);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PathEx MapToLocal(RemotePath remotePath)
    {
        if (remotePath.Kind != TargetKind.Local)
        {
            throw new ArgumentException($"Expected Local path, got {remotePath.Kind}", nameof(remotePath));
        }

        return (PathEx)(string)remotePath;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Uri MapUriToRemote(Uri vsUri)
    {
        // No transformation for local paths
        return vsUri;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Uri MapUriToLocal(Uri remoteUri)
    {
        // No transformation for local paths
        return remoteUri;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsPathForTarget(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Local paths are Windows paths that are NOT WSL UNC paths
        // Check for drive letter or standard UNC (but not \\wsl$)
        if (path.Length >= 2)
        {
            // Drive letter path: C:\...
            if (char.IsLetter(path[0]) && path[1] == ':')
            {
                return true;
            }

            // UNC path that is NOT WSL
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return !path.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase) &&
                       !path.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}


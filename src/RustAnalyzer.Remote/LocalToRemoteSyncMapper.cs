using System;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Path mapper for SSH Local Sync mode (Mode 1).
/// Maps between local Windows paths and remote Linux paths where files are synced.
///
/// Example mapping:
/// Local:  C:\Repos\my-project\src\main.rs
/// Remote: /home/user/vs-sync/my-project/src/main.rs
/// </summary>
public sealed class LocalToRemoteSyncMapper : IPathMapper
{
    private readonly PathEx _localRoot;
    private readonly RemotePath _remoteRoot;
    private readonly SshConnectionInfo _connectionInfo;

    /// <summary>
    /// Creates a new local-to-remote sync mapper.
    /// </summary>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="localRoot">Local workspace root path (e.g., C:\Repos\my-project).</param>
    /// <param name="remoteSyncBasePath">Remote base path for synced projects (e.g., ~/vs-sync).</param>
    public LocalToRemoteSyncMapper(SshConnectionInfo connectionInfo, PathEx localRoot, string remoteSyncBasePath = "~/vs-sync")
    {
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _localRoot = localRoot;

        // Replace ~ with $HOME so it expands properly even in quoted strings
        var projectName = localRoot.GetFileName();
        var expandedBasePath = remoteSyncBasePath.StartsWith("~/", StringComparison.Ordinal)
            ? "$HOME" + remoteSyncBasePath.Substring(1)
            : remoteSyncBasePath.StartsWith("~", StringComparison.Ordinal)
                ? "$HOME" + remoteSyncBasePath.Substring(1)
                : remoteSyncBasePath;

        _remoteRoot = new RemotePath($"{expandedBasePath}/{projectName}", TargetKind.Ssh);
    }

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Ssh;

    /// <summary>
    /// Gets the local workspace root path.
    /// </summary>
    public PathEx LocalRoot => _localRoot;

    /// <summary>
    /// Gets the remote sync root path.
    /// </summary>
    public RemotePath RemoteRoot => _remoteRoot;

    /// <summary>
    /// Gets the SSH connection info.
    /// </summary>
    public SshConnectionInfo ConnectionInfo => _connectionInfo;

    /// <inheritdoc/>
    public RemotePath MapToRemote(PathEx vsPath)
    {
        string path = vsPath;

        // Check if the path starts with our local root
        if (path.StartsWith((string)_localRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Get the relative path from local root
            var relativePath = path.Substring(((string)_localRoot).Length).TrimStart('\\', '/');

            // Convert to Linux path format
            var linuxRelativePath = relativePath.Replace(@"\", "/");

            // Combine with remote root
            if (string.IsNullOrEmpty(linuxRelativePath))
            {
                return _remoteRoot;
            }

            return new RemotePath($"{_remoteRoot}/{linuxRelativePath}", TargetKind.Ssh);
        }

        // If it's already a Linux-style path, just wrap it
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return new RemotePath(path, TargetKind.Ssh);
        }

        throw new ArgumentException($"Path '{path}' is not within the local workspace root '{_localRoot}'");
    }

    /// <inheritdoc/>
    public PathEx MapToLocal(RemotePath remotePath)
    {
        if (remotePath.Kind != TargetKind.Ssh)
        {
            throw new ArgumentException("Expected SSH path");
        }

        string path = (string)remotePath;

        // Check if the path starts with our remote root
        string remoteRootStr = (string)_remoteRoot;
        if (path.StartsWith(remoteRootStr, StringComparison.Ordinal))
        {
            // Get the relative path from remote root
            var relativePath = path.Substring(remoteRootStr.Length).TrimStart('/');

            // Convert to Windows path format
            var windowsRelativePath = relativePath.Replace("/", @"\");

            // Combine with local root
            if (string.IsNullOrEmpty(windowsRelativePath))
            {
                return _localRoot;
            }

            return (PathEx)System.IO.Path.Combine((string)_localRoot, windowsRelativePath);
        }

        // If it doesn't start with our remote root, we can't map it
        throw new ArgumentException($"Path '{path}' is not within the remote sync root '{_remoteRoot}'");
    }

    /// <inheritdoc/>
    public Uri MapUriToRemote(Uri vsUri)
    {
        if (vsUri.Scheme != "file")
        {
            return vsUri;
        }

        try
        {
            var localPath = vsUri.LocalPath;
            var remotePath = MapToRemote((PathEx)localPath);

            // Build a file:// URI for the Linux path
            return new Uri($"file://{remotePath}");
        }
        catch
        {
            return vsUri;
        }
    }

    /// <inheritdoc/>
    public Uri MapUriToLocal(Uri remoteUri)
    {
        if (remoteUri.Scheme != "file")
        {
            return remoteUri;
        }

        try
        {
            var remotePath = remoteUri.LocalPath;
            var localPath = MapToLocal(new RemotePath(remotePath, TargetKind.Ssh));

            return new Uri($"file:///{localPath.ToString().Replace(@"\", "/")}");
        }
        catch
        {
            return remoteUri;
        }
    }

    /// <inheritdoc/>
    public bool IsPathForTarget(string path)
    {
        // This mapper handles local Windows paths within our workspace root
        if (path.StartsWith((string)_localRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also handles remote paths within our sync root
        if (path.StartsWith((string)_remoteRoot, StringComparison.Ordinal))
        {
            return true;
        }

        // Linux-style absolute paths are assumed to be remote
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}

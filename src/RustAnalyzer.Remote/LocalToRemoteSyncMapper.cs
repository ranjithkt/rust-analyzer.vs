using System;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Path mapper for SSH Local Sync mode (Mode 1).
/// Maps between local Windows paths and remote Linux paths where files are synced.
///
/// The remote structure preserves the local directory hierarchy relative to a common root,
/// ensuring all relative paths in Cargo.toml files remain valid.
///
/// Example mapping (with common root C:\Repos\Rust):
/// Local:  C:\Repos\Rust\trader-one\trader\src\main.rs
/// Remote: $HOME/vs-sync/trader-one/trader/src/main.rs
/// </summary>
public sealed class LocalToRemoteSyncMapper : IPathMapper
{
    private readonly PathEx _localRoot;
    private readonly string _remoteSyncBase;
    private readonly SshConnectionInfo _connectionInfo;
    private RemotePath _remoteRoot;
    private string _commonLocalRoot;

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
        _remoteSyncBase = remoteSyncBasePath.StartsWith("~/", StringComparison.Ordinal)
            ? "$HOME" + remoteSyncBasePath.Substring(1)
            : remoteSyncBasePath.StartsWith("~", StringComparison.Ordinal)
                ? "$HOME" + remoteSyncBasePath.Substring(1)
                : remoteSyncBasePath;

        // Default: use parent directory as common root to preserve structure
        _commonLocalRoot = System.IO.Path.GetDirectoryName((string)localRoot) ?? (string)localRoot;
        UpdateRemoteRoot();
    }

    /// <summary>
    /// Gets the remote sync base path (e.g., $HOME/vs-sync).
    /// </summary>
    public string RemoteSyncBase => _remoteSyncBase;

    /// <summary>
    /// Gets the common local root for path mapping.
    /// </summary>
    public string CommonLocalRoot => _commonLocalRoot;

    /// <summary>
    /// Sets the common local root for all synced paths.
    /// This should be called after discovering all dependencies to ensure correct path mapping.
    /// </summary>
    public void SetCommonLocalRoot(string commonRoot)
    {
        _commonLocalRoot = commonRoot;
        UpdateRemoteRoot();
    }

    private void UpdateRemoteRoot()
    {
        var relativePath = GetRelativePathFromRoot(_commonLocalRoot, (string)_localRoot);
        _remoteRoot = new RemotePath($"{_remoteSyncBase}/{relativePath}", TargetKind.Ssh);
    }

    private static string GetRelativePathFromRoot(string root, string targetPath)
    {
        var normalizedRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        var normalizedTarget = System.IO.Path.GetFullPath(targetPath).TrimEnd('\\', '/');

        if (normalizedTarget.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedTarget.Substring(normalizedRoot.Length).TrimStart('\\', '/');
            return relative.Replace('\\', '/');
        }

        // Fallback: just use the directory name
        return System.IO.Path.GetFileName(normalizedTarget);
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

        // Check if the path starts with our common local root (not just workspace root)
        // This handles paths like C:\Repos\Rust\... when common root is C:\Repos\Rust
        if (path.StartsWith(_commonLocalRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = path.Substring(_commonLocalRoot.Length).TrimStart('\\', '/');
            var linuxRelativePath = relativePath.Replace(@"\", "/");

            if (string.IsNullOrEmpty(linuxRelativePath))
            {
                return new RemotePath(_remoteSyncBase, TargetKind.Ssh);
            }

            return new RemotePath($"{_remoteSyncBase}/{linuxRelativePath}", TargetKind.Ssh);
        }

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

        // Try to map using the remote sync base (handles all synced paths, not just main project)
        // The remote sync base is $HOME/vs-sync, but cargo returns expanded paths like /root/vs-sync/...
        var syncBaseSuffix = _remoteSyncBase.StartsWith("$HOME", StringComparison.Ordinal)
            ? _remoteSyncBase.Substring(5)  // Get "/vs-sync" part after $HOME
            : _remoteSyncBase;

        // Check common home directory patterns for expanded sync base
        string expandedSyncBase = null;

        if (path.StartsWith("/root" + syncBaseSuffix, StringComparison.Ordinal))
        {
            expandedSyncBase = "/root" + syncBaseSuffix;
        }
        else if (path.StartsWith("/home/", StringComparison.Ordinal))
        {
            // Handle /home/username/vs-sync/... pattern
            var afterHome = path.Substring(6); // After "/home/"
            var slashIndex = afterHome.IndexOf('/');
            if (slashIndex > 0)
            {
                var restOfPath = afterHome.Substring(slashIndex);
                if (restOfPath.StartsWith(syncBaseSuffix, StringComparison.Ordinal))
                {
                    expandedSyncBase = "/home/" + afterHome.Substring(0, slashIndex) + syncBaseSuffix;
                }
            }
        }
        else if (path.StartsWith(_remoteSyncBase, StringComparison.Ordinal))
        {
            expandedSyncBase = _remoteSyncBase;
        }

        if (expandedSyncBase != null)
        {
            // Get the path relative to the sync base
            var pathAfterSyncBase = path.Substring(expandedSyncBase.Length).TrimStart('/');

            // Convert to Windows path format
            var windowsRelativePath = pathAfterSyncBase.Replace("/", @"\");

            // Combine with common local root (not project root!)
            if (string.IsNullOrEmpty(windowsRelativePath))
            {
                return (PathEx)_commonLocalRoot;
            }

            return (PathEx)System.IO.Path.Combine(_commonLocalRoot, windowsRelativePath);
        }

        // If it doesn't match any pattern, we can't map it
        throw new ArgumentException($"Path '{path}' is not within the remote sync base '{_remoteSyncBase}' (expanded patterns checked: /root{syncBaseSuffix}, /home/*{syncBaseSuffix})");
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
        // This mapper handles local Windows paths within our common local root
        if (path.StartsWith(_commonLocalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also handles local paths within the workspace root
        if (path.StartsWith((string)_localRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for remote paths under the sync base
        var syncBaseSuffix = _remoteSyncBase.StartsWith("$HOME", StringComparison.Ordinal)
            ? _remoteSyncBase.Substring(5)  // Get "/vs-sync" part after $HOME
            : _remoteSyncBase;

        // Handle expanded home directory patterns
        if (path.StartsWith("/root" + syncBaseSuffix, StringComparison.Ordinal))
        {
            return true;
        }

        if (path.StartsWith("/home/", StringComparison.Ordinal))
        {
            var afterHome = path.Substring(6);
            var slashIndex = afterHome.IndexOf('/');
            if (slashIndex > 0)
            {
                var restOfPath = afterHome.Substring(slashIndex);
                if (restOfPath.StartsWith(syncBaseSuffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        // Handle $HOME prefix directly
        if (path.StartsWith(_remoteSyncBase, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}

using System;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Path mapper for SSH remote hosts.
/// Maps between local cache paths and remote paths.
///
/// When using SSH, VS works with a local cache of the remote files:
/// Local cache: %LOCALAPPDATA%\rust-analyzer.vs\ssh-cache\{connection-id}\{remote-path}
/// Remote: /home/user/project/...
/// </summary>
public sealed class SshPathMapper : IPathMapper
{
    private readonly SshConnectionInfo _connectionInfo;
    private readonly string _cacheRoot;
    private readonly string _remoteRoot;

    /// <summary>
    /// Creates a new SSH path mapper.
    /// </summary>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="remoteRoot">The remote root path being mapped (e.g., /home/user/project).</param>
    public SshPathMapper(SshConnectionInfo connectionInfo, string remoteRoot = null)
    {
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _remoteRoot = remoteRoot ?? "/";

        // Build cache root path
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var safeConnectionId = MakeSafeFileName(connectionInfo.Id);
        _cacheRoot = System.IO.Path.Combine(localAppData, "rust-analyzer.vs", "ssh-cache", safeConnectionId);
    }

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Ssh;

    /// <summary>
    /// Gets the local cache root path.
    /// </summary>
    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// Gets the remote root path.
    /// </summary>
    public string RemoteRoot => _remoteRoot;

    /// <inheritdoc/>
    public RemotePath MapToRemote(PathEx vsPath)
    {
        string path = vsPath;

        // Check if this is a cache path
        if (path.StartsWith(_cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Extract the relative path from the cache
            var relativePath = path.Substring(_cacheRoot.Length).TrimStart('\\', '/');
            var linuxPath = "/" + relativePath.Replace(@"\", "/");
            return new RemotePath(linuxPath, TargetKind.Ssh);
        }

        // If it's already a Linux-style path, just wrap it
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return new RemotePath(path, TargetKind.Ssh);
        }

        throw new ArgumentException($"Path '{path}' is not a valid SSH cache path for connection '{_connectionInfo.DisplayName}'");
    }

    /// <inheritdoc/>
    public PathEx MapToLocal(RemotePath remotePath)
    {
        if (remotePath.Kind != TargetKind.Ssh)
        {
            throw new ArgumentException("Expected SSH path");
        }

        string path = (string)remotePath;

        // Convert Linux path to local cache path
        // /home/user/project/src/main.rs -> {cache}\home\user\project\src\main.rs
        var localPath = path.TrimStart('/').Replace("/", @"\");
        return (PathEx)System.IO.Path.Combine(_cacheRoot, localPath);
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
        // Check if it's a cache path
        if (path.StartsWith(_cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if it's a Linux-style absolute path
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Makes a string safe for use as a file name.
    /// </summary>
    private static string MakeSafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tries to get the connection info from an SSH cache path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="connectionId">The connection ID if found.</param>
    /// <returns>True if the path is an SSH cache path.</returns>
    public static bool TryGetConnectionId(string path, out string connectionId)
    {
        connectionId = null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sshCacheRoot = System.IO.Path.Combine(localAppData, "rust-analyzer.vs", "ssh-cache");

        if (!path.StartsWith(sshCacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Extract the connection ID from the path
        var relativePath = path.Substring(sshCacheRoot.Length).TrimStart('\\', '/');
        var parts = relativePath.Split(new[] { '\\', '/' }, 2);

        if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
        {
            connectionId = parts[0];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a path is a valid SSH cache path (for RemoteCache mode).
    /// Used to distinguish between LocalSync mode (local Windows paths) and RemoteCache mode.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if this is an SSH cache path.</returns>
    public static bool IsValidSshCachePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sshCacheRoot = System.IO.Path.Combine(localAppData, "rust-analyzer.vs", "ssh-cache");

        return path.StartsWith(sshCacheRoot, StringComparison.OrdinalIgnoreCase);
    }
}

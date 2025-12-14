using System;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Represents an SSH remote target system.
/// Supports two workspace modes:
/// - LocalSync: Local Windows source synced to remote for builds (C++ style)
/// - RemoteCache: Remote source cached locally for editing
/// </summary>
public sealed class SshTargetSystem : ITargetSystem
{
    private readonly SshConnectionInfo _connectionInfo;
    private readonly string _remoteRoot;
    private readonly string _remoteSyncBasePath;
    private SshExecutionContext _executionContext;
    private IPathMapper _pathMapper;
    private PathEx? _workspacePath;
    private SshWorkspaceMode _workspaceMode;

    /// <summary>
    /// Creates a new SSH target system.
    /// </summary>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="remoteRoot">The remote root path (for RemoteCache mode).</param>
    /// <param name="remoteSyncBasePath">Base path on remote for synced projects (default: ~/vs-sync).</param>
    public SshTargetSystem(SshConnectionInfo connectionInfo, string remoteRoot = null, string remoteSyncBasePath = "~/vs-sync")
    {
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _remoteRoot = remoteRoot;
        _remoteSyncBasePath = remoteSyncBasePath ?? "~/vs-sync";
    }

    /// <inheritdoc/>
    public string Id => _connectionInfo.Id;

    /// <inheritdoc/>
    public string DisplayName => $"SSH: {_connectionInfo.DisplayName}";

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Ssh;

    /// <summary>
    /// Gets the SSH connection information.
    /// </summary>
    public SshConnectionInfo ConnectionInfo => _connectionInfo;

    /// <summary>
    /// Gets the current workspace mode.
    /// </summary>
    public SshWorkspaceMode WorkspaceMode => _workspaceMode;

    /// <summary>
    /// Gets the remote sync base path.
    /// </summary>
    public string RemoteSyncBasePath => _remoteSyncBasePath;

    /// <summary>
    /// Sets the current workspace path and determines the appropriate mode.
    /// </summary>
    /// <param name="workspacePath">The VS workspace path.</param>
    public void SetWorkspacePath(PathEx workspacePath)
    {
        _workspacePath = workspacePath;
        _workspaceMode = DetermineWorkspaceMode(workspacePath);
        _pathMapper = null; // Reset to create new mapper with correct mode
    }

    /// <summary>
    /// Determines the workspace mode based on the path.
    /// </summary>
    /// <param name="workspacePath">The workspace path to analyze.</param>
    /// <returns>The detected workspace mode.</returns>
    public static SshWorkspaceMode DetermineWorkspaceMode(PathEx workspacePath)
    {
        if (workspacePath == null)
        {
            return SshWorkspaceMode.LocalSync;
        }

        string path = workspacePath;

        // Check if it's in the SSH cache directory
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sshCacheRoot = System.IO.Path.Combine(localAppData, "rust-analyzer.vs", "ssh-cache");

        if (path.StartsWith(sshCacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return SshWorkspaceMode.RemoteCache;
        }

        // Otherwise, it's a local folder - use LocalSync mode
        return SshWorkspaceMode.LocalSync;
    }

    /// <inheritdoc/>
    public IExecutionContext GetExecutionContext()
    {
        return _executionContext ??= new SshExecutionContext(_connectionInfo);
    }

    /// <inheritdoc/>
    public IPathMapper GetPathMapper()
    {
        if (_pathMapper == null)
        {
            _pathMapper = CreatePathMapper();
        }

        return _pathMapper;
    }

    /// <summary>
    /// Gets the path mapper for the specified workspace, determining mode automatically.
    /// </summary>
    /// <param name="workspacePath">The workspace path.</param>
    /// <returns>The appropriate path mapper for the workspace mode.</returns>
    public IPathMapper GetPathMapperForWorkspace(PathEx workspacePath)
    {
        SetWorkspacePath(workspacePath);
        return GetPathMapper();
    }

    private IPathMapper CreatePathMapper()
    {
        if (_workspaceMode == SshWorkspaceMode.LocalSync && _workspacePath.HasValue)
        {
            // Mode 1: Local source synced to remote
            return new LocalToRemoteSyncMapper(_connectionInfo, _workspacePath.Value, _remoteSyncBasePath);
        }
        else
        {
            // Mode 2: Remote source cached locally
            return new SshPathMapper(_connectionInfo, _remoteRoot);
        }
    }
}

using System;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Represents an SSH remote target system.
/// </summary>
public sealed class SshTargetSystem : ITargetSystem
{
    private readonly SshConnectionInfo _connectionInfo;
    private readonly string _remoteRoot;
    private SshExecutionContext _executionContext;
    private SshPathMapper _pathMapper;

    /// <summary>
    /// Creates a new SSH target system.
    /// </summary>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="remoteRoot">The remote root path (optional).</param>
    public SshTargetSystem(SshConnectionInfo connectionInfo, string remoteRoot = null)
    {
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _remoteRoot = remoteRoot;
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

    /// <inheritdoc/>
    public IExecutionContext GetExecutionContext()
    {
        return _executionContext ??= new SshExecutionContext(_connectionInfo);
    }

    /// <inheritdoc/>
    public IPathMapper GetPathMapper()
    {
        return _pathMapper ??= new SshPathMapper(_connectionInfo, _remoteRoot);
    }
}

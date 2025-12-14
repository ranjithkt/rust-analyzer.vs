using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Service for managing target systems for a workspace.
/// </summary>
public sealed class TargetSystemService : ITargetSystemService
{
    private readonly PathEx _workspaceRoot;
    private readonly object _lock = new object();

    private ITargetSystem _currentTarget;
    private List<ITargetSystem> _availableTargets;
    private bool _wslEnabled;
    private bool _sshEnabled;

    /// <summary>
    /// Creates a new TargetSystemService.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root path.</param>
    /// <param name="wslEnabled">Whether WSL support is enabled.</param>
    /// <param name="sshEnabled">Whether SSH support is enabled.</param>
    public TargetSystemService(PathEx workspaceRoot, bool wslEnabled, bool sshEnabled)
    {
        _workspaceRoot = workspaceRoot;
        _wslEnabled = wslEnabled;
        _sshEnabled = sshEnabled;

        // Initialize with local target
        _availableTargets = new List<ITargetSystem> { LocalTargetSystem.Instance };
        _currentTarget = LocalTargetSystem.Instance;

        // Auto-detect target based on workspace path
        var detectedTarget = DetectTargetForWorkspace(workspaceRoot);
        if (detectedTarget != null && detectedTarget.Kind != TargetKind.Local)
        {
            _currentTarget = detectedTarget;
            if (!_availableTargets.Any(t => t.Id == detectedTarget.Id))
            {
                _availableTargets.Add(detectedTarget);
            }
        }
    }

    /// <inheritdoc/>
    public ITargetSystem CurrentTarget
    {
        get
        {
            lock (_lock)
            {
                return _currentTarget;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ITargetSystem> AvailableTargets
    {
        get
        {
            lock (_lock)
            {
                return _availableTargets.ToList().AsReadOnly();
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<TargetChangedEventArgs> TargetChanged;

    /// <inheritdoc/>
    public Task SetCurrentTargetAsync(ITargetSystem target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        ITargetSystem oldTarget;
        lock (_lock)
        {
            if (_currentTarget?.Id == target.Id)
            {
                return Task.CompletedTask;
            }

            // Validate target is available
            if (!_availableTargets.Any(t => t.Id == target.Id))
            {
                throw new ArgumentException($"Target '{target.Id}' is not in the available targets list.", nameof(target));
            }

            oldTarget = _currentTarget;
            _currentTarget = target;
        }

        // Raise event outside lock
        TargetChanged?.Invoke(this, new TargetChangedEventArgs(oldTarget, target));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task RefreshAvailableTargetsAsync(CancellationToken ct)
    {
        var newTargets = new List<ITargetSystem> { LocalTargetSystem.Instance };

        // Add WSL distros if enabled
        if (_wslEnabled && WslExecutionContext.IsWslAvailable())
        {
            var distros = await WslExecutionContext.GetInstalledDistrosAsync(ct).ConfigureAwait(false);
            foreach (var distro in distros)
            {
                newTargets.Add(new WslTargetSystem(distro));
            }
        }

        // Add SSH profiles if enabled
        if (_sshEnabled && SshExecutionContext.IsSshAvailable())
        {
            var sshProfiles = GetSshProfiles();
            newTargets.AddRange(sshProfiles);
        }

        lock (_lock)
        {
            _availableTargets = newTargets;

            // Ensure current target is still valid
            if (!_availableTargets.Any(t => t.Id == _currentTarget?.Id))
            {
                var oldTarget = _currentTarget;
                _currentTarget = LocalTargetSystem.Instance;

                // Raise event outside lock (but we're still in lock, so do it after)
                Task.Run(() => TargetChanged?.Invoke(this, new TargetChangedEventArgs(oldTarget, _currentTarget)));
            }
        }
    }

    /// <summary>
    /// Gets saved SSH profiles.
    /// For now, returns an empty list. In the future, this could read from:
    /// - Visual Studio's Connection Manager
    /// - A custom configuration file
    /// - SSH config file (~/.ssh/config)
    /// </summary>
    private IEnumerable<ITargetSystem> GetSshProfiles()
    {
        // TODO: Implement SSH profile discovery
        // Options:
        // 1. Read from VS Connection Manager if available
        // 2. Read from custom settings file
        // 3. Parse ~/.ssh/config for Host entries

        // For now, return empty - users can add connections programmatically
        // or we can add a UI for adding SSH connections later
        return Enumerable.Empty<ITargetSystem>();
    }

    /// <inheritdoc/>
    public ITargetSystem DetectTargetForWorkspace(PathEx workspacePath)
    {
        if (string.IsNullOrEmpty(workspacePath))
        {
            return LocalTargetSystem.Instance;
        }

        string path = workspacePath;

        // Check for WSL UNC path
        if (WslPathMapper.TryGetDistroName(path, out var distroName))
        {
            return new WslTargetSystem(distroName);
        }

        // Check for SSH cache paths
        if (SshPathMapper.TryGetConnectionId(path, out var connectionId))
        {
            // Parse connection ID back to connection info
            // Format: ssh:user@host or ssh:user@host:port
            var connectionInfo = ParseConnectionId(connectionId);
            if (connectionInfo != null)
            {
                return new SshTargetSystem(connectionInfo);
            }
        }

        return LocalTargetSystem.Instance;
    }

    /// <summary>
    /// Parses an SSH connection ID back to connection info.
    /// </summary>
    private static SshConnectionInfo ParseConnectionId(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId) || !connectionId.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = connectionId.Substring(4); // Remove "ssh:" prefix

        string username = null;
        string host;
        int port = 22;

        // Parse user@host:port
        var atIndex = rest.IndexOf('@');
        if (atIndex > 0)
        {
            username = rest.Substring(0, atIndex);
            rest = rest.Substring(atIndex + 1);
        }

        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(rest.Substring(colonIndex + 1), out var parsedPort))
        {
            port = parsedPort;
            host = rest.Substring(0, colonIndex);
        }
        else
        {
            host = rest;
        }

        return new SshConnectionInfo
        {
            Host = host,
            Port = port,
            Username = username,
        };
    }

    /// <summary>
    /// Updates the feature flags. Should be called when options change.
    /// </summary>
    /// <param name="wslEnabled">Whether WSL support is enabled.</param>
    /// <param name="sshEnabled">Whether SSH support is enabled.</param>
    public void UpdateFeatureFlags(bool wslEnabled, bool sshEnabled)
    {
        lock (_lock)
        {
            _wslEnabled = wslEnabled;
            _sshEnabled = sshEnabled;
        }
    }
}


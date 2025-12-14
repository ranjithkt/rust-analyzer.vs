using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Service for managing target systems for a workspace.
/// This is the primary abstraction for selecting between local, WSL, and SSH targets.
/// </summary>
public interface ITargetSystemService
{
    /// <summary>
    /// Gets the current target for the workspace.
    /// </summary>
    ITargetSystem CurrentTarget { get; }

    /// <summary>
    /// Gets all available targets (Local + WSL distros + SSH profiles).
    /// </summary>
    IReadOnlyList<ITargetSystem> AvailableTargets { get; }

    /// <summary>
    /// Raised when the current target changes. Consumers should refresh state.
    /// </summary>
    event EventHandler<TargetChangedEventArgs> TargetChanged;

    /// <summary>
    /// Change the active target system.
    /// </summary>
    /// <param name="target">The target to switch to.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetCurrentTargetAsync(ITargetSystem target, CancellationToken ct);

    /// <summary>
    /// Refresh available targets (re-enumerate WSL distros, SSH profiles).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshAvailableTargetsAsync(CancellationToken ct);

    /// <summary>
    /// Auto-detect appropriate target for a workspace path.
    /// </summary>
    /// <param name="workspacePath">The workspace path.</param>
    /// <returns>The detected target system.</returns>
    ITargetSystem DetectTargetForWorkspace(PathEx workspacePath);
}

/// <summary>
/// Factory for creating ITargetSystemService instances.
/// </summary>
public interface ITargetSystemServiceFactory
{
    /// <summary>
    /// Creates a target system service for the specified workspace root.
    /// </summary>
    /// <param name="workspaceRoot">The workspace root path.</param>
    /// <returns>A new target system service.</returns>
    ITargetSystemService Create(PathEx workspaceRoot);
}


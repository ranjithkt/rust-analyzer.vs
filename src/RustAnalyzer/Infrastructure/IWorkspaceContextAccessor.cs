using System.ComponentModel.Composition;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.Shell;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;

namespace KS.RustAnalyzer.Infrastructure;

/// <summary>
/// Provides access to the current workspace's target system, execution context, and path mapper.
/// </summary>
public interface IWorkspaceContextAccessor
{
    /// <summary>
    /// Gets the target system service for the current workspace.
    /// </summary>
    ITargetSystemService GetTargetSystemService();

    /// <summary>
    /// Gets the current path mapper for the workspace.
    /// </summary>
    IPathMapper GetCurrentPathMapper();

    /// <summary>
    /// Gets the current execution context for the workspace.
    /// </summary>
    IExecutionContext GetCurrentExecutionContext();

    /// <summary>
    /// Gets the current target system.
    /// </summary>
    ITargetSystem GetCurrentTarget();
}

/// <summary>
/// Default implementation of <see cref="IWorkspaceContextAccessor"/>.
/// Uses TargetSystemStore to ensure the same service instance is shared across all components.
/// </summary>
[Export(typeof(IWorkspaceContextAccessor))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class WorkspaceContextAccessor : IWorkspaceContextAccessor
{
    [Import]
    public IVsFolderWorkspaceService WorkspaceService { get; set; }

    /// <inheritdoc/>
    public ITargetSystemService GetTargetSystemService()
    {
        var workspace = WorkspaceService?.CurrentWorkspace;
        if (workspace == null)
        {
            return null;
        }

        // Use TargetSystemStore to get the shared service instance
        // This ensures the dropdown and all other components use the same instance
        return TargetSystemStore.GetService((PathEx)workspace.Location);
    }

    /// <inheritdoc/>
    public IPathMapper GetCurrentPathMapper()
    {
        return GetCurrentTarget()?.GetPathMapper();
    }

    /// <inheritdoc/>
    public IExecutionContext GetCurrentExecutionContext()
    {
        return GetCurrentTarget()?.GetExecutionContext();
    }

    /// <inheritdoc/>
    public ITargetSystem GetCurrentTarget()
    {
        return GetTargetSystemService()?.CurrentTarget;
    }
}

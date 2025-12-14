using System.ComponentModel.Composition;
using System.Collections.Concurrent;
using System.Threading;
using KS.RustAnalyzer.Remote;
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
/// </summary>
[Export(typeof(IWorkspaceContextAccessor))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class WorkspaceContextAccessor : IWorkspaceContextAccessor
{
    private readonly ConcurrentDictionary<string, ITargetSystemService> _cache = new();

    [Import]
    public IVsFolderWorkspaceService WorkspaceService { get; set; }

    /// <inheritdoc/>
    public ITargetSystemService GetTargetSystemService()
    {
        var workspace = WorkspaceService.CurrentWorkspace;
        if (workspace == null)
        {
            return null;
        }

        var location = workspace.Location;
        return _cache.GetOrAdd(location, path =>
        {
            var options = Options.GetLiveInstanceAsync().GetAwaiter().GetResult();
            var service = new TargetSystemService(
                (PathEx)path,
                options?.EnableWslSupport ?? false,
                options?.EnableSshSupport ?? false);

            // Initialize available targets
            _ = service.RefreshAvailableTargetsAsync(CancellationToken.None);
            return service;
        });
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

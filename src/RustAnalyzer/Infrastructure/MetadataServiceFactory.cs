using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Workspace;

namespace KS.RustAnalyzer.Infrastructure;

[ExportWorkspaceServiceFactory(WorkspaceServiceFactoryOptions.None, typeof(IMetadataService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class MetadataServiceFactory : IWorkspaceServiceFactory
{
    [Import]
    public ITelemetryService T { get; set; }

    [Import]
    public ILogger L { get; set; }

    [Import]
    public IToolchainService CargoService { get; set; }

    [Import]
    public IWorkspaceContextAccessor WorkspaceContextAccessor { get; set; }

    public object CreateService(IWorkspace workspaceContext)
    {
        var workspaceLocation = (PathEx)workspaceContext.Location;

        // Check if this is a WSL workspace - if so, we may need to create a fallback context
        var isWslWorkspace = WslPathMapper.TryGetDistroName(workspaceLocation, out var detectedDistro);

        // Create a provider that fetches the current target context on demand
        // This allows MetadataService to use the correct execution context for remote targets
        TargetContextProvider targetContextProvider = () =>
        {
            var executionContext = WorkspaceContextAccessor?.GetCurrentExecutionContext();
            var pathMapper = WorkspaceContextAccessor?.GetCurrentPathMapper();

            // If we have a valid execution context, use it
            if (executionContext != null && executionContext.Kind != TargetKind.Local)
            {
                return (executionContext, pathMapper);
            }

            // Fallback: For WSL workspaces, create a WSL execution context even if
            // the target system service hasn't been initialized yet
            // This ensures metadata discovery works for WSL-first users
            if (isWslWorkspace && !string.IsNullOrEmpty(detectedDistro))
            {
                L?.WriteLine("[MetadataServiceFactory] Using fallback WSL context for distro: {0}", detectedDistro);
                var wslContext = new WslExecutionContext(detectedDistro);
                // Use the same path format (wsl$ vs wsl.localhost) as the workspace location
                var wslMapper = WslPathMapper.CreateMatchingFormat(detectedDistro, (string)workspaceLocation);
                return (wslContext, wslMapper);
            }

            return null;
        };

        var mds = new MetadataService(
            CargoService,
            workspaceLocation,
            new TL { T = T, L = L, },
            targetContextProvider);

        Func<object, BatchFileSystemEventArgs, Task> eh = async (_, e) => await BatchFileSystemChangedEventHandlerAsync(e, mds);
        workspaceContext.GetFileWatcherService().OnBatchFileSystemChanged += eh;
        mds.DisconnectEvents = () => { workspaceContext.GetFileWatcherService().OnBatchFileSystemChanged -= eh; };

        return mds;
    }

    private async Task BatchFileSystemChangedEventHandlerAsync(BatchFileSystemEventArgs eventArgs, IMetadataService mds)
    {
        var filePaths = eventArgs
            .FileSystemEvents
            .Select(fse => (PathEx?)fse.FullPath)
            .Where(x => x.HasValue)
            .Select(x => x.Value)
            .Where(x => x.IsTestContainer() || x.IsManifest() || x.IsRustFile())
            .Distinct();
        if (!filePaths.Any())
        {
            return;
        }

        await mds.OnWorkspaceUpdateAsync(filePaths, default);
    }
}

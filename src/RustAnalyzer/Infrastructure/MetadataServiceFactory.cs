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
        // Create a provider that fetches the current target context on demand
        // This allows MetadataService to use the correct execution context for remote targets
        TargetContextProvider targetContextProvider = () =>
        {
            var executionContext = WorkspaceContextAccessor?.GetCurrentExecutionContext();
            var pathMapper = WorkspaceContextAccessor?.GetCurrentPathMapper();

            if (executionContext == null)
            {
                return null;
            }

            return (executionContext, pathMapper);
        };

        var mds = new MetadataService(
            CargoService,
            (PathEx)workspaceContext.Location,
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

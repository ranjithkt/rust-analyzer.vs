using System;
using System.ComponentModel.Composition;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;

namespace KS.RustAnalyzer.Editor;

// NOTE:
// ExportFileContextProvider does NOT allow multiple attributes; export both Build and Clean
// in a single attribute. Use positional args (vs named args) for better cross-version tolerance.
[ExportFileContextProvider(
    ProviderType,
    ProviderPriority.Normal,
    new[] { typeof(string) },
    new[] { BuildContextTypes.BuildContextType, BuildContextTypes.CleanContextType, })]
public sealed class FileContextProviderFactory : IWorkspaceProviderFactory<IFileContextProvider>
{
    public const string ProviderType = "72D3FCEF-0001-4266-B8DD-D3ED06E35A2B";

    public static readonly Guid ProviderTypeGuid = new(ProviderType);

    [Import]
    public IBuildOutputSink OutputPane { get; set; }

    [Import]
    public ILogger L { get; set; }

    [Import]
    public ITelemetryService T { get; set; }

    [Import]
    public IToolchainService CargoService { get; set; }

    [Import]
    public IPreReqsCheckService PreReqs { get; set; }

    public IFileContextProvider CreateProvider(IWorkspace workspaceContext)
    {
        try
        {
            ActivityLog.LogInformation("rust-analyzer.vs", $"FileContextProviderFactory.CreateProvider Location='{workspaceContext?.Location}'");
        }
        catch
        {
            // Best-effort diagnostics only.
        }

        T.TrackEvent(
            "Create Context Provider",
            new[] { ("Location", workspaceContext.Location) });
        L.WriteLine("Creating {0}.", GetType().Name);

        return new FileContextProvider(workspaceContext.GetService<IMetadataService>(), CargoService, OutputPane, workspaceContext.GetService<ISettingsService>());
    }
}

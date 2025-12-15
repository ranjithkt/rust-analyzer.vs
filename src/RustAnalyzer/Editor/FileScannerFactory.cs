using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Indexing;

namespace KS.RustAnalyzer.Editor;

// NOTE:
// Use the simplest SDK-sample-style constructor for maximum compatibility across VS versions.
// (VS 2026 / 18.x has been sensitive to richer/named-arg overloads for some workspace exports.)
[ExportFileScanner(
    ProviderType,
    "RustAnalyzerFileScanner",
    new[] { Constants.ManifestFileExtension, Constants.RustFileExtension, },
    new[] { typeof(IReadOnlyCollection<FileDataValue>), typeof(IReadOnlyCollection<FileReferenceInfo>) })]
public class FileScannerFactory : IWorkspaceProviderFactory<IFileScanner>
{
    public const string ProviderType = "F5628EAD-0001-4683-B597-D8314B971ED6";

    public static readonly Guid ProviderTypeGuid = new(ProviderType);

    [Import]
    public ILogger L { get; set; }

    [Import]
    public ITelemetryService T { get; set; }

    [Import]
    public IPreReqsCheckService PreReqs { get; set; }

    public IFileScanner CreateProvider(IWorkspace workspaceContext)
    {
        try
        {
            ActivityLog.LogInformation("rust-analyzer.vs", $"FileScannerFactory.CreateProvider Location='{workspaceContext?.Location}'");
        }
        catch
        {
            // Best-effort diagnostics only.
        }

        T.TrackEvent(
            "Create Scanner",
            new[] { ("Location", workspaceContext.Location) });
        L.WriteLine("Creating {0}.", GetType().Name);

        return new FileScanner(workspaceContext.GetService<IMetadataService>());
    }
}

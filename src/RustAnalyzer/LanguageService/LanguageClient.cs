using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using StreamJsonRpc;

namespace KS.RustAnalyzer.LanguageService;

[ContentType(Constants.RustLanguageContentType)]
[Export(typeof(ILanguageClient))]
[RunOnContext(RunningContext.RunOnHost)]
public class LanguageClient : ILanguageClient, ILanguageClientCustomMessage2
{
    private RustAnalyzerMiddleLayer _middleLayer;
    private Process _remoteProcess;

    public event AsyncEventHandler<EventArgs> StartAsync;

    public event AsyncEventHandler<EventArgs> StopAsync;

    [Import]
    public IVsFolderWorkspaceService WorkspaceService { get; set; }

    [Import]
    public ILogger L { get; set; }

    [Import]
    public ITelemetryService T { get; set; }

    [Import]
    public IRlsInstallerService RADownloader { get; set; }

    [Import]
    public IWorkspaceContextAccessor WorkspaceContextAccessor { get; set; }

    public JsonRpc Rpc { get; set; }

    public string Name => "Rust Language Extension";

    public IEnumerable<string> ConfigurationSections
    {
        get
        {
            yield return Constants.ConfigurationSectionName;
        }
    }

    public object InitializationOptions => null;

    public IEnumerable<string> FilesToWatch => null;

    public object MiddleLayer => _middleLayer;

    public object CustomMessageTarget => null;

    public bool ShowNotificationOnInitializeFailed => true;

    public async Task<Connection> ActivateAsync(CancellationToken token)
    {
        var target = WorkspaceContextAccessor?.GetCurrentTarget();
        var executionContext = target?.GetExecutionContext();
        var pathMapper = target?.GetPathMapper();

        // Check if we should use remote execution
        if (executionContext != null && executionContext.Kind != TargetKind.Local)
        {
            return await ActivateRemoteAsync(executionContext, pathMapper, token).ConfigureAwait(false);
        }

        // Local execution (existing behavior)
        return await ActivateLocalAsync(token).ConfigureAwait(false);
    }

    private async Task<Connection> ActivateLocalAsync(CancellationToken token)
    {
        var rlsPath = await RADownloader.GetExePathAsync().ConfigureAwait(false);
        L.WriteLine("Starting rust-analyzer from path: {0}.", rlsPath);
        ProcessStartInfo info = new()
        {
            FileName = rlsPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = WorkspaceService.CurrentWorkspace?.Location ?? Path.GetDirectoryName(rlsPath),
        };

        Process process = new()
        {
            StartInfo = info
        };

        if (process.Start())
        {
            L.WriteLine("Done starting rust-analyzer from path. PID: {0}", process.Id);
            T.TrackEvent("rust-analyzer-start", ("Path", rlsPath));

            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        L.WriteLine("Error starting rust-analyzer from path.");
        T.TrackException(new InvalidOperationException(), new[] { ("Path", (string)rlsPath) });
        return null;
    }

    private async Task<Connection> ActivateRemoteAsync(IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken token)
    {
        var workspaceLocation = WorkspaceService.CurrentWorkspace?.Location;
        if (string.IsNullOrEmpty(workspaceLocation))
        {
            L.WriteError("Cannot start remote rust-analyzer: no workspace location.");
            return null;
        }

        var remoteWorkingDir = pathMapper.MapToRemote((PathEx)workspaceLocation);

        L.WriteLine("Starting remote rust-analyzer for target: {0}", executionContext.Kind);
        L.WriteLine("Remote working directory: {0}", remoteWorkingDir);

        try
        {
            // Create the middle layer for URI rewriting
            _middleLayer = new RustAnalyzerMiddleLayer(pathMapper, L);

            // Start rust-analyzer on the remote target
            var (inputStream, outputStream) = await executionContext.StartRustAnalyzerAsync(remoteWorkingDir, token).ConfigureAwait(false);

            L.WriteLine("Done starting remote rust-analyzer.");
            T.TrackEvent("rust-analyzer-start-remote", ("Target", executionContext.Kind.ToString()));

            return new Connection(outputStream, inputStream);
        }
        catch (Exception ex)
        {
            L.WriteError("Error starting remote rust-analyzer: {0}", ex.Message);
            T.TrackException(ex, new[] { ("Target", executionContext.Kind.ToString()) });
            return null;
        }
    }

    public async Task OnLoadedAsync()
    {
        if (StartAsync != null)
        {
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }
    }

    public async Task StopServerAsync()
    {
        if (StopAsync != null)
        {
            await StopAsync.InvokeAsync(this, EventArgs.Empty);
        }
    }

    public Task OnServerInitializedAsync()
    {
        return Task.CompletedTask;
    }

    public Task AttachForCustomMessageAsync(JsonRpc rpc)
    {
        Rpc = rpc;

        return Task.CompletedTask;
    }

    public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        string message = "Oh no! rust-analyzer failed to activate, now we can't test LSP! :(";
        string exception = initializationState.InitializationException?.ToString() ?? string.Empty;
        message = $"{message}\n {exception}";

        L.WriteLine(message);
        T.TrackException(initializationState.InitializationException);

        var failureContext = new InitializationFailureContext()
        {
            FailureMessage = message,
        };

        return Task.FromResult(failureContext);
    }
}

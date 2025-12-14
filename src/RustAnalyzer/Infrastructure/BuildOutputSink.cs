using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using EnsureThat;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace KS.RustAnalyzer.Infrastructure;

[Export(typeof(IBuildOutputSink))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class BuildOutputSink : IBuildOutputSink
{
    private static readonly Guid BuildOutputPaneGuid = VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid;
    private static readonly StringBuildMessagePreprocessor SbmPreprocessor = new();
    private IVsOutputWindowPane _buildOutputPane;

    [Import]
    private ITelemetryService T { get; set; }

    [Import]
    private SVsServiceProvider ServiceProvider { get; set; }

    public void WriteLine(PathEx rootPath, Func<BuildMessage, Task> buildOutputTaskReporter, BuildMessage message)
    {
        try
        {
            RustAnalyzerPackage.JTF.RunAsync(async () =>
            {
                try
                {
                    await RustAnalyzerPackage.JTF.SwitchToMainThreadAsync();
                    Initialize();
                    _buildOutputPane.Activate();

                    if (message is StringBuildMessage sm)
                    {
                        if (string.IsNullOrEmpty(sm.Message))
                        {
                            return;
                        }

                        foreach (var msg in SbmPreprocessor.Preprocess(rootPath, sm.Message))
                        {
                            var hr = _buildOutputPane.OutputStringThreadSafe(msg + Environment.NewLine);
                            Ensure.That(ErrorHandler.Succeeded(hr));
                        }
                    }
                    else if (message is DetailedBuildMessage bm)
                    {
                        // Report to Error List - VS's VsBuildMessagesManager will also output to Output Window
                        // so we don't need to call OutputStringThreadSafe ourselves (that would cause duplicates)
                        await buildOutputTaskReporter(bm);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(message));
                    }
                }
                catch (Exception ex)
                {
                    // #region agent log
                    try { System.IO.File.AppendAllText(@"c:\Repos3\rust-analyzer.vs\.cursor\debug.log", $"{{\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"loc\":\"BuildOutputSink:WriteLineError\",\"hyp\":\"H5\",\"err\":\"{ex.GetType().Name}: {ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ")}\"}}\n"); } catch { }
                    // #endregion
                    throw;
                }
            }).FireAndForget();
        }
        catch (Exception e)
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"c:\Repos3\rust-analyzer.vs\.cursor\debug.log", $"{{\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"loc\":\"BuildOutputSink:WriteLineOuterError\",\"hyp\":\"H5\",\"err\":\"{e.GetType().Name}: {e.Message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ")}\"}}\n"); } catch { }
            // #endregion
            T.TrackException(e);
        }
    }

    public void Clear()
    {
        try
        {
            RustAnalyzerPackage.JTF.RunAsync(async () =>
            {
                await RustAnalyzerPackage.JTF.SwitchToMainThreadAsync();
                Initialize();
                _buildOutputPane.Clear();
            }).FireAndForget();
        }
        catch (Exception e)
        {
            T.TrackException(e);
        }
    }

    private void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (IsInitialized())
        {
            return;
        }

        _buildOutputPane = InitializeOutputPane(Vsix.Name, BuildOutputPaneGuid);
    }

    private IVsOutputWindowPane InitializeOutputPane(string title, Guid paneId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var outputWindow = ServiceProvider.GetService<SVsOutputWindow, IVsOutputWindow>();

        // Try to get the workspace pane if it has already been registered
        var hr = outputWindow.GetPane(paneId, out var lazyOutputPane);

        // If the workspace pane has not been registered before, create it
        if (lazyOutputPane == null || ErrorHandler.Failed(hr))
        {
            if (ErrorHandler.Failed(outputWindow.CreatePane(paneId, title, fInitVisible: 1, fClearWithSolution: 1)) ||
                ErrorHandler.Failed(outputWindow.GetPane(paneId, out lazyOutputPane)))
            {
                return null;
            }
        }

        return lazyOutputPane;
    }

    private bool IsInitialized()
    {
        return _buildOutputPane != null;
    }
}

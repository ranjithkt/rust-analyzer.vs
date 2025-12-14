using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using ShellInterop = Microsoft.VisualStudio.Shell.Interop;
using WorkspaceBuildMessage = Microsoft.VisualStudio.Workspace.Build.BuildMessage;

namespace KS.RustAnalyzer.Shell;

using ToolchainOperation = System.Func<KS.RustAnalyzer.TestAdapter.Common.IToolchainService, System.Func<KS.RustAnalyzer.TestAdapter.Common.BuildTargetInfo, KS.RustAnalyzer.TestAdapter.Common.BuildOutputSinks, System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>>;
using RemoteToolchainOperation = System.Func<KS.RustAnalyzer.TestAdapter.Common.IToolchainService, System.Func<KS.RustAnalyzer.TestAdapter.Common.BuildTargetInfo, KS.RustAnalyzer.TestAdapter.Common.BuildOutputSinks, KS.RustAnalyzer.Remote.IExecutionContext, KS.RustAnalyzer.Remote.IPathMapper, System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>>;

public sealed class CmdServices
{
    private IComponentModel2 _mef;
    private ILogger _l;
    private ShellInterop.IVsSolution _solution;
    private ShellInterop.IVsDebugger _debugger;
    private ShellInterop.IVsUIShell _vsUIShell;
    private IToolchainService _toolchainService;
    private ISettingsService _settingsService;
    private IBuildOutputSink _buildOutputSink;
    private IVsFolderWorkspaceService _folderWorkspaceService;
    private IWorkspaceContextAccessor _workspaceContextAccessor;

    public CmdServices(Func<AsyncPackage> getPackage)
    {
        GetPackage = getPackage;
    }

    public Func<AsyncPackage> GetPackage { get; }

    public ShellInterop.IVsUIShell VsUIShell => _vsUIShell ??= GetPackage().GetService<ShellInterop.SVsUIShell, ShellInterop.IVsUIShell>(false);

    public IBuildOutputSink BuildOutputSink => _buildOutputSink ??= Mef?.GetService<IBuildOutputSink>();

    public IComponentModel2 Mef => _mef ??= GetPackage().GetService<SComponentModel, IComponentModel2>(false);

    public ILogger L => _l ??= Mef?.GetService<ILogger>();

    public ShellInterop.IVsSolution Solution => _solution ??= GetPackage().GetService<ShellInterop.SVsSolution, ShellInterop.IVsSolution>(false);

    public ShellInterop.IVsDebugger Debugger => _debugger ??= GetPackage().GetService<ShellInterop.SVsShellDebugger, ShellInterop.IVsDebugger>(false);

    public IToolchainService ToolchainService => _toolchainService ??= Mef?.GetService<IToolchainService>();

    public ISettingsService SettingsService => _settingsService ??= FolderWorkspaceService?.CurrentWorkspace?.GetService<ISettingsService>();

    public IVsFolderWorkspaceService FolderWorkspaceService => _folderWorkspaceService ??= Mef?.GetService<IVsFolderWorkspaceService>();

    public IWorkspaceContextAccessor WorkspaceContextAccessor => _workspaceContextAccessor ??= Mef?.GetService<IWorkspaceContextAccessor>();

    private readonly IMapper _buildMessageMapper = new MapperConfiguration(cfg => cfg.CreateMap<DetailedBuildMessage, WorkspaceBuildMessage>()).CreateMapper();

    public async Task ExecuteToolchainOperationAsync(ToolchainOperation op, PathEx manifestPath, Func<Options, string> getOpts)
    {
        var profile = Mef.GetProfile(manifestPath);
        var opts = await Options.GetLiveInstanceAsync();

        var bms = await FolderWorkspaceService.CurrentWorkspace.GetBuildMessageServiceAsync();
        var bti = new BuildTargetInfo
        {
            ManifestPath = manifestPath,
            AdditionalBuildArgs = getOpts(opts),
            Profile = profile,
            WorkspaceRoot = manifestPath.GetDirectoryName(),
        };
        var bos = new BuildOutputSinks { OutputSink = BuildOutputSink, BuildActionProgressReporter = bm => bms.ReportBuildMessages(new[] { _buildMessageMapper.Map<WorkspaceBuildMessage>(bm) }) };

        // Get the current target system
        var currentTarget = WorkspaceContextAccessor?.GetCurrentTarget();
        var executionContext = currentTarget?.GetExecutionContext();
        var pathMapper = currentTarget?.GetPathMapper();

        // Log the target for debugging
        L?.WriteLine("[CmdServices] Executing toolchain operation on target: {0} (Kind: {1})", currentTarget?.DisplayName ?? "Local", currentTarget?.Kind.ToString() ?? "Local");

        // Map the operation to the remote-aware version
        RemoteToolchainOperation remoteOp = MapToRemoteOperation(op);

        // Execute with execution context and path mapper (null for local targets)
        await remoteOp(ToolchainService)(bti, bos, executionContext, pathMapper, CancellationToken.None);
    }

    /// <summary>
    /// Maps a local ToolchainOperation to its remote-aware equivalent.
    /// </summary>
    private static RemoteToolchainOperation MapToRemoteOperation(ToolchainOperation op)
    {
        // Create a test service instance to get the method reference
        // This is a bit hacky but allows us to determine which operation was requested
        return its =>
        {
            var localFunc = op(its);

            // Match by comparing the delegate target
            if (localFunc.Method.Name == nameof(IToolchainService.BuildAsync))
            {
                return its.BuildAsync;
            }
            else if (localFunc.Method.Name == nameof(IToolchainService.CleanAsync))
            {
                return its.CleanAsync;
            }
            else if (localFunc.Method.Name == nameof(IToolchainService.RunClippyAsync))
            {
                return its.RunClippyAsync;
            }
            else if (localFunc.Method.Name == nameof(IToolchainService.RunFmtAsync))
            {
                return its.RunFmtAsync;
            }
            else
            {
                // Fallback: wrap the local operation (won't support remote)
                return (bti, bos, ec, pm, ct) => localFunc(bti, bos, ct);
            }
        };
    }

    public IEnumerable<PathEx> GetSelectedItems()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return VsCommon.GetSelectedItems()
            .Select(si => (PathEx?)si.GetFullName())
            .Where(p => p.HasValue).Select(p => p.Value);
    }

    public PathEx? GetWorkspaceRoot()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string workspaceRoot = null;
        if (ErrorHandler.Failed(Solution?.GetSolutionInfo(out workspaceRoot, out var _, out var _) ?? VSConstants.E_FAIL))
        {
            L?.WriteError("Unable to determine workspace root.");
        }

        return (PathEx?)workspaceRoot;
    }

    public bool IsIdeInDesignMode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dbgMode = new ShellInterop.DBGMODE[1];
        if (ErrorHandler.Failed(Debugger?.GetMode(dbgMode) ?? VSConstants.E_FAIL))
        {
            L.WriteError("Unable to determine debugger mode.");
            return false;
        }

        return dbgMode[0] == ShellInterop.DBGMODE.DBGMODE_Design;
    }
}

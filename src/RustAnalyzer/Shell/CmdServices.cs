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

        // For SSH targets with local Windows folders, use LocalSync mode
        if (currentTarget != null && currentTarget.Kind == TargetKind.Ssh && currentTarget is SshTargetSystem sshTarget)
        {
            // Set the workspace path to enable automatic mode detection
            sshTarget.SetWorkspacePath(manifestPath.GetDirectoryName());
            pathMapper = sshTarget.GetPathMapper();

            L?.WriteLine("[CmdServices] SSH target mode: {0}, PathMapper type: {1}",
                sshTarget.WorkspaceMode, pathMapper?.GetType().Name ?? "null");
        }
        // For WSL targets, validate path compatibility
        else if (currentTarget != null && currentTarget.Kind == TargetKind.Wsl && pathMapper != null)
        {
            if (!pathMapper.IsPathForTarget(manifestPath))
            {
                var errorMessage = GetRemotePathMismatchMessage(currentTarget, manifestPath);
                await VsCommon.ShowMessageBoxAsync(errorMessage, "Remote Build Configuration Error");
                return;
            }
        }
        // Fallback: If no target is set but the manifest path is a WSL path, auto-detect WSL
        else if ((currentTarget == null || currentTarget.Kind == TargetKind.Local) &&
                 WslPathMapper.TryGetDistroName(manifestPath, out var detectedDistro))
        {
            L?.WriteLine("[CmdServices] Auto-detecting WSL target for distro: {0}", detectedDistro);
            executionContext = new WslExecutionContext(detectedDistro);
            // Use the same path format (wsl$ vs wsl.localhost) as the manifest path
            pathMapper = WslPathMapper.CreateMatchingFormat(detectedDistro, (string)manifestPath);
        }

        // Map the operation to the remote-aware version
        RemoteToolchainOperation remoteOp = MapToRemoteOperation(op);

        // Execute with execution context and path mapper (null for local targets)
        await remoteOp(ToolchainService)(bti, bos, executionContext, pathMapper, CancellationToken.None);
    }

    /// <summary>
    /// Gets a user-friendly error message for remote path mismatch (used for WSL).
    /// Note: SSH uses LocalSync mode, so this is primarily for WSL.
    /// </summary>
    private static string GetRemotePathMismatchMessage(ITargetSystem target, PathEx localPath)
    {
        var targetName = target.DisplayName;

        if (target.Kind == TargetKind.Wsl)
        {
            return $"Cannot build on WSL target '{targetName}'.\n\n" +
                   $"The workspace '{localPath.GetDirectoryName()}' is a local Windows folder, " +
                   $"but the selected target is WSL.\n\n" +
                   $"To build on WSL, you can:\n" +
                   $"1. Open the project from WSL using a UNC path like:\n" +
                   $"   \\\\wsl$\\{targetName}\\path\\to\\project\n" +
                   $"2. Or switch to 'Local Machine' target to build locally.";
        }

        return $"Cannot build on {target.Kind} target '{targetName}'.\n\n" +
               $"The workspace path is not compatible with the selected target.";
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

    public PathEx GetWorkspaceRoot()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // First, try Open Folder workspace (IVsFolderWorkspaceService)
        var currentWorkspace = FolderWorkspaceService?.CurrentWorkspace;
        if (currentWorkspace != null)
        {
            var location = currentWorkspace.Location;
            if (!string.IsNullOrEmpty(location))
            {
                return (PathEx)location;
            }
        }

        // Fallback: try solution-based workspace
        string workspaceRoot = null;
        if (ErrorHandler.Failed(Solution?.GetSolutionInfo(out workspaceRoot, out var _, out var _) ?? VSConstants.E_FAIL))
        {
            L?.WriteError("Unable to determine workspace root.");
        }

        return (PathEx)workspaceRoot;
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

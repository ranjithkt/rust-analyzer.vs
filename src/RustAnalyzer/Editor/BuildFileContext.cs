using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.Shell;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Workspace.Build;
using WorkspaceBuildMessage = Microsoft.VisualStudio.Workspace.Build.BuildMessage;

namespace KS.RustAnalyzer.Editor;

/// <summary>
/// Delegate for remote-aware toolchain operations.
/// </summary>
public delegate Task<bool> RemoteToolchainFunc(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);

public class BuildFileContext : BuildFileContextBase
{
    public BuildFileContext(IToolchainService cs, BuildTargetInfo bti, IBuildOutputSink outputPane, IWorkspaceContextAccessor contextAccessor)
        : base(bti, outputPane, cs.BuildAsync, contextAccessor)
    {
    }
}

public class CleanFileContext : BuildFileContextBase
{
    public CleanFileContext(IToolchainService cs, BuildTargetInfo bti, IBuildOutputSink outputPane, IWorkspaceContextAccessor contextAccessor)
        : base(bti, outputPane, cs.CleanAsync, contextAccessor)
    {
    }
}

public abstract class BuildFileContextBase : IBuildFileContext
{
    private readonly RemoteToolchainFunc _commandFunc;
    private readonly IMapper _buildMessageMapper = new MapperConfiguration(cfg => cfg.CreateMap<DetailedBuildMessage, WorkspaceBuildMessage>()).CreateMapper();
    private readonly IBuildOutputSink _outputPane;
    private readonly IWorkspaceContextAccessor _contextAccessor;

    public BuildFileContextBase(BuildTargetInfo bti, IBuildOutputSink outputPane, RemoteToolchainFunc commandFunc, IWorkspaceContextAccessor contextAccessor)
    {
        BuildTargetInfo = bti;
        _outputPane = outputPane;
        _commandFunc = commandFunc;
        _contextAccessor = contextAccessor;
    }

    public string BuildConfiguration => BuildTargetInfo.Profile;

    public BuildTargetInfo BuildTargetInfo { get; }

    public async Task<bool> ExecuteBuildAsync(IBuildActionProgress progress, CancellationToken cancellationToken)
    {
        var bos = new BuildOutputSinks
        {
            BuildActionProgressReporter = bm => progress.ReportAsync(_buildMessageMapper.Map<WorkspaceBuildMessage>(bm), null),
            OutputSink = _outputPane,
        };

        // Get the current target system for remote execution
        var currentTarget = _contextAccessor?.GetCurrentTarget();
        var executionContext = currentTarget?.GetExecutionContext();
        var pathMapper = currentTarget?.GetPathMapper();

        // Validate that the workspace path can be mapped to the target
        if (currentTarget != null && currentTarget.Kind != TargetKind.Local && pathMapper != null)
        {
            if (!pathMapper.IsPathForTarget(BuildTargetInfo.ManifestPath))
            {
                var errorMessage = GetRemotePathMismatchMessage(currentTarget, BuildTargetInfo.ManifestPath);
                await VsCommon.ShowMessageBoxAsync(errorMessage, "Remote Build Configuration Error");
                return false;
            }
        }

        await RlsUpdatedNotification.ShowAsync();
        return await _commandFunc(BuildTargetInfo, bos, executionContext, pathMapper, cancellationToken);
    }

    /// <summary>
    /// Gets a user-friendly error message for remote path mismatch.
    /// </summary>
    private static string GetRemotePathMismatchMessage(ITargetSystem target, PathEx localPath)
    {
        var targetName = target.DisplayName;

        if (target.Kind == TargetKind.Ssh)
        {
            return $"Cannot build on SSH target '{targetName}'.\n\n" +
                   $"The workspace '{localPath.GetDirectoryName()}' is a local Windows folder, " +
                   $"but the selected target is an SSH remote host.\n\n" +
                   $"To build on an SSH target, you need to:\n" +
                   $"1. Open the project from the remote host using Remote File Explorer\n" +
                   $"   (View > Other Windows > Remote File Explorer), or\n" +
                   $"2. Switch to 'Local Machine' target to build locally.\n\n" +
                   $"Note: The code must exist on the remote machine for SSH builds.";
        }
        else if (target.Kind == TargetKind.Wsl)
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
}

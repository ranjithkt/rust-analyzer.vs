using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
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

        await RlsUpdatedNotification.ShowAsync();
        return await _commandFunc(BuildTargetInfo, bos, executionContext, pathMapper, cancellationToken);
    }
}

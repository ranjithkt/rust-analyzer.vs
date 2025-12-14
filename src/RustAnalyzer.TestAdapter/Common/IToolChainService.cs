using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Cargo;

namespace KS.RustAnalyzer.TestAdapter.Common;

public interface IToolchainService
{
    PathEx GetCargoExePath();

    Task<bool> BuildAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct);

    /// <summary>
    /// Builds the project using the specified execution context and path mapper.
    /// </summary>
    /// <param name="bti">Build target info.</param>
    /// <param name="bos">Build output sinks.</param>
    /// <param name="executionContext">Execution context for the target system.</param>
    /// <param name="pathMapper">Path mapper for the target system.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if build succeeded.</returns>
    Task<bool> BuildAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);

    Task<bool> CleanAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct);

    /// <summary>
    /// Cleans the project using the specified execution context.
    /// </summary>
    Task<bool> CleanAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);

    Task<bool> RunClippyAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct);

    /// <summary>
    /// Runs Clippy using the specified execution context.
    /// </summary>
    Task<bool> RunClippyAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);

    Task<bool> RunFmtAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct);

    /// <summary>
    /// Runs Fmt using the specified execution context.
    /// </summary>
    Task<bool> RunFmtAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);

    Task<Workspace> GetWorkspaceAsync(PathEx manifestPath, CancellationToken ct);

    /// <summary>
    /// Gets workspace metadata using the specified execution context and path mapper.
    /// </summary>
    /// <param name="manifestPath">Path to Cargo.toml (VS-visible path).</param>
    /// <param name="executionContext">Execution context for the target system.</param>
    /// <param name="pathMapper">Path mapper for the target system.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Workspace with properly mapped paths.</returns>
    Task<Workspace> GetWorkspaceAsync(PathEx manifestPath, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);

    /// <summary>
    /// NOTE: This is shameful. Cannot pull in IAsyncEnumerable without getting into dependency hell.
    /// </summary>
    Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoAsync(PathEx testContainerPath, string profile, CancellationToken ct);

    /// <summary>
    /// Gets test suite info using the specified execution context.
    /// </summary>
    Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoAsync(PathEx testContainerPath, string profile, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct);
}

public sealed class TestContainer
{
    public PathEx ThisPath { get; set; }

    public PathEx Manifest { get; set; }

    public PathEx TargetDir { get; set; }

    public string AdditionalTestDiscoveryArguments { get; set; }

    public string AdditionalTestExecutionArguments { get; set; }

    public string TestExecutionEnvironment { get; set; }

    public string Profile { get; set; }

    public PathEx[] TestExes { get; set; }
}

public sealed class BuildTargetInfo
{
    public PathEx WorkspaceRoot { get; set; }

    public PathEx ManifestPath { get; set; }

    public string Profile { get; set; }

    public string AdditionalBuildArgs { get; set; } = string.Empty;

    public string AdditionalTestDiscoveryArguments { get; set; } = string.Empty;

    public string AdditionalTestExecutionArguments { get; set; } = string.Empty;

    public string TestExecutionEnvironment { get; set; } = string.Empty;
}

public sealed class BuildOutputSinks
{
    public Func<BuildMessage, Task> BuildActionProgressReporter { get; set; }

    public IBuildOutputSink OutputSink { get; set; }
}

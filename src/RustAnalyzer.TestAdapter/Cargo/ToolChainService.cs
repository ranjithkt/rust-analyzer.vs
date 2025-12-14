using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;
using Newtonsoft.Json;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

[Export(typeof(IToolchainService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class ToolchainService : IToolchainService
{
    private static readonly Regex TestExecutablePathCracker = new(@"^\s*Executable( unittests)? (.*) \((.*\\(.*)\-[\da-f]{16}.exe)\)$$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for remote (Linux) test executable paths
    private static readonly Regex TestExecutablePathCrackerRemote = new(@"^\s*Executable( unittests)? (.*) \((.*/[^/]*-[\da-f]{16})\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TL _tl;
    private readonly ISshFileSyncService _syncService;

    [ImportingConstructor]
    public ToolchainService([Import] ITelemetryService t, [Import] ILogger l)
    {
        _tl = new TL
        {
            T = t,
            L = l,
        };

        // Create sync service for SSH file synchronization
        _syncService = new SshFileSyncService();
    }

    /// <summary>
    /// Not finding cargo.exe is a catastrophic error. Hence in prereq checks.
    /// </summary>
    public PathEx GetCargoExePath()
    {
        var cargoExePath = (PathEx)Constants.CargoExe.FindInPath();

        _tl.L.WriteLine("... using {0} from '{1}'.", Constants.CargoExe, cargoExePath);
        return cargoExePath;
    }

    public Task<bool> BuildAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
        return BuildAsync(bti, bos, null, null, ct);
    }

    public async Task<bool> BuildAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        bool success;

        if (executionContext != null && executionContext.Kind != TargetKind.Local)
        {
            // For SSH targets in LocalSync mode, sync files first
            if (executionContext.Kind == TargetKind.Ssh && pathMapper is LocalToRemoteSyncMapper syncMapper)
            {
                _tl.L.WriteLine("[SSH] LocalSync mode detected. Syncing files to remote...");
                bos.OutputSink?.Clear();

                // Create redirector to write sync status to output window
                var syncRedirector = new BuildOutputRedirector(bos.OutputSink, bti.ManifestPath.GetDirectoryName(), null, null);
                syncRedirector.WriteLineWithoutProcessing(string.Empty);
                syncRedirector.WriteLineWithoutProcessing("==== SSH Sync: Started (with dependencies) ====");
                syncRedirector.WriteLineWithoutProcessing($"       Local : {syncMapper.LocalRoot}");
                syncRedirector.WriteLineWithoutProcessing($"      Remote : {syncMapper.RemoteRoot}");
                syncRedirector.WriteLineWithoutProcessing($"  Connection : {syncMapper.ConnectionInfo.DisplayName}");
                syncRedirector.WriteLineWithoutProcessing($"  Note: Path dependencies from Cargo.toml will be synced automatically");
                syncRedirector.WriteLineWithoutProcessing(string.Empty);

                // Sync project AND all path dependencies from Cargo.toml
                var syncResult = await _syncService.SyncProjectWithDependenciesAsync(
                    syncMapper.LocalRoot,
                    syncMapper.RemoteRoot,
                    syncMapper.ConnectionInfo,
                    progress: null,
                    ct).ConfigureAwait(false);

                if (!syncResult.Success)
                {
                    _tl.L.WriteLine("[SSH] Sync failed: {0}", syncResult.ErrorMessage);
                    syncRedirector.WriteLineWithoutProcessing($"==== SSH Sync: Failed ====");
                    syncRedirector.WriteLineWithoutProcessing($"  Error: {syncResult.ErrorMessage}");
                    syncRedirector.WriteLineWithoutProcessing(string.Empty);
                    return false;
                }

                _tl.L.WriteLine("[SSH] Sync completed: {0} files synced (including dependencies), {1} skipped, {2} bytes in {3}ms",
                    syncResult.FilesSynced, syncResult.FilesSkipped, syncResult.BytesTransferred, syncResult.Duration.TotalMilliseconds);
                syncRedirector.WriteLineWithoutProcessing($"==== SSH Sync: Completed (with dependencies) ====");
                syncRedirector.WriteLineWithoutProcessing($"  Files synced: {syncResult.FilesSynced} (including path dependencies)");
                syncRedirector.WriteLineWithoutProcessing($"  Files skipped: {syncResult.FilesSkipped}");
                syncRedirector.WriteLineWithoutProcessing($"  Bytes transferred: {syncResult.BytesTransferred}");
                syncRedirector.WriteLineWithoutProcessing($"  Duration: {syncResult.Duration.TotalMilliseconds:F0}ms");
                syncRedirector.WriteLineWithoutProcessing(string.Empty);
            }

            // Remote execution
            success = await ExecuteRemoteOperationAsync(
                "build",
                bti.ManifestPath,
                new[] { "build", "--manifest-path", GetRemoteManifestPath(bti.ManifestPath, pathMapper), "--profile", bti.Profile, "--message-format", "json" }
                    .Concat(ParseAdditionalArgs(bti.AdditionalBuildArgs)).ToArray(),
                bti.Profile,
                bos.OutputSink,
                bos.BuildActionProgressReporter,
                x => BuildJsonOutputParser.Parse(bti.WorkspaceRoot, x, _tl, pathMapper),
                executionContext,
                pathMapper,
                ct).ConfigureAwait(false);
        }
        else
        {
            // Local execution (existing behavior)
            success = await ExecuteOperationAsync(
                "build",
                bti.ManifestPath,
                arguments: $"build --manifest-path \"{bti.ManifestPath}\" --profile {bti.Profile} --message-format json {bti.AdditionalBuildArgs}",
                profile: bti.Profile,
                outputPane: bos.OutputSink,
                buildMessageReporter: bos.BuildActionProgressReporter,
                outputPreprocessor: x => BuildJsonOutputParser.Parse(bti.WorkspaceRoot, x, _tl),
                ts: _tl.T,
                l: _tl.L,
                ct: ct).ConfigureAwait(false);
        }

        if (success)
        {
            var w = await GetWorkspaceAsync(bti.ManifestPath, executionContext, pathMapper, ct).ConfigureAwait(false);
            var testContainers = w.Packages.SelectMany(p => p.GetTestContainers(bti.Profile));
            w.TargetDirectory.MakeProfilePath(bti.Profile).CleanTestContainers(testContainers.Select(x => x.Container));
            var tasks = testContainers
                .Select(x => x.Container.WriteTestContainerAsync(x.Target.Parent.ManifestPath, w.TargetDirectory, bti.AdditionalTestDiscoveryArguments, bti.AdditionalTestExecutionArguments, bti.TestExecutionEnvironment, bti.Profile, Array.Empty<PathEx>(), ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return success;
    }

    public Task<bool> CleanAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
        return CleanAsync(bti, bos, null, null, ct);
    }

    public Task<bool> CleanAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        if (executionContext != null && executionContext.Kind != TargetKind.Local)
        {
            return ExecuteRemoteOperationAsync(
                "clean",
                bti.ManifestPath,
                new[] { "clean", "--manifest-path", GetRemoteManifestPath(bti.ManifestPath, pathMapper), "--profile", bti.Profile },
                bti.Profile,
                bos.OutputSink,
                bos.BuildActionProgressReporter,
                OutputPreprocessorForCargoToolsWithoutJsonOutput,
                executionContext,
                pathMapper,
                ct);
        }

        return ExecuteOperationAsync(
            "clean",
            bti.ManifestPath,
            arguments: $"clean --manifest-path \"{bti.ManifestPath}\" --profile {bti.Profile}",
            profile: bti.Profile,
            outputPane: bos.OutputSink,
            buildMessageReporter: bos.BuildActionProgressReporter,
            outputPreprocessor: OutputPreprocessorForCargoToolsWithoutJsonOutput,
            ts: _tl.T,
            l: _tl.L,
            ct: ct);
    }

    public Task<bool> RunClippyAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
        return RunClippyAsync(bti, bos, null, null, ct);
    }

    public Task<bool> RunClippyAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        if (executionContext != null && executionContext.Kind != TargetKind.Local)
        {
            return ExecuteRemoteOperationAsync(
                "Clippy",
                bti.ManifestPath,
                new[] { "clippy", "--manifest-path", GetRemoteManifestPath(bti.ManifestPath, pathMapper), "--profile", bti.Profile }
                    .Concat(ParseAdditionalArgs(bti.AdditionalBuildArgs)).ToArray(),
                bti.Profile,
                bos.OutputSink,
                bos.BuildActionProgressReporter,
                OutputPreprocessorForCargoToolsWithoutJsonOutput,
                executionContext,
                pathMapper,
                ct);
        }

        return ExecuteOperationAsync(
            "Clippy",
            bti.ManifestPath,
            arguments: $"clippy --manifest-path \"{bti.ManifestPath}\" --profile {bti.Profile} {bti.AdditionalBuildArgs}",
            profile: bti.Profile,
            outputPane: bos.OutputSink,
            buildMessageReporter: bos.BuildActionProgressReporter,
            outputPreprocessor: OutputPreprocessorForCargoToolsWithoutJsonOutput,
            ts: _tl.T,
            l: _tl.L,
            ct: ct);
    }

    public Task<bool> RunFmtAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
        return RunFmtAsync(bti, bos, null, null, ct);
    }

    public Task<bool> RunFmtAsync(BuildTargetInfo bti, BuildOutputSinks bos, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        if (executionContext != null && executionContext.Kind != TargetKind.Local)
        {
            return ExecuteRemoteOperationAsync(
                "Fmt",
                bti.ManifestPath,
                new[] { "fmt", "--manifest-path", GetRemoteManifestPath(bti.ManifestPath, pathMapper) }
                    .Concat(ParseAdditionalArgs(bti.AdditionalBuildArgs)).ToArray(),
                bti.Profile,
                bos.OutputSink,
                bos.BuildActionProgressReporter,
                OutputPreprocessorForCargoToolsWithoutJsonOutput,
                executionContext,
                pathMapper,
                ct);
        }

        return ExecuteOperationAsync(
            "Fmt",
            bti.ManifestPath,
            arguments: $"fmt --manifest-path \"{bti.ManifestPath}\" {bti.AdditionalBuildArgs}",
            profile: bti.Profile,
            outputPane: bos.OutputSink,
            buildMessageReporter: bos.BuildActionProgressReporter,
            outputPreprocessor: OutputPreprocessorForCargoToolsWithoutJsonOutput,
            ts: _tl.T,
            l: _tl.L,
            ct: ct);
    }

    public Task<Workspace> GetWorkspaceAsync(PathEx manifestPath, CancellationToken ct)
    {
        return GetWorkspaceAsync(manifestPath, null, null, ct);
    }

    public async Task<Workspace> GetWorkspaceAsync(PathEx manifestPath, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        try
        {
            if (executionContext != null && executionContext.Kind != TargetKind.Local)
            {
                // Remote execution - use execution context
                var remoteManifestPath = GetRemoteManifestPath(manifestPath, pathMapper);
                var args = new[] { "metadata", "--no-deps", "--format-version", "1", "--manifest-path", remoteManifestPath, "--offline" };
                var remoteWorkingDir = pathMapper.MapToRemote(manifestPath.GetDirectoryName());

                var result = await executionContext.ExecuteAndCaptureAsync(
                    executionContext.CargoCommand,
                    args,
                    remoteWorkingDir,
                    ct).ConfigureAwait(false);

                var json = string.Join(string.Empty, result);
                var rawWorkspace = JsonConvert.DeserializeObject<RawWorkspace>(json);
                var factory = new WorkspaceFactory();
                var w = factory.Create(rawWorkspace, pathMapper);
                return AddRootPackageIfNecessary(w, manifestPath);
            }
            else
            {
                // Local execution (existing behavior)
                var cargoFullPath = GetCargoExePath();

                using var proc = await ProcessRunner.RunWithLogging(
                    cargoFullPath,
                    new[] { "metadata", "--no-deps", "--format-version", "1", "--manifest-path", manifestPath, "--offline" },
                    cargoFullPath.GetDirectoryName(),
                    ImmutableDictionary<string, string>.Empty,
                    ct,
                    _tl.L).ConfigureAwait(false);
                var w = JsonConvert.DeserializeObject<Workspace>(string.Join(string.Empty, proc.StandardOutputLines));
                return AddRootPackageIfNecessary(w, manifestPath);
            }
        }
        catch (Exception e)
        {
            _tl.L.WriteLine("Unable to obtain metadata for file {0}. Ex: {1}", manifestPath, e);
            if (!e.IsCargo101Error())
            {
                _tl.T.TrackException(e);
            }

            throw;
        }
    }

    public Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoAsync(PathEx testContainerPath, string profile, CancellationToken ct)
    {
        return GetTestSuiteInfoAsync(testContainerPath, profile, null, null, ct);
    }

    public async Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoAsync(PathEx testContainerPath, string profile, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        var tc = await testContainerPath.ReadTestContainerAsync(ct).ConfigureAwait(false);
        _tl.L.WriteLine($"GetTestSuiteInfoAsync: Finding tests for {testContainerPath}");

        try
        {
            var workingDir = tc.Manifest.GetDirectoryName();

            if (executionContext != null && executionContext.Kind != TargetKind.Local)
            {
                // Remote execution
                return await GetTestSuiteInfoRemoteAsync(tc, testContainerPath, profile, executionContext, pathMapper, ct).ConfigureAwait(false);
            }
            else
            {
                // Local execution (existing behavior)
                return await GetTestSuiteInfoLocalAsync(tc, testContainerPath, profile, workingDir, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _tl.L.WriteLine("Unable to obtain metadata for file {0}. Ex: {1}", tc.Manifest, e);
            if (!e.IsCargo101Error())
            {
                _tl.T.TrackException(e);
            }

            throw;
        }
    }

    private async Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoLocalAsync(TestContainer tc, PathEx testContainerPath, string profile, PathEx workingDir, CancellationToken ct)
    {
        var cargoFullPath = GetCargoExePath();
        var cargoVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("cargo", "--version", workingDir, ct).ConfigureAwait(false);
        _tl.L.WriteLine($"Using: {cargoVersion}");
        var rustcVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("test", "--version", workingDir, ct).ConfigureAwait(false);
        _tl.L.WriteLine($"Using: {rustcVersion}");

        var args = new[] { "test", "--no-run", "--manifest-path", tc.Manifest, "--profile", profile }
            .Concat(tc.AdditionalTestDiscoveryArguments.FromNullSeparatedArray())
            .ToArray();

        _tl.T.TrackEvent("GetTestSuiteInfoAsync", ("TestContainer", testContainerPath), ("Profile", profile), ("Args", string.Join("|", args)));

        using var proc = await ProcessRunner.RunWithLogging(cargoFullPath, args, workingDir, ImmutableDictionary<string, string>.Empty, ct, _tl.L).ConfigureAwait(false);

        var testExeBuildInfos = proc.StandardErrorLines
            .Select(l => TestExecutablePathCracker.Matches(l))
            .Where(m => m.Count > 0 && m[0].Groups.Count == 5)
            .Select(m => (tc: (PathEx)m[0].Groups[4].Value, exe: (PathEx)m[0].Groups[3].Value, src: (PathEx)m[0].Groups[2].Value));
        if (!testExeBuildInfos.Any())
        {
            var e = new InvalidOperationException(string.Format("Unable to parse output of cargo test to obtain test exe paths. Command line '{0}'. Exit code: {1}", proc.Arguments, proc.ExitCode));
            _tl.L.WriteError(e.Message);
            _tl.T.TrackException(e);
            throw e;
        }

        var exes = testExeBuildInfos.Select(x => workingDir + x.exe);
        tc.TestExes = exes.ToArray();
        await testContainerPath.WriteTestContainerAsync(tc.Manifest, tc.TargetDir, tc.AdditionalTestDiscoveryArguments, tc.AdditionalTestExecutionArguments, tc.TestExecutionEnvironment, profile, tc.TestExes, ct).ConfigureAwait(false);

        if (!tc.TestExes.Any())
        {
            _tl.L.WriteError($"GetTestSuiteInfoAsync: Something is not right. No test executables found in '{tc.ThisPath}'.");
        }

        return tc.TestExes.Select(async exe => await GetTestSuiteInfoFromOneTestExeAsync(tc, exe, null, null, ct).ConfigureAwait(false));
    }

    private async Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoRemoteAsync(TestContainer tc, PathEx testContainerPath, string profile, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        var workingDir = tc.Manifest.GetDirectoryName();
        var remoteWorkingDir = pathMapper.MapToRemote(workingDir);
        var remoteManifest = GetRemoteManifestPath(tc.Manifest, pathMapper);

        // Get versions for logging
        var versionResult = await executionContext.ExecuteAndCaptureAsync(
            executionContext.CargoCommand,
            new[] { "--version" },
            remoteWorkingDir,
            ct).ConfigureAwait(false);
        _tl.L.WriteLine($"Using: {string.Join(" ", versionResult)}");

        var args = new[] { "test", "--no-run", "--manifest-path", remoteManifest, "--profile", profile }
            .Concat(tc.AdditionalTestDiscoveryArguments.FromNullSeparatedArray())
            .ToArray();

        _tl.T.TrackEvent("GetTestSuiteInfoAsync", ("TestContainer", testContainerPath), ("Profile", profile), ("Args", string.Join("|", args)), ("Target", executionContext.Kind.ToString()));

        var result = await executionContext.ExecuteAsync(
            executionContext.CargoCommand,
            args,
            remoteWorkingDir,
            null,
            null,
            ct).ConfigureAwait(false);

        // Use remote-aware regex for test executable paths (Linux paths)
        var testExeBuildInfos = result.StandardError
            .Select(l => TestExecutablePathCrackerRemote.Matches(l))
            .Where(m => m.Count > 0 && m[0].Groups.Count == 4)
            .Select(m => (name: m[0].Groups[1].Value, exe: m[0].Groups[3].Value))
            .ToList();

        if (!testExeBuildInfos.Any())
        {
            var e = new InvalidOperationException($"Unable to parse output of cargo test to obtain test exe paths. Exit code: {result.ExitCode}");
            _tl.L.WriteError(e.Message);
            _tl.T.TrackException(e);
            throw e;
        }

        // Map remote paths to local (UNC) paths
        tc.TestExes = testExeBuildInfos
            .Select(x => pathMapper.MapToLocal(new RemotePath(x.exe, pathMapper.Kind)))
            .ToArray();

        await testContainerPath.WriteTestContainerAsync(tc.Manifest, tc.TargetDir, tc.AdditionalTestDiscoveryArguments, tc.AdditionalTestExecutionArguments, tc.TestExecutionEnvironment, profile, tc.TestExes, ct).ConfigureAwait(false);

        if (!tc.TestExes.Any())
        {
            _tl.L.WriteError($"GetTestSuiteInfoAsync: Something is not right. No test executables found in '{tc.ThisPath}'.");
        }

        return tc.TestExes.Select(async exe => await GetTestSuiteInfoFromOneTestExeAsync(tc, exe, executionContext, pathMapper, ct).ConfigureAwait(false));
    }

    private BuildMessage[] OutputPreprocessorForCargoToolsWithoutJsonOutput(string msg) => new[] { new StringBuildMessage { Message = msg } };

    private async Task<TestSuiteInfo> GetTestSuiteInfoFromOneTestExeAsync(TestContainer container, PathEx testExePath, IExecutionContext executionContext, IPathMapper pathMapper, CancellationToken ct)
    {
        var workspaceRoot = container.TargetDir.GetDirectoryName();

        IEnumerable<string> outputLines;

        if (executionContext != null && executionContext.Kind != TargetKind.Local)
        {
            // Remote execution - execute the test binary in WSL/SSH
            var remoteExePath = pathMapper.MapToRemote(testExePath);
            var remoteWorkingDir = pathMapper.MapToRemote(workspaceRoot);

            var result = await executionContext.ExecuteAsync(
                (string)remoteExePath,
                new[] { "--list", "--format", "json", "-Zunstable-options" },
                remoteWorkingDir,
                null,
                null,
                ct).ConfigureAwait(false);

            outputLines = result.StandardOutput;
        }
        else
        {
            // Local execution (existing behavior)
            // Note: --list --format json requires nightly Rust with -Zunstable-options
            // This is tracked in https://github.com/rust-lang/rust/issues/49359
            using var proc = await ProcessRunner.RunWithLogging(workspaceRoot + testExePath, new[] { "--list", "--format", "json", "-Zunstable-options" }, workspaceRoot, ImmutableDictionary<string, string>.Empty, ct, _tl.L).ConfigureAwait(false);
            outputLines = proc.StandardOutputLines;
        }

        var tests = Enumerable.Empty<TestSuiteInfo.TestInfo>();
        if (!outputLines.FirstOrDefault()?.Trim()?.StartsWith("{") ?? false)
        {
            _tl.L.WriteError($"{Vsix.Name} requires nightly toolchain for test discovery. Please install the nightly toolchain: rustup install nightly && rustup default nightly. See https://github.com/rust-lang/rust/issues/49359 for tracking.");
        }
        else
        {
            tests = outputLines
                .Skip(1)
                .Take(outputLines.Count() - 2)
                .Select(l => DeserializeTest(workspaceRoot, l, pathMapper))
                .OrderBy(x => x.FQN).ThenBy(x => x.StartLine);
        }

        return new TestSuiteInfo
        {
            Container = container,
            Exe = testExePath,
            Tests = new Collection<TestSuiteInfo.TestInfo>(tests.ToList()),
        };
    }

    private static TestSuiteInfo.TestInfo DeserializeTest(PathEx workspaceRoot, string serializedVal, IPathMapper pathMapper = null)
    {
        var test = JsonConvert.DeserializeObject<TestSuiteInfo.TestInfo>(serializedVal);

        if (pathMapper != null && pathMapper.Kind != TargetKind.Local)
        {
            // Remote: source path from test is a Linux path, map it to VS-visible path
            if (test.SourcePath != null && ((string)test.SourcePath).StartsWith("/", StringComparison.Ordinal))
            {
                test.SourcePath = pathMapper.MapToLocal(new RemotePath((string)test.SourcePath, pathMapper.Kind));
            }
            else if (test.SourcePath != null)
            {
                // Relative path - combine with remote workspace root, then map
                var remoteWorkspace = pathMapper.MapToRemote(workspaceRoot);
                var fullRemotePath = remoteWorkspace.Combine((string)test.SourcePath);
                test.SourcePath = pathMapper.MapToLocal(fullRemotePath);
            }
        }
        else
        {
            // Local: combine with workspace root
            test.SourcePath = workspaceRoot + test.SourcePath;
        }

        return test;
    }

    private static Workspace AddRootPackageIfNecessary(Workspace w, PathEx manifestPath)
    {
        var p = w.Packages.FirstOrDefault(p => p.ManifestPath.GetFullPath() == manifestPath.GetFullPath());
        if (p == null)
        {
            // NOTE: Means this is the root Workspace Cargo.toml that is not a package.
            var p1 =
                new Workspace.Package
                {
                    ManifestPath = manifestPath,
                    Name = Workspace.Package.RootPackageName,
                };
            w.Packages.Add(p1);
        }

        return w;
    }

    private async Task<bool> ExecuteOperationAsync(string opName, PathEx filePath, string arguments, string profile, IBuildOutputSink outputPane, Func<BuildMessage, Task> buildMessageReporter, Func<string, BuildMessage[]> outputPreprocessor, ITelemetryService ts, ILogger l, CancellationToken ct)
    {
        outputPane.Clear();

        var cargoFullPath = GetCargoExePath();

        ts.TrackEvent(
            opName,
            new[] { ("FilePath", filePath), ("Profile", profile), ("CargoPath", cargoFullPath), ("Arguments", arguments) });

        return await RunAsync(
            cargoFullPath,
            opName,
            arguments,
            filePath.GetDirectoryName(),
            redirector: new BuildOutputRedirector(outputPane, (PathEx)Path.GetDirectoryName(filePath), buildMessageReporter, outputPreprocessor),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a cargo operation on a remote target (WSL/SSH).
    /// </summary>
    private async Task<bool> ExecuteRemoteOperationAsync(
        string opName,
        PathEx filePath,
        string[] arguments,
        string profile,
        IBuildOutputSink outputPane,
        Func<BuildMessage, Task> buildMessageReporter,
        Func<string, BuildMessage[]> outputPreprocessor,
        IExecutionContext executionContext,
        IPathMapper pathMapper,
        CancellationToken ct)
    {
        outputPane.Clear();

        var remoteWorkingDir = pathMapper.MapToRemote(filePath.GetDirectoryName());

        _tl.T.TrackEvent(
            opName,
            new[] { ("FilePath", (string)filePath), ("Profile", profile), ("Target", executionContext.Kind.ToString()), ("Arguments", string.Join(" ", arguments)) });

        var redirector = new BuildOutputRedirector(outputPane, filePath.GetDirectoryName(), buildMessageReporter, outputPreprocessor);

        redirector.WriteLineWithoutProcessing(string.Empty);
        redirector.WriteLineWithoutProcessing("==== Build step: Started ====");
        redirector.WriteLineWithoutProcessing($"      Target : {executionContext.Kind}");
        redirector.WriteLineWithoutProcessing($"     Command : {executionContext.CargoCommand}");
        redirector.WriteLineWithoutProcessing($"   Arguments : {string.Join(" ", arguments)}");
        redirector.WriteLineWithoutProcessing($"  WorkingDir : {remoteWorkingDir}");
        redirector.WriteLineWithoutProcessing(string.Empty);

        // Create output sink that forwards to redirector
        var outputSink = new BuildProcessOutputSink(redirector);

        var result = await executionContext.ExecuteAsync(
            executionContext.CargoCommand,
            arguments,
            remoteWorkingDir,
            null, // environment
            outputSink,
            ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            redirector.WriteLineWithoutProcessing("==== Build step: Finished ====\n");
        }
        else
        {
            redirector.WriteErrorLineWithoutProcessing($"==== Build step: Failed (exit code {result.ExitCode}) ====\n");
        }

        return result.ExitCode == 0;
    }

    /// <summary>
    /// Gets the remote manifest path from a VS-visible path.
    /// </summary>
    private static string GetRemoteManifestPath(PathEx manifestPath, IPathMapper pathMapper)
    {
        if (pathMapper == null || pathMapper.Kind == TargetKind.Local)
        {
            return manifestPath;
        }

        return (string)pathMapper.MapToRemote(manifestPath);
    }

    /// <summary>
    /// Parses additional arguments string into an array.
    /// </summary>
    private static IEnumerable<string> ParseAdditionalArgs(string additionalArgs)
    {
        if (string.IsNullOrWhiteSpace(additionalArgs))
        {
            return Enumerable.Empty<string>();
        }

        // Simple split on whitespace - for more complex needs, use proper argument parsing
        return additionalArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<bool> RunAsync(PathEx exeFullPath, string opName, string arguments, PathEx workingDir, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        EnsureArg.IsNotEmptyOrWhiteSpace(arguments, nameof(arguments));

        var cargoVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("cargo", "--version", workingDir, ct);
        var toolVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine(opName, "--version", workingDir, ct);

        redirector?.WriteLineWithoutProcessing($"");
        redirector?.WriteLineWithoutProcessing($"==== Build step: Started ====");
        redirector?.WriteLineWithoutProcessing($"        Using : {cargoVersion}");
        redirector?.WriteLineWithoutProcessing($"        Using : {toolVersion}");
        redirector?.WriteLineWithoutProcessing($"         Path : {exeFullPath}");
        redirector?.WriteLineWithoutProcessing($"    Arguments : {arguments}");
        redirector?.WriteLineWithoutProcessing($"   WorkingDir : {workingDir}");
        redirector?.WriteLineWithoutProcessing($"");

        return await exeFullPath.RunAsync(
            arguments,
            workingDir,
            redirector,
            finishedMsg: "==== Build step: Finished ====\n",
            cancelledMsg: "====  Build step canceled ====\n",
            ct);
    }
}

public sealed class BuildOutputRedirector : ProcessOutputRedirector
{
    private readonly IBuildOutputSink _outputPane;
    private readonly PathEx _rootPath;
    private readonly Func<BuildMessage, Task> _buildMessageReporter;
    private readonly Func<string, BuildMessage[]> _jsonProcessor;

    public BuildOutputRedirector(IBuildOutputSink outputPane, PathEx rootPath, Func<BuildMessage, Task> buildMessageReporter, Func<string, BuildMessage[]> jsonProcessor)
    {
        _outputPane = outputPane;
        _rootPath = rootPath;
        _buildMessageReporter = buildMessageReporter;
        _jsonProcessor = jsonProcessor;
    }

    public override void WriteErrorLine(string line)
    {
        WriteErrorLineWithoutProcessing(line);
    }

    public override void WriteErrorLineWithoutProcessing(string line)
    {
        WriteLineCore(line, x => new[] { new StringBuildMessage { Message = x } });
    }

    public override void WriteLine(string line)
    {
        WriteLineCore(line, _jsonProcessor);
    }

    public override void WriteLineWithoutProcessing(string line)
    {
        WriteLineCore(line, x => new[] { new StringBuildMessage { Message = x } });
    }

    private void WriteLineCore(string jsonLine, Func<string, BuildMessage[]> jsonProcessor)
    {
        var lines = jsonProcessor(jsonLine);
        Array.ForEach(
            lines,
            l =>
            {
                _outputPane.WriteLine(_rootPath, _buildMessageReporter, l);
            });
    }
}

/// <summary>
/// Adapts IProcessOutputSink to BuildOutputRedirector for remote operations.
/// </summary>
internal sealed class BuildProcessOutputSink : IProcessOutputSink
{
    private readonly BuildOutputRedirector _redirector;

    public BuildProcessOutputSink(BuildOutputRedirector redirector)
    {
        _redirector = redirector;
    }

    public void OnStdout(string line)
    {
        _redirector.WriteLine(line);
    }

    public void OnStderr(string line)
    {
        _redirector.WriteErrorLine(line);
    }

    public void OnProcessStarted(int? processId)
    {
        // Nothing to do
    }

    public void OnProcessExited(int exitCode)
    {
        // Nothing to do
    }
}


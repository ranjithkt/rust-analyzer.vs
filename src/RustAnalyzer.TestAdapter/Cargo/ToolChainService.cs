using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using KS.RustAnalyzer.TestAdapter.Common;
using Newtonsoft.Json;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

[Export(typeof(IToolchainService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class ToolchainService : IToolchainService
{
    // Windows regex: matches paths like ...\target\debug\deps\name-hash.exe
    private static readonly Regex TestExecutablePathCracker = new(@"^\s*Executable( unittests)? (.*) \((.*\\(.*)\-[\da-f]{16}.exe)\)$$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // WSL/Linux regex: matches paths like .../target/debug/deps/name-hash (no .exe extension)
    private static readonly Regex TestExecutablePathCrackerWsl = new(@"^\s*Executable( unittests)? (.*) \((.*/(.*)(?:-[\da-f]{16}))\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TL _tl;

    [ImportingConstructor]
    public ToolchainService([Import] ITelemetryService t, [Import] ILogger l)
    {
        _tl = new TL
        {
            T = t,
            L = l,
        };
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

    public async Task<bool> BuildAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
        var success = await ExecuteOperationAsync(
            "build",
            bti.ManifestPath,
            arguments: $"build --manifest-path \"{bti.ManifestPath}\" --profile {bti.Profile} --message-format json {bti.AdditionalBuildArgs}",
            profile: bti.Profile,
            outputPane: bos.OutputSink,
            buildMessageReporter: bos.BuildActionProgressReporter,
            outputPreprocessor: x => BuildJsonOutputParser.Parse(bti.WorkspaceRoot, x, _tl),
            ts: _tl.T,
            l: _tl.L,
            ct: ct);

        if (success)
        {
            var w = await GetWorkspaceAsync(bti.ManifestPath, ct);
            var testContainers = w.Packages.SelectMany(p => p.GetTestContainers(bti.Profile));
            w.TargetDirectory.MakeProfilePath(bti.Profile).CleanTestContainers(testContainers.Select(x => x.Container));
            var tasks = testContainers
                .Select(x => x.Container.WriteTestContainerAsync(x.Target.Parent.ManifestPath, w.TargetDirectory, bti.AdditionalTestDiscoveryArguments, bti.AdditionalTestExecutionArguments, bti.TestExecutionEnvironment, bti.Profile, Array.Empty<PathEx>(), ct));
            await Task.WhenAll(tasks);
        }

        return success;
    }

    public Task<bool> CleanAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
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

    public async Task<Workspace> GetWorkspaceAsync(PathEx manifestPath, CancellationToken ct)
    {
        try
        {
            string metadataJson;

            // Check if this is a WSL workspace
            if (WslInfo.TryParse(manifestPath, out var wslInfo))
            {
                metadataJson = await GetWorkspaceMetadataWslAsync(manifestPath, wslInfo, ct);
            }
            else
            {
                metadataJson = await GetWorkspaceMetadataWindowsAsync(manifestPath, ct);
            }

            var w = JsonConvert.DeserializeObject<Workspace>(metadataJson);
            return AddRootPackageIfNecessary(w, manifestPath);
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

    private async Task<string> GetWorkspaceMetadataWindowsAsync(PathEx manifestPath, CancellationToken ct)
    {
        var cargoFullPath = GetCargoExePath();

        using var proc = await ProcessRunner.RunWithLogging(
            cargoFullPath,
            new[] { "metadata", "--no-deps", "--format-version", "1", "--manifest-path", manifestPath, "--offline" },
            cargoFullPath.GetDirectoryName(),
            ImmutableDictionary<string, string>.Empty,
            ct,
            _tl.L);

        return string.Join(string.Empty, proc.StandardOutputLines);
    }

    private async Task<string> GetWorkspaceMetadataWslAsync(PathEx manifestPath, WslInfo wslInfo, CancellationToken ct)
    {
        var linuxManifestPath = wslInfo.ToLinuxPath(manifestPath);
        var linuxWorkingDir = wslInfo.ToLinuxPath(manifestPath.GetDirectoryName());

        var cargoArgs = new[] { "metadata", "--no-deps", "--format-version", "1", "--manifest-path", linuxManifestPath, "--offline" };

        using var proc = ToolchainServiceExtensions.RunCargoInWsl(wslInfo, cargoArgs, linuxWorkingDir, ct);
        _tl.L.WriteLine("Started WSL PID:{0} with args: {1}...", proc.ProcessId, proc.Arguments);
        var exitCode = await proc;
        _tl.L.WriteLine("... Finished WSL PID {0} with exit code {1}.", proc.ProcessId, proc.ExitCode);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"cargo metadata (WSL) returned {exitCode}\n{string.Join("\n", proc.StandardErrorLines)}").AddExitCode(exitCode);
        }

        var metadataJson = string.Join(string.Empty, proc.StandardOutputLines);

        // Rewrite Linux paths in the JSON to Windows UNC paths
        return RewriteCargoMetadataPathsForWsl(metadataJson, wslInfo);
    }

    /// <summary>
    /// Rewrites path fields in cargo metadata JSON from Linux paths to Windows UNC paths.
    /// </summary>
    private string RewriteCargoMetadataPathsForWsl(string json, WslInfo wslInfo)
    {
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

            // Rewrite workspace_root
            RewritePathProperty(obj, "workspace_root", wslInfo);

            // Rewrite target_directory
            RewritePathProperty(obj, "target_directory", wslInfo);

            // Rewrite paths in packages array
            if (obj["packages"] is Newtonsoft.Json.Linq.JArray packages)
            {
                foreach (var package in packages)
                {
                    // Rewrite manifest_path
                    RewritePathProperty(package, "manifest_path", wslInfo);

                    // Rewrite targets array
                    if (package["targets"] is Newtonsoft.Json.Linq.JArray targets)
                    {
                        foreach (var target in targets)
                        {
                            RewritePathProperty(target, "src_path", wslInfo);
                        }
                    }
                }
            }

            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception e)
        {
            _tl.L.WriteLine("Failed to rewrite cargo metadata paths for WSL: {0}", e.Message);

            // Return original JSON if rewriting fails
            return json;
        }
    }

    private static void RewritePathProperty(Newtonsoft.Json.Linq.JToken token, string propertyName, WslInfo wslInfo)
    {
        if (token[propertyName] is Newtonsoft.Json.Linq.JValue value && value.Value is string linuxPath)
        {
            if (WslInfo.IsLinuxAbsolutePath(linuxPath))
            {
                token[propertyName] = wslInfo.ToUncPath(linuxPath);
            }
        }
    }

    public async Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoAsync(PathEx testContainerPath, string profile, CancellationToken ct)
    {
        var tc = await testContainerPath.ReadTestContainerAsync(ct);
        _tl.L.WriteLine($"GetTestSuiteInfoAsync: Finding tests for {testContainerPath}");

        try
        {
            var workingDir = tc.Manifest.GetDirectoryName();
            var isWsl = WslInfo.TryParse(workingDir, out var wslInfo);

            var cargoVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("cargo", "--version", workingDir, ct);
            _tl.L.WriteLine($"Using: {cargoVersion}");
            var rustcVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("test", "--version", workingDir, ct);
            _tl.L.WriteLine($"Using: {rustcVersion}");

            _tl.T.TrackEvent("GetTestSuiteInfoAsync", ("TestContainer", testContainerPath), ("Profile", profile), ("IsWsl", $"{isWsl}"));

            ProcessRunner proc;
            if (isWsl)
            {
                var linuxManifestPath = wslInfo.ToLinuxPath(tc.Manifest);
                var linuxWorkingDir = wslInfo.ToLinuxPath(workingDir);

                var args = new[] { "test", "--no-run", "--manifest-path", linuxManifestPath, "--profile", profile }
                    .Concat(tc.AdditionalTestDiscoveryArguments.FromNullSeparatedArray())
                    .ToArray();

                proc = ToolchainServiceExtensions.RunCargoInWsl(wslInfo, args, linuxWorkingDir, ct);
            }
            else
            {
                var cargoFullPath = GetCargoExePath();
                var args = new[] { "test", "--no-run", "--manifest-path", tc.Manifest, "--profile", profile }
                    .Concat(tc.AdditionalTestDiscoveryArguments.FromNullSeparatedArray())
                    .ToArray();

                proc = ProcessRunner.Run(cargoFullPath, args, workingDir, ImmutableDictionary<string, string>.Empty, ct);
            }

            using (proc)
            {
                _tl.L.WriteLine("Started PID:{0} with args: {1}...", proc.ProcessId, proc.Arguments);
                var exitCode = await proc;
                _tl.L.WriteLine("... Finished PID {0} with exit code {1}.", proc.ProcessId, proc.ExitCode);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"cargo test --no-run returned {exitCode}\n{string.Join("\n", proc.StandardErrorLines)}").AddExitCode(exitCode);
                }

                // Use the appropriate regex based on WSL mode
                var regex = isWsl ? TestExecutablePathCrackerWsl : TestExecutablePathCracker;
                var testExeBuildInfos = proc.StandardErrorLines
                    .Select(l => regex.Matches(l))
                    .Where(m => m.Count > 0 && m[0].Groups.Count >= 4)
                    .Select(m => ParseTestExeMatch(m[0], isWsl, wslInfo));

                if (!testExeBuildInfos.Any())
                {
                    var e = new InvalidOperationException(string.Format("Unable to parse output of cargo test to obtain test exe paths. Command line '{0}'. Exit code: {1}", proc.Arguments, proc.ExitCode));
                    _tl.L.WriteError(e.Message);
                    _tl.T.TrackException(e);
                    throw e;
                }

                PathEx[] exes;
                if (isWsl)
                {
                    // For WSL, convert Linux paths to UNC paths
                    exes = testExeBuildInfos.Select(x => wslInfo.ToUncPathEx(x.Exe)).ToArray();
                }
                else
                {
                    exes = testExeBuildInfos.Select(x => workingDir + x.Exe).ToArray();
                }

                tc.TestExes = exes;
                await testContainerPath.WriteTestContainerAsync(tc.Manifest, tc.TargetDir, tc.AdditionalTestDiscoveryArguments, tc.AdditionalTestExecutionArguments, tc.TestExecutionEnvironment, profile, tc.TestExes, ct);

                if (!tc.TestExes.Any())
                {
                    _tl.L.WriteError($"GetTestSuiteInfoAsync: Something is not right. No test executables found in '{tc.ThisPath}'.");
                }

                return tc.TestExes.Select(async exe => await GetTestSuiteInfoFromOneTestExeAsync(tc, exe, ct));
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

    private static (PathEx Tc, string Exe, PathEx Src) ParseTestExeMatch(Match match, bool isWsl, WslInfo wslInfo)
    {
        // Groups: [0]=full match, [1]=optional " unittests", [2]=src, [3]=full exe path, [4]=exe name
        var tc = (PathEx)(match.Groups.Count > 4 ? match.Groups[4].Value : match.Groups[3].Value);
        var exe = match.Groups[3].Value;
        var src = (PathEx)match.Groups[2].Value;
        return (tc, exe, src);
    }

    private BuildMessage[] OutputPreprocessorForCargoToolsWithoutJsonOutput(string msg) => new[] { new StringBuildMessage { Message = msg } };

    private async Task<TestSuiteInfo> GetTestSuiteInfoFromOneTestExeAsync(TestContainer container, PathEx testExePath, CancellationToken ct)
    {
        var workspaceRoot = container.TargetDir.GetDirectoryName();
        var isWsl = WslInfo.TryParse(workspaceRoot, out var wslInfo);

        ProcessRunner proc;
        if (isWsl)
        {
            // For WSL, testExePath is already a full UNC path, convert it to Linux path and run via WSL
            var linuxExePath = wslInfo.ToLinuxPath(testExePath);
            var linuxWorkingDir = wslInfo.ToLinuxPath(workspaceRoot);
            var testArgs = new[] { "--list", "--format", "json", "-Zunstable-options" };

            proc = ToolchainServiceExtensions.RunInWsl(wslInfo, linuxExePath, testArgs, linuxWorkingDir, ImmutableDictionary<string, string>.Empty, ct);
        }
        else
        {
            proc = ProcessRunner.Run(workspaceRoot + testExePath, new[] { "--list", "--format", "json", "-Zunstable-options" }, workspaceRoot, ImmutableDictionary<string, string>.Empty, ct);
        }

        using (proc)
        {
            _tl.L.WriteLine("Started PID:{0} with args: {1}...", proc.ProcessId, proc.Arguments);
            var exitCode = await proc;
            _tl.L.WriteLine("... Finished PID {0} with exit code {1}.", proc.ProcessId, proc.ExitCode);

            var tests = Enumerable.Empty<TestSuiteInfo.TestInfo>();
            if (!proc.StandardOutputLines.FirstOrDefault()?.Trim()?.StartsWith("{") ?? false)
            {
                _tl.L.WriteError($"{Vsix.Name} requires nightly toolchain. Please install the nightly toolchain following instructions in https://rust-lang.github.io/rustup/concepts/channels.html. Details: Fix for https://github.com/rust-lang/rust/issues/49359 is required to support unit testing experience. The RFC process is currently underway. Till then the fix is available only in nightly toolchain.");
            }
            else
            {
                tests = proc.StandardOutputLines
                    .Skip(1)
                    .Take(proc.StandardOutputLines.Count() - 2)
                    .Select(l => DeserializeTest(workspaceRoot, l, wslInfo))
                    .OrderBy(x => x.FQN).ThenBy(x => x.StartLine);
            }

            return new TestSuiteInfo
            {
                Container = container,
                Exe = testExePath,
                Tests = new Collection<TestSuiteInfo.TestInfo>(tests.ToList()),
            };
        }
    }

    private static TestSuiteInfo.TestInfo DeserializeTest(PathEx workspaceRoot, string serializedVal, WslInfo wslInfo)
    {
        // NOTE: We need to extract the source_path from the raw JSON BEFORE deserialization,
        // because PathEx constructor converts "/" to "\" which breaks IsLinuxAbsolutePath check.
        string rawSourcePath = null;
        try
        {
            var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(serializedVal);
            rawSourcePath = (string)jsonObj["source_path"];
        }
        catch
        {
            // If parsing fails, fall back to post-deserialization handling
        }

        var test = JsonConvert.DeserializeObject<TestSuiteInfo.TestInfo>(serializedVal);

        if (wslInfo != null && rawSourcePath != null && WslInfo.IsLinuxAbsolutePath(rawSourcePath))
        {
            // For WSL, the source path from the test binary is a Linux absolute path
            // Convert it to a Windows UNC path
            test.SourcePath = wslInfo.ToUncPathEx(rawSourcePath);
        }
        else
        {
            // For Windows, combine with workspace root
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

        var workingDir = filePath.GetDirectoryName();
        var isWsl = WslInfo.TryParse(workingDir, out var wslInfo);

        ts.TrackEvent(
            opName,
            new[] { ("FilePath", filePath), ("Profile", profile), ("IsWsl", $"{isWsl}"), ("Arguments", arguments) });

        if (isWsl)
        {
            return await RunWslAsync(
                wslInfo,
                opName,
                arguments,
                workingDir,
                redirector: new BuildOutputRedirector(outputPane, workingDir, buildMessageReporter, outputPreprocessor),
                ct: ct);
        }
        else
        {
            var cargoFullPath = GetCargoExePath();

            return await RunAsync(
                cargoFullPath,
                opName,
                arguments,
                workingDir,
                redirector: new BuildOutputRedirector(outputPane, workingDir, buildMessageReporter, outputPreprocessor),
                ct: ct);
        }
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

    private static async Task<bool> RunWslAsync(WslInfo wslInfo, string opName, string arguments, PathEx workingDir, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        EnsureArg.IsNotEmptyOrWhiteSpace(arguments, nameof(arguments));
        EnsureArg.IsNotNull(wslInfo, nameof(wslInfo));

        var linuxWorkingDir = wslInfo.ToLinuxPath(workingDir);

        var cargoVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("cargo", "--version", workingDir, ct);
        var toolVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine(opName, "--version", workingDir, ct);

        redirector?.WriteLineWithoutProcessing($"");
        redirector?.WriteLineWithoutProcessing($"==== Build step (WSL): Started ====");
        redirector?.WriteLineWithoutProcessing($"        Using : {cargoVersion}");
        redirector?.WriteLineWithoutProcessing($"        Using : {toolVersion}");
        redirector?.WriteLineWithoutProcessing($"       Distro : {wslInfo.DistroName}");
        redirector?.WriteLineWithoutProcessing($"    Arguments : {arguments}");
        redirector?.WriteLineWithoutProcessing($"   WorkingDir : {linuxWorkingDir} (WSL)");
        redirector?.WriteLineWithoutProcessing($"");

        // Convert arguments to array, handling quoted strings properly
        var argList = ParseArgumentsForWsl(arguments, wslInfo);

        using var process = ToolchainServiceExtensions.RunCargoInWsl(wslInfo, argList, linuxWorkingDir, ct);
        var whnd = process.WaitHandle;
        if (whnd == null)
        {
            redirector?.WriteErrorLineWithoutProcessing($"Error - Failed to start cargo in WSL distro '{wslInfo.DistroName}'");
            return false;
        }

        // Set up output redirection
        process.Exited += (s, e) => { };

        var finished = await Task.Run(() => whnd.WaitOne(), ct);
        if (finished)
        {
            process.Wait();

            // Write output lines
            foreach (var line in process.StandardOutputLines)
            {
                redirector?.WriteLine(line);
            }

            foreach (var line in process.StandardErrorLines)
            {
                redirector?.WriteErrorLine(line);
            }

            redirector?.WriteLineWithoutProcessing("==== Build step (WSL): Finished ====\n");

            return process.ExitCode == 0;
        }
        else
        {
            process.Kill();
            redirector?.WriteErrorLineWithoutProcessing("====  Build step (WSL) canceled ====\n");
            return false;
        }
    }

    /// <summary>
    /// Parses cargo arguments and converts any Windows UNC paths to Linux paths for WSL.
    /// </summary>
    private static string[] ParseArgumentsForWsl(string arguments, WslInfo wslInfo)
    {
        // Split arguments, handling quoted strings
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(ConvertPathArgumentForWsl(current.ToString(), wslInfo));
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            args.Add(ConvertPathArgumentForWsl(current.ToString(), wslInfo));
        }

        return args.ToArray();
    }

    /// <summary>
    /// If an argument is a Windows UNC path under the WSL distro, convert it to a Linux path.
    /// </summary>
    private static string ConvertPathArgumentForWsl(string arg, WslInfo wslInfo)
    {
        // Check if this looks like a path under our WSL distro
        if (arg.StartsWith(wslInfo.UncDistroRoot, StringComparison.OrdinalIgnoreCase))
        {
            return wslInfo.ToLinuxPath(arg);
        }

        return arg;
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


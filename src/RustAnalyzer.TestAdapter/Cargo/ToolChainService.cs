using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly Regex TestExecutablePathCracker = new(@"^\s*Executable( unittests)? (.*) \((.*\\(.*)\-[\da-f]{16}.exe)\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var packageArg = TryGetPackageArgForManifest(bti.ManifestPath);

        var success = await ExecuteOperationAsync(
            "build",
            bti.WorkspaceRoot,
            bti.ManifestPath,
            arguments: $"build --manifest-path \"{bti.ManifestPath}\" {packageArg} --profile {bti.Profile} --message-format json {bti.AdditionalBuildArgs}",
            profile: bti.Profile,
            outputPane: bos.OutputSink,
            buildMessageReporter: bos.BuildActionProgressReporter,
            outputPreprocessor: x => BuildJsonOutputParser.Parse(bti.WorkspaceRoot, x, _tl),
            ts: _tl.T,
            l: _tl.L,
            ct: ct);

        if (success)
        {
            var w = await GetWorkspaceAsync(bti.ManifestPath, bti.WorkspaceRoot, ct);

            // IMPORTANT:
            // Do NOT rewrite all *.rusttests files on every build.
            // Rewriting them (especially with TestExes cleared) triggers VS Test Explorer rediscovery,
            // which runs `cargo test --no-run` and makes it look like VS "rebuilds the whole workspace"
            // every time you hit Build.
            //
            // Instead:
            // - delete stale containers (best-effort), and
            // - only create missing containers (leave existing ones untouched so discovery can be incremental).
            var testContainers = w.Packages.SelectMany(p => p.GetTestContainers(bti.Profile)).ToArray();
            w.TargetDirectory.MakeProfilePath(bti.Profile).CleanTestContainers(testContainers.Select(x => x.Container));

            var createTasks = testContainers
                .Where(x => !x.Container.FileExists())
                .Select(x => x.Container.WriteTestContainerAsync(
                    x.Target.Parent.ManifestPath,
                    w.TargetDirectory,
                    bti.AdditionalTestDiscoveryArguments,
                    bti.AdditionalTestExecutionArguments,
                    bti.TestExecutionEnvironment,
                    bti.Profile,
                    Array.Empty<PathEx>(),
                    ct));
            await Task.WhenAll(createTasks);
        }

        return success;
    }

    public Task<bool> CleanAsync(BuildTargetInfo bti, BuildOutputSinks bos, CancellationToken ct)
    {
        var packageArg = TryGetPackageArgForManifest(bti.ManifestPath);
        return ExecuteOperationAsync(
            "clean",
            bti.WorkspaceRoot,
            bti.ManifestPath,
            arguments: $"clean --manifest-path \"{bti.ManifestPath}\" {packageArg} --profile {bti.Profile}",
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
        var packageArg = TryGetPackageArgForManifest(bti.ManifestPath);
        return ExecuteOperationAsync(
            "Clippy",
            bti.WorkspaceRoot,
            bti.ManifestPath,
            arguments: $"clippy --manifest-path \"{bti.ManifestPath}\" {packageArg} --profile {bti.Profile} {bti.AdditionalBuildArgs}",
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
        var packageArg = TryGetPackageArgForManifest(bti.ManifestPath);
        return ExecuteOperationAsync(
            "Fmt",
            bti.WorkspaceRoot,
            bti.ManifestPath,
            arguments: $"fmt --manifest-path \"{bti.ManifestPath}\" {packageArg} {bti.AdditionalBuildArgs}",
            profile: bti.Profile,
            outputPane: bos.OutputSink,
            buildMessageReporter: bos.BuildActionProgressReporter,
            outputPreprocessor: OutputPreprocessorForCargoToolsWithoutJsonOutput,
            ts: _tl.T,
            l: _tl.L,
            ct: ct);
    }

    private static string TryGetPackageArgForManifest(PathEx manifestPath)
    {
        try
        {
            var path = (string)manifestPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            // Quick & safe-ish TOML sniffing: only look for `name = "..."` inside the `[package]` table.
            // If it's a virtual workspace manifest (no [package]) we return empty and cargo will use default behavior.
            var inPackage = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inPackage = string.Equals(line, "[package]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inPackage)
                {
                    continue;
                }

                // name = "common"
                if (line.StartsWith("name", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0 && eq < line.Length - 1)
                    {
                        var rhs = line.Substring(eq + 1).Trim();
                        if (rhs.StartsWith("\"") && rhs.EndsWith("\"") && rhs.Length >= 2)
                        {
                            var name = rhs.Substring(1, rhs.Length - 2);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                return $"--package \"{name}\"";
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort only.
        }

        return string.Empty;
    }

    public async Task<Workspace> GetWorkspaceAsync(PathEx manifestPath, PathEx workspaceRoot, CancellationToken ct)
    {
        try
        {
            string metadataJson;

            // Mode 1: WSL UNC workspace
            // Mode 2: Windows-local workspace + WSL execution (if selected)
            if (TargetSystemSelection.TryGetWslExecutionContext(workspaceRoot, out var wslInfo, out var distroName))
            {
                metadataJson = wslInfo != null
                    ? await GetWorkspaceMetadataWslAsync(manifestPath, wslInfo, ct)
                    : await GetWorkspaceMetadataWslForWindowsWorkspaceAsync(manifestPath, workspaceRoot, distroName, ct);
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

        // Rewrite Linux paths in the JSON to Windows paths
        return RewriteCargoMetadataPathsFromLinuxToWindows(metadataJson, wslInfo);
    }

    /// <summary>
    /// Mode 2: Windows-local workspace + WSL execution.
    /// </summary>
    private async Task<string> GetWorkspaceMetadataWslForWindowsWorkspaceAsync(PathEx manifestPath, PathEx workspaceRoot, string distroName, CancellationToken ct)
    {
        // Initialize mirror config early so metadata can point TargetDirectory to UNC mirror outputs.
        // This is lightweight (queries $HOME) and does NOT perform rsync.
        await WslMirrorManager.EnsureInitializedAsync(workspaceRoot, distroName, ct);

        if (!WslPathMapper.TryWindowsToWslPath(manifestPath, out var linuxManifestPath) ||
            !WslPathMapper.TryWindowsToWslPath(manifestPath.GetDirectoryName(), out var linuxWorkingDir))
        {
            throw new InvalidOperationException($"Unable to map manifest path '{manifestPath}' to a WSL /mnt path.").AddExitCode(-1);
        }

        var cargoArgs = new[] { "metadata", "--no-deps", "--format-version", "1", "--manifest-path", linuxManifestPath, "--offline" };

        using var proc = ToolchainServiceExtensions.RunCargoInWsl(distroName, cargoArgs, linuxWorkingDir, ct);
        _tl.L.WriteLine("Started WSL PID:{0} with args: {1}...", proc.ProcessId, proc.Arguments);
        var exitCode = await proc;
        _tl.L.WriteLine("... Finished WSL PID {0} with exit code {1}.", proc.ProcessId, proc.ExitCode);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"cargo metadata (WSL) returned {exitCode}\n{string.Join("\n", proc.StandardErrorLines)}").AddExitCode(exitCode);
        }

        var metadataJson = string.Join(string.Empty, proc.StandardOutputLines);

        // Rewrite /mnt/<drive>/... paths in the JSON to Windows paths.
        // Additionally, when using mirror mode, rewrite target_directory to UNC pointing to the mirror target dir.
        return RewriteCargoMetadataPathsFromLinuxToWindows(metadataJson, wslInfo: null, workspaceRoot: workspaceRoot, distroName: distroName);
    }

    /// <summary>
    /// Rewrites path fields in cargo metadata JSON from Linux paths to Windows paths.
    /// Mode 1: Linux paths -> Windows UNC (\\wsl.localhost\...)
    /// Mode 2: /mnt/&lt;drive&gt;/... -> X:\...
    /// </summary>
    private string RewriteCargoMetadataPathsFromLinuxToWindows(string json, WslInfo wslInfo, PathEx workspaceRoot = default, string distroName = null)
    {
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

            // Rewrite workspace_root
            RewritePathProperty(obj, "workspace_root", wslInfo);

            // Rewrite target_directory:
            // - Mode 1: Linux -> UNC (workspace UNC)
            // - Mode 2 legacy (/mnt): Linux -> Windows drive path
            // - Mode 2 mirror: set to UNC of mirror target dir so debug/tests reference real outputs.
            if (wslInfo != null)
            {
                RewritePathProperty(obj, "target_directory", wslInfo);
            }
            else
            {
                // If a mirror instance exists (initialized lazily), prefer its UNC target dir.
                if (!string.IsNullOrWhiteSpace(distroName) && (string)workspaceRoot != null &&
                    WslMirrorManager.TryGetMirrorTargetDirUnc(workspaceRoot, distroName, out var uncTargetDir) &&
                    !string.IsNullOrWhiteSpace(uncTargetDir))
                {
                    obj["target_directory"] = uncTargetDir;
                }
                else
                {
                    RewritePathProperty(obj, "target_directory", wslInfo: null);
                }
            }

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
            _tl.L.WriteLine("Failed to rewrite cargo metadata paths from Linux to Windows: {0}", e.Message);

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
                if (wslInfo != null)
                {
                    token[propertyName] = wslInfo.ToUncPath(linuxPath);
                }
                else if (WslPathMapper.TryWslToWindowsPath(linuxPath, out var winPath))
                {
                    token[propertyName] = winPath;
                }
                else if (WslMirrorPathMapper.TryMirrorLinuxToWindowsPathBySentinel(linuxPath, out var mirrorWin))
                {
                    // Future-proofing: if metadata is ever produced from mirror paths, map them back too.
                    token[propertyName] = mirrorWin;
                }
            }
        }
    }

    public async Task<IEnumerable<Task<TestSuiteInfo>>> GetTestSuiteInfoAsync(PathEx testContainerPath, string profile, CancellationToken ct)
    {
        var tc = await testContainerPath.ReadTestContainerAsync(ct);
        _tl.L.WriteLine($"GetTestSuiteInfoAsync: Finding tests for {testContainerPath}");

        try
        {
            // Fast path:
            // If the test container already knows its test executables and they still exist,
            // don't run `cargo test --no-run` again. That command can be very expensive and
            // is the main reason users observe "rebuilds every time" behavior in VS.
            if (tc.TestExes != null &&
                tc.TestExes.Length > 0 &&
                tc.TestExes.All(e => e.FileExists()))
            {
                return tc.TestExes.Select(async exe => await GetTestSuiteInfoFromOneTestExeAsync(tc, exe, ct));
            }

            var workingDir = tc.Manifest.GetDirectoryName();
            var isWsl = TargetSystemSelection.TryGetWslExecutionContext(workingDir, out var wslInfo, out var distroName);
            string linuxWorkingDirForCargoTestNoRun = null;

            var cargoVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("cargo", "--version", workingDir, ct);
            _tl.L.WriteLine($"Using: {cargoVersion}");
            var rustcVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("test", "--version", workingDir, ct);
            _tl.L.WriteLine($"Using: {rustcVersion}");

            _tl.T.TrackEvent("GetTestSuiteInfoAsync", ("TestContainer", testContainerPath), ("Profile", profile), ("IsWsl", $"{isWsl}"));

            ProcessRunner proc;
            if (isWsl)
            {
                string linuxManifestPath;
                string linuxWorkingDir;

                if (wslInfo != null)
                {
                    // Mode 1: UNC workspace
                    linuxManifestPath = wslInfo.ToLinuxPath(tc.Manifest);
                    linuxWorkingDir = wslInfo.ToLinuxPath(workingDir);
                }
                else
                {
                    // Mode 2: Windows workspace -> ensure mirror is synced and run against mirror paths.
                    var wsRoot = TargetSystemSelection.TryGetWorkspaceRoot(out var wr) ? wr : tc.Manifest.GetDirectoryName();
                    var mirror = WslMirrorManager.GetOrCreate(wsRoot, distroName);
                    await mirror.EnsureSynchronizedAsync(ct);
                    var cfg = mirror.Config;

                    linuxManifestPath = WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)tc.Manifest, cfg, out var lm) ? lm : null;
                    linuxWorkingDir = WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)workingDir, cfg, out var ld) ? ld : null;
                }

                if (linuxManifestPath == null || linuxWorkingDir == null)
                {
                    throw new InvalidOperationException($"Unable to map manifest/working directory to WSL paths. Manifest='{tc.Manifest}', WorkingDir='{workingDir}'.");
                }

                linuxWorkingDirForCargoTestNoRun = linuxWorkingDir;

                var args = new[] { "test", "--no-run", "--manifest-path", linuxManifestPath, "--profile", profile }
                    .Concat(tc.AdditionalTestDiscoveryArguments.FromNullSeparatedArray())
                    .ToArray();

                proc = wslInfo != null
                    ? ToolchainServiceExtensions.RunCargoInWsl(wslInfo, args, linuxWorkingDir, ct)
                    : ToolchainServiceExtensions.RunCargoInWsl(distroName, args, linuxWorkingDir, ct);
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

                    // Both regexes are expected to produce 5 groups total:
                    // [0]=full match, [1]=optional " unittests", [2]=src, [3]=full exe path, [4]=exe name.
                    // Guard against unexpected output/regex changes.
                    .Where(m => m.Count > 0 && m[0].Groups.Count >= 5)
                    .Select(m => ParseTestExeMatch(m[0], isWsl, wslInfo))
                    .ToArray();

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
                    // For WSL, cargo may emit either absolute Linux paths or paths relative to the --cd directory.
                    // Mode 1: store UNC.
                    // Mode 2 mirror: also store UNC (mirror output) so execution/debug works via WslInfo mapping.
                    try
                    {
                        if (wslInfo != null)
                        {
                            exes = testExeBuildInfos.Select(x => ConvertWslTestExePathToUnc(x.Exe, linuxWorkingDirForCargoTestNoRun, wslInfo)).ToArray();
                        }
                        else
                        {
                            // Mode 2 mirror: build outputs are in mirror, map Linux -> UNC via distro UNC root.
                            var uncRoot = $"\\\\wsl.localhost\\{distroName}\\";
                            if (!WslInfo.TryParse(uncRoot, out var distroInfo) || distroInfo == null)
                            {
                                throw new InvalidOperationException($"Unable to create WslInfo for distro '{distroName}'.");
                            }

                            exes = testExeBuildInfos.Select(x => ConvertWslTestExePathToUnc(x.Exe, linuxWorkingDirForCargoTestNoRun, distroInfo)).ToArray();
                        }
                    }
                    catch (Exception ex)
                    {
                        var raw = string.Join(" | ", testExeBuildInfos.Select(i => i.Exe ?? "<null>"));
                        var e = new InvalidOperationException(
                            $"Unable to convert cargo-reported WSL test exe paths to Windows paths. Raw paths: {raw}. Command line '{proc.Arguments}'. Exit code: {proc.ExitCode}",
                            ex);
                        _tl.L.WriteError(e.Message);
                        _tl.T.TrackException(e);
                        throw e;
                    }
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

    private static PathEx ConvertWslTestExePathToUnc(string exePathFromCargo, string linuxWorkingDir, WslInfo wslInfo)
    {
        if (string.IsNullOrWhiteSpace(exePathFromCargo))
        {
            throw new ArgumentException("WSL test exe path was empty.", nameof(exePathFromCargo));
        }

        if (wslInfo == null)
        {
            throw new ArgumentNullException(nameof(wslInfo));
        }

        var normalized = exePathFromCargo.Trim().Replace('\\', '/');

        // Absolute Linux path: convert directly.
        if (WslInfo.IsLinuxAbsolutePath(normalized))
        {
            return wslInfo.ToUncPathEx(normalized);
        }

        // Relative Linux path: resolve relative to the WSL working directory used for cargo (--cd).
        if (!WslInfo.IsLinuxAbsolutePath(linuxWorkingDir))
        {
            throw new ArgumentException($"Linux working directory '{linuxWorkingDir}' must be an absolute Linux path.", nameof(linuxWorkingDir));
        }

        var combined = linuxWorkingDir.TrimEnd('/') + "/" + normalized.TrimStart('/');
        return wslInfo.ToUncPathEx(combined);
    }

    private static PathEx ConvertWslTestExePathToWindows(string exePathFromCargo, string linuxWorkingDir)
    {
        if (string.IsNullOrWhiteSpace(exePathFromCargo))
        {
            throw new ArgumentException("WSL test exe path was empty.", nameof(exePathFromCargo));
        }

        var normalized = exePathFromCargo.Trim().Replace('\\', '/');
        string absoluteLinux;

        if (WslInfo.IsLinuxAbsolutePath(normalized))
        {
            absoluteLinux = normalized;
        }
        else
        {
            if (!WslInfo.IsLinuxAbsolutePath(linuxWorkingDir))
            {
                throw new ArgumentException($"Linux working directory '{linuxWorkingDir}' must be an absolute Linux path.", nameof(linuxWorkingDir));
            }

            absoluteLinux = linuxWorkingDir.TrimEnd('/') + "/" + normalized.TrimStart('/');
        }

        if (!WslPathMapper.TryWslToWindowsPath(absoluteLinux, out var winPath))
        {
            throw new InvalidOperationException($"Unable to map Linux path '{absoluteLinux}' to a Windows path.");
        }

        return (PathEx)winPath;
    }

    private BuildMessage[] OutputPreprocessorForCargoToolsWithoutJsonOutput(string msg) => new[] { new StringBuildMessage { Message = msg } };

    private async Task<TestSuiteInfo> GetTestSuiteInfoFromOneTestExeAsync(TestContainer container, PathEx testExePath, CancellationToken ct)
    {
        var workspaceRoot = container.TargetDir.GetDirectoryName();
        var isWsl = TargetSystemSelection.TryGetWslExecutionContext(workspaceRoot, out var wslInfo, out var distroName);

        ProcessRunner proc;
        if (isWsl)
        {
            // For WSL, the test exe is a Linux ELF binary. Run it inside WSL.
            // Mirror mode typically stores test exes/target dir as UNC (\\wsl.localhost\...),
            // but be defensive: if wslInfo is null (Mode 2 selection) try to derive a WslInfo from the paths.
            var effectiveWslInfo = wslInfo;
            if (effectiveWslInfo == null)
            {
                if (WslInfo.TryParse(testExePath, out var fromExe) && fromExe != null)
                {
                    effectiveWslInfo = fromExe;
                    distroName = fromExe.DistroName;
                }
                else if (WslInfo.TryParse(container.TargetDir, out var fromTarget) && fromTarget != null)
                {
                    effectiveWslInfo = fromTarget;
                    distroName = fromTarget.DistroName;
                }
            }

            string linuxExePath;
            string linuxWorkingDir;
            if (effectiveWslInfo != null)
            {
                linuxExePath = effectiveWslInfo.ToLinuxPath(testExePath);
                linuxWorkingDir = effectiveWslInfo.ToLinuxPath(workspaceRoot);
            }
            else
            {
                // Mode 2 (Windows workspace + WSL execution). Prefer mirror working dir when available.
                var wsRootWin = TargetSystemSelection.TryGetWorkspaceRoot(out var wr) ? wr : workspaceRoot;
                var mirror = WslMirrorManager.GetOrCreate(wsRootWin, distroName);
                await mirror.EnsureInitializedAsync(ct); // cheap: does not rsync
                var cfg = mirror.Config;

                if (cfg != null &&
                    WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)testExePath, cfg, out var lex) &&
                    WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)workspaceRoot, cfg, out var lwd))
                {
                    linuxExePath = lex;
                    linuxWorkingDir = lwd;
                }
                else
                {
                    // Fallback (legacy): /mnt mapping.
                    linuxExePath = WslPathMapper.TryWindowsToWslPath(testExePath, out var lex2) ? lex2 : null;
                    linuxWorkingDir = WslPathMapper.TryWindowsToWslPath(workspaceRoot, out var lwd2) ? lwd2 : null;
                }
            }

            var testArgs = new[] { "--list", "--format", "json", "-Zunstable-options" };

            if (linuxExePath == null || linuxWorkingDir == null)
            {
                throw new InvalidOperationException($"Unable to map test exe/working directory to WSL paths. Exe='{testExePath}', WorkingDir='{workspaceRoot}'.");
            }

            proc = effectiveWslInfo != null
                ? ToolchainServiceExtensions.RunInWsl(effectiveWslInfo, linuxExePath, testArgs, linuxWorkingDir, ImmutableDictionary<string, string>.Empty, ct)
                : ToolchainServiceExtensions.RunInWsl(distroName, linuxExePath, testArgs, linuxWorkingDir, ImmutableDictionary<string, string>.Empty, ct);
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
                    .Select(l => DeserializeTest(workspaceRoot, l, isWsl, wslInfo))
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

    private static TestSuiteInfo.TestInfo DeserializeTest(PathEx workspaceRoot, string serializedVal, bool isWsl, WslInfo wslInfo)
    {
        // NOTE: We need to extract the source_path from the raw JSON BEFORE deserialization,
        // because PathEx constructor converts "/" to "\" which breaks IsLinuxAbsolutePath check.
        string rawSourcePath = null;
        var jsonForDeserialization = serializedVal;
        try
        {
            var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(serializedVal);
            rawSourcePath = (string)jsonObj["source_path"];

            // IMPORTANT:
            // In WSL runs, `source_path` can be a Linux absolute path (e.g. /mnt/... or /home/...).
            // PathEx must never hold Linux absolute paths (it normalizes '/' -> '\', and may be strict).
            // Rewrite it to a Windows path (UNC for Mode 1, drive path for Mode 2) before deserializing.
            if (isWsl && !string.IsNullOrWhiteSpace(rawSourcePath) && WslInfo.IsLinuxAbsolutePath(rawSourcePath))
            {
                string mapped = null;
                if (wslInfo != null)
                {
                    mapped = wslInfo.ToUncPath(rawSourcePath);
                }
                else if (WslPathMapper.TryWslToWindowsPath(rawSourcePath, out var winPath))
                {
                    mapped = winPath;
                }
                else if (WslMirrorPathMapper.TryMirrorLinuxToWindowsPathBySentinel(rawSourcePath, out var mirrorWin))
                {
                    mapped = mirrorWin;
                }

                if (!string.IsNullOrWhiteSpace(mapped))
                {
                    jsonObj["source_path"] = mapped;
                }
                else
                {
                    // If we cannot map it, remove it so deserialization doesn't throw and consumers get no navigation.
                    jsonObj.Remove("source_path");
                    rawSourcePath = null;
                }
            }

            jsonForDeserialization = jsonObj.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch
        {
            // If parsing fails, fall back to post-deserialization handling
        }

        var test = JsonConvert.DeserializeObject<TestSuiteInfo.TestInfo>(jsonForDeserialization);

        if (isWsl && rawSourcePath != null && WslInfo.IsLinuxAbsolutePath(rawSourcePath))
        {
            // For WSL, the source path from the test binary is a Linux absolute path
            // Convert it to a Windows path (UNC for Mode 1, drive path for Mode 2).
            if (wslInfo != null)
            {
                test.SourcePath = wslInfo.ToUncPathEx(rawSourcePath);
            }
            else if (WslPathMapper.TryWslToWindowsPath(rawSourcePath, out var winPath))
            {
                test.SourcePath = (PathEx)winPath;
            }
            else if (WslMirrorPathMapper.TryMirrorLinuxToWindowsPathBySentinel(rawSourcePath, out var mirrorWin))
            {
                test.SourcePath = (PathEx)mirrorWin;
            }
        }
        else if (isWsl)
        {
            // WSL execution but rawSourcePath extraction failed or path is relative.
            // PathEx may have converted a Linux absolute path like "/mnt/c/..." to "\mnt\c\...".
            // Check if it looks like a converted Linux absolute path (starts with \ but not \\).
            var pathStr = (string)test.SourcePath;
            if (!string.IsNullOrEmpty(pathStr) && pathStr.StartsWith(@"\") && !pathStr.StartsWith(@"\\"))
            {
                // Convert back to Linux format and then to Windows
                var linuxPath = "/" + pathStr.Substring(1).Replace('\\', '/');
                if (wslInfo != null)
                {
                    test.SourcePath = wslInfo.ToUncPathEx(linuxPath);
                }
                else if (WslPathMapper.TryWslToWindowsPath(linuxPath, out var winPath))
                {
                    test.SourcePath = (PathEx)winPath;
                }
                else if (WslMirrorPathMapper.TryMirrorLinuxToWindowsPathBySentinel(linuxPath, out var mirrorWin))
                {
                    test.SourcePath = (PathEx)mirrorWin;
                }
            }
            else
            {
                // Relative path - combine with workspace root, but guard against missing/uninitialized source_path.
                if (!string.IsNullOrEmpty(pathStr) && !Path.IsPathRooted(pathStr) && !pathStr.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    test.SourcePath = workspaceRoot + test.SourcePath;
                }
            }
        }
        else
        {
            // Windows workspace - combine with workspace root, but guard against missing/uninitialized source_path.
            var pathStr = (string)test.SourcePath;
            if (!string.IsNullOrEmpty(pathStr) && !Path.IsPathRooted(pathStr) && !pathStr.StartsWith(@"\\", StringComparison.Ordinal))
            {
                test.SourcePath = workspaceRoot + test.SourcePath;
            }
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

    private async Task<bool> ExecuteOperationAsync(string opName, PathEx workspaceRoot, PathEx filePath, string arguments, string profile, IBuildOutputSink outputPane, Func<BuildMessage, Task> buildMessageReporter, Func<string, BuildMessage[]> outputPreprocessor, ITelemetryService ts, ILogger l, CancellationToken ct)
    {
        outputPane.Clear();

        var workingDir = filePath.GetDirectoryName();
        var isWsl = TargetSystemSelection.TryGetWslExecutionContext(workingDir, out var wslInfo, out var distroName);

        ts.TrackEvent(
            opName,
            new[] { ("FilePath", filePath), ("Profile", profile), ("IsWsl", $"{isWsl}"), ("Arguments", arguments) });

        if (isWsl)
        {
            // Special-case: cargo fmt should prefer a Windows-native run (when available) for performance.
            // If cargo.exe/rustfmt are not available on Windows, fall back to WSL fmt (currently /mnt mapping).
            //
            // This applies only to Mode 2 (Windows workspace + WSL selected). For Mode 1 (WSL UNC workspace),
            // we always run inside WSL because sources are already on the Linux filesystem.
            if (wslInfo == null && IsSourceMutatingCargoOperation(opName))
            {
                var fmtResult = await TryRunCargoFmtWindowsNativeAsync(
                    opName,
                    arguments,
                    workingDir,
                    redirector: new BuildOutputRedirector(outputPane, workingDir, buildMessageReporter, outputPreprocessor),
                    ct: ct);

                if (fmtResult.HasValue)
                {
                    return fmtResult.Value;
                }
            }

            return await RunWslAsync(
                wslInfo,
                distroName,
                opName,
                arguments,
                workspaceRoot,
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

    /// <summary>
    /// Attempts to run cargo fmt on Windows (cargo.exe) for performance.
    /// Returns:
    /// - true/false when a Windows-native fmt was attempted (success indicated by return value),
    /// - null when cargo.exe is not available on Windows (caller should fall back).
    /// </summary>
    private static async Task<bool?> TryRunCargoFmtWindowsNativeAsync(string opName, string arguments, PathEx workingDir, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        // Only for fmt.
        if (!IsSourceMutatingCargoOperation(opName))
        {
            return null;
        }

        // Check for cargo.exe.
        var cargoExe = Constants.CargoExe.FindInPath();
        if (string.IsNullOrWhiteSpace(cargoExe) || !File.Exists(cargoExe))
        {
            return null;
        }

        string cargoVersion = string.Empty;
        string rustfmtVersion = string.Empty;
        try
        {
            // Best-effort: capture versions without invoking WSL detection.
            using (var v = ProcessRunner.Run(
                       cargoExe,
                       new[] { "--version" },
                       workingDir,
                       ImmutableDictionary<string, string>.Empty,
                       ct))
            {
                await v;
                cargoVersion = string.Join(string.Empty, v.StandardOutputLines).Trim();
            }

            var rustfmtExe = "rustfmt.exe".FindInPath() ?? "rustfmt".FindInPath();
            if (!string.IsNullOrWhiteSpace(rustfmtExe) && File.Exists(rustfmtExe))
            {
                using var rv = ProcessRunner.Run(
                    rustfmtExe,
                    new[] { "--version" },
                    workingDir,
                    ImmutableDictionary<string, string>.Empty,
                    ct);
                await rv;
                rustfmtVersion = string.Join(string.Empty, rv.StandardOutputLines).Trim();
            }
        }
        catch
        {
            // ignore
        }

        redirector?.WriteLineWithoutProcessing($"");
        redirector?.WriteLineWithoutProcessing($"==== Build step (Windows): Started ====");
        if (!string.IsNullOrWhiteSpace(cargoVersion))
        {
            redirector?.WriteLineWithoutProcessing($"        Using : {cargoVersion}");
        }

        if (!string.IsNullOrWhiteSpace(rustfmtVersion))
        {
            redirector?.WriteLineWithoutProcessing($"        Using : {rustfmtVersion}");
        }

        redirector?.WriteLineWithoutProcessing($"         Path : {cargoExe}");
        redirector?.WriteLineWithoutProcessing($"    Arguments : {arguments}");
        redirector?.WriteLineWithoutProcessing($"   WorkingDir : {workingDir}");
        redirector?.WriteLineWithoutProcessing($"");

        using var proc = ProcessRunner.Run(
            cargoExe,
            new[] { arguments },
            workingDir,
            env: null,
            visible: false,
            redirector: redirector,
            quoteArgs: false,
            outputEncoding: Encoding.UTF8,
            cancellationToken: ct);

        var whnd = proc.WaitHandle;
        if (whnd == null)
        {
            redirector?.WriteErrorLineWithoutProcessing($"Error - Failed to start '{cargoExe}'");
            redirector?.WriteLineWithoutProcessing("==== Build step (Windows): Finished ====\n");
            return false;
        }

        try
        {
            await Task.Run(() => whnd.WaitOne(), ct);
        }
        catch (OperationCanceledException)
        {
            proc.Kill();
            redirector?.WriteErrorLineWithoutProcessing("====  Build step (Windows) canceled ====\n");
            return false;
        }

        proc.Wait();
        redirector?.WriteLineWithoutProcessing("==== Build step (Windows): Finished ====\n");

        // If Windows-native fmt failed because rustfmt isn't installed on Windows, fall back to WSL fmt.
        // (Other failures should be surfaced to the user; WSL would likely fail similarly.)
        if (proc.ExitCode != 0)
        {
            var stderr = string.Join("\n", proc.StandardErrorLines ?? Array.Empty<string>());
            var lower = stderr.ToLowerInvariant();
            if (lower.Contains("rustfmt") && (lower.Contains("not installed") || lower.Contains("not found") || lower.Contains("not recognized") || lower.Contains("could not execute")))
            {
                return null;
            }
        }

        return proc.ExitCode == 0;
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

    private static async Task<bool> RunWslAsync(WslInfo wslInfo, string distroName, string opName, string arguments, PathEx workspaceRoot, PathEx workingDir, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        EnsureArg.IsNotEmptyOrWhiteSpace(arguments, nameof(arguments));

        EnsureArg.IsNotNullOrWhiteSpace(distroName, nameof(distroName));

        // Mode 1: WSL UNC workspace -> execute directly in that workspace path (no mirror).
        // Mode 2: Windows-local workspace + WSL execution -> execute on WSL-native mirror (rsync-on-build).
        WslMirrorConfig mirrorConfig = null;
        string linuxWorkingDir;
        if (wslInfo != null)
        {
            linuxWorkingDir = wslInfo.ToLinuxPath(workingDir);
        }
        else
        {
            // IMPORTANT:
            // Some cargo operations *modify source files* (e.g. `cargo fmt`) and must therefore run
            // against the real Windows workspace (via /mnt) so changes apply to the user's files.
            // Mirror mode is safe for operations that are read-only w.r.t the source tree (build/test/clippy),
            // and is required to avoid the /mnt "rebuild every time" issue.
            var useMirror = !IsSourceMutatingCargoOperation(opName);

            if (useMirror)
            {
                var mirror = WslMirrorManager.GetOrCreate(workspaceRoot, distroName);
                await mirror.EnsureSynchronizedAsync(ct);
                mirrorConfig = mirror.Config;

                // Working dir must be inside mirror.
                if (mirrorConfig != null && WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)workingDir, mirrorConfig, out var mirrorWd))
                {
                    linuxWorkingDir = mirrorWd;
                }
                else if (!WslPathMapper.TryWindowsToWslPath(workingDir, out linuxWorkingDir))
                {
                    // Fallback (legacy): /mnt mapping.
                    throw new InvalidOperationException($"Unable to map working directory '{workingDir}' to a WSL path.").AddExitCode(-1);
                }
            }
            else
            {
                // Source-mutating operation (e.g. fmt): run in WSL but target the Windows tree via /mnt.
                mirrorConfig = null;
                if (!WslPathMapper.TryWindowsToWslPath(workingDir, out linuxWorkingDir))
                {
                    throw new InvalidOperationException($"Unable to map working directory '{workingDir}' to a WSL path.").AddExitCode(-1);
                }
            }
        }

        var cargoVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine("cargo", "--version", workingDir, ct);
        var toolVersion = await ToolchainServiceExtensions.GetCommandOutputSingleLine(opName, "--version", workingDir, ct);

        // Convert arguments to array, handling quoted strings properly, and map any Windows paths to Linux.
        var argList = ParseArgumentsForWsl(arguments, wslInfo, mirrorConfig);
        var argsForLog = string.Join(" ", argList.Select(ProcessRunner.QuoteSingleArgument));

        redirector?.WriteLineWithoutProcessing($"");
        redirector?.WriteLineWithoutProcessing($"==== Build step (WSL): Started ====");
        redirector?.WriteLineWithoutProcessing($"        Using : {cargoVersion}");
        redirector?.WriteLineWithoutProcessing($"        Using : {toolVersion}");
        redirector?.WriteLineWithoutProcessing($"       Distro : {distroName}");
        redirector?.WriteLineWithoutProcessing($"    Arguments : {argsForLog}");
        redirector?.WriteLineWithoutProcessing($"   WorkingDir : {linuxWorkingDir} (WSL)");
        redirector?.WriteLineWithoutProcessing($"");

        using var process = wslInfo != null
            ? ToolchainServiceExtensions.RunCargoInWsl(wslInfo, argList, linuxWorkingDir, redirector, ct)
            : ToolchainServiceExtensions.RunCargoInWsl(distroName, argList, linuxWorkingDir, redirector, ct);
        var whnd = process.WaitHandle;
        if (whnd == null)
        {
            redirector?.WriteErrorLineWithoutProcessing($"Error - Failed to start cargo in WSL distro '{distroName}'");
            return false;
        }

        try
        {
            // Wait for the process to exit, but allow cancellation.
            await Task.Run(() => whnd.WaitOne(), ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            redirector?.WriteErrorLineWithoutProcessing("====  Build step (WSL) canceled ====\n");
            return false;
        }

        // Ensure exit code is available.
        process.Wait();

        redirector?.WriteLineWithoutProcessing("==== Build step (WSL): Finished ====\n");

        // Best-effort: keep Windows source tree in sync for files cargo may mutate during mirror runs.
        // The most important one is Cargo.lock (cargo can update it during build/test/clippy).
        //
        // If we don't sync it back, subsequent mirror syncs may overwrite it from Windows again and cargo will
        // rewrite it repeatedly, which can look like "rebuilds every time" even when the user made no changes.
        if (process.ExitCode == 0 &&
            wslInfo == null &&
            mirrorConfig != null &&
            ShouldSyncBackCargoLock(opName))
        {
            TrySyncBackCargoLock(workspaceRoot, distroName, mirrorConfig, redirector);
        }

        return process.ExitCode == 0;
    }

    private static bool IsSourceMutatingCargoOperation(string opName)
    {
        // `cargo fmt` rewrites files in-place. In mirror mode that would only change the mirror,
        // not the actual Windows workspace the user is editing.
        return string.Equals(opName, "fmt", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(opName, "Fmt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSyncBackCargoLock(string opName)
    {
        // cargo can update Cargo.lock during many commands. Keep this conservative but useful.
        return string.Equals(opName, "build", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(opName, "clippy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(opName, "Clippy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(opName, "test", StringComparison.OrdinalIgnoreCase);
    }

    private static void TrySyncBackCargoLock(PathEx workspaceRootWindows, string distroName, WslMirrorConfig cfg, ProcessOutputRedirector redirector)
    {
        try
        {
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.MirrorWorkspaceRootLinux))
            {
                return;
            }

            var wsRoot = (string)workspaceRootWindows.GetFullPath();
            if (string.IsNullOrWhiteSpace(wsRoot))
            {
                return;
            }

            // Build mirror lock path (Linux) and map it to UNC so Windows can read it.
            var linuxLock = cfg.MirrorWorkspaceRootLinux.TrimEnd('/') + "/Cargo.lock";
            var uncRoot = $"\\\\wsl.localhost\\{distroName}\\";
            if (!WslInfo.TryParse(uncRoot, out var info) || info == null)
            {
                return;
            }

            var uncLock = info.ToUncPath(linuxLock);
            if (!File.Exists(uncLock))
            {
                return;
            }

            var winLock = Path.Combine(wsRoot, "Cargo.lock");

            // Copy only if content differs (avoid needless mtime bumps).
            var srcBytes = File.ReadAllBytes(uncLock);
            if (File.Exists(winLock))
            {
                var dstBytes = File.ReadAllBytes(winLock);
                if (dstBytes.Length == srcBytes.Length && dstBytes.SequenceEqual(srcBytes))
                {
                    return;
                }
            }

            File.WriteAllBytes(winLock, srcBytes);
            redirector?.WriteLineWithoutProcessing($"(WSL) Updated '{winLock}' from mirror Cargo.lock.");
        }
        catch (Exception ex)
        {
            // Best-effort: never fail the build because of sync-back.
            try
            {
                redirector?.WriteLineWithoutProcessing($"(WSL) Note: failed to sync back Cargo.lock from mirror. {ex.Message}");
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Parses cargo arguments and converts any Windows paths to the correct Linux paths for WSL.
    /// - Mode 1: UNC workspace -> direct UNC-to-Linux mapping
    /// - Mode 2: Windows workspace -> mirror Linux paths (not /mnt).
    /// </summary>
    private static string[] ParseArgumentsForWsl(string arguments, WslInfo wslInfo, WslMirrorConfig mirrorConfig)
    {
        // Robust tokenization (supports nested quoting / complex --config strings).
        var rawArgs = SplitCommandLineWindows(arguments);
        return rawArgs
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => ConvertPathArgumentForWsl(a, wslInfo, mirrorConfig))
            .ToArray();
    }

    private static string[] SplitCommandLineWindows(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return Array.Empty<string>();
        }

        // CommandLineToArgvW implements the Windows command-line parsing rules (quotes/backslashes),
        // which is much more correct than hand-rolled splitting for cargo's --config and similar args.
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero || argc <= 0)
        {
            // Very conservative fallback; should be rare.
            return commandLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        try
        {
            var args = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                var p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p);
            }

            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// If an argument is a Windows path that WSL will see, convert it to a Linux path.
    /// - Mode 1: \\wsl.localhost\... -> /home/...
    /// - Mode 2: C:\... -> /mnt/c/...
    /// </summary>
    private static string ConvertPathArgumentForWsl(string arg, WslInfo wslInfo, WslMirrorConfig mirrorConfig)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return arg;
        }

        // Mode 1: UNC workspace.
        if (wslInfo != null)
        {
            // Check if this looks like a path under our WSL distro
            if (arg.StartsWith(wslInfo.UncDistroRoot, StringComparison.OrdinalIgnoreCase))
            {
                return wslInfo.ToLinuxPath(arg);
            }

            // Handle common patterns like --foo=\\wsl.localhost\Distro\path or -C=\\wsl$...
            var idx = arg.IndexOf(wslInfo.UncDistroRoot, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var prefix = arg.Substring(0, idx);
                var uncPath = arg.Substring(idx);
                return prefix + wslInfo.ToLinuxPath(uncPath);
            }
        }

        // Mode 2: Windows workspace.
        // Prefer mirror mapping when available; fall back to /mnt mapping (legacy) otherwise.
        if (mirrorConfig != null)
        {
            return WslMirrorPathMapper.ConvertArgumentWindowsPathsToMirror(arg, mirrorConfig);
        }

        return WslPathMapper.ConvertArgumentWindowsPathsToWsl(arg);
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


using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using KS.RustAnalyzer.TestAdapter.Common;
using Newtonsoft.Json;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

/// <summary>
/// Following files not in path is a catastrophic error.
/// - rustup.exe
/// - cargo.exe
///
/// Hence they are in prereq checks.
/// </summary>
public static class ToolchainServiceExtensions
{
    public const string AlwaysAvailableTarget = "x86_64-pc-windows-msvc";

    public static readonly string[] CommonTargets = new[]
    {
        "wasm32-unknown-unknown",
    };

    private static readonly Regex NameCracker =
        new(@"^((?<name>.*)(?<aOrD> \((active|active, default|default)\))?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.RightToLeft);

    private static readonly Regex VersionCracker =
        new(@"^rustc (?<version>\d+.\d+.\d+(-.*)?) (\(.* (?<date>\d{4}-\d{2}-\d{2})\))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> OpNameToToolNameMapper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "rustup", "rustup" },
        { "cargo", "cargo" },
        { "build", "rustc" },
        { "clean", "rustc" },
        { "test", "rustc" },
        { "fmt", "cargo-fmt" },
        { "clippy", "cargo-clippy" },
    };

    /// <summary>
    /// Not finding rustup.exe is a catastrophic error. Hence in prereq checks.
    /// </summary>
    public static PathEx GetRustupPath() =>
        (PathEx)Constants.RustUpExe.FindInPath();

    public static PathEx GetRustupSettingsPath() =>
        GetRustupPath().GetDirectoryName().GetDirectoryName().GetDirectoryName() + ".rustup" + Constants.RustupSettingsFileName;

    public static async Task<(PathEx Bin, PathEx Lib)> GetBinAndLibPathsAsync(PathEx workingDirectory, CancellationToken ct)
    {
        var root = GetRustupSettingsPath().GetDirectoryName() + @$"toolchains\{await GetDefaultToolchainAsync(workingDirectory, ct)}";
        return (root + "bin", root + $@"lib\rustlib\{AlwaysAvailableTarget}\lib");
    }

    public abstract class RustupShowOutput
    {
        private RustupShowOutput()
        {
        }

        public sealed class Simulated : RustupShowOutput
        {
            public string[] Value { get; }

            public Simulated(string[] value) => Value = value;
        }

        public sealed class Real : RustupShowOutput
        {
            public Real()
            {
            }
        }
    }

    public static async Task<Toolchain[]> GetInstalledToolchainsAsync(RustupShowOutput rustupShowOutput, PathEx workingDirectory, CancellationToken ct)
    {
        string[] output = null;
        switch (rustupShowOutput)
        {
            case RustupShowOutput.Simulated simulated:
                {
                    output = simulated.Value;
                    break;
                }

            case RustupShowOutput.Real _:
                {
                    output = await GetCommandOutput("rustup", "show --verbose", workingDirectory, ct);
                    break;
                }

            default:
                throw new NotSupportedException($"Unsupported rustup show output type: {rustupShowOutput.GetType()}");
        }

        var tcRaw = output
            .Select(x => x.Trim())
            .Where(x => !x.IsNullOrEmptyOrWhiteSpace())
            .SkipWhile(x => !x.Equals("installed toolchains"))
            .Skip(2)
            .TakeWhile(x => !x.Equals("active toolchain"))
            .ToList();

        var tcs1 = Enumerable.Range(0, tcRaw.Count / 3)
            .Select(i => (tcRaw[i * 3], tcRaw[(i * 3) + 1]));
        var tcs = tcs1
            .Select(x => (NameCracker.Match(x.Item1), VersionCracker.Match(x.Item2)))
            .Where(x => x.Item1.Success && x.Item2.Success)
            .Select(x =>
                new Toolchain
                {
                    Name = x.Item1.Groups["name"].Value,
                    Version = $"{x.Item2.Groups["version"].Value} ({x.Item2.Groups["date"].Value})",
                    IsActive = x.Item1.Groups["aOrD"].Value?.Contains("active") ?? false,
                })
            .OrderBy(tc => tc.Name)
            .ThenBy(tc => tc.Version)
            .ToArray();

        return tcs;
    }

    public static async Task<string[]> GetTargets(CancellationToken ct)
    {
        var output = await GetCommandOutput("rustup", "target list", GetRustupPath().GetDirectoryName(), ct);
        var targets = output
            .Where(x => !x.IsNullOrEmptyOrWhiteSpace())
            .Select(x => x.Replace(" (installed)", string.Empty))
            .Where(x => x != AlwaysAvailableTarget)
            .Where(x => !CommonTargets.Contains(x))
            .Select(x => x.Trim())
            .OrderBy(t => t);

        return CommonTargets.OrderBy(x => x).Concat(targets).ToArray();
    }

    public static async Task<string> GetDefaultToolchainAsync(PathEx workingDirectory, CancellationToken ct)
    {
        return (await GetInstalledToolchainsAsync(new RustupShowOutput.Real(), workingDirectory, ct)).Where(x => x.IsActive).First().Name;
    }

    public static async Task<TestContainer> ReadTestContainerAsync(this PathEx @this, CancellationToken ct)
    {
        return JsonConvert.DeserializeObject<TestContainer>(await @this.ReadAllTextAsync(ct));
    }

    public static Task WriteTestContainerAsync(this PathEx @this, PathEx manifestPath, PathEx targetPath, string additionalTestDiscoveryArguments, string additionalTestExecutionArguments, string testExecutionEnvironment, string profile, PathEx[] testExes, CancellationToken ct)
    {
        return @this.WriteAllTextAsync(
            JsonConvert.SerializeObject(
                new TestContainer
                {
                    ThisPath = @this,
                    Manifest = manifestPath,
                    TargetDir = targetPath,
                    AdditionalTestDiscoveryArguments = additionalTestDiscoveryArguments,
                    AdditionalTestExecutionArguments = additionalTestExecutionArguments,
                    TestExecutionEnvironment = testExecutionEnvironment,
                    Profile = profile,
                    TestExes = testExes,
                },
                Formatting.Indented,
                new PathExJsonConverter()),
            ct);
    }

    public static void CleanTestContainers(this PathEx @this, IEnumerable<PathEx> except)
    {
        if (!@this.DirectoryExists())
        {
            return;
        }

        Directory.EnumerateFiles(@this, $"*{Constants.TestsContainerExtension}")
            .Where(f => !except.Any(c => c == (PathEx)f))
            .ForEach(File.Delete);
    }

    public static async Task<bool> RunAsync(this PathEx exeFullPath, string args, PathEx workingDir, ProcessOutputRedirector redirector, string finishedMsg, string cancelledMsg, CancellationToken ct)
    {
        using var process = ProcessRunner.Run(
            exeFullPath,
            new[] { args },
            workingDir,
            env: null,
            visible: false,
            redirector: redirector,
            quoteArgs: false,
            outputEncoding: Encoding.UTF8,
            cancellationToken: ct);
        var whnd = process.WaitHandle;
        if (whnd == null)
        {
            // Process failed to start, and any exception message has
            // already been sent through the redirector
            redirector.WriteErrorLineWithoutProcessing(string.Format("Error - Failed to start '{0}'", exeFullPath));
            return false;
        }
        else
        {
            var finished = await Task.Run(() => whnd.WaitOne());
            if (finished)
            {
                Debug.Assert(process.ExitCode.HasValue, "process has not really exited");

                // there seems to be a case when we're signalled as completed, but the
                // process hasn't actually exited
                process.Wait();

                redirector.WriteLineWithoutProcessing(finishedMsg);

                return process.ExitCode == 0;
            }
            else
            {
                process.Kill();
                redirector.WriteErrorLineWithoutProcessing(cancelledMsg);

                return false;
            }
        }
    }

    public static async Task SetToolchainOverrideAsync(this PathEx workspaceRoot, string toolChain, ILogger l, CancellationToken ct)
    {
        var opName = "rustup";
        var args = $"override set {toolChain}";

        l.WriteLine("Running: {0} {1}", opName, args);
        l.WriteLine("Workspace: {0}", workspaceRoot);

        var output = await GetCommandOutput(opName, args, workspaceRoot, ct);
        l.WriteLine("{0}", string.Join("\n", output));
    }

    public static async Task<string[]> GetCommandOutput(string opName, string args, PathEx workingDirectory, CancellationToken ct)
    {
        // Mode 1: WSL UNC workspace
        // Mode 2: Windows-local workspace + WSL execution (if selected)
        if (TargetSystemSelection.TryGetWslExecutionContext(workingDirectory, out var wslInfo, out var distroName))
        {
            return await GetCommandOutputWsl(opName, args, workingDirectory, wslInfo, distroName, ct);
        }

        // Safety: if this looks like a WSL UNC path but parsing failed, don't fall back to Windows tools.
        // This avoids confusing "cargo.exe not found" failures later.
        var wd = (string)workingDirectory;
        if (!string.IsNullOrEmpty(wd) && wd.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { $"WSL path detected but distro could not be parsed from '{wd}'. Please re-open the folder under a valid WSL UNC path (\\\\wsl.localhost\\<distro>\\...) or select a WSL Target System." };
        }

        var toolName = OpNameToToolNameMapper[opName];
        using var proc = ProcessRunner.Run("cmd.exe", new[] { "/c", $"{toolName} {args}" }, workingDirectory, ImmutableDictionary<string, string>.Empty, ct);

        var ec = await proc;
        var output = proc.StandardOutputLines.Concat(proc.StandardErrorLines).ToArray();
        if (ec != 0)
        {
            return new[] { $"{toolName} returned {ec}.\nOutput: {string.Join(Environment.NewLine, output)}" };
        }

        return output;
    }

    /// <summary>
    /// Executes a command inside WSL using wsl.exe.
    /// </summary>
    public static async Task<string[]> GetCommandOutputWsl(string opName, string args, PathEx workingDirectory, WslInfo wslInfo, string distroName, CancellationToken ct)
    {
        var toolName = OpNameToToolNameMapper[opName];
        var linuxWorkingDir = GetLinuxWorkingDirectory(workingDirectory, wslInfo);
        if (linuxWorkingDir == null)
        {
            return new[] { $"Unable to map '{workingDirectory}' to a Linux working directory for WSL execution." };
        }

        // IMPORTANT:
        // Direct `wsl.exe --exec cargo ...` can fail on some setups because the non-interactive PATH
        // does not include ~/.cargo/bin (rustup installs cargo there).
        // Run through /bin/bash -lc when needed (handled by RunInWsl).
        var splitArgs = SplitCommandLineArgs(args);
        using var proc = RunInWsl(distroName, toolName, splitArgs.ToArray(), linuxWorkingDir, env: null, ct);

        var ec = await proc;
        var output = proc.StandardOutputLines.Concat(proc.StandardErrorLines).ToArray();
        if (ec != 0)
        {
            return new[] { $"{toolName} (WSL) returned {ec}.\nOutput: {string.Join(Environment.NewLine, output)}" };
        }

        return output;
    }

    /// <summary>
    /// Runs a process in WSL. Returns the ProcessRunner for capturing output.
    /// </summary>
    public static ProcessRunner RunInWsl(WslInfo wslInfo, string command, string[] args, string linuxWorkingDir, IDictionary<string, string> env, CancellationToken ct)
    {
        EnsureArg.IsNotNull(wslInfo, nameof(wslInfo));

        return RunInWsl(wslInfo.DistroName, command, args, linuxWorkingDir, env, redirector: null, ct);
    }

    /// <summary>
    /// Runs a process in WSL. Streams output via the provided redirector.
    /// </summary>
    public static ProcessRunner RunInWsl(WslInfo wslInfo, string command, string[] args, string linuxWorkingDir, IDictionary<string, string> env, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        EnsureArg.IsNotNull(wslInfo, nameof(wslInfo));

        return RunInWsl(wslInfo.DistroName, command, args, linuxWorkingDir, env, redirector, ct);
    }

    /// <summary>
    /// Runs a process in WSL by distro name. Supports "Mode 2" where workspace is Windows-local.
    /// </summary>
    public static ProcessRunner RunInWsl(string distroName, string command, string[] args, string linuxWorkingDir, IDictionary<string, string> env, CancellationToken ct)
    {
        return RunInWsl(distroName, command, args, linuxWorkingDir, env, redirector: null, ct);
    }

    /// <summary>
    /// Runs a process in WSL by distro name. Supports streaming output via <paramref name="redirector"/>.
    /// </summary>
    public static ProcessRunner RunInWsl(string distroName, string command, string[] args, string linuxWorkingDir, IDictionary<string, string> env, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        EnsureArg.IsNotNullOrWhiteSpace(distroName, nameof(distroName));

        var wslExePath = WslInfo.GetWslExePath();

        // Build wsl.exe arguments: -d <distro> --cd <dir> --exec <command> <args>
        var wslArgs = new List<string> { "-d", distroName };

        if (!string.IsNullOrEmpty(linuxWorkingDir))
        {
            wslArgs.Add("--cd");
            wslArgs.Add(linuxWorkingDir);
        }

        wslArgs.Add("--exec");

        // NOTE:
        // On some systems `wsl.exe --exec cargo ...` fails because PATH is minimal and doesn't include ~/.cargo/bin.
        // Also, passing env vars to the Linux process via Windows env is unreliable and can require WSLENV.
        // So, when the command is not an absolute Linux path OR env overrides are provided, run via:
        //   /bin/bash -lc "env KEY='VALUE' ... command 'arg1' ..."
        var needsShell = (env != null && env.Any()) || !WslInfo.IsLinuxAbsolutePath(command);
        if (needsShell)
        {
            wslArgs.Add("/bin/bash");
            wslArgs.Add("-lc");
            wslArgs.Add(BuildBashCommand(command, args, env));
        }
        else
        {
            wslArgs.Add(command);
            if (args != null && args.Length > 0)
            {
                wslArgs.AddRange(args);
            }
        }

        // NOTE: ProcessStartInfo.WorkingDirectory must be a valid Windows directory.
        // wsl.exe handles the Linux-side cwd via --cd, but we still set a safe Windows cwd here.
        // Use the redirector overload so build output can stream live.
        var envForProc = env ?? (IDictionary<string, string>)ImmutableDictionary<string, string>.Empty;
        return ProcessRunner.Run(
            wslExePath,
            wslArgs,
            Environment.SystemDirectory,
            envForProc,
            visible: false,
            redirector: redirector,
            cancellationToken: ct);
    }

    /// <summary>
    /// Runs cargo via WSL and returns the ProcessRunner for output capture.
    /// </summary>
    public static ProcessRunner RunCargoInWsl(WslInfo wslInfo, string[] cargoArgs, string linuxWorkingDir, CancellationToken ct)
    {
        return RunInWsl(wslInfo, Constants.WslCargoExe, cargoArgs, linuxWorkingDir, null, ct);
    }

    public static ProcessRunner RunCargoInWsl(string distroName, string[] cargoArgs, string linuxWorkingDir, CancellationToken ct)
    {
        return RunInWsl(distroName, Constants.WslCargoExe, cargoArgs, linuxWorkingDir, null, ct);
    }

    public static ProcessRunner RunCargoInWsl(WslInfo wslInfo, string[] cargoArgs, string linuxWorkingDir, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        return RunInWsl(wslInfo, Constants.WslCargoExe, cargoArgs, linuxWorkingDir, env: null, redirector: redirector, ct);
    }

    public static ProcessRunner RunCargoInWsl(string distroName, string[] cargoArgs, string linuxWorkingDir, ProcessOutputRedirector redirector, CancellationToken ct)
    {
        return RunInWsl(distroName, Constants.WslCargoExe, cargoArgs, linuxWorkingDir, env: null, redirector: redirector, ct);
    }

    /// <summary>
    /// Gets the bin and lib paths for debugging from within WSL.
    /// </summary>
    public static async Task<(string Bin, string Lib)> GetWslBinAndLibPathsAsync(WslInfo wslInfo, PathEx workingDirectory, CancellationToken ct)
    {
        EnsureArg.IsNotNull(wslInfo, nameof(wslInfo));

        var linuxWorkingDir = wslInfo.ToLinuxPath(workingDirectory);

        // Get sysroot from rustc inside WSL (run via RunInWsl to ensure rustc is discoverable)
        using var proc = RunInWsl(wslInfo, "rustc", new[] { "--print", "sysroot" }, linuxWorkingDir, env: null, ct);
        var ec = await proc;

        if (ec != 0 || !proc.StandardOutputLines.Any())
        {
            // Fallback: return empty paths, debugger will rely on WSL environment
            return (string.Empty, string.Empty);
        }

        var sysroot = proc.StandardOutputLines.First().Trim();

        // Derive bin and lib paths from sysroot
        var bin = $"{sysroot}/bin";

        // Get the target triple
        using var tripleProc = RunInWsl(wslInfo, "rustc", new[] { "-vV" }, linuxWorkingDir, env: null, ct);
        var tripleEc = await tripleProc;

        var targetTriple = "x86_64-unknown-linux-gnu"; // Default fallback
        if (tripleEc == 0)
        {
            var hostLine = tripleProc.StandardOutputLines.FirstOrDefault(l => l.StartsWith("host:"));
            if (hostLine != null)
            {
                targetTriple = hostLine.Substring("host:".Length).Trim();
            }
        }

        var lib = $"{sysroot}/lib/rustlib/{targetTriple}/lib";

        return (bin, lib);
    }

    public static async Task<string> GetCommandOutputSingleLine(string opName, string versionArgs, PathEx workingDirectory, CancellationToken ct)
    {
        var lines = await GetCommandOutput(opName, versionArgs, workingDirectory, ct);

        return string.Join(string.Empty, lines.Where(l => !l.IsNullOrEmptyOrWhiteSpace()));
    }

    public static Task<bool> InstallToolchain(string commandline, IBuildOutputSink bos, CancellationToken ct)
    {
        bos.Clear();

        var rustupPath = GetRustupPath();
        return rustupPath.RunAsync(
            commandline,
            rustupPath.GetPathRoot(),
            new BuildOutputRedirector(
                bos,
                rustupPath.GetFileName(),
                _ => Task.CompletedTask,
                x => new[] { new StringBuildMessage { Message = x } }),
            $"==== {rustupPath.GetFileName()} done. ====\n",
            $"==== {rustupPath.GetFileName()} cancelled.====\n",
            ct);
    }

    /// <summary>
    /// Splits a command-line argument string into tokens, respecting double quotes.
    /// This is intentionally simple but avoids the most common WSL breakage from string.Split(' ').
    /// </summary>
    private static IEnumerable<string> SplitCommandLineArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string BuildBashCommand(string command, string[] args, IDictionary<string, string> env)
    {
        var parts = new List<string>();

        if (env != null && env.Any())
        {
            parts.Add("env");
            foreach (var kv in env.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)))
            {
                parts.Add($"{kv.Key}={QuoteForPosixShell(kv.Value ?? string.Empty)}");
            }
        }

        parts.Add(QuoteForPosixShell(command));
        if (args != null && args.Length > 0)
        {
            parts.AddRange(args.Where(a => a != null).Select(QuoteForPosixShell));
        }

        return string.Join(" ", parts);
    }

    private static string QuoteForPosixShell(string arg)
    {
        // Single-quote for POSIX shells, escaping embedded single quotes.
        // foo'bar => 'foo'"'"'bar'
        if (arg == null)
        {
            return "''";
        }

        return "'" + arg.Replace("'", "'\"'\"'") + "'";
    }

    private static string GetLinuxWorkingDirectory(PathEx workingDirectory, WslInfo wslInfo)
    {
        if (wslInfo != null)
        {
            return wslInfo.ToLinuxPath(workingDirectory);
        }

        // Mode 2: workspace is Windows-local
        return WslPathMapper.TryWindowsToWslPath(workingDirectory, out var linuxWorkingDir) ? linuxWorkingDir : null;
    }
}

public struct Toolchain
{
    public string Name { get; set; }

    public string Version { get; set; }

    public bool IsActive { get; set; }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Execution context for WSL (Windows Subsystem for Linux).
/// Executes commands via wsl.exe.
/// </summary>
public sealed class WslExecutionContext : IExecutionContext
{
    private const string WslExePath = @"C:\Windows\System32\wsl.exe";

    private readonly string _distroName;
    private readonly IPathMapper _pathMapper;

    /// <summary>
    /// Creates a new WSL execution context for the specified distro.
    /// </summary>
    /// <param name="distroName">The WSL distribution name.</param>
    public WslExecutionContext(string distroName)
    {
        _distroName = distroName ?? throw new ArgumentNullException(nameof(distroName));
        _pathMapper = new WslPathMapper(distroName);
    }

    /// <summary>
    /// Gets the WSL distribution name.
    /// </summary>
    public string DistroName => _distroName;

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Wsl;

    /// <inheritdoc/>
    public ExecutionCapabilities Capabilities =>
        ExecutionCapabilities.CanBuild |
        ExecutionCapabilities.CanRunLsp |
        ExecutionCapabilities.CanRunTests |
        ExecutionCapabilities.CanDebug;

    /// <inheritdoc/>
    public string CargoCommand => "cargo";

    /// <inheritdoc/>
    public string RustupCommand => "rustup";

    /// <inheritdoc/>
    public string BinaryExtension => string.Empty;

    /// <inheritdoc/>
    public async Task<ProcessResult> ExecuteAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        IDictionary<string, string> environment,
        IProcessOutputSink outputSink,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Build wsl.exe command line
        var wslArgs = BuildWslArguments(command, arguments, workingDirectory, environment);

        // Create a redirector to stream output to the sink in real-time (same pattern as SshExecutionContext)
        ProcessOutputRedirector redirector = outputSink != null
            ? new OutputSinkRedirector(outputSink)
            : null;

        using var proc = ProcessRunner.Run(
            WslExePath,
            wslArgs,
            workingDirectory: null, // wsl.exe handles this via --cd
            env: null,
            visible: false,
            redirector: redirector,
            quoteArgs: true,
            outputEncoding: null,
            errorEncoding: null,
            cancellationToken: ct);

        outputSink?.OnProcessStarted(proc.ProcessId);

        var exitCode = await proc;

        stopwatch.Stop();
        outputSink?.OnProcessExited(exitCode);

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = proc.StandardOutputLines.ToList(),
            StandardError = proc.StandardErrorLines.ToList(),
            Duration = stopwatch.Elapsed,
        };
    }

    /// <inheritdoc/>
    public async Task<string[]> ExecuteAndCaptureAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        CancellationToken ct)
    {
        var result = await ExecuteAsync(
            command,
            arguments,
            workingDirectory,
            environment: null,
            outputSink: null,
            ct).ConfigureAwait(false);

        // Log stderr for debugging - it often contains warnings/info messages
        // (e.g., "Updating crates.io index", "Downloading xyz", etc.)
        if (result.StandardError.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[WslExecutionContext] {command} stderr:");
            foreach (var line in result.StandardError.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                System.Diagnostics.Debug.WriteLine($"  {line}");
            }
        }

        // Only return stdout - stderr breaks JSON parsing but is logged above
        return result.StandardOutput
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task<bool> FileExistsAsync(RemotePath path, CancellationToken ct)
    {
        // Use sh -c to properly interpret shell operators like &&
        // The path is escaped to handle spaces and special characters
        var escapedPath = EscapeForShell((string)path);
        var result = await ExecuteAndCaptureAsync(
            "sh",
            new[] { "-c", $"test -f {escapedPath} && echo 1" },
            new RemotePath("/", TargetKind.Wsl),
            ct).ConfigureAwait(false);

        return result.Any(l => l.Trim() == "1");
    }

    /// <inheritdoc/>
    public async Task<bool> DirectoryExistsAsync(RemotePath path, CancellationToken ct)
    {
        // Use sh -c to properly interpret shell operators like &&
        // The path is escaped to handle spaces and special characters
        var escapedPath = EscapeForShell((string)path);
        var result = await ExecuteAndCaptureAsync(
            "sh",
            new[] { "-c", $"test -d {escapedPath} && echo 1" },
            new RemotePath("/", TargetKind.Wsl),
            ct).ConfigureAwait(false);

        return result.Any(l => l.Trim() == "1");
    }

    /// <inheritdoc/>
    public async Task<string> ReadFileAsync(RemotePath path, CancellationToken ct)
    {
        var result = await ExecuteAndCaptureAsync(
            "cat",
            new[] { (string)path },
            new RemotePath("/", TargetKind.Wsl),
            ct).ConfigureAwait(false);

        return string.Join(Environment.NewLine, result);
    }

    /// <inheritdoc/>
    public async Task<RemotePath> GetRustAnalyzerPathAsync(CancellationToken ct)
    {
        // Try to find rust-analyzer via which
        var result = await ExecuteAndCaptureAsync(
            "which",
            new[] { "rust-analyzer" },
            new RemotePath("/", TargetKind.Wsl),
            ct).ConfigureAwait(false);

        if (result.Length > 0 && !string.IsNullOrWhiteSpace(result[0]))
        {
            var path = result[0].Trim();
            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                return new RemotePath(path, TargetKind.Wsl);
            }
        }

        // Try ~/.cargo/bin/rust-analyzer
        var homeResult = await ExecuteAndCaptureAsync(
            "sh",
            new[] { "-c", "echo $HOME" },
            new RemotePath("/", TargetKind.Wsl),
            ct).ConfigureAwait(false);

        if (homeResult.Length > 0 && !string.IsNullOrWhiteSpace(homeResult[0]))
        {
            var home = homeResult[0].Trim();
            var raPath = new RemotePath($"{home}/.cargo/bin/rust-analyzer", TargetKind.Wsl);

            if (await FileExistsAsync(raPath, ct).ConfigureAwait(false))
            {
                return raPath;
            }
        }

        throw new FileNotFoundException(
            $"rust-analyzer not found in WSL distro '{_distroName}'. " +
            $"Install it by running: rustup component add rust-analyzer");
    }

    /// <inheritdoc/>
    public async Task<(Stream Input, Stream Output)> StartRustAnalyzerAsync(
        RemotePath workingDirectory,
        CancellationToken ct)
    {
        var raPath = await GetRustAnalyzerPathAsync(ct).ConfigureAwait(false);

        // Build command that sources cargo env and runs rust-analyzer
        var command = $"[ -f ~/.cargo/env ] && . ~/.cargo/env; cd {EscapeForShell((string)workingDirectory)} && {EscapeForShell((string)raPath)}";

        var wslArgs = new[]
        {
            "-d", _distroName,
            "--",
            "bash", "-c", command,
        };

        var psi = new ProcessStartInfo(WslExePath)
        {
            Arguments = ProcessRunner.GetArguments(wslArgs, quoteArgs: true),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi };
        process.Start();

        return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
    }

    /// <summary>
    /// Builds the wsl.exe argument array.
    /// Uses a login shell to ensure ~/.cargo/bin is in PATH.
    /// </summary>
    private string[] BuildWslArguments(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        IDictionary<string, string> environment)
    {
        // Build the full command to execute
        var cmdBuilder = new StringBuilder();

        // Source cargo environment if it exists (for rustup-installed cargo)
        cmdBuilder.Append("[ -f ~/.cargo/env ] && . ~/.cargo/env; ");

        // Add cd to working directory
        cmdBuilder.Append($"cd {EscapeForShell((string)workingDirectory)} && ");

        // Add environment variables
        if (environment != null && environment.Count > 0)
        {
            foreach (var kv in environment)
            {
                var escapedValue = EscapeForShell(kv.Value);
                cmdBuilder.Append($"{kv.Key}={escapedValue} ");
            }
        }

        // Add the command
        cmdBuilder.Append(command);

        // Add arguments
        if (arguments != null)
        {
            foreach (var arg in arguments)
            {
                cmdBuilder.Append(' ');
                cmdBuilder.Append(EscapeForShell(arg));
            }
        }

        var fullCommand = cmdBuilder.ToString();

        // Run via bash -c to execute the full command
        return new[]
        {
            "-d", _distroName,
            "--",
            "bash", "-c", fullCommand,
        };
    }

    /// <summary>
    /// Escapes a value for shell usage.
    /// </summary>
    private static string EscapeForShell(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        // If value contains no special characters, return as-is
        if (value.IndexOfAny(new[] { ' ', '"', '\'', '\\', '$', '`', '!', '*', '?', '[', ']', '(', ')', '{', '}', '|', '&', ';', '<', '>', '\n', '\r', '\t' }) < 0)
        {
            return value;
        }

        // Escape single quotes and wrap in single quotes
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Checks if WSL is available on this system.
    /// </summary>
    public static bool IsWslAvailable()
    {
        return File.Exists(WslExePath);
    }

    /// <summary>
    /// Enumerates installed WSL distributions.
    /// </summary>
    public static async Task<string[]> GetInstalledDistrosAsync(CancellationToken ct)
    {
        if (!IsWslAvailable())
        {
            return Array.Empty<string>();
        }

        try
        {
            using var proc = ProcessRunner.Run(
                WslExePath,
                new[] { "--list", "--quiet" },
                workingDirectory: null,
                env: null,
                ct);

            await proc;

            // Filter out empty lines and the null character that WSL sometimes emits
            return proc.StandardOutputLines
                .Select(l => l.Trim().TrimEnd('\0'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}


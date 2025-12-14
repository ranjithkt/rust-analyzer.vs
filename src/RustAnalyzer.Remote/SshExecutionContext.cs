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
/// Execution context for SSH remote hosts.
/// Executes commands via ssh.exe (OpenSSH client).
/// </summary>
public sealed class SshExecutionContext : IExecutionContext
{
    private readonly SshConnectionInfo _connectionInfo;
    private readonly IPathMapper _pathMapper;

    /// <summary>
    /// Creates a new SSH execution context.
    /// </summary>
    /// <param name="connectionInfo">SSH connection information.</param>
    public SshExecutionContext(SshConnectionInfo connectionInfo)
    {
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _pathMapper = new SshPathMapper(connectionInfo);
    }

    /// <summary>
    /// Gets the SSH connection info.
    /// </summary>
    public SshConnectionInfo ConnectionInfo => _connectionInfo;

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Ssh;

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

        // Build the remote command
        var remoteCommand = BuildRemoteCommand(command, arguments, workingDirectory, environment);

        // Build ssh arguments
        var sshArgs = BuildSshArguments(remoteCommand);

        // Create a redirector to stream output to the sink in real-time
        ProcessOutputRedirector redirector = outputSink != null
            ? new OutputSinkRedirector(outputSink)
            : null;

        using var proc = ProcessRunner.Run(
            "ssh",
            sshArgs,
            workingDirectory: null,
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

        return result.StandardOutput
            .Concat(result.StandardError)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    /// <inheritdoc/>
    public async Task<bool> FileExistsAsync(RemotePath path, CancellationToken ct)
    {
        var result = await ExecuteAndCaptureAsync(
            "test",
            new[] { "-f", (string)path, "&&", "echo", "1" },
            new RemotePath("/", TargetKind.Ssh),
            ct).ConfigureAwait(false);

        return result.Any(l => l.Trim() == "1");
    }

    /// <inheritdoc/>
    public async Task<bool> DirectoryExistsAsync(RemotePath path, CancellationToken ct)
    {
        var result = await ExecuteAndCaptureAsync(
            "test",
            new[] { "-d", (string)path, "&&", "echo", "1" },
            new RemotePath("/", TargetKind.Ssh),
            ct).ConfigureAwait(false);

        return result.Any(l => l.Trim() == "1");
    }

    /// <inheritdoc/>
    public async Task<string> ReadFileAsync(RemotePath path, CancellationToken ct)
    {
        var result = await ExecuteAndCaptureAsync(
            "cat",
            new[] { (string)path },
            new RemotePath("/", TargetKind.Ssh),
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
            new RemotePath("/", TargetKind.Ssh),
            ct).ConfigureAwait(false);

        if (result.Length > 0 && !string.IsNullOrWhiteSpace(result[0]))
        {
            var path = result[0].Trim();
            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                return new RemotePath(path, TargetKind.Ssh);
            }
        }

        // Try ~/.cargo/bin/rust-analyzer
        var homeResult = await ExecuteAndCaptureAsync(
            "sh",
            new[] { "-c", "echo $HOME" },
            new RemotePath("/", TargetKind.Ssh),
            ct).ConfigureAwait(false);

        if (homeResult.Length > 0 && !string.IsNullOrWhiteSpace(homeResult[0]))
        {
            var home = homeResult[0].Trim();
            var raPath = new RemotePath($"{home}/.cargo/bin/rust-analyzer", TargetKind.Ssh);

            if (await FileExistsAsync(raPath, ct).ConfigureAwait(false))
            {
                return raPath;
            }
        }

        throw new FileNotFoundException(
            $"rust-analyzer not found on SSH host '{_connectionInfo.Host}'. " +
            $"Install it by running: rustup component add rust-analyzer");
    }

    /// <inheritdoc/>
    public async Task<(Stream Input, Stream Output)> StartRustAnalyzerAsync(
        RemotePath workingDirectory,
        CancellationToken ct)
    {
        var raPath = await GetRustAnalyzerPathAsync(ct).ConfigureAwait(false);

        // Build the remote command to start rust-analyzer
        var remoteCommand = $"cd \"{workingDirectory}\" && \"{raPath}\"";

        var sshArgs = BuildSshArguments(remoteCommand);

        var psi = new ProcessStartInfo("ssh")
        {
            Arguments = string.Join(" ", sshArgs.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
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
    /// Builds the remote command string.
    /// </summary>
    private string BuildRemoteCommand(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        IDictionary<string, string> environment)
    {
        var sb = new StringBuilder();

        // Add cd to working directory
        // Use double quotes but $HOME will still expand in double quotes in bash
        var workingDirStr = (string)workingDirectory;
        sb.Append($"cd \"{workingDirStr}\" && ");

        // Add environment variables
        if (environment != null && environment.Count > 0)
        {
            foreach (var kv in environment)
            {
                var escapedValue = EscapeForShell(kv.Value);
                sb.Append($"{kv.Key}={escapedValue} ");
            }
        }

        // Add command
        sb.Append(command);

        // Add arguments
        if (arguments != null)
        {
            foreach (var arg in arguments)
            {
                sb.Append(' ');
                sb.Append(arg.Contains(" ") ? $"\"{arg}\"" : arg);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the ssh command arguments.
    /// </summary>
    private string[] BuildSshArguments(string remoteCommand)
    {
        var args = new List<string>();

        // Add port if non-standard
        if (_connectionInfo.Port != 22)
        {
            args.Add("-p");
            args.Add(_connectionInfo.Port.ToString());
        }

        // Add identity file if specified
        if (!string.IsNullOrEmpty(_connectionInfo.IdentityFile))
        {
            args.Add("-i");
            args.Add(_connectionInfo.IdentityFile);
        }

        // Disable strict host key checking for ease of use (can be made configurable)
        args.Add("-o");
        args.Add("StrictHostKeyChecking=accept-new");

        // Add batch mode to avoid password prompts (assumes key auth or ssh-agent)
        args.Add("-o");
        args.Add("BatchMode=yes");

        // Add user@host
        var userHost = string.IsNullOrEmpty(_connectionInfo.Username)
            ? _connectionInfo.Host
            : $"{_connectionInfo.Username}@{_connectionInfo.Host}";
        args.Add(userHost);

        // Add the command to execute
        args.Add(remoteCommand);

        return args.ToArray();
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
    /// Checks if SSH is available on this system.
    /// </summary>
    public static bool IsSshAvailable()
    {
        try
        {
            using var proc = ProcessRunner.Run(
                "ssh",
                new[] { "-V" },
                workingDirectory: null,
                env: null,
                CancellationToken.None);

            proc.Wait(TimeSpan.FromSeconds(5));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// SSH connection information.
/// </summary>
public sealed class SshConnectionInfo
{
    /// <summary>
    /// The remote host name or IP address.
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// The SSH port (default: 22).
    /// </summary>
    public int Port { get; set; } = 22;

    /// <summary>
    /// The username for SSH connection.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Optional path to the private key file.
    /// </summary>
    public string IdentityFile { get; set; }

    /// <summary>
    /// Gets a display name for this connection.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Username)
        ? (Port == 22 ? Host : $"{Host}:{Port}")
        : (Port == 22 ? $"{Username}@{Host}" : $"{Username}@{Host}:{Port}");

    /// <summary>
    /// Gets a unique ID for this connection.
    /// </summary>
    public string Id => $"ssh:{DisplayName}";
}

/// <summary>
/// Adapts an IProcessOutputSink to a ProcessOutputRedirector for real-time output streaming.
/// </summary>
internal sealed class OutputSinkRedirector : ProcessOutputRedirector
{
    private readonly IProcessOutputSink _sink;

    public OutputSinkRedirector(IProcessOutputSink sink)
    {
        _sink = sink;
    }

    public override void WriteLine(string line)
    {
        _sink.OnStdout(line);
    }

    public override void WriteLineWithoutProcessing(string line)
    {
        _sink.OnStdout(line);
    }

    public override void WriteErrorLine(string line)
    {
        _sink.OnStderr(line);
    }

    public override void WriteErrorLineWithoutProcessing(string line)
    {
        _sink.OnStderr(line);
    }
}

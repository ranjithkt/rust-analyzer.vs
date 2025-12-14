using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Execution context for local Windows execution.
/// Wraps the existing ProcessRunner for compatibility.
/// </summary>
public sealed class LocalExecutionContext : IExecutionContext
{
    /// <summary>
    /// Singleton instance for local execution.
    /// </summary>
    public static readonly LocalExecutionContext Instance = new LocalExecutionContext();

    private LocalExecutionContext()
    {
    }

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Local;

    /// <inheritdoc/>
    public ExecutionCapabilities Capabilities => ExecutionCapabilities.All;

    /// <inheritdoc/>
    public string CargoCommand => Constants.CargoExe;

    /// <inheritdoc/>
    public string RustupCommand => Constants.RustUpExe;

    /// <inheritdoc/>
    public string BinaryExtension => ".exe";

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
        var args = arguments?.ToArray() ?? Array.Empty<string>();

        using var proc = ProcessRunner.Run(
            command,
            args,
            (string)workingDirectory,
            environment,
            ct);

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

        // Combine stdout and stderr, filtering empty lines
        return result.StandardOutput
            .Concat(result.StandardError)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    /// <inheritdoc/>
    public Task<bool> FileExistsAsync(RemotePath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists((string)path));
    }

    /// <inheritdoc/>
    public Task<bool> DirectoryExistsAsync(RemotePath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.Exists((string)path));
    }

    /// <inheritdoc/>
    public Task<string> ReadFileAsync(RemotePath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.ReadAllText((string)path));
    }

    /// <inheritdoc/>
    public Task<RemotePath> GetRustAnalyzerPathAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // For local, we use the bundled rust-analyzer.exe or find it in PATH
        var raPath = Constants.RAExeNameNoExtension + ".exe";
        var foundPath = raPath.FindInPath();

        if (!string.IsNullOrEmpty(foundPath))
        {
            return Task.FromResult(new RemotePath(foundPath, TargetKind.Local));
        }

        throw new FileNotFoundException(
            $"rust-analyzer.exe not found in PATH. " +
            $"Please ensure rust-analyzer is installed and available.");
    }

    /// <inheritdoc/>
    public async Task<(Stream Input, Stream Output)> StartRustAnalyzerAsync(
        RemotePath workingDirectory,
        CancellationToken ct)
    {
        var raPath = await GetRustAnalyzerPathAsync(ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo((string)raPath)
        {
            WorkingDirectory = (string)workingDirectory,
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
}


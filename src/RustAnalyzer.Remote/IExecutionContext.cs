using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Abstraction for executing commands on a target system (local, WSL, or SSH).
/// This is the key seam for all remote operations.
/// </summary>
public interface IExecutionContext
{
    /// <summary>
    /// Gets the target kind for this execution context.
    /// </summary>
    TargetKind Kind { get; }

    /// <summary>
    /// Gets the capabilities of this execution context.
    /// </summary>
    ExecutionCapabilities Capabilities { get; }

    /// <summary>
    /// Execute a command in the target environment.
    /// </summary>
    /// <param name="command">Command to execute (e.g., "cargo").</param>
    /// <param name="arguments">Command arguments.</param>
    /// <param name="workingDirectory">Working directory on target system.</param>
    /// <param name="environment">Environment variables to set. Can be null.</param>
    /// <param name="outputSink">Sink for stdout/stderr streaming. Can be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Process result with exit code and captured output.</returns>
    Task<ProcessResult> ExecuteAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        IDictionary<string, string> environment,
        IProcessOutputSink outputSink,
        CancellationToken ct);

    /// <summary>
    /// Execute a command and return output lines (for simple queries).
    /// </summary>
    /// <param name="command">Command to execute.</param>
    /// <param name="arguments">Command arguments.</param>
    /// <param name="workingDirectory">Working directory on target system.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Output lines from the command.</returns>
    Task<string[]> ExecuteAndCaptureAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        CancellationToken ct);

    /// <summary>
    /// Check if a file exists on the target system.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the file exists.</returns>
    Task<bool> FileExistsAsync(RemotePath path, CancellationToken ct);

    /// <summary>
    /// Check if a directory exists on the target system.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the directory exists.</returns>
    Task<bool> DirectoryExistsAsync(RemotePath path, CancellationToken ct);

    /// <summary>
    /// Read file contents from target system.
    /// </summary>
    /// <param name="path">The path to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file contents as a string.</returns>
    Task<string> ReadFileAsync(RemotePath path, CancellationToken ct);

    /// <summary>
    /// Get the path to rust-analyzer on the target system.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path to rust-analyzer executable.</returns>
    Task<RemotePath> GetRustAnalyzerPathAsync(CancellationToken ct);

    /// <summary>
    /// Start rust-analyzer process and return streams for LSP communication.
    /// </summary>
    /// <param name="workingDirectory">Working directory for rust-analyzer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (input stream to write to, output stream to read from).</returns>
    Task<(Stream Input, Stream Output)> StartRustAnalyzerAsync(
        RemotePath workingDirectory,
        CancellationToken ct);

    /// <summary>
    /// Gets the command name for cargo on this target.
    /// For Windows: "cargo.exe", for Linux: "cargo".
    /// </summary>
    string CargoCommand { get; }

    /// <summary>
    /// Gets the command name for rustup on this target.
    /// For Windows: "rustup.exe", for Linux: "rustup".
    /// </summary>
    string RustupCommand { get; }

    /// <summary>
    /// Gets the binary file extension for this target.
    /// For Windows: ".exe", for Linux: "".
    /// </summary>
    string BinaryExtension { get; }
}


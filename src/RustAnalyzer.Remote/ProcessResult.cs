using System;
using System.Collections.Generic;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Result of executing a process.
/// </summary>
public sealed class ProcessResult
{
    /// <summary>
    /// Gets or sets the process exit code.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Gets or sets the standard output lines.
    /// </summary>
    public IReadOnlyList<string> StandardOutput { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the standard error lines.
    /// </summary>
    public IReadOnlyList<string> StandardError { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the duration of the process execution.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Returns true if the process exited successfully (exit code 0).
    /// </summary>
    public bool Success => ExitCode == 0;
}


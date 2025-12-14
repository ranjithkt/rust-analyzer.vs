namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Sink for receiving process output during execution.
/// </summary>
public interface IProcessOutputSink
{
    /// <summary>
    /// Called when a line is written to standard output.
    /// </summary>
    /// <param name="line">The output line.</param>
    void OnStdout(string line);

    /// <summary>
    /// Called when a line is written to standard error.
    /// </summary>
    /// <param name="line">The error line.</param>
    void OnStderr(string line);

    /// <summary>
    /// Called when the process starts.
    /// </summary>
    /// <param name="processId">The process ID, if available.</param>
    void OnProcessStarted(int? processId);

    /// <summary>
    /// Called when the process exits.
    /// </summary>
    /// <param name="exitCode">The process exit code.</param>
    void OnProcessExited(int exitCode);
}

/// <summary>
/// Null implementation of IProcessOutputSink that discards all output.
/// </summary>
public sealed class NullProcessOutputSink : IProcessOutputSink
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly NullProcessOutputSink Instance = new NullProcessOutputSink();

    private NullProcessOutputSink()
    {
    }

    public void OnStdout(string line)
    {
    }

    public void OnStderr(string line)
    {
    }

    public void OnProcessStarted(int? processId)
    {
    }

    public void OnProcessExited(int exitCode)
    {
    }
}


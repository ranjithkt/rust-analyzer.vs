namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Identifies the type of target system for remote execution.
/// </summary>
public enum TargetKind
{
    /// <summary>
    /// Windows local machine (default).
    /// </summary>
    Local = 0,

    /// <summary>
    /// Windows Subsystem for Linux distro.
    /// </summary>
    Wsl = 1,

    /// <summary>
    /// Remote SSH host.
    /// </summary>
    Ssh = 2,
}


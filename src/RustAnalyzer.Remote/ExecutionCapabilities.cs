using System;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Capabilities of an execution context.
/// </summary>
[Flags]
public enum ExecutionCapabilities
{
    /// <summary>
    /// No capabilities.
    /// </summary>
    None = 0,

    /// <summary>
    /// Can build projects (cargo build).
    /// </summary>
    CanBuild = 1,

    /// <summary>
    /// Can debug applications (gdb/lldb).
    /// </summary>
    CanDebug = 2,

    /// <summary>
    /// Can run Language Server Protocol (rust-analyzer).
    /// </summary>
    CanRunLsp = 4,

    /// <summary>
    /// Can run tests.
    /// </summary>
    CanRunTests = 8,

    /// <summary>
    /// Can open remote folders.
    /// </summary>
    CanOpenRemoteFolder = 16,

    /// <summary>
    /// All capabilities.
    /// </summary>
    All = CanBuild | CanDebug | CanRunLsp | CanRunTests | CanOpenRemoteFolder,
}


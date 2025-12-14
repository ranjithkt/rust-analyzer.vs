namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Represents a target system for build/debug/LSP operations.
/// </summary>
public interface ITargetSystem
{
    /// <summary>
    /// Gets the unique identifier for this target.
    /// Examples: "local", "wsl:Ubuntu", "ssh:myserver"
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the display name for UI.
    /// Examples: "Local Machine", "WSL: Ubuntu", "SSH: myserver"
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the kind of target system.
    /// </summary>
    TargetKind Kind { get; }

    /// <summary>
    /// Gets the execution context for this target.
    /// </summary>
    /// <returns>The execution context.</returns>
    IExecutionContext GetExecutionContext();

    /// <summary>
    /// Gets the path mapper for this target.
    /// </summary>
    /// <returns>The path mapper.</returns>
    IPathMapper GetPathMapper();
}


using System;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Event arguments for target system change events.
/// </summary>
public class TargetChangedEventArgs : EventArgs
{
    /// <summary>
    /// Creates a new TargetChangedEventArgs.
    /// </summary>
    /// <param name="oldTarget">The previous target (can be null on initial set).</param>
    /// <param name="newTarget">The new target.</param>
    public TargetChangedEventArgs(ITargetSystem oldTarget, ITargetSystem newTarget)
    {
        OldTarget = oldTarget;
        NewTarget = newTarget ?? throw new ArgumentNullException(nameof(newTarget));
    }

    /// <summary>
    /// Gets the previous target system. Can be null if this is the initial target.
    /// </summary>
    public ITargetSystem OldTarget { get; }

    /// <summary>
    /// Gets the new target system.
    /// </summary>
    public ITargetSystem NewTarget { get; }
}


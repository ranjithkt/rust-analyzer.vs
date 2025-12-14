namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Target system for local Windows execution.
/// </summary>
public sealed class LocalTargetSystem : ITargetSystem
{
    /// <summary>
    /// The ID for local target.
    /// </summary>
    public const string LocalId = "local";

    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly LocalTargetSystem Instance = new LocalTargetSystem();

    private LocalTargetSystem()
    {
    }

    /// <inheritdoc/>
    public string Id => LocalId;

    /// <inheritdoc/>
    public string DisplayName => "Local Machine";

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Local;

    /// <inheritdoc/>
    public IExecutionContext GetExecutionContext() => LocalExecutionContext.Instance;

    /// <inheritdoc/>
    public IPathMapper GetPathMapper() => LocalPathMapper.Instance;
}


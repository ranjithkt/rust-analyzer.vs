using System;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Target system for WSL (Windows Subsystem for Linux).
/// </summary>
public sealed class WslTargetSystem : ITargetSystem
{
    private readonly string _distroName;
    private readonly string _referencePath; // Used to determine UNC format (wsl$ vs wsl.localhost)
    private readonly Lazy<WslExecutionContext> _executionContext;
    private readonly Lazy<WslPathMapper> _pathMapper;

    /// <summary>
    /// Creates a new WSL target system for the specified distro.
    /// </summary>
    /// <param name="distroName">The WSL distribution name.</param>
    /// <param name="referencePath">Optional reference path to match UNC format (wsl$ vs wsl.localhost).</param>
    public WslTargetSystem(string distroName, string referencePath = null)
    {
        _distroName = distroName ?? throw new ArgumentNullException(nameof(distroName));
        _referencePath = referencePath;

        // Lazy initialization for execution context and path mapper
        _executionContext = new Lazy<WslExecutionContext>(() => new WslExecutionContext(_distroName));
        _pathMapper = new Lazy<WslPathMapper>(() =>
            _referencePath != null
                ? WslPathMapper.CreateMatchingFormat(_distroName, _referencePath)
                : new WslPathMapper(_distroName));
    }

    /// <summary>
    /// Gets the WSL distribution name.
    /// </summary>
    public string DistroName => _distroName;

    /// <inheritdoc/>
    public string Id => $"wsl:{_distroName}";

    /// <inheritdoc/>
    public string DisplayName => $"WSL: {_distroName}";

    /// <inheritdoc/>
    public TargetKind Kind => TargetKind.Wsl;

    /// <inheritdoc/>
    public IExecutionContext GetExecutionContext() => _executionContext.Value;

    /// <inheritdoc/>
    public IPathMapper GetPathMapper() => _pathMapper.Value;

    /// <summary>
    /// Creates a WSL target system from a UNC path.
    /// </summary>
    /// <param name="uncPath">The WSL UNC path (e.g., \\wsl$\Ubuntu\...).</param>
    /// <returns>A WslTargetSystem for the distro, or null if the path is not a WSL path.</returns>
    public static WslTargetSystem FromUncPath(string uncPath)
    {
        if (WslPathMapper.TryGetDistroName(uncPath, out var distroName))
        {
            // Pass the UNC path as reference to match the format
            return new WslTargetSystem(distroName, uncPath);
        }

        return null;
    }
}


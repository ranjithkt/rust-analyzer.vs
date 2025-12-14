using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Represents a path on a remote system (WSL or SSH).
/// Unlike PathEx, this preserves forward slashes for Linux paths.
/// Optimized for performance with minimal allocations.
/// </summary>
[DebuggerDisplay("{_path} ({Kind})")]
public readonly struct RemotePath : IEquatable<RemotePath>
{
    private readonly string _path;
    private readonly int _lastSeparatorIndex;

    public TargetKind Kind { get; }

    /// <summary>
    /// Creates a new RemotePath from a string path.
    /// </summary>
    /// <param name="path">The path string. Cannot be null.</param>
    /// <param name="kind">The target system kind.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    public RemotePath(string path, TargetKind kind)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Kind = kind;

        // Normalize to forward slashes for WSL/SSH - use Replace only if needed
        if (kind != TargetKind.Local && _path.IndexOf('\\') >= 0)
        {
            _path = _path.Replace('\\', '/');
        }

        // Pre-compute last separator index for fast GetFileName/GetDirectoryName
        _lastSeparatorIndex = _path.LastIndexOf('/');
    }

    /// <summary>
    /// Private constructor for optimized path creation without re-normalization.
    /// </summary>
    private RemotePath(string normalizedPath, TargetKind kind, int lastSeparatorIndex)
    {
        _path = normalizedPath;
        Kind = kind;
        _lastSeparatorIndex = lastSeparatorIndex;
    }

    /// <summary>
    /// Returns true if this path is empty.
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(_path);

    /// <summary>
    /// Gets the path length.
    /// </summary>
    public int Length => _path?.Length ?? 0;

    /// <summary>
    /// Implicit conversion to string.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator string(RemotePath p) => p._path;

    /// <summary>
    /// Combines this path with another segment.
    /// </summary>
    /// <param name="segment">The path segment to append.</param>
    /// <returns>A new RemotePath with the combined path.</returns>
    public RemotePath Combine(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return this;
        }

        // Trim leading slashes from segment
        var segmentSpan = segment.AsSpan();
        while (segmentSpan.Length > 0 && segmentSpan[0] == '/')
        {
            segmentSpan = segmentSpan.Slice(1);
        }

        if (segmentSpan.Length == 0)
        {
            return this;
        }

        // Build combined path - single allocation
        var newPath = string.Concat(_path, "/", segmentSpan.ToString());
        var newLastSep = newPath.LastIndexOf('/');

        return new RemotePath(newPath, Kind, newLastSep);
    }

    /// <summary>
    /// Gets the file name from the path (the part after the last separator).
    /// </summary>
    /// <returns>The file name portion of the path.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetFileName()
    {
        if (_lastSeparatorIndex < 0 || _lastSeparatorIndex >= _path.Length - 1)
        {
            return _path ?? string.Empty;
        }

        return _path.Substring(_lastSeparatorIndex + 1);
    }

    /// <summary>
    /// Gets the directory name from the path (the part before the last separator).
    /// </summary>
    /// <returns>A new RemotePath representing the directory.</returns>
    public RemotePath GetDirectoryName()
    {
        if (_lastSeparatorIndex <= 0)
        {
            // Root or no separator - return root for Linux paths
            return Kind == TargetKind.Local
                ? new RemotePath(string.Empty, Kind, -1)
                : new RemotePath("/", Kind, -1);
        }

        var dirPath = _path.Substring(0, _lastSeparatorIndex);
        var newLastSep = dirPath.LastIndexOf('/');

        return new RemotePath(dirPath, Kind, newLastSep);
    }

    /// <summary>
    /// Gets the file extension including the dot, or empty string if none.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetExtension()
    {
        var fileName = GetFileName();
        var dotIndex = fileName.LastIndexOf('.');

        return dotIndex >= 0 ? fileName.Substring(dotIndex) : string.Empty;
    }

    /// <summary>
    /// Determines whether this path starts with the specified prefix.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool StartsWith(string prefix)
    {
        return _path != null && prefix != null && _path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether this path starts with the specified prefix using the specified comparison.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool StartsWith(string prefix, StringComparison comparison)
    {
        return _path != null && prefix != null && _path.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Returns the path as a span for zero-allocation parsing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> AsSpan() => _path.AsSpan();

    public override string ToString() => _path ?? string.Empty;

    public override int GetHashCode()
    {
        // Use ordinal comparison for Linux paths (case-sensitive)
        if (Kind == TargetKind.Local)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(_path ?? string.Empty);
        }

        return StringComparer.Ordinal.GetHashCode(_path ?? string.Empty);
    }

    public override bool Equals(object obj) => obj is RemotePath other && Equals(other);

    public bool Equals(RemotePath other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        // Local paths are case-insensitive, remote paths are case-sensitive
        var comparison = Kind == TargetKind.Local
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(_path, other._path, comparison);
    }

    public static bool operator ==(RemotePath left, RemotePath right) => left.Equals(right);

    public static bool operator !=(RemotePath left, RemotePath right) => !left.Equals(right);
}


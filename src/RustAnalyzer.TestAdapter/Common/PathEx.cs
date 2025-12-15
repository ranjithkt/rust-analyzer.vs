using System;
using System.Diagnostics;
using EnsureThat;

namespace KS.RustAnalyzer.TestAdapter.Common;

[DebuggerDisplay("{_path}")]
public readonly struct PathEx : IEquatable<PathEx>
{
    public const StringComparison DefaultComparison = StringComparison.OrdinalIgnoreCase;
    public static readonly StringComparer DefaultComparer = StringComparer.OrdinalIgnoreCase;

    private readonly string _path;

    public PathEx(string path)
    {
        EnsureArg.IsNotNull(path, nameof(path));

        // Linux absolute paths must not be stored in PathEx because it normalizes '/' -> '\'.
        // Keep Linux paths as strings and map them to Windows (UNC or drive) before converting to PathEx.
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Do not construct PathEx from a Linux absolute path: '{path}'.", nameof(path));
        }

        _path = path.Replace("/", @"\");
    }

    public static implicit operator string(PathEx p) => p._path;

    public static implicit operator PathEx?(string p) => p != null ? new(p) : null;

    public static bool operator ==(PathEx left, PathEx right) => left.Equals(right);

    public static bool operator !=(PathEx left, PathEx right) => !(left == right);

    public static PathEx operator +(PathEx a, PathEx b) => a.Combine(b);

    public static PathEx operator +(string a, PathEx b) => ((PathEx)a).Combine(b);

    public static PathEx operator +(PathEx a, string b) => a.Combine((PathEx)b);

    public override bool Equals(object obj) => obj is PathEx p && Equals(p);

    public override int GetHashCode()
    {
        return 2090457805 + DefaultComparer.GetHashCode(_path);
    }

    public override string ToString() => _path;

    public bool Equals(PathEx other) => _path.Equals(other._path, DefaultComparison);
}

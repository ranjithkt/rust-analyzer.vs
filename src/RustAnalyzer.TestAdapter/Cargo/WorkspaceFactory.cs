using System;
using System.Linq;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

/// <summary>
/// Factory for creating <see cref="Workspace"/> from <see cref="RawWorkspace"/>.
/// Handles path mapping for remote targets (WSL/SSH).
/// </summary>
public sealed class WorkspaceFactory
{
    /// <summary>
    /// Creates a <see cref="Workspace"/> from raw cargo metadata JSON.
    /// </summary>
    /// <param name="raw">The raw workspace data from JSON deserialization.</param>
    /// <param name="pathMapper">Optional path mapper for remote targets. Null for local.</param>
    /// <returns>A workspace with properly mapped paths.</returns>
    public Workspace Create(RawWorkspace raw, IPathMapper pathMapper)
    {
        if (raw == null)
        {
            throw new ArgumentNullException(nameof(raw));
        }

        var workspace = new Workspace
        {
            Version = raw.Version,
            WorkspaceRoot = MapPath(raw.WorkspaceRoot, pathMapper),
            TargetDirectory = MapPath(raw.TargetDirectory, pathMapper),
        };

        if (raw.Packages != null)
        {
            foreach (var rawPkg in raw.Packages)
            {
                var pkg = CreatePackage(rawPkg, pathMapper);
                workspace.Packages.Add(pkg);
            }
        }

        return workspace;
    }

    private Workspace.Package CreatePackage(RawPackage raw, IPathMapper pathMapper)
    {
        var pkg = new Workspace.Package
        {
            Name = raw.Name,
            ManifestPath = MapPath(raw.ManifestPath, pathMapper),
        };

        if (raw.Targets != null)
        {
            foreach (var rawTarget in raw.Targets)
            {
                var target = CreateTarget(rawTarget, pathMapper);
                pkg.Targets.Add(target);
            }
        }

        return pkg;
    }

    private static Workspace.Target CreateTarget(RawTarget raw, IPathMapper pathMapper)
    {
        return new Workspace.Target
        {
            Name = raw.Name,
            SourcePath = MapPath(raw.SourcePath, pathMapper),
            Kinds = raw.Kinds ?? Array.Empty<Workspace.Kind>(),
            CrateTypes = raw.CrateTypes ?? Array.Empty<Workspace.CrateType>(),
        };
    }

    /// <summary>
    /// Maps a path from cargo output to a VS-visible path.
    /// For local targets, returns the path as-is.
    /// For remote targets, maps Linux paths to UNC paths.
    /// </summary>
    private static PathEx MapPath(string remotePath, IPathMapper pathMapper)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return PathEx.Empty;
        }

        // No mapper or local target - return as-is
        if (pathMapper == null || pathMapper.Kind == TargetKind.Local)
        {
            return (PathEx)remotePath;
        }

        // Remote path from cargo output - convert to VS-visible path
        return pathMapper.MapToLocal(new RemotePath(remotePath, pathMapper.Kind));
    }
}

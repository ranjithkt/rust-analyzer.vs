using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Parses Cargo.toml files to extract path dependencies.
/// </summary>
public static class CargoTomlParser
{
    // Regex to match path dependencies in various formats:
    // dep = { path = "../foo" }
    // dep = { path = "../foo", features = [...] }
    // dep.path = "../foo"
    private static readonly Regex PathDependencyRegex = new(
        @"path\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Gets all path dependencies from a Cargo.toml file.
    /// </summary>
    /// <param name="cargoTomlPath">Path to the Cargo.toml file.</param>
    /// <returns>List of absolute paths to dependency directories.</returns>
    public static IReadOnlyList<PathDependency> GetPathDependencies(PathEx cargoTomlPath)
    {
        var dependencies = new List<PathDependency>();

        if (!File.Exists((string)cargoTomlPath))
        {
            return dependencies;
        }

        var cargoTomlDir = cargoTomlPath.GetDirectoryName();
        var content = File.ReadAllText((string)cargoTomlPath);

        var matches = PathDependencyRegex.Matches(content);
        foreach (Match match in matches)
        {
            if (match.Success && match.Groups.Count > 1)
            {
                var relativePath = match.Groups[1].Value;

                // Resolve to absolute path
                var absolutePath = ResolvePath(cargoTomlDir, relativePath);

                if (Directory.Exists((string)absolutePath))
                {
                    dependencies.Add(new PathDependency
                    {
                        RelativePath = relativePath,
                        AbsoluteLocalPath = absolutePath,
                        SourceCargoToml = cargoTomlPath,
                    });
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Gets all path dependencies recursively, including transitive dependencies.
    /// </summary>
    /// <param name="cargoTomlPath">Path to the root Cargo.toml file.</param>
    /// <returns>All path dependencies including transitive ones.</returns>
    public static IReadOnlyList<PathDependency> GetAllPathDependenciesRecursive(PathEx cargoTomlPath)
    {
        var allDependencies = new Dictionary<string, PathDependency>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectDependenciesRecursive(cargoTomlPath, allDependencies, visited);

        return allDependencies.Values.ToList();
    }

    private static void CollectDependenciesRecursive(
        PathEx cargoTomlPath,
        Dictionary<string, PathDependency> allDependencies,
        HashSet<string> visited)
    {
        var cargoTomlStr = (string)cargoTomlPath;
        if (visited.Contains(cargoTomlStr))
        {
            return;
        }

        visited.Add(cargoTomlStr);

        var dependencies = GetPathDependencies(cargoTomlPath);

        foreach (var dep in dependencies)
        {
            var depKey = (string)dep.AbsoluteLocalPath;
            if (!allDependencies.ContainsKey(depKey))
            {
                allDependencies[depKey] = dep;

                // Check for nested dependencies
                var nestedCargoToml = dep.AbsoluteLocalPath + "Cargo.toml";
                if (File.Exists((string)nestedCargoToml))
                {
                    CollectDependenciesRecursive(nestedCargoToml, allDependencies, visited);
                }
            }
        }
    }

    /// <summary>
    /// Resolves a relative path from a base directory.
    /// </summary>
    private static PathEx ResolvePath(PathEx baseDir, string relativePath)
    {
        // Convert forward slashes to backslashes for Windows
        var windowsRelativePath = relativePath.Replace("/", @"\");

        // Combine and normalize
        var combined = Path.Combine((string)baseDir, windowsRelativePath);
        var fullPath = Path.GetFullPath(combined);

        return (PathEx)fullPath;
    }

    /// <summary>
    /// Calculates the remote path for a dependency based on its relative path.
    /// </summary>
    /// <param name="dependency">The dependency info.</param>
    /// <param name="projectRemoteRoot">The remote root where the project is synced (e.g., ~/vs-sync/trader).</param>
    /// <returns>The remote path where this dependency should be placed.</returns>
    public static RemotePath CalculateRemotePath(PathDependency dependency, RemotePath projectRemoteRoot)
    {
        // Parse the relative path to understand how many levels up we need to go
        var relativePath = dependency.RelativePath;
        var parts = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        // Count ".." segments
        int levelsUp = 0;
        var remainingParts = new List<string>();

        foreach (var part in parts)
        {
            if (part == "..")
            {
                levelsUp++;
            }
            else if (part != ".")
            {
                remainingParts.Add(part);
            }
        }

        // Start from project remote root and go up
        var remoteRootStr = (string)projectRemoteRoot;
        var remotePathParts = remoteRootStr.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Handle $HOME prefix
        bool hasHomePrefix = remoteRootStr.StartsWith("$HOME", StringComparison.Ordinal);
        if (hasHomePrefix && remotePathParts.Count > 0 && remotePathParts[0] == "$HOME")
        {
            remotePathParts.RemoveAt(0);
        }

        // Go up the required number of levels
        for (int i = 0; i < levelsUp && remotePathParts.Count > 0; i++)
        {
            remotePathParts.RemoveAt(remotePathParts.Count - 1);
        }

        // Add the remaining path parts
        remotePathParts.AddRange(remainingParts);

        // Reconstruct the path
        var resultPath = (hasHomePrefix ? "$HOME/" : "/") + string.Join("/", remotePathParts);

        return new RemotePath(resultPath, TargetKind.Ssh);
    }
}

/// <summary>
/// Represents a path dependency found in Cargo.toml.
/// </summary>
public class PathDependency
{
    /// <summary>
    /// Gets or sets the relative path as specified in Cargo.toml (e.g., "../common", "../../alpaca-core").
    /// </summary>
    public string RelativePath { get; set; }

    /// <summary>
    /// Gets or sets the resolved absolute local path.
    /// </summary>
    public PathEx AbsoluteLocalPath { get; set; }

    /// <summary>
    /// Gets or sets the Cargo.toml file where this dependency was found.
    /// </summary>
    public PathEx SourceCargoToml { get; set; }

    /// <summary>
    /// Gets the name of the dependency (folder name).
    /// </summary>
    public string Name => AbsoluteLocalPath.GetFileName();
}

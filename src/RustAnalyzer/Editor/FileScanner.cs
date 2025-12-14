using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Build;
using Microsoft.VisualStudio.Workspace.Debug;
using Microsoft.VisualStudio.Workspace.Indexing;

namespace KS.RustAnalyzer.Editor;

public class FileScanner : IFileScanner, IFileScannerUpToDateCheck
{
    private readonly IMetadataService _mds;
    private readonly IWorkspaceContextAccessor _workspaceContextAccessor;

    public FileScanner(IMetadataService mds, IWorkspaceContextAccessor workspaceContextAccessor)
    {
        _mds = mds;
        _workspaceContextAccessor = workspaceContextAccessor;
    }

    /// <summary>
    /// Gets the current target kind for determining binary file extensions.
    /// </summary>
    private TargetKind GetCurrentTargetKind()
    {
        return _workspaceContextAccessor?.GetCurrentTarget()?.Kind ?? TargetKind.Local;
    }

    public async Task<T> ScanContentAsync<T>(string filePath, CancellationToken cancellationToken)
        where T : class
    {
        System.Diagnostics.Debug.WriteLine($"[FileScanner] ScanContentAsync called for: {filePath}, Type: {typeof(T).Name}");

        var package = await _mds.GetContainingPackageAsync((PathEx)filePath, cancellationToken);
        if (package == null)
        {
            System.Diagnostics.Debug.WriteLine($"[FileScanner] Package is null for: {filePath}");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[FileScanner] Package found: {package.Name}, ManifestPath: {package.ManifestPath}, IsPackage: {package.IsPackage}");

        if (typeof(T) == FileScannerTypeConstants.FileDataValuesType)
        {
            var ret = GetFileDataValues(package, (PathEx)filePath);
            System.Diagnostics.Debug.WriteLine($"[FileScanner] FileDataValues count: {ret.Count}");
            return await Task.FromResult((T)(IReadOnlyCollection<FileDataValue>)ret);
        }
        else if (typeof(T) == FileScannerTypeConstants.FileReferenceInfoType)
        {
            var ret = GetFileReferenceInfos(package, (PathEx)filePath);
            System.Diagnostics.Debug.WriteLine($"[FileScanner] FileReferenceInfos count: {ret.Count}");
            return await Task.FromResult((T)(IReadOnlyCollection<FileReferenceInfo>)ret);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    public virtual async Task<bool> IsUpToDateAsync(DateTimeOffset? lastScanTimestamp, string filePath, FileScannerType scannerType, CancellationToken cancellationToken)
    {
        if (await IsValidFileAsync(filePath))
        {
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(filePath);
                return lastScanTimestamp.HasValue && lastWrite < lastScanTimestamp.Value.UtcDateTime;
            }
            catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
            {
                // We have already loaded the file in VS,
                // so any I/O related exceptions are very unlikely
                // and we def. don't want to crash VS on that.
            }
        }

        return false;
    }

    private Task<bool> IsValidFileAsync(string filePath)
    {
        var path = (PathEx)filePath;
        var ext = path.GetExtension();
        var fileName = path.GetFileName();

        // Check for .rs files or specifically Cargo.toml (not just any .toml)
        var isRustFile = ext.Equals(Constants.RustFileExtension);
        var isCargoToml = fileName.Equals(Constants.ManifestFileName2, StringComparison.OrdinalIgnoreCase);

        return (isRustFile || isCargoToml).ToTask();
    }

    private List<FileDataValue> GetFileDataValues(Workspace.Package package, PathEx filePath)
    {
        var allFileDataValues = new List<FileDataValue>();
        var targetKind = GetCurrentTargetKind();

        System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] filePath={filePath}");
        System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] package.ManifestPath={package.ManifestPath}");
        System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] ManifestPath==filePath: {package.ManifestPath == filePath}");

        // For binaries - use normalized path comparison for WSL paths
        // WSL paths can be \\wsl$\ or \\wsl.localhost\ - both should match
        var isManifestMatch = PathsMatchForWsl(package.ManifestPath, filePath);
        System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] Normalized match: {isManifestMatch}");

        if (isManifestMatch)
        {
            System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] IsPackage: {package.IsPackage}");

            if (package.IsPackage)
            {
                var runnableTargets = package.GetTargets().Where(t => t.IsRunnable).ToList();
                System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] Runnable targets: {runnableTargets.Count}");

                foreach (var target in runnableTargets)
                {
                    System.Diagnostics.Debug.WriteLine($"[FileScanner.GetFileDataValues] Creating debug config for target: {target.QualifiedTargetFileName}");

                    var launchSettings = new PropertySettings
                    {
                        [LaunchConfigurationConstants.NameKey] = target.QualifiedTargetFileName,
                        [LaunchConfigurationConstants.DebugTypeKey] = LaunchConfigurationConstants.NativeOptionKey,
                        [LaunchConfigurationConstants.ProjectKey] = (string)package.FullPath,
                        [LaunchConfigurationConstants.ProjectTargetKey] = target.QualifiedTargetFileName,
                        [LaunchConfigurationConstants.ProgramKey] = (string)package.FullPath,
                    };

                    allFileDataValues.Add(
                        new FileDataValue(
                            type: DebugLaunchActionContext.ContextTypeGuid,
                            name: DebugLaunchActionContext.IsDefaultStartupProjectEntry,
                            value: launchSettings,
                            target: null,
                            context: null));

                    var fileDataValuesForAllProfiles1 = package.GetProfiles().Select(
                        profile =>
                            new FileDataValue(
                                type: BuildConfigurationContext.ContextTypeGuid,
                                name: BuildConfigurationContext.DataValueName,
                                value: null,
                                target: target.GetPath(profile, targetKind),
                                context: profile));

                    allFileDataValues.AddRange(fileDataValuesForAllProfiles1);
                }
            }

            var fileDataValuesForAllProfiles = package.GetProfiles().Select(
            profile =>
                new FileDataValue(
                    type: BuildConfigurationContext.ContextTypeGuid,
                    name: BuildConfigurationContext.DataValueName,
                    value: null,
                    target: null,
                    context: profile));

            allFileDataValues.AddRange(fileDataValuesForAllProfiles);
        }

        // For examples.
        var forExamples = package.GetTargets()
            .Where(t => t.IsExample())
            .Where(t => t.SourcePath == filePath)
            .SelectMany(
                t =>
                {
                    var allFileDataValues = new List<FileDataValue>();

                    var launchSettings = new PropertySettings
                    {
                        [LaunchConfigurationConstants.NameKey] = t.QualifiedTargetFileName,
                        [LaunchConfigurationConstants.DebugTypeKey] = LaunchConfigurationConstants.NativeOptionKey,
                        [LaunchConfigurationConstants.ProjectKey] = (string)t.SourcePath,
                        [LaunchConfigurationConstants.ProjectTargetKey] = t.QualifiedTargetFileName,
                        [LaunchConfigurationConstants.ProgramKey] = (string)package.FullPath,
                    };

                    allFileDataValues.Add(
                        new FileDataValue(
                            type: DebugLaunchActionContext.ContextTypeGuid,
                            name: DebugLaunchActionContext.IsDefaultStartupProjectEntry,
                            value: launchSettings,
                            target: null,
                            context: null));

                    var fileDataValuesForAllProfiles1 = package.GetProfiles().Select(
                        profile =>
                            new FileDataValue(
                                type: BuildConfigurationContext.ContextTypeGuid,
                                name: BuildConfigurationContext.DataValueName,
                                value: null,
                                target: t.GetPath(profile, targetKind),
                                context: profile));

                    allFileDataValues.AddRange(fileDataValuesForAllProfiles1);

                    var fileDataValuesForAllProfiles = package.GetProfiles().Select(
                    profile =>
                        new FileDataValue(
                            type: BuildConfigurationContext.ContextTypeGuid,
                            name: BuildConfigurationContext.DataValueName,
                            value: null,
                            target: null,
                            context: profile));

                    allFileDataValues.AddRange(fileDataValuesForAllProfiles);
                    return allFileDataValues;
                });

        allFileDataValues.AddRange(forExamples);

        return allFileDataValues;
    }

    private List<FileReferenceInfo> GetFileReferenceInfos(Workspace.Package package, PathEx filePath)
    {
        var allFileRefInfos = new List<FileReferenceInfo>();
        var targetKind = GetCurrentTargetKind();

        // For binaries - use normalized path comparison for WSL paths
        if (PathsMatchForWsl(package.ManifestPath, filePath) && package.IsPackage)
        {
            var targets = package.GetTargets();

            var refInfos = package.GetProfiles()
                .SelectMany(p => targets.Select(t => (Target: t, Profile: p)))
                .Where(x => !x.Target.IsExample())
                .Select(x =>
                    new FileReferenceInfo(
                        relativePath: x.Target.GetPathRelativeTo(x.Profile, filePath, targetKind),
                        target: x.Target.GetPath(x.Profile, targetKind),
                        context: x.Profile,
                        referenceType: (int)FileReferenceInfoType.Output));

            allFileRefInfos.AddRange(refInfos);
        }

        // For examples.
        var forExamples = package.GetTargets()
            .Where(t => t.IsExample())
            .Where(t => t.SourcePath == filePath)
            .SelectMany(t => package.GetProfiles().Select(p => (Target: t, Profile: p)))
            .Select(x =>
                new FileReferenceInfo(
                    relativePath: x.Target.GetPathRelativeTo(x.Profile, filePath, targetKind),
                    target: x.Target.GetPath(x.Profile, targetKind),
                    context: x.Profile,
                    referenceType: (int)FileReferenceInfoType.Output));

        allFileRefInfos.AddRange(forExamples);

        return allFileRefInfos;
    }

    /// <summary>
    /// Compares two paths, normalizing WSL path prefixes (\\wsl$ vs \\wsl.localhost).
    /// Both formats refer to the same location and should be treated as equal.
    /// </summary>
    private static bool PathsMatchForWsl(PathEx path1, PathEx path2)
    {
        string p1 = path1;
        string p2 = path2;

        // Quick check - if they're equal, return true
        if (p1.Equals(p2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Normalize WSL path prefixes
        p1 = NormalizeWslPath(p1);
        p2 = NormalizeWslPath(p2);

        return p1.Equals(p2, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes WSL UNC paths by converting \\wsl.localhost\ to \\wsl$\.
    /// </summary>
    private static string NormalizeWslPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Convert \\wsl.localhost\ to \\wsl$\ for consistent comparison
        const string wslLocalhostPrefix = @"\\wsl.localhost\";
        const string wslDollarPrefix = @"\\wsl$\";

        if (path.StartsWith(wslLocalhostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return wslDollarPrefix + path.Substring(wslLocalhostPrefix.Length);
        }

        return path;
    }
}

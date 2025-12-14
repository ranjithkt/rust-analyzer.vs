using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

public static class WorkspaceExtensions
{
    /// <summary>
    /// Crate type info for Windows targets.
    /// </summary>
    public static readonly IReadOnlyDictionary<Workspace.CrateType, (string Prefix, string Extension)> CrateTypeInfos =
        new Dictionary<Workspace.CrateType, (string, string)>
        {
            [Workspace.CrateType.Lib] = ("lib", ".rlib"),
            [Workspace.CrateType.RLib] = ("lib", ".rlib"),
            [Workspace.CrateType.DyLib] = (string.Empty, ".dylib"),
            [Workspace.CrateType.CdyLib] = (string.Empty, ".cydlib"),
            [Workspace.CrateType.StaticLib] = (string.Empty, ".staticlib"),
            [Workspace.CrateType.ProcMacro] = (string.Empty, ".procmacro"),
            [Workspace.CrateType.Bin] = (string.Empty, ".exe"),
        };

    /// <summary>
    /// Crate type info for Linux/remote targets (no .exe extension for binaries).
    /// </summary>
    public static readonly IReadOnlyDictionary<Workspace.CrateType, (string Prefix, string Extension)> CrateTypeInfosLinux =
        new Dictionary<Workspace.CrateType, (string, string)>
        {
            [Workspace.CrateType.Lib] = ("lib", ".rlib"),
            [Workspace.CrateType.RLib] = ("lib", ".rlib"),
            [Workspace.CrateType.DyLib] = (string.Empty, ".so"),
            [Workspace.CrateType.CdyLib] = (string.Empty, ".so"),
            [Workspace.CrateType.StaticLib] = (string.Empty, ".a"),
            [Workspace.CrateType.ProcMacro] = (string.Empty, ".so"),
            [Workspace.CrateType.Bin] = (string.Empty, string.Empty), // No extension on Linux
        };

    /// <summary>
    /// Gets the appropriate crate type info for the given target kind.
    /// </summary>
    public static IReadOnlyDictionary<Workspace.CrateType, (string Prefix, string Extension)> GetCrateTypeInfos(TargetKind kind)
    {
        return kind == TargetKind.Local ? CrateTypeInfos : CrateTypeInfosLinux;
    }

    private static readonly IReadOnlyDictionary<string, PathEx> ProfileInfos = new Dictionary<string, PathEx>
    {
        ["dev"] = (PathEx)"debug",
        ["release"] = (PathEx)"release",
        ["test"] = (PathEx)"debug",
        ["bench"] = (PathEx)"release",
    };

    public static IEnumerable<Workspace.Target> GetTargets(this Workspace.Package @this) => @this.Targets;

    /// <summary>
    /// Creates the target filename for Windows (default) targets.
    /// </summary>
    public static PathEx CreateTargetFileName(this Workspace.Target @this)
    {
        return CreateTargetFileName(@this, TargetKind.Local);
    }

    /// <summary>
    /// Creates the target filename for the specified target kind.
    /// </summary>
    public static PathEx CreateTargetFileName(this Workspace.Target @this, TargetKind targetKind)
    {
        var infos = GetCrateTypeInfos(targetKind);
        return (PathEx)$"{infos[@this.CrateTypes[0]].Prefix}{@this.Name}{infos[@this.CrateTypes[0]].Extension}";
    }

    /// <summary>
    /// Creates the target filename as a RemotePath for Linux targets.
    /// </summary>
    public static string CreateRemoteTargetFileName(this Workspace.Target @this)
    {
        var infos = CrateTypeInfosLinux;
        return $"{infos[@this.CrateTypes[0]].Prefix}{@this.Name}{infos[@this.CrateTypes[0]].Extension}";
    }

    /// <summary>
    /// Gets the path to the target binary for Windows (default) targets.
    /// </summary>
    public static PathEx GetPath(this Workspace.Target @this, string profile)
    {
        var profileTargetPath = @this.Parent.Parent.TargetDirectory.MakeProfilePath(profile);
        if (@this.Kinds[0] == Workspace.Kind.Example)
        {
            return profileTargetPath + (PathEx)"examples" + @this.TargetFileName;
        }
        else
        {
            return profileTargetPath + @this.TargetFileName;
        }
    }

    /// <summary>
    /// Gets the path to the target binary for a specific target kind.
    /// </summary>
    public static PathEx GetPath(this Workspace.Target @this, string profile, TargetKind targetKind)
    {
        var profileTargetPath = @this.Parent.Parent.TargetDirectory.MakeProfilePath(profile);
        var targetFileName = @this.CreateTargetFileName(targetKind);
        if (@this.Kinds[0] == Workspace.Kind.Example)
        {
            return profileTargetPath + (PathEx)"examples" + targetFileName;
        }
        else
        {
            return profileTargetPath + targetFileName;
        }
    }

    /// <summary>
    /// Gets the remote path to the target binary (for WSL/SSH targets).
    /// </summary>
    public static RemotePath GetRemotePath(this Workspace.Target @this, string profile, RemotePath remoteTargetDirectory)
    {
        var profilePath = profile == "dev" || profile == "test" ? "debug" : profile;
        var targetFileName = @this.CreateRemoteTargetFileName();
        if (@this.Kinds[0] == Workspace.Kind.Example)
        {
            return new RemotePath($"{remoteTargetDirectory}/{profilePath}/examples/{targetFileName}", TargetKind.Ssh);
        }
        else
        {
            return new RemotePath($"{remoteTargetDirectory}/{profilePath}/{targetFileName}", TargetKind.Ssh);
        }
    }

    public static PathEx GetPathRelativeTo(this Workspace.Target @this, string profile, string rootPath)
    {
        return (PathEx)PathExtensions.MakeRelativePath(Path.GetDirectoryName(rootPath), @this.GetPath(profile));
    }

    public static PathEx GetTargetPathRelativeToWorkspace(this Workspace.Target @this)
    {
        var relPath = Path.GetDirectoryName(PathExtensions.MakeRelativePath(@this.Parent.WorkspaceRoot, @this.Parent.FullPath));
        return (PathEx)@$"{relPath}\";
    }

    public static IEnumerable<string> GetProfiles(this Workspace.Package @this)
    {
        return ProfileInfos.Keys;
    }

    public static bool TryGetParentManifestOrThisUnderWorkspace(this PathEx fileOrFolderPath, PathEx workspaceRoot, out PathEx? parentManifest)
    {
        if (fileOrFolderPath.IsManifest())
        {
            parentManifest = fileOrFolderPath;
            return true;
        }

        if (!fileOrFolderPath.IsContainedIn(workspaceRoot))
        {
            parentManifest = default;
            return false;
        }

        var currentPath = fileOrFolderPath;
        while (currentPath != workspaceRoot)
        {
            currentPath = currentPath.GetDirectoryName();
            if (currentPath.Combine(Constants.ManifestFileName2).FileExists())
            {
                parentManifest = currentPath.Combine(Constants.ManifestFileName2);
                return true;
            }
        }

        if (currentPath.Combine(Constants.ManifestFileName2).FileExists())
        {
            parentManifest = currentPath.Combine(Constants.ManifestFileName2);
            return true;
        }

        parentManifest = null;
        return false;
    }

    public static bool IsManifest(this PathEx @this) => @this.GetFileName() == Constants.ManifestFileName2;

    public static bool IsRustFile(this PathEx @this) => @this.GetExtension() == Constants.RustFileExtension2;

    public static bool IsTestContainer(this PathEx @this) => @this.GetExtension() == Constants.TestsContainerExtension;

    public static async Task<(bool HasTargets, bool IsExe)> GetTargetInfoAsync(this IMetadataService @this, PathEx filePath, CancellationToken ct)
    {
        var ts = await @this.GetTargets(filePath, ct);
        return (ts.Any(), ts.Any(t => t.IsRunnable && (t.SourcePath == filePath || t.Parent.ManifestPath == filePath)));
    }

    public static bool IsExample(this Workspace.Target @this) => @this.Kinds[0] == Workspace.Kind.Example;

    public static PathEx GetTestContainerPath(this Workspace.Target @this, string profile)
    {
        return @this.Parent.Parent.TargetDirectory.MakeProfilePath(profile) + (PathEx)$"{@this.Parent.Name}_{@this.TargetFileName.GetFileNameWithoutExtension()}{Constants.TestsContainerExtension2}";
    }

    public static PathEx GetDepsPath(this Workspace.Package @this, string profile)
    {
        return @this.Parent.TargetDirectory.MakeProfilePath(profile) + (PathEx)"deps";
    }

    public static PathEx GetTargetPath(this Workspace.Package @this, string profile)
    {
        return @this.Parent.TargetDirectory.MakeProfilePath(profile);
    }

    public static PathEx MakeProfilePath(this PathEx @this, string profile)
    {
        return @this + ProfileInfos[profile];
    }

    public static IEnumerable<(PathEx Container, Workspace.Target Target)> GetTestContainers(this Workspace.Package @this, string profile)
    {
        return @this.Targets
            .Where(t => t.CanHaveTests)
            .Select(t => (Container: t.GetTestContainerPath(profile), Target: t));
    }

    private static async Task<IEnumerable<Workspace.Target>> GetTargets(this IMetadataService @this, PathEx filePath, CancellationToken ct)
    {
        if (!filePath.IsManifest() && !filePath.IsRustFile())
        {
            return Enumerable.Empty<Workspace.Target>();
        }

        var p = await @this?.GetContainingPackageAsync(filePath, ct);
        if (p == null)
        {
            return Enumerable.Empty<Workspace.Target>();
        }

        return p.Targets;
    }
}

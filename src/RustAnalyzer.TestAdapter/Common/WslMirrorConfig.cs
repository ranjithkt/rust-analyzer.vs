using System;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Configuration for WSL mirror mode (Mode 2: Windows workspace + WSL execution on ext4).
/// </summary>
public sealed class WslMirrorConfig
{
    public WslMirrorConfig(PathEx workspaceRootWindows, string distroName, string linuxHome, string mirrorBaseLinux, string workspaceId)
    {
        WorkspaceRootWindows = workspaceRootWindows;
        DistroName = distroName;
        LinuxHome = linuxHome;
        MirrorBaseLinux = mirrorBaseLinux;
        WorkspaceId = workspaceId;

        MirrorRootLinux = CombineLinuxPaths(MirrorBaseLinux, WorkspaceId);
        MirrorWindowsRootLinux = CombineLinuxPaths(MirrorRootLinux, "win");

        // Map the Windows workspace root into the mirror.
        if (!WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)WorkspaceRootWindows, this, out var mirrorWorkspaceRoot))
        {
            throw new InvalidOperationException($"Unable to map workspace root '{WorkspaceRootWindows}' to mirror linux path.");
        }

        MirrorWorkspaceRootLinux = mirrorWorkspaceRoot;
        MirrorTargetDirLinux = CombineLinuxPaths(MirrorWorkspaceRootLinux, "target");
    }

    /// <summary>Gets the Windows workspace root (e.g. C:\Repos\proj).</summary>
    public PathEx WorkspaceRootWindows { get; }

    /// <summary>Gets the WSL distro name (e.g. Ubuntu-22.04).</summary>
    public string DistroName { get; }

    /// <summary>Gets the Linux $HOME for the distro (e.g. /home/user).</summary>
    public string LinuxHome { get; }

    /// <summary>Gets the mirror base directory (e.g. /home/user/.cache/rust-analyzer.vs/mirrors).</summary>
    public string MirrorBaseLinux { get; }

    /// <summary>Gets the stable workspace id used as a subfolder under MirrorBaseLinux.</summary>
    public string WorkspaceId { get; }

    /// <summary>Gets the mirror root directory (e.g. .../mirrors/&lt;id&gt;).</summary>
    public string MirrorRootLinux { get; }

    /// <summary>Gets the root under which Windows drive trees are mirrored (e.g. .../mirrors/&lt;id&gt;/win).</summary>
    public string MirrorWindowsRootLinux { get; }

    /// <summary>Gets the mirror path for the workspace root (e.g. .../win/c/Repos/proj).</summary>
    public string MirrorWorkspaceRootLinux { get; }

    /// <summary>Gets the mirror target dir (e.g. .../win/c/Repos/proj/target).</summary>
    public string MirrorTargetDirLinux { get; }

    /// <summary>Gets or sets the UNC path to the mirror target dir (e.g. \\wsl.localhost\Ubuntu\home\user\...\target).</summary>
    public string MirrorTargetDirUnc { get; set; }

    /// <summary>
    /// Gets or sets excluded directory names (case-insensitive) for sync and watcher ignore.
    /// NOTE: Excluding "target" is required so rsync doesn't delete cargo outputs in the mirror.
    /// </summary>
    public string[] ExcludedDirectoryNames { get; set; } = new[]
    {
        ".git",
        ".vs",
        "target",
    };

    /// <summary>Gets or sets rsync flags tuned for correctness + speed.</summary>
    public string[] RsyncArgsPrefix { get; set; } = new[]
    {
        "-rt",
        "--whole-file",
        "--delete",
        "--modify-window=1",
    };

    public static string CombineLinuxPaths(string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right ?? string.Empty;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        if (left.EndsWith("/", StringComparison.Ordinal))
        {
            return left + right.TrimStart('/');
        }

        return left + "/" + right.TrimStart('/');
    }
}

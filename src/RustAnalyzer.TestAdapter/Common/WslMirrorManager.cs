using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Process-wide cache of WSL mirror instances keyed by (workspace root, distro).
/// </summary>
public static class WslMirrorManager
{
    private static readonly ConcurrentDictionary<string, WslMirrorInstance> Instances = new(StringComparer.OrdinalIgnoreCase);

    public static WslMirrorInstance GetOrCreate(PathEx workspaceRootWindows, string distroName)
    {
        var key = MakeKey(workspaceRootWindows, distroName);
        return Instances.GetOrAdd(key, _ => new WslMirrorInstance(workspaceRootWindows, distroName));
    }

    public static bool TryGet(PathEx workspaceRootWindows, string distroName, out WslMirrorInstance instance)
    {
        return Instances.TryGetValue(MakeKey(workspaceRootWindows, distroName), out instance);
    }

    public static async Task<WslMirrorConfig> EnsureInitializedAsync(PathEx workspaceRootWindows, string distroName, CancellationToken ct)
    {
        var inst = GetOrCreate(workspaceRootWindows, distroName);
        await inst.EnsureInitializedAsync(ct);
        return inst.Config;
    }

    public static bool TryMapMirrorLinuxToWindows(PathEx workspaceRootWindows, string distroName, string linuxPath, out string windowsPath)
    {
        windowsPath = null;
        if (!TryGet(workspaceRootWindows, distroName, out var inst) || inst.Config == null)
        {
            return false;
        }

        return WslMirrorPathMapper.TryMirrorLinuxToWindowsPath(linuxPath, inst.Config, out windowsPath);
    }

    /// <summary>
    /// Variant that accepts any path inside the workspace (e.g. a crate directory) and finds the matching mirror instance.
    /// This is used by output preprocessors which only know the current working directory.
    /// </summary>
    public static bool TryMapMirrorLinuxToWindowsFromWorkspacePath(PathEx anyWorkspacePath, string distroName, string linuxPath, out string windowsPath)
    {
        windowsPath = null;
        if (string.IsNullOrWhiteSpace(distroName) || string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        // Fast path: if we know the active workspace root, use direct lookup (avoids O(N) scan during build output parsing).
        if (TargetSystemSelection.TryGetWorkspaceRoot(out var wsRoot))
        {
            try
            {
                if (anyWorkspacePath.IsContainedIn(wsRoot) &&
                    TryGet(wsRoot, distroName, out var instFast) &&
                    instFast?.Config != null &&
                    WslMirrorPathMapper.TryMirrorLinuxToWindowsPath(linuxPath, instFast.Config, out windowsPath))
                {
                    return true;
                }
            }
            catch
            {
                // fall back
            }
        }

        foreach (var inst in Instances.Values)
        {
            var cfg = inst?.Config;
            if (cfg == null)
            {
                continue;
            }

            if (!string.Equals(cfg.DistroName, distroName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!anyWorkspacePath.IsContainedIn(cfg.WorkspaceRootWindows))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            return WslMirrorPathMapper.TryMirrorLinuxToWindowsPath(linuxPath, cfg, out windowsPath);
        }

        return false;
    }

    /// <summary>
    /// Releases (disposes) all mirror instances associated with the specified workspace root.
    /// This prevents FileSystemWatcher leaks when workspaces are closed/reopened.
    /// </summary>
    public static void ReleaseForWorkspace(PathEx workspaceRootWindows)
    {
        var root = workspaceRootWindows.GetFullPath();
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var prefix = root + "|";
        foreach (var key in Instances.Keys.ToArray())
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Instances.TryRemove(key, out var inst))
            {
                try
                {
                    inst.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public static bool TryGetMirrorTargetDirUnc(PathEx workspaceRootWindows, string distroName, out string uncTargetDir)
    {
        uncTargetDir = null;
        if (!TryGet(workspaceRootWindows, distroName, out var inst) || inst.Config == null)
        {
            return false;
        }

        uncTargetDir = inst.Config.MirrorTargetDirUnc;
        return !string.IsNullOrWhiteSpace(uncTargetDir);
    }

    private static string MakeKey(PathEx workspaceRootWindows, string distroName)
    {
        return $"{workspaceRootWindows.GetFullPath()}|{(distroName ?? string.Empty).Trim()}";
    }
}

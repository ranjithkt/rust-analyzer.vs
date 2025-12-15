using System;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Process-scoped target system selection.
/// Intended for VS Open Folder: typically only one workspace is active per VS instance.
/// </summary>
public static class TargetSystemSelection
{
    /// <summary>
    /// Returns true if user selected WSL execution mode for the current process.
    /// </summary>
    public static bool IsWslSelected(out string distroName)
    {
        distroName = null;

        var mode = Environment.GetEnvironmentVariable(Constants.RAVsTargetSystem);
        if (!string.Equals(mode, "wsl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var distro = Environment.GetEnvironmentVariable(Constants.RAVsWslDistroName);
        if (!string.IsNullOrEmpty(distro) && distro.IndexOf('\0') >= 0)
        {
            distro = distro.Replace("\0", string.Empty);
        }

        if (string.IsNullOrWhiteSpace(distro))
        {
            return false;
        }

        distroName = distro.Trim();
        return true;
    }

    /// <summary>
    /// Determines whether to run via WSL for a workspace path, returning either:
    /// - WslInfo (when workspace path is WSL UNC), or
    /// - a distroName (when workspace path is Windows-local and the user selected WSL).
    /// </summary>
    public static bool TryGetWslExecutionContext(PathEx workspacePath, out WslInfo wslInfo, out string distroName)
    {
        wslInfo = null;
        distroName = null;

        // Mode 1: WSL UNC workspace
        if (WslInfo.TryParse(workspacePath, out var parsed))
        {
            wslInfo = parsed;
            distroName = parsed.DistroName;
            return true;
        }

        // Mode 2: Windows-local workspace + WSL execution
        if (IsWslSelected(out var distro))
        {
            distroName = distro;
            return true;
        }

        return false;
    }
}


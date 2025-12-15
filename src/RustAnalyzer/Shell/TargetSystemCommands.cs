using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Community.VisualStudio.Toolkit;
using EnsureThat;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;
using TestAdapterConstants = KS.RustAnalyzer.TestAdapter.Constants;

namespace KS.RustAnalyzer.Shell;

public static class TemporaryTargetSystemStore
{
    private static readonly object Locker = new();
    private static DateTime _lastRefreshUtc = DateTime.MinValue;
    private static string[] _cachedTargetSystems = { "Local Machine" };

    public static string[] TargetSystems
    {
        get
        {
            RefreshIfNeeded();
            return _cachedTargetSystems;
        }
    }

    public static string CurrentTargetSystem { get; set; } = TargetSystems[0];

    private static void RefreshIfNeeded()
    {
        // Avoid running wsl.exe too frequently; this combo can be queried often.
        var now = DateTime.UtcNow;
        if ((now - _lastRefreshUtc) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        lock (Locker)
        {
            if ((now - _lastRefreshUtc) < TimeSpan.FromSeconds(5))
            {
                return;
            }

            var systems = new List<string> { "Local Machine" };
            try
            {
                var wslExe = WslInfo.GetWslExePath();
                var psi = new ProcessStartInfo
                {
                    FileName = wslExe,
                    Arguments = "-l -q",
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var p = Process.Start(psi);
                if (p != null)
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    // wsl.exe output can contain embedded NULs on some systems when redirected.
                    if (!string.IsNullOrEmpty(stdout) && stdout.IndexOf('\0') >= 0)
                    {
                        stdout = stdout.Replace("\0", string.Empty);
                    }

                    p.WaitForExit(2000);
                    var distros = stdout
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => (s?.IndexOf('\0') >= 0 ? s.Replace("\0", string.Empty) : s).Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

                    systems.AddRange(distros.Select(d => $"WSL: {d}"));
                }
            }
            catch
            {
                // Best-effort only.
            }

            _cachedTargetSystems = systems.ToArray();
            _lastRefreshUtc = now;

            // Ensure current selection is valid.
            if (string.IsNullOrWhiteSpace(CurrentTargetSystem) ||
                !_cachedTargetSystems.Contains(CurrentTargetSystem, StringComparer.OrdinalIgnoreCase))
            {
                CurrentTargetSystem = _cachedTargetSystems[0];
            }
        }
    }
}

[Command(PackageGuids.guidRustAnalyzerTargetSystemCmdSetString, PackageIds.IdTargetSystemCombo)]
public sealed class TargetSystemComboCommand : BaseRustAnalyzerCommand<TargetSystemComboCommand>
{
    private static bool _loadedFromWorkspaceSettings;

    protected override void ExecuteCore(object sender, OleMenuCmdEventArgs eventArgs)
    {
        EnsureArg.IsNotNull(eventArgs);
        EnsureArg.IsTrue(eventArgs.InValue != default || eventArgs.OutValue != default);

        var input = eventArgs.InValue;
        var vOut = eventArgs.OutValue;

        // IDE is requesting the current value for the combo.
        if (vOut != IntPtr.Zero)
        {
            EnsureLoadedFromWorkspaceSettings();
            Marshal.GetNativeVariantForObject(TemporaryTargetSystemStore.CurrentTargetSystem, vOut);
            return;
        }

        // New value was selected in the combo.
        if (input != null)
        {
            TemporaryTargetSystemStore.CurrentTargetSystem = input.ToString();

            // Propagate selection to process environment so the TestAdapter layer can read it.
            // (We keep this process-scoped to avoid impacting other VS instances.)
            if (TemporaryTargetSystemStore.CurrentTargetSystem.StartsWith("WSL:", StringComparison.OrdinalIgnoreCase))
            {
                var distro = TemporaryTargetSystemStore.CurrentTargetSystem.Substring("WSL:".Length);
                if (!string.IsNullOrEmpty(distro) && distro.IndexOf('\0') >= 0)
                {
                    distro = distro.Replace("\0", string.Empty);
                }

                distro = distro.Trim();
                ApplyProcessTargetSystem("wsl", distro);
                PersistWorkspaceTargetSystem("wsl", distro);
            }
            else
            {
                ApplyProcessTargetSystem("local", null);
                PersistWorkspaceTargetSystem("local", null);
            }
        }
    }

    private void EnsureLoadedFromWorkspaceSettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_loadedFromWorkspaceSettings)
        {
            return;
        }

        _loadedFromWorkspaceSettings = true;

        var ss = CmdServices.SettingsService;
        var workspaceRoot = CmdServices.GetWorkspaceRoot();
        if (ss == null || string.IsNullOrWhiteSpace((string)workspaceRoot))
        {
            return;
        }

        try
        {
            // Ensure distro list is populated so we can validate persisted settings.
            // This also strips any redirected-output NULs from wsl.exe output.
            var validTargets = TemporaryTargetSystemStore.TargetSystems;

            var mode = ThreadHelper.JoinableTaskFactory.Run(async () => await ss.GetAsync(SettingsInfo.TypeTargetSystem, workspaceRoot));
            var distro = ThreadHelper.JoinableTaskFactory.Run(async () => await ss.GetAsync(SettingsInfo.TypeWslDistroName, workspaceRoot));

            if (string.Equals(mode, "wsl", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(distro))
            {
                var cleaned = distro;
                if (cleaned.IndexOf('\0') >= 0)
                {
                    cleaned = cleaned.Replace("\0", string.Empty);
                }

                cleaned = cleaned.Trim();
                var candidate = $"WSL: {cleaned}";

                // If the distro from settings isn't currently installed (per wsl -l -q),
                // don't show it as selected (it will lead to WSL_E_DISTRO_NOT_FOUND later).
                if (validTargets.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    TemporaryTargetSystemStore.CurrentTargetSystem = candidate;
                    ApplyProcessTargetSystem("wsl", cleaned);
                }
                else
                {
                    TemporaryTargetSystemStore.CurrentTargetSystem = "Local Machine";
                    ApplyProcessTargetSystem("local", null);
                }
            }
            else
            {
                TemporaryTargetSystemStore.CurrentTargetSystem = "Local Machine";
                ApplyProcessTargetSystem("local", null);
            }
        }
        catch
        {
            // Best-effort only (never break combo status).
        }
    }

    private static void ApplyProcessTargetSystem(string mode, string distro)
    {
        Environment.SetEnvironmentVariable(TestAdapterConstants.RAVsTargetSystem, mode, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(TestAdapterConstants.RAVsWslDistroName, string.IsNullOrWhiteSpace(distro) ? null : distro, EnvironmentVariableTarget.Process);
        // Stamp used by file scanners to force rescan (debug dropdown refresh) when target system changes.
        Environment.SetEnvironmentVariable(TestAdapterConstants.RAVsTargetSystemStampUtcTicks, DateTime.UtcNow.Ticks.ToString(), EnvironmentVariableTarget.Process);
    }

    private void PersistWorkspaceTargetSystem(string mode, string distro)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var ss = CmdServices.SettingsService;
        var workspaceRoot = CmdServices.GetWorkspaceRoot();
        if (ss == null || string.IsNullOrWhiteSpace((string)workspaceRoot))
        {
            return;
        }

        // Don't block the UI thread; persist best-effort.
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ss.SetAsync(SettingsInfo.TypeTargetSystem, workspaceRoot, mode ?? string.Empty);
            await ss.SetAsync(SettingsInfo.TypeWslDistroName, workspaceRoot, distro ?? string.Empty);
        });
    }
}

[Command(PackageGuids.guidRustAnalyzerTargetSystemCmdSetString, PackageIds.IdTargetSystemComboGetList)]
public sealed class TargetSystemComboGetListCommand : BaseRustAnalyzerCommand<TargetSystemComboGetListCommand>
{
    protected override void ExecuteCore(object sender, OleMenuCmdEventArgs eventArgs)
    {
        EnsureArg.IsNotNull(eventArgs);
        EnsureArg.IsNotDefault(eventArgs.OutValue);

        var vOut = eventArgs.OutValue;

        Marshal.GetNativeVariantForObject(TemporaryTargetSystemStore.TargetSystems, vOut);
    }
}

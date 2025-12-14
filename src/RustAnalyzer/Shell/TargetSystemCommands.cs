using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using EnsureThat;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;

namespace KS.RustAnalyzer.Shell;

/// <summary>
/// Provides access to the current target system service.
/// This is used by the commands to get/set the active target.
/// </summary>
public static class TargetSystemStore
{
    private static ITargetSystemService _service;
    private static readonly object _lock = new object();

    /// <summary>
    /// Gets or creates the target system service for the current workspace.
    /// </summary>
    public static ITargetSystemService GetService(PathEx workspaceRoot)
    {
        lock (_lock)
        {
            if (_service == null && workspaceRoot != null)
            {
                var options = Options.GetLiveInstanceAsync().GetAwaiter().GetResult();
                _service = new TargetSystemService(
                    workspaceRoot,
                    options?.EnableWslSupport ?? false,
                    options?.EnableSshSupport ?? false);

                // Initialize available targets
                _ = _service.RefreshAvailableTargetsAsync(CancellationToken.None);
            }

            return _service;
        }
    }

    /// <summary>
    /// Clears the cached service (call when workspace changes).
    /// </summary>
    public static void ClearService()
    {
        lock (_lock)
        {
            _service = null;
        }
    }

    /// <summary>
    /// Gets the current target display name for the combo.
    /// </summary>
    public static string CurrentTargetDisplayName
    {
        get
        {
            var service = _service;
            return service?.CurrentTarget?.DisplayName ?? LocalTargetSystem.Instance.DisplayName;
        }
    }

    /// <summary>
    /// Gets the available target display names for the combo.
    /// </summary>
    public static string[] AvailableTargetDisplayNames
    {
        get
        {
            var service = _service;
            if (service == null)
            {
                return new[] { LocalTargetSystem.Instance.DisplayName };
            }

            return service.AvailableTargets.Select(t => t.DisplayName).ToArray();
        }
    }

    /// <summary>
    /// Sets the current target by display name.
    /// </summary>
    public static void SetCurrentTargetByDisplayName(string displayName)
    {
        var service = _service;
        if (service == null)
        {
            return;
        }

        var target = service.AvailableTargets.FirstOrDefault(t =>
            string.Equals(t.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

        if (target != null)
        {
            _ = service.SetCurrentTargetAsync(target, CancellationToken.None);
        }
    }
}

[Command(PackageGuids.guidRustAnalyzerTargetSystemCmdSetString, PackageIds.IdTargetSystemCombo)]
public sealed class TargetSystemComboCommand : BaseRustAnalyzerCommand<TargetSystemComboCommand>
{
    protected override void ExecuteCore(object sender, OleMenuCmdEventArgs eventArgs)
    {
        EnsureArg.IsNotNull(eventArgs);
        EnsureArg.IsTrue(eventArgs.InValue != default || eventArgs.OutValue != default);

        var input = eventArgs.InValue;
        var vOut = eventArgs.OutValue;

        // IDE is requesting the current value for the combo.
        if (vOut != IntPtr.Zero)
        {
            Marshal.GetNativeVariantForObject(TargetSystemStore.CurrentTargetDisplayName, vOut);
            return;
        }

        // New value was selected in the combo.
        if (input != null)
        {
            TargetSystemStore.SetCurrentTargetByDisplayName(input.ToString());
        }
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

        Marshal.GetNativeVariantForObject(TargetSystemStore.AvailableTargetDisplayNames, vOut);
    }
}

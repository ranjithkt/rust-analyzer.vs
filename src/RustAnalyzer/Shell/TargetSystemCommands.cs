using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private static PathEx _currentWorkspaceRoot;
    private static readonly object _lock = new object();

    private const string SettingsFolder = ".vs";
    private const string RustAnalyzerFolder = "rust-analyzer";
    private const string SettingsFileName = "workspace-settings.json";

    /// <summary>
    /// Gets or creates the target system service for the current workspace.
    /// </summary>
    public static ITargetSystemService GetService(PathEx workspaceRoot)
    {
        lock (_lock)
        {
            if (_service == null && workspaceRoot != null)
            {
                _currentWorkspaceRoot = workspaceRoot;

                // Auto-detect WSL workspace from path - this is synchronous and safe
                var isWslWorkspace = WslPathMapper.TryGetDistroName(workspaceRoot, out var detectedDistro);

                // For WSL workspaces, always enable WSL support regardless of options
                // For non-WSL workspaces, start with defaults and refresh later
                var wslEnabled = isWslWorkspace;
                var sshEnabled = false;

                if (isWslWorkspace)
                {
                    System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] WSL workspace detected in distro: {detectedDistro}");
                }

                System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Creating service: WSL={wslEnabled}, SSH={sshEnabled}, Workspace={workspaceRoot}");

                _service = new TargetSystemService(
                    workspaceRoot,
                    wslEnabled,
                    sshEnabled);

                // Subscribe to target changes to persist selection
                _service.TargetChanged += OnTargetChanged;

                // Initialize asynchronously to avoid deadlock - fire and forget
                // The service will update its targets in the background
                _ = InitializeServiceAsync(_service, workspaceRoot, isWslWorkspace);
            }

            return _service;
        }
    }

    /// <summary>
    /// Initializes the service asynchronously to avoid blocking the UI thread.
    /// </summary>
    private static async Task InitializeServiceAsync(ITargetSystemService service, PathEx workspaceRoot, bool isWslWorkspace)
    {
        try
        {
            // Load options on background thread
            var options = await Options.GetLiveInstanceAsync().ConfigureAwait(false);
            var wslEnabled = isWslWorkspace || (options?.EnableWslSupport ?? false);
            var sshEnabled = options?.EnableSshSupport ?? false;

            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Async init: WSL={wslEnabled}, SSH={sshEnabled}");

            // Update service settings if needed (for non-WSL workspaces that have options enabled)
            if (service is TargetSystemService tss)
            {
                tss.UpdateSettings(wslEnabled, sshEnabled);
            }

            // Refresh available targets
            await service.RefreshAvailableTargetsAsync(CancellationToken.None).ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Available targets: {string.Join(", ", service.AvailableTargets.Select(t => t.DisplayName))}");

            // Restore saved target selection
            if (isWslWorkspace)
            {
                // For WSL workspaces, only restore if saved target is also WSL
                var savedTargetId = LoadLastSelectedTargetId(workspaceRoot);
                if (!string.IsNullOrEmpty(savedTargetId) && savedTargetId.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
                {
                    RestoreLastSelectedTarget(workspaceRoot);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] WSL workspace - keeping auto-detected target: {service.CurrentTarget?.DisplayName}");
                }
            }
            else
            {
                RestoreLastSelectedTarget(workspaceRoot);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Error in async init: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the cached service (call when workspace changes).
    /// </summary>
    public static void ClearService()
    {
        lock (_lock)
        {
            if (_service != null)
            {
                _service.TargetChanged -= OnTargetChanged;
            }

            _service = null;
            _currentWorkspaceRoot = default;
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
    public static string[] GetAvailableTargetDisplayNames(PathEx workspaceRoot)
    {
        var service = GetService(workspaceRoot);
        if (service == null)
        {
            return new[] { LocalTargetSystem.Instance.DisplayName };
        }

        return service.AvailableTargets.Select(t => t.DisplayName).ToArray();
    }

    /// <summary>
    /// Gets the available target display names for the combo (uses cached service).
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

    /// <summary>
    /// Handles target changed event to persist the selection.
    /// </summary>
    private static void OnTargetChanged(object sender, TargetChangedEventArgs e)
    {
        if (_currentWorkspaceRoot != default && e.NewTarget != null)
        {
            SaveLastSelectedTarget(_currentWorkspaceRoot, e.NewTarget.Id);
        }
    }

    /// <summary>
    /// Restores the last selected target from workspace settings.
    /// </summary>
    private static void RestoreLastSelectedTarget(PathEx workspaceRoot)
    {
        try
        {
            var savedTargetId = LoadLastSelectedTargetId(workspaceRoot);
            if (string.IsNullOrEmpty(savedTargetId))
            {
                System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] No saved target found for workspace");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Restoring saved target: {savedTargetId}");

            var target = _service?.AvailableTargets.FirstOrDefault(t =>
                string.Equals(t.Id, savedTargetId, StringComparison.OrdinalIgnoreCase));

            if (target != null)
            {
                _ = _service.SetCurrentTargetAsync(target, CancellationToken.None);
                System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Restored target: {target.DisplayName}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Saved target '{savedTargetId}' not found in available targets");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Error restoring target: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves the selected target ID to workspace settings.
    /// </summary>
    private static void SaveLastSelectedTarget(PathEx workspaceRoot, string targetId)
    {
        try
        {
            var settingsPath = GetSettingsFilePath(workspaceRoot);
            var settingsDir = Path.GetDirectoryName(settingsPath);

            // Ensure directory exists
            if (!Directory.Exists(settingsDir))
            {
                Directory.CreateDirectory(settingsDir);
            }

            // Load existing settings or create new
            WorkspaceSettings settings;
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                settings = JsonSerializer.Deserialize<WorkspaceSettings>(json) ?? new WorkspaceSettings();
            }
            else
            {
                settings = new WorkspaceSettings();
            }

            settings.LastSelectedTargetId = targetId;

            // Save settings
            var options = new JsonSerializerOptions { WriteIndented = true };
            var newJson = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(settingsPath, newJson);

            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Saved target selection: {targetId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Error saving target: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the last selected target ID from workspace settings.
    /// </summary>
    private static string LoadLastSelectedTargetId(PathEx workspaceRoot)
    {
        try
        {
            var settingsPath = GetSettingsFilePath(workspaceRoot);
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<WorkspaceSettings>(json);
            return settings?.LastSelectedTargetId;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TargetSystemStore] Error loading settings: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the path to the workspace settings file.
    /// </summary>
    private static string GetSettingsFilePath(PathEx workspaceRoot)
    {
        return Path.Combine((string)workspaceRoot, SettingsFolder, RustAnalyzerFolder, SettingsFileName);
    }

    /// <summary>
    /// Workspace settings persisted per workspace.
    /// </summary>
    private class WorkspaceSettings
    {
        public string LastSelectedTargetId { get; set; }
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
        ThreadHelper.ThrowIfNotOnUIThread();

        EnsureArg.IsNotNull(eventArgs);
        EnsureArg.IsNotDefault(eventArgs.OutValue);

        var vOut = eventArgs.OutValue;

        // Get workspace root to initialize the service if needed
        var workspaceRoot = CmdServices.GetWorkspaceRoot();
        var targets = workspaceRoot.HasValue
            ? TargetSystemStore.GetAvailableTargetDisplayNames(workspaceRoot.Value)
            : new[] { LocalTargetSystem.Instance.DisplayName };

        Marshal.GetNativeVariantForObject(targets, vOut);
    }
}

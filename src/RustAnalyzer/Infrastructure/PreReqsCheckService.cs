using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;
using CommunityVS = Community.VisualStudio.Toolkit.VS;
using Constants = KS.RustAnalyzer.TestAdapter.Constants;

namespace KS.RustAnalyzer.Infrastructure;

public interface IPreReqsCheckService
{
    Task SatisfyAsync(CancellationToken ct);

    /// <summary>
    /// Performs workspace-specific prerequisite checks.
    /// For WSL workspaces, checks WSL availability and toolchain inside the distro.
    /// </summary>
    Task SatisfyForWorkspaceAsync(PathEx workspaceRoot, CancellationToken ct);
}

[Export(typeof(IPreReqsCheckService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class PreReqsCheckService : IPreReqsCheckService
{
    private static readonly TimeSpan WslPrereqTimeout = TimeSpan.FromSeconds(5);

    private readonly IToolchainService _cargoService;
    private readonly TL _tl;

    private readonly IReadOnlyDictionary<string, Func<IToolchainService, CancellationToken, Task<(bool, string)>>> _preReqChecks =
        new Dictionary<string, Func<IToolchainService, CancellationToken, Task<(bool, string)>>>
        {
            [nameof(VsVersionCheck)] = VsVersionCheck.CheckAsync,

            // TODO: https://github.com/kitamstudios/rust-analyzer.vs/issues/54
            // [nameof(CheckRustupToolchainInstallationAsync)] = CheckRustupToolchainInstallationAsync,
            [nameof(CheckRustupAsync)] = CheckRustupAsync,
            [nameof(CheckCargoAsync)] = CheckCargoAsync,
        };

    [ImportingConstructor]
    public PreReqsCheckService([Import] IToolchainService cargoService, [Import] ITelemetryService t, [Import] ILogger l)
    {
        _cargoService = cargoService;
        _tl = new TL
        {
            T = t,
            L = l,
        };
    }

    public async Task SatisfyAsync(CancellationToken ct)
    {
        var results = await DoChecksAsync(ct);

        var failures = results.Where(x => !x.Success);
        if (failures.Any())
        {
            var line1 = failures
                .Aggregate(
                    new StringBuilder("Prerequisite check(s) failed:"),
                    (acc, e) => acc.AppendLine().AppendFormat("- {0}", e.Message))
                .ToString();
            await VsCommon.ShowMessageBoxAsync(
                line1,
                $"Pressing OK will open prerequsites install instructions and restart the IDE.");
            VsShellUtilities.OpenSystemBrowser(Constants.PrerequisitesUrl);
            await CommunityVS.Shell.RestartAsync();
        }
    }

    public async Task SatisfyForWorkspaceAsync(PathEx workspaceRoot, CancellationToken ct)
    {
        // Mode 1: WSL UNC workspace
        // Mode 2: Windows-local workspace + WSL execution (when user selected a WSL target system)
        if (!TargetSystemSelection.TryGetWslExecutionContext(workspaceRoot, out var wslInfo, out var distroName))
        {
            await SatisfyAsync(ct);
            return;
        }

        // WSL workspace: do NOT require cargo.exe/rustup.exe on Windows.
        // Still require a compatible VS version.
        var results = new List<(bool Success, string Message)>();

        _tl.L.WriteLine("Running WSL PreReqCheck: VsVersionCheck...");
        var (vsSuccess, vsMessage) = await VsVersionCheck.CheckAsync(_cargoService, ct);
        if (!vsSuccess)
        {
            results.Add((vsSuccess, vsMessage));
        }

        results.AddRange(await DoWslChecksAsync(distroName, ct));

        var failures = results.Where(x => !x.Success);
        if (failures.Any())
        {
            var line1 = failures
                .Aggregate(
                    new StringBuilder($"Prerequisite check(s) failed for WSL distro '{distroName}':"),
                    (acc, e) => acc.AppendLine().AppendFormat("- {0}", e.Message))
                .ToString();

            await VsCommon.ShowMessageBoxAsync(
                line1,
                $"Please ensure WSL is properly configured and the Rust toolchain is installed inside WSL distro '{distroName}'.");
        }
    }

    private async Task<IEnumerable<(bool Success, string Message)>> DoWslChecksAsync(string distroName, CancellationToken ct)
    {
        var results = new List<(bool Success, string Message)>();

        // Check wsl.exe availability
        _tl.L.WriteLine("Running WSL PreReqCheck: CheckWslExeAsync...");
        var (wslSuccess, wslMessage) = await CheckWslExeAsync(ct);
        if (!wslSuccess)
        {
            _tl.L.WriteLine("... CheckWslExeAsync failed: {0}.", wslMessage);
            _tl.T.TrackException(new ArgumentOutOfRangeException(wslMessage));
            results.Add((wslSuccess, wslMessage));
            return results; // Cannot proceed without wsl.exe
        }

        // Check cargo inside WSL
        _tl.L.WriteLine("Running WSL PreReqCheck: CheckCargoInWslAsync...");
        var (cargoSuccess, cargoMessage) = await CheckCargoInWslAsync(distroName, ct);
        if (!cargoSuccess)
        {
            _tl.L.WriteLine("... CheckCargoInWslAsync failed: {0}.", cargoMessage);
            _tl.T.TrackException(new ArgumentOutOfRangeException(cargoMessage));
            results.Add((cargoSuccess, cargoMessage));
        }

        // Check rustup inside WSL
        _tl.L.WriteLine("Running WSL PreReqCheck: CheckRustupInWslAsync...");
        var (rustupSuccess, rustupMessage) = await CheckRustupInWslAsync(distroName, ct);
        if (!rustupSuccess)
        {
            _tl.L.WriteLine("... CheckRustupInWslAsync failed: {0}.", rustupMessage);
            _tl.T.TrackException(new ArgumentOutOfRangeException(rustupMessage));
            results.Add((rustupSuccess, rustupMessage));
        }

        return results;
    }

    private static async Task<(bool Success, string Message)> CheckWslExeAsync(CancellationToken ct)
    {
        try
        {
            if (WslInfo.IsWslAvailable())
            {
                return await (true, string.Empty).ToTask();
            }
        }
        catch (Exception e)
        {
            TryLogPrereqException("CheckWslExeAsync", e);
        }

        return (false, "wsl.exe not found. Please ensure WSL is installed.");
    }

    private static async Task<(bool Success, string Message)> CheckCargoInWslAsync(string distroName, CancellationToken ct)
    {
        try
        {
            var wslExePath = WslInfo.GetWslExePath();
            // Direct `wsl.exe --exec cargo` can fail if PATH doesn't include ~/.cargo/bin.
            // Run via a login shell so rustup-installed cargo is discoverable.
            var wslArgs = new[] { "-d", distroName, "--exec", "/bin/bash", "-lc", "cargo --version" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(WslPrereqTimeout);
            using var proc = ProcessRunner.Run(wslExePath, wslArgs, Environment.SystemDirectory, ImmutableDictionary<string, string>.Empty, cts.Token);
            var ec = await proc;

            if (ec != 0 && IsWslDistroNotFound(proc))
            {
                return (false, $"WSL distro '{distroName}' was not found. Run 'wsl -l -q' to see installed distributions.");
            }

            if (ec == 0 && proc.StandardOutputLines.Any())
            {
                return (true, string.Empty);
            }
        }
        catch (OperationCanceledException e)
        {
            TryLogPrereqException("CheckCargoInWslAsync(timeout)", e);
            return (false, $"Timed out while checking cargo in WSL distro '{distroName}'.");
        }
        catch (Exception e)
        {
            TryLogPrereqException("CheckCargoInWslAsync", e);
        }

        return (false, $"cargo not found in WSL distro '{distroName}'. Please install Rust toolchain inside WSL.");
    }

    private static async Task<(bool Success, string Message)> CheckRustupInWslAsync(string distroName, CancellationToken ct)
    {
        try
        {
            var wslExePath = WslInfo.GetWslExePath();
            // Same PATH caveat as cargo; use a login shell.
            var wslArgs = new[] { "-d", distroName, "--exec", "/bin/bash", "-lc", "rustup --version" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(WslPrereqTimeout);
            using var proc = ProcessRunner.Run(wslExePath, wslArgs, Environment.SystemDirectory, ImmutableDictionary<string, string>.Empty, cts.Token);
            var ec = await proc;

            if (ec != 0 && IsWslDistroNotFound(proc))
            {
                return (false, $"WSL distro '{distroName}' was not found. Run 'wsl -l -q' to see installed distributions.");
            }

            if (ec == 0 && proc.StandardOutputLines.Any())
            {
                return (true, string.Empty);
            }
        }
        catch (OperationCanceledException e)
        {
            TryLogPrereqException("CheckRustupInWslAsync(timeout)", e);
            return (false, $"Timed out while checking rustup in WSL distro '{distroName}'.");
        }
        catch (Exception e)
        {
            TryLogPrereqException("CheckRustupInWslAsync", e);
        }

        return (false, $"rustup not found in WSL distro '{distroName}'. Please install rustup inside WSL.");
    }

    private async Task<IEnumerable<(bool Success, string Message)>> DoChecksAsync(CancellationToken ct)
    {
        var results = new List<(bool Success, string Message)>();
        foreach (var kv in _preReqChecks)
        {
            _tl.L.WriteLine("Running PreReqCheck: {0}...", kv.Key);
            var (success, message) = await kv.Value(_cargoService, ct);
            if (!success)
            {
                _tl.L.WriteLine("... {0} failed: {1}.", kv.Key, message);
                _tl.T.TrackException(new ArgumentOutOfRangeException(message));
                results.Add((success, message));
            }
        }

        return results;
    }

    private static async Task<(bool Success, string Message)> CheckCargoAsync(IToolchainService ts, CancellationToken ct)
    {
        try
        {
            if (ts.GetCargoExePath().FileExists())
            {
                return await (true, string.Empty).ToTask();
            }
        }
        catch (Exception e)
        {
            TryLogPrereqException("CheckCargoAsync", e);
        }

        return (false, $"{Constants.CargoExe} component is not found in any active toolchain.");
    }

    private static async Task<(bool Success, string Message)> CheckRustupAsync(IToolchainService ts, CancellationToken ct)
    {
        try
        {
            if (ToolchainServiceExtensions.GetRustupPath().FileExists())
            {
                return await (true, string.Empty).ToTask();
            }
        }
        catch (Exception e)
        {
            TryLogPrereqException("CheckRustupAsync", e);
        }

        return (false, $"{Constants.RustUpExe} not installed.");
    }

    private static void TryLogPrereqException(string check, Exception e)
    {
        try
        {
            ActivityLog.LogInformation(Vsix.Name, $"PreReqsCheck '{check}' threw: {e.GetType().Name}: {e.Message}");
        }
        catch
        {
            // Best-effort only.
        }

        Debug.WriteLine($"[{Vsix.Name}] PreReqsCheck '{check}' threw: {e}");
    }

    private static bool IsWslDistroNotFound(ProcessRunner proc)
    {
        try
        {
            var combined = string.Join("\n", proc.StandardOutputLines.Concat(proc.StandardErrorLines));
            if (string.IsNullOrEmpty(combined))
            {
                return false;
            }

            return combined.IndexOf("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Success, string Message)> CheckRustupToolchainInstallationAsync(IToolchainService ts, CancellationToken ct)
    {
        try
        {
            if (!(await ToolchainServiceExtensions.GetDefaultToolchainAsync((PathEx)Environment.GetEnvironmentVariable("WINDIR"), ct)).IsNullOrEmptyOrWhiteSpace())
            {
                return await (true, string.Empty).ToTask();
            }
        }
        catch
        {
        }

        return (false, $"Rust installation not found or is corrupted. Reinstall {Constants.RustUpExe} and toolchains.");
    }

    #region VsVersionCheck

    public static class VsVersionCheck
    {
        public static async Task<(bool Success, string Message)> CheckAsync(IToolchainService ts, CancellationToken ct)
        {
            var version = await CommunityVS.Shell.GetVsVersionAsync();
            if (version == null)
            {
                return (false, "GetVsVersionAsync returned null. Indicates an issue with VS installation, restarting or latest updates may help.");
            }

            if (version <= Constants.MinimumRequiredVsVersion)
            {
                return (false, $"VS Version check failed. Minimum {Constants.MinimumRequiredVsVersion}, found {version}.\n\nInstall the latest VS update.\n\nThis is a one time thing. Unfortunately it is required as VS {Constants.MinimumRequiredVsVersion} introduced breaking changes. Sorry about that!");
            }

            return (true, string.Empty);
        }
    }

    #endregion
}

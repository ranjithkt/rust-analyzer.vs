using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        // Check if this is a WSL workspace
        if (!WslInfo.TryParse(workspaceRoot, out var wslInfo))
        {
            // Not a WSL workspace - standard checks already passed in SatisfyAsync
            return;
        }

        var results = await DoWslChecksAsync(wslInfo, ct);

        var failures = results.Where(x => !x.Success);
        if (failures.Any())
        {
            var line1 = failures
                .Aggregate(
                    new StringBuilder($"WSL prerequisite check(s) failed for distro '{wslInfo.DistroName}':"),
                    (acc, e) => acc.AppendLine().AppendFormat("- {0}", e.Message))
                .ToString();
            await VsCommon.ShowMessageBoxAsync(
                line1,
                $"Please ensure WSL is properly configured and Rust toolchain is installed inside WSL distro '{wslInfo.DistroName}'.");
        }
    }

    private async Task<IEnumerable<(bool Success, string Message)>> DoWslChecksAsync(WslInfo wslInfo, CancellationToken ct)
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
        var (cargoSuccess, cargoMessage) = await CheckCargoInWslAsync(wslInfo, ct);
        if (!cargoSuccess)
        {
            _tl.L.WriteLine("... CheckCargoInWslAsync failed: {0}.", cargoMessage);
            _tl.T.TrackException(new ArgumentOutOfRangeException(cargoMessage));
            results.Add((cargoSuccess, cargoMessage));
        }

        // Check rustup inside WSL
        _tl.L.WriteLine("Running WSL PreReqCheck: CheckRustupInWslAsync...");
        var (rustupSuccess, rustupMessage) = await CheckRustupInWslAsync(wslInfo, ct);
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
        catch
        {
        }

        return (false, "wsl.exe not found. Please ensure WSL is installed.");
    }

    private static async Task<(bool Success, string Message)> CheckCargoInWslAsync(WslInfo wslInfo, CancellationToken ct)
    {
        try
        {
            var wslExePath = WslInfo.GetWslExePath();
            var wslArgs = new[] { "-d", wslInfo.DistroName, "--exec", Constants.WslCargoExe, "--version" };

            using var proc = ProcessRunner.Run(wslExePath, wslArgs, null, ImmutableDictionary<string, string>.Empty, ct);
            var ec = await proc;

            if (ec == 0 && proc.StandardOutputLines.Any())
            {
                return (true, string.Empty);
            }
        }
        catch
        {
        }

        return (false, $"cargo not found in WSL distro '{wslInfo.DistroName}'. Please install Rust toolchain inside WSL.");
    }

    private static async Task<(bool Success, string Message)> CheckRustupInWslAsync(WslInfo wslInfo, CancellationToken ct)
    {
        try
        {
            var wslExePath = WslInfo.GetWslExePath();
            var wslArgs = new[] { "-d", wslInfo.DistroName, "--exec", Constants.WslRustUpExe, "--version" };

            using var proc = ProcessRunner.Run(wslExePath, wslArgs, null, ImmutableDictionary<string, string>.Empty, ct);
            var ec = await proc;

            if (ec == 0 && proc.StandardOutputLines.Any())
            {
                return (true, string.Empty);
            }
        }
        catch
        {
        }

        return (false, $"rustup not found in WSL distro '{wslInfo.DistroName}'. Please install rustup inside WSL.");
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
        catch
        {
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
        catch
        {
        }

        return (false, $"{Constants.RustUpExe} not installed.");
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

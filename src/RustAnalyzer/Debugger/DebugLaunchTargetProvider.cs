using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Debug;
using static Microsoft.VisualStudio.VSConstants;

// WSL debugging support: uses bstrPortName = "SSH:wsl+<distro>" for remote debugging

namespace KS.RustAnalyzer.Debugger;

// TODO: Workaround for https://github.com/kitamstudios/rust-analyzer.vs/issues/24. Just implementing LaunchDebugTargetProviderOptions.IsRuntimeSupportContext should be enough but it does not work, for now setting priority to low.
// NOTE:
// - Windows binaries are typically ".exe".
// - WSL/Linux binaries are typically extensionless.
// Some VS builds appear to not consistently route extensionless binaries via an empty-string extension filter,
// so we register broadly and do strict filtering in SupportsContext.
[ExportLaunchDebugTarget(LaunchDebugTargetProviderOptions.IsRuntimeSupportContext, ProviderType, new[] { ".toml", ".exe", "" }, ProviderPriority.Lowest)]
public sealed class DebugLaunchTargetProvider : ILaunchDebugTargetProvider
{
    public const string ProviderType = "{72D3FCEF-1111-4266-B8DD-D3ED06E35A2B}";
    public static readonly Guid ProviderTypeGuid = new(ProviderType);
    // Visual Studio (AD7) engine + port supplier GUIDs (from VS pkgdef registrations).
    // - "Native (GDB)" engine: Microsoft.MIDebugEngine.pkgdef -> {91744D97-430F-42C1-9779-A5813EBD6AB2}
    // - "Windows Subsystem for Linux (WSL)" port supplier: Microsoft.SSHDebugPS.pkgdef -> {267B1341-AC92-44DC-94DF-2EE4205DD17E}
    private static readonly Guid GdbEngineGuid = new("91744D97-430F-42C1-9779-A5813EBD6AB2");
    private static readonly Guid WslPortSupplierGuid = new("267B1341-AC92-44DC-94DF-2EE4205DD17E");

    // Used for creating the managed WSL port supplier type (no COM activation needed).
    // Microsoft.SSHDebugPS.pkgdef registers:
    //   AD7Metrics\\PortSupplier\\{267B...} -> CLSID {B8587A49-...} -> class Microsoft.SSHDebugPS.WSL.WSLPortSupplier
    private static readonly Guid WslPortSupplierClsid = new("B8587A49-00BD-4DEE-94B9-6EBF49003E04");
    private static Assembly _sshDebugPsAssembly;

    [Import]
    public ILogger L { get; set; }

    [Import]
    public ITelemetryService T { get; set; }

    public void LaunchDebugTarget(IWorkspace workspaceContext, IServiceProvider serviceProvider, DebugLaunchActionContext debugLaunchActionContext)
    {
        var lcw = new LaunchConfigWrapper(debugLaunchActionContext.LaunchConfiguration, new TL { T = T, L = L, });
        workspaceContext.JTF.Run(async () => await LaunchDebugTargetAsync(workspaceContext, serviceProvider, lcw, default));
    }

    public bool SupportsContext(IWorkspace workspaceContext, string targetFilePath)
    {
        try
        {
            if (workspaceContext == null || string.IsNullOrWhiteSpace(targetFilePath))
            {
                return false;
            }

            // VS can call SupportsContext with either:
            // - the selected project/manifest file (Cargo.toml), or
            // - the built output under target\...
            //
            // For Open Folder "Debug" on Cargo.toml, we must accept Cargo.toml here or VS may never call
            // LaunchDebugTarget at all (resulting in "it builds but doesn't launch").
            if (targetFilePath.EndsWith("Cargo.toml", StringComparison.OrdinalIgnoreCase))
            {
                var mdsToml = workspaceContext.GetService<IMetadataService>();
                if (mdsToml == null)
                {
                    return false;
                }

                var pkgToml = workspaceContext.JTF.Run(async () => await mdsToml.GetContainingPackageAsync((PathEx)targetFilePath, default));
                if (pkgToml == null)
                {
                    return false;
                }

                // If any runnable target exists, we can handle launching for this manifest.
                return pkgToml.GetTargets().Any(t => t.IsRunnable) ||
                       pkgToml.Parent?.Packages?.Any(p => p.GetTargets().Any(t => t.IsRunnable)) == true;
            }

            // Only handle build outputs under "target\..." to avoid stealing unrelated debug launches.
            // (On WSL UNC and Windows-local paths, VS still passes backslash-separated paths to us.)
            if (targetFilePath.IndexOf(@"\target\", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var mds = workspaceContext.GetService<IMetadataService>();
            if (mds == null)
            {
                return false;
            }

            // Prefer cached packages to avoid expensive metadata loads, but fall back to loading.
            var packages = workspaceContext.JTF.Run(async () => (await mds.GetCachedPackagesAsync(default))?.ToArray() ?? Array.Empty<Workspace.Package>());
            if (packages.Length == 0)
            {
                var containing = workspaceContext.JTF.Run(async () => await mds.GetContainingPackageAsync((PathEx)targetFilePath, default));
                return containing != null && MatchesRunnableTarget(containing, targetFilePath);
            }

            return packages.Any(p => MatchesRunnableTarget(p, targetFilePath));
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesRunnableTarget(Workspace.Package package, string targetFilePath)
    {
        try
        {
            var fp = ((PathEx)targetFilePath).GetFullPath();
            foreach (var target in package.GetTargets().Where(t => t.IsRunnable))
            {
                foreach (var profile in package.GetProfiles())
                {
                    if (target.GetPath(profile).GetFullPath() == fp)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore and treat as no-match.
        }

        return false;
    }

    private async Task LaunchDebugTargetAsync(IWorkspace workspaceContext, IServiceProvider serviceProvider, LaunchConfigWrapper lcw, CancellationToken ct)
    {
        const string diagMessage = "Delete the .vs folder and try again. If that does not work please file a bug with the repro steps.";
        try
        {
            var mds = workspaceContext.GetService<IMetadataService>();
            var package = await mds.GetContainingPackageAsync((PathEx)lcw[LaunchConfigurationConstants.ProgramKey], default);
            var profile = workspaceContext.GetProfile(package.ManifestPath);
            var targetFQN = lcw[LaunchConfigurationConstants.NameKey];
            var target = package.GetTargets().FirstOrDefault(t => t.QualifiedTargetFileName == targetFQN);
            if (target == null)
            {
                string message = string.Format("Cannot find target '{0}' in '{1}', for profile '{2}'.", targetFQN, package?.FullPath, profile);
                L.WriteError(message);
                T.TrackException(new ArgumentOutOfRangeException("target", message));
                await VsCommon.ShowMessageBoxAsync(message, diagMessage);
                return;
            }

            var processName = target.GetPath(profile);

            // Mode 1: WSL UNC workspace
            // Mode 2: Windows-local workspace + WSL execution (if selected)
            var isWsl = TargetSystemSelection.TryGetWslExecutionContext(package.Parent.WorkspaceRoot, out var wslInfo, out var distroName);

            if (isWsl)
            {
                // IMPORTANT:
                // In WSL mode (especially Mode 2 mirror), the binary is produced inside Linux.
                // `processName` can be a Windows path (e.g. C:\...\target\debug\foo) that won't exist on Windows.
                // Validate existence in WSL using the resolved Linux path, not Windows File.Exists.
                var linuxExePathCheck = ResolveLinuxPathForDebugExe(processName, wslInfo, distroName, package.Parent.WorkspaceRoot);
                if (!await WslExecutableExistsAsync(distroName, linuxExePathCheck, ct))
                {
                    var message =
                        "Unable to find WSL executable.\n\n" +
                        $"Windows target path: '{processName}'\n" +
                        $"Resolved Linux path: '{linuxExePathCheck}'\n\n" +
                        "This usually means the build output was produced in a different location than the configured target system.";
                    L.WriteLine(message);
                    T.TrackException(new FileNotFoundException(message, linuxExePathCheck));
                    await VsCommon.ShowMessageBoxAsync(message, diagMessage);
                    return;
                }
            }
            else
            {
                if (!File.Exists(processName))
                {
                    var message = string.Format("Unable to find file: '{0}'.", processName);
                    L.WriteLine(message);
                    T.TrackException(new FileNotFoundException(message, processName));
                    await VsCommon.ShowMessageBoxAsync(message, diagMessage);
                    return;
                }
            }

            var args = await GetSettingsAsync(SettingsInfo.TypeCommandLineArguments, workspaceContext.GetService<ISettingsService>(), lcw);
            var env = await GetSettingsAsync(SettingsInfo.TypeDebuggerEnvironment, workspaceContext.GetService<ISettingsService>(), lcw);
            var workingDirectory = await GetSettingsAsync(SettingsInfo.TypeDebuggerWorkingDirectory, workspaceContext.GetService<ISettingsService>(), lcw);
            var noDebugFlag = lcw.ContainsKey(LaunchConfigurationConstants.NoDebugKey) ? __VSDBGLAUNCHFLAGS.DBGLAUNCH_NoDebug : 0;

            L.WriteLine("LaunchDebugTarget with profile: {0}, launchConfiguration: {1}, IsWsl: {2}", profile, lcw.SerializeObject(), isWsl);
            T.TrackEvent("Debug", ("Target", targetFQN), ("Profile", profile), ("Manifest", package.FullPath), ("Args", args), ("Env", env.ReplaceNullWithBar()), ("IsWsl", $"{isWsl}"));

            VsDebugTargetInfo info;
            if (isWsl)
            {
                // WSL: use the newer debugger API (IVsDebugger4 + VsDebugTargetInfo4).
                // The older VsShellUtilities.LaunchDebugger path can silently no-op for SSH:wsl+ transports.
                var linuxExePath = ResolveLinuxPathForDebugExe(processName, wslInfo, distroName, package.Parent.WorkspaceRoot);
                var linuxWorkingDir = ResolveLinuxWorkingDirectory(workingDirectory, package.Parent.WorkspaceRoot, processName, wslInfo, distroName);

                // Ensure a MI debugger exists in WSL; otherwise VS often fails without a useful message.
                if (!await WslExecutableExistsAsync(distroName, "/usr/bin/gdb", ct))
                {
                    var msg = "WSL debugging requires GDB in the selected distro.\n\n" +
                              $"Distro: '{distroName}'\n" +
                              "Missing: /usr/bin/gdb\n\n" +
                              "Install it in WSL (example for Debian/Ubuntu):\n" +
                              "  sudo apt update && sudo apt install -y gdb\n";
                    L.WriteError(msg);
                    await VsCommon.ShowMessageBoxAsync(msg, diagMessage);
                    return;
                }

                // WSL debugging in Visual Studio typically requires an ssh server in the distro.
                // If missing/not running, the launch often fails with generic HRESULTs.
                if (!await WslExecutableExistsAsync(distroName, "/usr/sbin/sshd", ct))
                {
                    var msg = "WSL debugging requires OpenSSH server in the selected distro.\n\n" +
                              $"Distro: '{distroName}'\n" +
                              "Missing: /usr/sbin/sshd\n\n" +
                              "Install/start it in WSL (example for Debian/Ubuntu):\n" +
                              "  sudo apt update && sudo apt install -y openssh-server\n" +
                              "  sudo service ssh start\n";
                    L.WriteError(msg);
                    await VsCommon.ShowMessageBoxAsync(msg, diagMessage);
                    return;
                }

                // Best-effort: ensure sshd is actually running. VS WSL port supplier uses SSH under the hood.
                // If sshd isn't running, launches frequently fail with 0x8971001x errors.
                var sshdState = await TryEnsureSshdRunningAsync(distroName, ct);

                var (hr, attempts) = await LaunchWslDebuggerAsync(serviceProvider, distroName, linuxExePath, linuxWorkingDir, args, noDebugFlag, ct);
                if (ErrorHandler.Failed(hr))
                {
                    var msg = $"Failed to launch WSL debugger (HRESULT=0x{hr:X8}).\n\n" +
                              $"Exe='{linuxExePath}'\n" +
                              $"CurDir='{linuxWorkingDir}'\n" +
                              $"Args='{args}'\n" +
                              $"\nsshd: {sshdState}\n" +
                              $"\nAttempts:\n{attempts}\n\n" +
                              "WSL debugging should not require configuring an SSH Connection Manager entry.\n" +
                              "If Visual Studio prompts for SSH, that indicates VS could not resolve a WSL debug port.\n" +
                              "We now query VS's own WSL port supplier to find the correct port name; if none are exposed,\n" +
                              "use 'Debug > Attach to Process' with Connection type 'Windows Subsystem for Linux (WSL)' once\n" +
                              "to let VS initialize the WSL debug transport for that distro.";
                    L.WriteError(msg);
                    await VsCommon.ShowMessageBoxAsync(msg, diagMessage);
                }

                return;
            }
            else
            {
                info = await CreateWindowsDebugTargetInfoAsync(package, profile, processName, args, env, workingDirectory, noDebugFlag, ct);
            }

            try
            {
                VsShellUtilities.LaunchDebugger(serviceProvider, info);
            }
            catch (Exception ex)
            {
                // VS sometimes fails to launch remote/MIEngine sessions without surfacing a good error.
                // Turn this into a visible message so we can diagnose missing components (e.g. MIEngine workload).
                T.TrackException(ex);
                var msg = $"Failed to launch debugger.\n\n" +
                          $"IsWsl={isWsl}\n" +
                          $"Exe='{info.bstrExe}'\n" +
                          $"CurDir='{info.bstrCurDir}'\n" +
                          $"Args='{info.bstrArg}'\n" +
                          $"Port='{info.bstrPortName}'\n\n" +
                          $"{ex.GetType().Name}: {ex.Message}";
                L.WriteError(msg);
                await VsCommon.ShowMessageBoxAsync(msg, diagMessage);
            }
        }
        catch (KeyNotFoundException knfe)
        {
            await VsCommon.ShowMessageBoxAsync(
                knfe.Message,
                "Debugger will not be launched. Please report the repro steps + this message as this issue is hard to track down. 🙏");
        }
        catch (Exception e)
        {
            T.TrackException(e);
            throw;
        }
    }

    private static async Task<bool> WslExecutableExistsAsync(string distroName, string linuxPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(distroName) || string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        try
        {
            // Use an absolute command so we don't depend on WSL PATH.
            using var proc = ToolchainServiceExtensions.RunInWsl(
                distroName,
                "/usr/bin/test",
                new[] { "-x", linuxPath },
                linuxWorkingDir: "/",
                env: null,
                ct: ct);

            var ec = await proc;
            return ec == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(int Hr, string Attempts)> LaunchWslDebuggerAsync(
        IServiceProvider serviceProvider,
        string distroName,
        string linuxExePath,
        string linuxWorkingDir,
        string args,
        __VSDBGLAUNCHFLAGS noDebugFlag,
        CancellationToken ct)
    {
        var attempts = string.Empty;
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Prefer VS Debugger4 to get a real HRESULT and to support WSL transport reliably.
            var dbg4 = serviceProvider.GetService(typeof(SVsShellDebugger)) as IVsDebugger4
                       ?? Package.GetGlobalService(typeof(SVsShellDebugger)) as IVsDebugger4;
            if (dbg4 == null)
            {
                return (E_FAIL, "IVsDebugger4 not available");
            }

            // In VsDebugTargetInfo4, the port supplier is explicit. Without it, launches often fail
            // (and may surface as generic 0x8971xxxx errors).
            //
            // IMPORTANT:
            // Prefer WSL only (no SSH fallback). We must pass a *valid port name* for the WSL port supplier.
            // VS may not accept just the distro name; instead, it expects one of the names returned by the
            // WSL port supplier's EnumPorts() list.
            var resolved = TryResolveWslPort(distroName);
            var usedPortName = resolved?.PortName ?? distroName;

            // Try a small set of launch variants. In different VS builds, the WSL supplier expects
            // either bstrRemoteMachine to be null or set to the same string as the port name.
            var a1 = TryLaunchRemoteDebugTargets(
                dbg4,
                portSupplier: WslPortSupplierGuid,
                portName: usedPortName,
                remoteMachine: null,
                linuxExePath,
                linuxWorkingDir,
                args,
                noDebugFlag);

            var a2 = a1 == S_OK
                ? S_OK
                : TryLaunchRemoteDebugTargets(
                    dbg4,
                    portSupplier: WslPortSupplierGuid,
                    portName: usedPortName,
                    remoteMachine: usedPortName,
                    linuxExePath,
                    linuxWorkingDir,
                    args,
                    noDebugFlag);

            attempts += $"WSL PortSupplier={WslPortSupplierGuid}\n" +
                        $"ResolvedPortName='{(resolved?.PortName ?? "<null>")}'\n" +
                        $"ResolvedPortId='{(resolved?.PortId ?? Guid.Empty)}'\n" +
                        $"EnumeratedPorts='{(resolved?.AllPortsSummary ?? "<none>")}'\n" +
                        $"Attempt1(RemoteMachine=null)=0x{a1:X8}\n" +
                        $"Attempt2(RemoteMachine=PortName)=0x{a2:X8}";

            return (a2, attempts);
        }
        catch (COMException comEx)
        {
            // Preserve the HRESULT so we can surface it to the user.
            T.TrackException(comEx);
            attempts += $"COMException: HRESULT=0x{comEx.ErrorCode:X8}";
            return (comEx.ErrorCode, attempts);
        }
        catch (Exception ex)
        {
            T.TrackException(ex);
            var hr = Marshal.GetHRForException(ex);
            attempts += $"{ex.GetType().Name}: HRESULT=0x{hr:X8}";
            return (hr, attempts);
        }
    }

    private int TryLaunchRemoteDebugTargets(
        IVsDebugger4 dbg4,
        Guid portSupplier,
        string portName,
        string remoteMachine,
        string linuxExePath,
        string linuxWorkingDir,
        string args,
        __VSDBGLAUNCHFLAGS noDebugFlag)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        IntPtr enginesPtr = IntPtr.Zero;
        try
        {
            // Provide an explicit engine list (one engine) to avoid ambiguity.
            enginesPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
            Marshal.StructureToPtr(GdbEngineGuid, enginesPtr, false);

            var ti = new VsDebugTargetInfo4
            {
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = linuxExePath,
                bstrCurDir = linuxWorkingDir,
                bstrArg = args,
                bstrEnv = null,
                bstrOptions = null,
                guidPortSupplier = portSupplier,
                bstrPortName = portName,
                bstrRemoteMachine = remoteMachine,
                guidLaunchDebugEngine = GdbEngineGuid,
                dwDebugEngineCount = 1,
                pDebugEngines = enginesPtr,
                LaunchFlags = (uint)(noDebugFlag | __VSDBGLAUNCHFLAGS.DBGLAUNCH_Silent | __VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd),
                fSendToOutputWindow = false,
            };

            var results = new VsDebugTargetProcessInfo[1];
            dbg4.LaunchDebugTargets4(1, new[] { ti }, results);
            L.WriteLine(
                "WSL debugger launch issued. PortSupplier={0}, PortName={1}, ProcessId={2}",
                portSupplier,
                portName,
                results[0].dwProcessId);
            return S_OK;
        }
        catch (COMException comEx)
        {
            T.TrackException(comEx);
            L.WriteError("WSL debugger launch failed. PortSupplier={0}, PortName={1}, HRESULT=0x{2:X8}", portSupplier, portName, comEx.ErrorCode);
            return comEx.ErrorCode;
        }
        finally
        {
            if (enginesPtr != IntPtr.Zero)
            {
                try
                {
                    Marshal.FreeHGlobal(enginesPtr);
                }
                catch
                {
                }
            }
        }
    }

    private static string TryResolveWslPortName(string distroName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(distroName))
            {
                return null;
            }

            var supplier = TryCreateManagedWslPortSupplier();
            if (supplier == null)
            {
                return null;
            }

            // Enumerate ports and pick the one that best matches the distro name.
            if (ErrorHandler.Failed(supplier.EnumPorts(out var enumPorts)) || enumPorts == null)
            {
                return null;
            }

            string best = null;
            var ports = new IDebugPort2[1];
            uint fetched = 0;
            while (true)
            {
                var hr = enumPorts.Next(1, ports, ref fetched);
                if (hr != S_OK || fetched == 0 || ports[0] == null)
                {
                    break;
                }

                if (ErrorHandler.Failed(ports[0].GetPortName(out var name)) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Exact match first.
                if (string.Equals(name, distroName, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }

                // Common encodings.
                if (string.Equals(name, $"wsl+{distroName}", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(distroName, StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf(distroName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best ??= name;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private sealed class WslPortResolution
    {
        public string PortName { get; set; }
        public Guid PortId { get; set; }
        public string AllPortsSummary { get; set; }
    }

    private static WslPortResolution TryResolveWslPort(string distroName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(distroName))
            {
                return null;
            }

            var supplier = TryCreateManagedWslPortSupplier();
            if (supplier == null)
            {
                return null;
            }

            if (ErrorHandler.Failed(supplier.EnumPorts(out var enumPorts)) || enumPorts == null)
            {
                return null;
            }

            var ports = new IDebugPort2[1];
            uint fetched = 0;
            string bestName = null;
            Guid bestId = Guid.Empty;
            var all = new List<string>();

            while (true)
            {
                var hr = enumPorts.Next(1, ports, ref fetched);
                if (hr != S_OK || fetched == 0 || ports[0] == null)
                {
                    break;
                }

                var p = ports[0];
                if (ErrorHandler.Failed(p.GetPortName(out var name)) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var id = Guid.Empty;
                try
                {
                    p.GetPortId(out id);
                }
                catch
                {
                }

                all.Add($"{name}({id})");

                if (string.Equals(name, distroName, StringComparison.OrdinalIgnoreCase))
                {
                    return new WslPortResolution { PortName = name, PortId = id, AllPortsSummary = string.Join(", ", all) };
                }

                if (bestName == null &&
                    (string.Equals(name, $"wsl+{distroName}", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(distroName, StringComparison.OrdinalIgnoreCase) ||
                     name.IndexOf(distroName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    bestName = name;
                    bestId = id;
                }
            }

            if (bestName == null)
            {
                return new WslPortResolution { PortName = null, PortId = Guid.Empty, AllPortsSummary = string.Join(", ", all) };
            }

            return new WslPortResolution { PortName = bestName, PortId = bestId, AllPortsSummary = string.Join(", ", all) };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> TryEnsureSshdRunningAsync(string distroName, CancellationToken ct)
    {
        try
        {
            // Check if sshd is already running.
            using (var pgrep = ToolchainServiceExtensions.RunInWsl(
                       distroName,
                       "/usr/bin/pgrep",
                       new[] { "-x", "sshd" },
                       linuxWorkingDir: "/",
                       env: null,
                       ct: ct))
            {
                var ec = await pgrep;
                if (ec == 0)
                {
                    return "running";
                }
            }

            // Try to start it as root (no sudo prompt).
            var wslExe = WslInfo.GetWslExePath();
            if (string.IsNullOrWhiteSpace(wslExe) || !File.Exists(wslExe))
            {
                // WslInfo.GetWslExePath can return "wsl.exe" which doesn't File.Exists; treat as best-effort.
                wslExe = "wsl.exe";
            }

            // Prefer /usr/sbin/service, fallback to /etc/init.d/ssh.
            var attempts = new[]
            {
                new[] { "-d", distroName, "-u", "root", "--exec", "/usr/sbin/service", "ssh", "start" },
                new[] { "-d", distroName, "-u", "root", "--exec", "/etc/init.d/ssh", "start" },
            };

            foreach (var args in attempts)
            {
                using var proc = ProcessRunner.Run(
                    wslExe,
                    args,
                    Environment.SystemDirectory,
                    env: null,
                    visible: false,
                    redirector: null,
                    cancellationToken: ct);

                var ec = await proc;
                if (ec == 0)
                {
                    // Re-check.
                    using var pgrep2 = ToolchainServiceExtensions.RunInWsl(
                        distroName,
                        "/usr/bin/pgrep",
                        new[] { "-x", "sshd" },
                        linuxWorkingDir: "/",
                        env: null,
                        ct: ct);

                    var ec2 = await pgrep2;
                    if (ec2 == 0)
                    {
                        return "started";
                    }
                }
            }

            return "not running (failed to start)";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static IDebugPortSupplier2 TryCreateManagedWslPortSupplier()
    {
        try
        {
            var asm = _sshDebugPsAssembly ??= TryLoadSshDebugPsAssembly();
            if (asm == null)
            {
                return null;
            }

            // Create managed type directly (no COM registry required).
            var t = asm.GetType("Microsoft.SSHDebugPS.WSL.WSLPortSupplier", throwOnError: false, ignoreCase: false);
            if (t == null)
            {
                return null;
            }

            var inst = Activator.CreateInstance(t);
            return inst as IDebugPortSupplier2;
        }
        catch
        {
            return null;
        }
    }

    private static Assembly TryLoadSshDebugPsAssembly()
    {
        try
        {
            // In VS, base directory is ...\\Common7\\IDE\\
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return null;
            }

            var path = Path.Combine(baseDir, "CommonExtensions", "Microsoft", "MDD", "Debugger", "Microsoft.SSHDebugPS.dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates debug target info for Windows workspaces (original behavior).
    /// </summary>
    private async Task<VsDebugTargetInfo> CreateWindowsDebugTargetInfoAsync(
        Workspace.Package package,
        string profile,
        PathEx processName,
        string args,
        string env,
        string workingDirectory,
        __VSDBGLAUNCHFLAGS noDebugFlag,
        CancellationToken ct)
    {
        var binLibPaths = await ToolchainServiceExtensions.GetBinAndLibPathsAsync(package.Parent.WorkspaceRoot, ct);

        return new VsDebugTargetInfo
        {
            dlo = DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
            bstrExe = processName,
            bstrCurDir = workingDirectory.IsNullOrEmpty() ? Path.GetDirectoryName(processName) : workingDirectory,
            bstrArg = args,
            bstrEnv = env.OverrideProcessEnvironment()
                .PrependToPathInEnviroment(
                    package.GetDepsPath(profile),
                    package.GetTargetPath(profile),
                    binLibPaths.Lib,
                    binLibPaths.Bin).ToEnvironmentBlock(),
            bstrOptions = null,
            bstrPortName = null,
            bstrMdmRegisteredName = null,
            bstrRemoteMachine = null,
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VsDebugTargetInfo>(),
            grfLaunch = (uint)(noDebugFlag | __VSDBGLAUNCHFLAGS.DBGLAUNCH_Silent | __VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd),
            fSendStdoutToOutputWindow = 0,
            clsidCustom = DebugEnginesGuids.NativeOnly_guid,
        };
    }

    /// <summary>
    /// Creates debug target info for WSL workspaces using SSH:wsl+distro transport.
    /// </summary>
    private async Task<VsDebugTargetInfo> CreateWslDebugTargetInfoAsync(
        Workspace.Package package,
        Workspace.Target target,
        string profile,
        PathEx processName,
        string args,
        string workingDirectory,
        __VSDBGLAUNCHFLAGS noDebugFlag,
        WslInfo wslInfo,
        string distroName,
        CancellationToken ct)
    {
        // Convert Windows paths to Linux paths for WSL debugging
        var linuxExePath = ResolveLinuxPathForDebugExe(processName, wslInfo, distroName, package.Parent.WorkspaceRoot);
        var linuxWorkingDir = ResolveLinuxWorkingDirectory(workingDirectory, package.Parent.WorkspaceRoot, processName, wslInfo, distroName);

        // For WSL debugging, we use the SSH:wsl+<distro> port name
        // This tells VS to use the WSL debugging transport
        var portName = $"SSH:wsl+{distroName}";

        L.WriteLine("Creating WSL debug target: exe={0}, workDir={1}, portName={2}", linuxExePath, linuxWorkingDir, portName);

        return new VsDebugTargetInfo
        {
            dlo = DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
            bstrExe = linuxExePath,
            bstrCurDir = linuxWorkingDir,
            bstrArg = args,
            // For WSL debugging, we don't pass the Windows environment block
            // The debugger will use the WSL environment
            bstrEnv = null,
            bstrOptions = null,
            bstrPortName = portName,
            bstrMdmRegisteredName = null,
            bstrRemoteMachine = null,
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VsDebugTargetInfo>(),
            grfLaunch = (uint)(noDebugFlag | __VSDBGLAUNCHFLAGS.DBGLAUNCH_Silent | __VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd),
            fSendStdoutToOutputWindow = 0,
            // WSL/Linux debugging requires MIEngine (GDB/LLDB MI debug engine), not the Windows native-only engine.
            clsidCustom = GdbEngineGuid,
        };
    }

    private static string ResolveLinuxPathForDebugExe(PathEx exePath, WslInfo wslInfo, string distroName, PathEx workspaceRoot)
    {
        if (wslInfo != null)
        {
            return wslInfo.ToLinuxPath(exePath);
        }

        // Mirror mode typically stores target paths as UNC (\\wsl.localhost\Distro\...).
        if (WslInfo.TryParse(exePath, out var parsed) && parsed != null)
        {
            return parsed.ToLinuxPath(exePath);
        }

        // Mode 2 mirror (Windows workspace + WSL execution): map the Windows path into the mirror if possible.
        var wsRoot = TargetSystemSelection.TryGetWorkspaceRoot(out var wr) ? wr : workspaceRoot;
        if (!string.IsNullOrWhiteSpace(distroName) &&
            (string)wsRoot != null &&
            WslMirrorManager.TryGet(wsRoot, distroName, out var mirror) &&
            mirror?.Config != null &&
            WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)exePath, mirror.Config, out var mirrorLinux))
        {
            return mirrorLinux;
        }

        if (WslPathMapper.TryWindowsToWslPath(exePath, out var linuxExePath))
        {
            return linuxExePath;
        }

        // Best-effort fallback: try treating it as a Linux absolute path.
        return exePath.ToString().Replace('\\', '/');
    }

    private static string ResolveLinuxWorkingDirectory(string workingDirectorySetting, PathEx workspaceRoot, PathEx exePath, WslInfo wslInfo, string distroName)
    {
        // Default: directory of the exe
        var fallback = ResolveLinuxPathForDebugExe(exePath.GetDirectoryName(), wslInfo, distroName, workspaceRoot);

        if (workingDirectorySetting.IsNullOrEmpty())
        {
            return fallback;
        }

        // If user already provided a Linux absolute path, keep it.
        if (workingDirectorySetting.StartsWith("/"))
        {
            return workingDirectorySetting;
        }

        // If user provided a WSL UNC path, convert it.
        if (WslInfo.IsWslPath(workingDirectorySetting))
        {
            if (wslInfo != null)
            {
                return wslInfo.ToLinuxPath(workingDirectorySetting);
            }

            if (WslInfo.TryParse(workingDirectorySetting, out var parsed) && parsed != null)
            {
                return parsed.ToLinuxPath(workingDirectorySetting);
            }

            return fallback;
        }

        // If user provided a Windows-rooted path (e.g. C:\...), map it for Mode 2.
        if (Path.IsPathRooted(workingDirectorySetting))
        {
            // Prefer mirror mapping when available.
            var wsRoot = TargetSystemSelection.TryGetWorkspaceRoot(out var wr) ? wr : workspaceRoot;
            if (!string.IsNullOrWhiteSpace(distroName) &&
                (string)wsRoot != null &&
                WslMirrorManager.TryGet(wsRoot, distroName, out var mirror) &&
                mirror?.Config != null &&
                WslMirrorPathMapper.TryWindowsToMirrorLinuxPath(workingDirectorySetting, mirror.Config, out var mirrorLinux))
            {
                return mirrorLinux;
            }

            return WslPathMapper.TryWindowsToWslPath(workingDirectorySetting, out var linuxPath) ? linuxPath : fallback;
        }

        // Otherwise treat it as a relative path under the workspace root.
        // This preserves the common “target/debug” style values.
        var combined = workspaceRoot + (PathEx)workingDirectorySetting;
        if (wslInfo != null && WslInfo.IsWslPath(combined))
        {
            return wslInfo.ToLinuxPath(combined);
        }

        // Mirror mapping for relative working dirs under workspace root.
        var wsRoot2 = TargetSystemSelection.TryGetWorkspaceRoot(out var wr2) ? wr2 : workspaceRoot;
        if (!string.IsNullOrWhiteSpace(distroName) &&
            (string)wsRoot2 != null &&
            WslMirrorManager.TryGet(wsRoot2, distroName, out var mirror2) &&
            mirror2?.Config != null &&
            WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)combined, mirror2.Config, out var combinedMirrorLinux))
        {
            return combinedMirrorLinux;
        }

        if (WslPathMapper.TryWindowsToWslPath(combined, out var combinedLinux))
        {
            return combinedLinux;
        }

        return fallback;
    }

    private Task<string> GetSettingsAsync(string type, ISettingsService settingsService, LaunchConfigWrapper lcw)
    {
        var projectKey = lcw[LaunchConfigurationConstants.ProjectKey];
        return settingsService.GetAsync(type, (PathEx)projectKey);
    }

    /// <summary>
    /// Wrapper to track a number of KeyNotFoundExceptions being thrown on users' machines.
    /// </summary>
    public sealed class LaunchConfigWrapper
    {
        private readonly IPropertySettings _lc;
        private readonly TL _tl;

        public LaunchConfigWrapper(IPropertySettings lc, TL tl)
        {
            _lc = lc;
            _tl = tl;
        }

        public string this[string key]
        {
            get
            {
                if (!_lc.ContainsKey(key) || _lc[key].GetType() != typeof(string))
                {
                    var msg = $"Key '{key}' is not set in launch configuration and / or is not a string.";
                    var e = new KeyNotFoundException(msg);
                    _tl.T.TrackException(e, new[] { ("Key", key) });
                    _tl.L.WriteError(msg);
                    throw e;
                }

                return _lc[key] as string;
            }
        }

        public bool ContainsKey(string noDebugKey) => _lc.ContainsKey(noDebugKey);
    }
}

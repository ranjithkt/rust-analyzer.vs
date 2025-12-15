using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Debug;
using static Microsoft.VisualStudio.VSConstants;

// WSL debugging support: uses bstrPortName = "SSH:wsl+<distro>" for remote debugging

namespace KS.RustAnalyzer.Debugger;

// TODO: Workaround for https://github.com/kitamstudios/rust-analyzer.vs/issues/24. Just implementing LaunchDebugTargetProviderOptions.IsRuntimeSupportContext should be enough but it does not work, for now setting priority to low.
// NOTE: WSL binaries are typically extensionless, so we register for both ".exe" (Windows) and "" (no extension).
[ExportLaunchDebugTarget(LaunchDebugTargetProviderOptions.IsRuntimeSupportContext, ProviderType, new[] { ".exe", "" }, ProviderPriority.Lowest)]
public sealed class DebugLaunchTargetProvider : ILaunchDebugTargetProvider
{
    public const string ProviderType = "{72D3FCEF-1111-4266-B8DD-D3ED06E35A2B}";
    public static readonly Guid ProviderTypeGuid = new(ProviderType);

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
        var mds = workspaceContext.GetService<IMetadataService>();
        var package = workspaceContext.JTF.Run(async () => await workspaceContext.GetService<IMetadataService>()?.GetContainingPackageAsync((PathEx)targetFilePath, default));

        return package != null;
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

            if (!File.Exists(processName))
            {
                var message = string.Format("Unable to find file: '{0}'.", processName);
                L.WriteLine(message);
                T.TrackException(new FileNotFoundException(message, processName));
                await VsCommon.ShowMessageBoxAsync(message, diagMessage);
                return;
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
                info = await CreateWslDebugTargetInfoAsync(package, target, profile, processName, args, workingDirectory, noDebugFlag, wslInfo, distroName, ct);
            }
            else
            {
                info = await CreateWindowsDebugTargetInfoAsync(package, profile, processName, args, env, workingDirectory, noDebugFlag, ct);
            }

            VsShellUtilities.LaunchDebugger(serviceProvider, info);
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
        var linuxExePath = ResolveLinuxPathForDebugExe(processName, wslInfo);
        var linuxWorkingDir = ResolveLinuxWorkingDirectory(workingDirectory, package.Parent.WorkspaceRoot, processName, wslInfo);

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
            clsidCustom = DebugEnginesGuids.NativeOnly_guid,
        };
    }

    private static string ResolveLinuxPathForDebugExe(PathEx exePath, WslInfo wslInfo)
    {
        if (wslInfo != null)
        {
            return wslInfo.ToLinuxPath(exePath);
        }

        if (WslPathMapper.TryWindowsToWslPath(exePath, out var linuxExePath))
        {
            return linuxExePath;
        }

        // Best-effort fallback: try treating it as a Linux absolute path.
        return exePath.ToString().Replace('\\', '/');
    }

    private static string ResolveLinuxWorkingDirectory(string workingDirectorySetting, PathEx workspaceRoot, PathEx exePath, WslInfo wslInfo)
    {
        // Default: directory of the exe
        var fallback = ResolveLinuxPathForDebugExe(exePath.GetDirectoryName(), wslInfo);

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
            return wslInfo != null ? wslInfo.ToLinuxPath(workingDirectorySetting) : fallback;
        }

        // If user provided a Windows-rooted path (e.g. C:\...), map it for Mode 2.
        if (Path.IsPathRooted(workingDirectorySetting))
        {
            return WslPathMapper.TryWindowsToWslPath(workingDirectorySetting, out var linuxPath) ? linuxPath : fallback;
        }

        // Otherwise treat it as a relative path under the workspace root.
        // This preserves the common “target/debug” style values.
        var combined = workspaceRoot + (PathEx)workingDirectorySetting;
        if (wslInfo != null && WslInfo.IsWslPath(combined))
        {
            return wslInfo.ToLinuxPath(combined);
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

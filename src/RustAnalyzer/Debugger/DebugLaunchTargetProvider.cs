using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Debug;
using static Microsoft.VisualStudio.VSConstants;

namespace KS.RustAnalyzer.Debugger;

// TODO: Workaround for https://github.com/kitamstudios/rust-analyzer.vs/issues/24. Just implementing LaunchDebugTargetProviderOptions.IsRuntimeSupportContext should be enough but it does not work, for now setting priority to low.
[ExportLaunchDebugTarget(LaunchDebugTargetProviderOptions.IsRuntimeSupportContext, ProviderType, new[] { ".exe" }, ProviderPriority.Lowest)]
public sealed class DebugLaunchTargetProvider : ILaunchDebugTargetProvider
{
    public const string ProviderType = "{72D3FCEF-1111-4266-B8DD-D3ED06E35A2B}";
    public static readonly Guid ProviderTypeGuid = new(ProviderType);

    [Import]
    public ILogger L { get; set; }

    [Import]
    public ITelemetryService T { get; set; }

    [Import]
    public IWorkspaceContextAccessor WorkspaceContextAccessor { get; set; }

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

            var args = await GetSettingsAsync(SettingsInfo.TypeCommandLineArguments, workspaceContext.GetService<ISettingsService>(), lcw);
            var env = await GetSettingsAsync(SettingsInfo.TypeDebuggerEnvironment, workspaceContext.GetService<ISettingsService>(), lcw);
            var workingDirectory = await GetSettingsAsync(SettingsInfo.TypeDebuggerWorkingDirectory, workspaceContext.GetService<ISettingsService>(), lcw);
            var noDebugFlag = lcw.ContainsKey(LaunchConfigurationConstants.NoDebugKey) ? __VSDBGLAUNCHFLAGS.DBGLAUNCH_NoDebug : 0;

            // Check if this is a remote target (WSL/SSH)
            var targetSystem = WorkspaceContextAccessor?.GetCurrentTarget();
            var pathMapper = targetSystem?.GetPathMapper();

            L.WriteLine("LaunchDebugTarget: Target system = {0} (Kind: {1})", targetSystem?.DisplayName ?? "null", targetSystem?.Kind.ToString() ?? "unknown");

            if (targetSystem != null && targetSystem.Kind != TargetKind.Local)
            {
                await LaunchRemoteDebugTargetAsync(
                    workspaceContext, serviceProvider, target, package, profile, targetFQN,
                    args, env, workingDirectory, noDebugFlag, targetSystem, pathMapper, ct);
            }
            else
            {
                await LaunchLocalDebugTargetAsync(
                    workspaceContext, serviceProvider, target, package, profile, targetFQN,
                    args, env, workingDirectory, noDebugFlag, ct);
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

    private async Task LaunchLocalDebugTargetAsync(
        IWorkspace workspaceContext,
        IServiceProvider serviceProvider,
        Workspace.Target target,
        Workspace.Package package,
        string profile,
        string targetFQN,
        string args,
        string env,
        string workingDirectory,
        __VSDBGLAUNCHFLAGS noDebugFlag,
        CancellationToken ct)
    {
        const string diagMessage = "Delete the .vs folder and try again. If that does not work please file a bug with the repro steps.";

        var processName = target.GetPath(profile);
        if (!File.Exists(processName))
        {
            var message = string.Format("Unable to find file: '{0}'.", processName);
            L.WriteLine(message);
            T.TrackException(new FileNotFoundException(message, processName));
            await VsCommon.ShowMessageBoxAsync(message, diagMessage);
            return;
        }

        L.WriteLine("LaunchDebugTarget (local) with profile: {0}", profile);
        T.TrackEvent("Debug", ("Target", targetFQN), ("Profile", profile), ("Manifest", package.FullPath), ("Args", args), ("Env", env.ReplaceNullWithBar()));

        var binLibPaths = await ToolchainServiceExtensions.GetBinAndLibPathsAsync(package.Parent.WorkspaceRoot, ct);
        var info = new VsDebugTargetInfo
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

        VsShellUtilities.LaunchDebugger(serviceProvider, info);
    }

    private async Task LaunchRemoteDebugTargetAsync(
        IWorkspace workspaceContext,
        IServiceProvider serviceProvider,
        Workspace.Target target,
        Workspace.Package package,
        string profile,
        string targetFQN,
        string args,
        string env,
        string workingDirectory,
        __VSDBGLAUNCHFLAGS noDebugFlag,
        ITargetSystem targetSystem,
        IPathMapper pathMapper,
        CancellationToken ct)
    {
        L.WriteLine("LaunchDebugTarget (remote: {0}) with profile: {1}", targetSystem.Kind, profile);
        T.TrackEvent("DebugRemote", ("Target", targetFQN), ("Profile", profile), ("TargetKind", targetSystem.Kind.ToString()));

        // For WSL debugging, we need to use a different approach:
        // Option 1: Launch gdbserver in WSL and connect MIEngine to it
        // Option 2: Use VS's native WSL debugging support (VS 2022+)

        // For now, we'll show a message that remote debugging is in preview
        // and provide the command to manually start gdbserver

        var processName = target.GetPath(profile);
        var remoteProcessPath = pathMapper.MapToRemote((PathEx)processName);

        // Get the remote working directory
        var remoteWorkingDir = workingDirectory.IsNullOrEmpty()
            ? pathMapper.MapToRemote(((PathEx)processName).GetDirectoryName())
            : pathMapper.MapToRemote((PathEx)workingDirectory);

        if (targetSystem.Kind == TargetKind.Wsl)
        {
            await LaunchWslDebugTargetAsync(
                serviceProvider, targetSystem, remoteProcessPath, remoteWorkingDir, args, env, noDebugFlag, ct);
        }
        else if (targetSystem.Kind == TargetKind.Ssh)
        {
            L.WriteLine("SSH debugging requested for: {0}", remoteProcessPath);

            // SSH debugging - show message for now
            await VsCommon.ShowMessageBoxAsync(
                $"SSH debugging is not yet fully implemented.\n\n" +
                $"Remote executable: {remoteProcessPath}\n" +
                $"Arguments: {args}\n\n" +
                $"To debug manually, you can:\n" +
                $"1. SSH to the remote host\n" +
                $"2. Run: gdbserver :1234 {remoteProcessPath} {args}\n" +
                $"3. In VS, use Debug > Attach to Process > Connection type: SSH",
                "SSH Debugging (Preview)");
        }
        else
        {
            L.WriteError("Unknown target kind for remote debugging: {0}", targetSystem.Kind);
        }
    }

    private async Task LaunchWslDebugTargetAsync(
        IServiceProvider serviceProvider,
        ITargetSystem targetSystem,
        RemotePath remoteProcessPath,
        RemotePath remoteWorkingDir,
        string args,
        string env,
        __VSDBGLAUNCHFLAGS noDebugFlag,
        CancellationToken ct)
    {
        // WSL debugging using Visual Studio's native support (VS 2022 17.0+)
        // This uses the "WSL" debug transport

        L.WriteLine("Starting WSL debug session for: {0}", remoteProcessPath);

        // Build the WSL debug target info
        // Note: This requires the "Linux development with C++" workload in VS

        var wslDistro = targetSystem.Id.Replace("wsl:", "");

        // For WSL2, use the native WSL debugging support in VS
        // We launch the process via wsl.exe and attach the native debugger
        var wslExePath = @"C:\Windows\System32\wsl.exe";
        var wslArgs = $"-d {wslDistro} --cd \"{remoteWorkingDir}\" -- \"{remoteProcessPath}\" {args}";

        var info = new VsDebugTargetInfo
        {
            dlo = DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
            bstrExe = wslExePath,
            bstrCurDir = remoteWorkingDir.ToString().Replace("/", @"\"),
            bstrArg = wslArgs,
            bstrEnv = string.IsNullOrEmpty(env) ? null : env,
            bstrOptions = null,
            bstrPortName = null,
            bstrMdmRegisteredName = null,
            bstrRemoteMachine = null,
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VsDebugTargetInfo>(),
            grfLaunch = (uint)(noDebugFlag | __VSDBGLAUNCHFLAGS.DBGLAUNCH_Silent | __VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd),
            fSendStdoutToOutputWindow = 0,
            clsidCustom = DebugEnginesGuids.NativeOnly_guid,
        };

        L.WriteLine("WSL debug command: {0} {1}", wslExePath, wslArgs);

        try
        {
            VsShellUtilities.LaunchDebugger(serviceProvider, info);
        }
        catch (Exception ex)
        {
            L.WriteError("Failed to launch WSL debugger: {0}", ex.Message);

            // Show a helpful message about alternative debugging options
            await VsCommon.ShowMessageBoxAsync(
                $"WSL debugging failed.\n\n" +
                $"Alternative: You can debug manually using gdbserver:\n\n" +
                $"1. In WSL terminal, run:\n" +
                $"   gdbserver :1234 {remoteProcessPath} {args}\n\n" +
                $"2. In VS, attach to 'localhost:1234' using MIEngine.\n\n" +
                $"Error: {ex.Message}",
                "WSL Debugging Failed");
        }
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

using System;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using KS.RustAnalyzer.Infrastructure;
using KS.RustAnalyzer.Remote;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Cargo;
using KS.RustAnalyzer.TestAdapter.Common;
using Microsoft.VisualStudio.Shell;

namespace KS.RustAnalyzer.Shell;

using ToolchainOperation = System.Func<KS.RustAnalyzer.TestAdapter.Common.IToolchainService, System.Func<KS.RustAnalyzer.TestAdapter.Common.BuildTargetInfo, KS.RustAnalyzer.TestAdapter.Common.BuildOutputSinks, System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>>;

public abstract class BaseToolchainCommand<T> : BaseCommand<T>
    where T : class, new()
{
    protected BaseToolchainCommand()
    {
        CmdServices = new CmdServices(() => Package);
    }

    public CmdServices CmdServices { get; }

    protected abstract ToolchainOperation Operation { get; }

    protected abstract string GetOptions(Options opts);

    protected override void BeforeQueryStatus(EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var selectedItems = CmdServices.GetSelectedItems();
        if (selectedItems.Count() != 1)
        {
            Command.Visible = Command.Enabled = false;
            return;
        }

        var path = selectedItems.First();

        // For WSL paths, File.Exists might not work properly, so just check if it's a manifest
        var isWslPath = WslPathMapper.TryGetDistroName(path, out _);
        Command.Visible = Command.Enabled = path.IsManifest() && (isWslPath || path.FileExists());
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await RustAnalyzerPackage.JTF.SwitchToMainThreadAsync();

        var selectedPath = CmdServices.GetSelectedItems().FirstOrDefault();
        await CmdServices.ExecuteToolchainOperationAsync(Operation, selectedPath, GetOptions);
    }
}

[Command(PackageGuids.guidRustAnalyzerPackageString, PackageIds.IdCargoClippy)]
public class CargoClippyCommand : BaseToolchainCommand<CargoClippyCommand>
{
    protected override ToolchainOperation Operation => its => its.RunClippyAsync;

    protected override string GetOptions(Options opts) => opts.DefaultCargoClippyArgs;
}

[Command(PackageGuids.guidRustAnalyzerPackageString, PackageIds.IdCargoFmt)]
public class CargoFmtCommand : BaseToolchainCommand<CargoFmtCommand>
{
    protected override ToolchainOperation Operation => its => its.RunFmtAsync;

    protected override string GetOptions(Options opts) => opts.DefaultCargoFmtArgs;
}

public abstract class BaseBuildToolChainCommand<T> : BaseCommand<T>
    where T : class, new()
{
    protected BaseBuildToolChainCommand()
    {
        CmdServices = new CmdServices(() => Package);
    }

    public CmdServices CmdServices { get; }

    protected abstract ToolchainOperation Operation { get; }

    protected abstract string GetOptions(Options opts);

    protected override void BeforeQueryStatus(EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        Command.Visible = Command.Enabled = Command.Supported = IsCommandActive();
    }

    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
        await RustAnalyzerPackage.JTF.SwitchToMainThreadAsync();

        var selectedPath = GetManifestPath();
        if (!selectedPath.HasValue)
        {
            return;
        }

        await CmdServices.ExecuteToolchainOperationAsync(Operation, selectedPath.Value, GetOptions);
    }

    protected PathEx? GetManifestPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var workspaceRoot = CmdServices.GetWorkspaceRoot();
        return workspaceRoot.HasValue ? workspaceRoot.Value + Constants.ManifestFileName2 : null;
    }

    protected string GetToolArgsFromSettings(string argName)
        => RustAnalyzerPackage.JTF.Run(
            async () =>
            {
                await RustAnalyzerPackage.JTF.SwitchToMainThreadAsync();

                var manifestPath = GetManifestPath();
                if (!manifestPath.HasValue)
                {
                    return string.Empty;
                }

                return await CmdServices.SettingsService.GetAsync(argName, manifestPath.Value);
            });

    private bool IsCommandActive()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var workspaceRoot = CmdServices.GetWorkspaceRoot();
        if (workspaceRoot == null || !CmdServices.IsIdeInDesignMode())
        {
            return false;
        }

        // Check if Cargo.toml exists at the workspace root
        var manifestPath = workspaceRoot.Value + Constants.ManifestFileName2;
        if (manifestPath.FileExists())
        {
            return true;
        }

        // For WSL workspaces, enable commands even if the file check fails
        // (UNC paths might have issues with File.Exists)
        if (WslPathMapper.TryGetDistroName(workspaceRoot.Value, out _))
        {
            return true;
        }

        return false;
    }
}

[Command(PackageGuids.guidRustAnalyzerPackageString, PackageIds.IdBuildAll)]
public class BuildAllCommand : BaseBuildToolChainCommand<BuildAllCommand>
{
    protected override ToolchainOperation Operation => its => its.BuildAsync;

    protected override string GetOptions(Options opts) => GetToolArgsFromSettings(SettingsInfo.TypeAdditionalBuildArguments);
}

[Command(PackageGuids.guidRustAnalyzerPackageString, PackageIds.IdCleanAll)]
public class CleanAllCommand : BaseBuildToolChainCommand<CleanAllCommand>
{
    protected override ToolchainOperation Operation => its => its.CleanAsync;

    protected override string GetOptions(Options opts) => string.Empty;
}

[Command(PackageGuids.guidRustAnalyzerPackageString, PackageIds.IdClippyAll)]
public class ClippyAll : BaseBuildToolChainCommand<ClippyAll>
{
    protected override ToolchainOperation Operation => its => its.RunClippyAsync;

    protected override string GetOptions(Options opts) => opts.DefaultCargoClippyArgs;
}

[Command(PackageGuids.guidRustAnalyzerPackageString, PackageIds.IdFmtAll)]
public class FmtAllCommand : BaseBuildToolChainCommand<FmtAllCommand>
{
    protected override ToolchainOperation Operation => its => its.RunFmtAsync;

    protected override string GetOptions(Options opts) => opts.DefaultCargoFmtArgs;
}

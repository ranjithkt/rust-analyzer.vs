using System;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using KS.RustAnalyzer.Infrastructure;
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

        try
        {
            // VS 2026 Open Folder selection can be transient; don't rely solely on selection.
            // Prefer selected Cargo.toml, otherwise fall back to workspace root Cargo.toml.
            var selectedItems = CmdServices.GetSelectedItems();
            PathEx manifest;
            if (selectedItems.Count() == 1 && selectedItems.First().IsManifest() && selectedItems.First().FileExists())
            {
                manifest = selectedItems.First();
            }
            else
            {
                manifest = CmdServices.GetWorkspaceRoot() + Constants.ManifestFileName2;
            }

            var canRun = manifest.IsManifest() && manifest.FileExists();
            Command.Visible = Command.Enabled = Command.Supported = canRun;
        }
        catch
        {
            // In some VS/Open Folder contexts selection APIs can be transient.
            // Never throw from status queries.
            Command.Visible = Command.Enabled = Command.Supported = false;
        }
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
        await CmdServices.ExecuteToolchainOperationAsync(Operation, selectedPath, GetOptions);
    }

    protected PathEx GetManifestPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return CmdServices.GetWorkspaceRoot() + Constants.ManifestFileName2;
    }

    protected string GetToolArgsFromSettings(string argName)
        => RustAnalyzerPackage.JTF.Run(
            async () =>
            {
                await RustAnalyzerPackage.JTF.SwitchToMainThreadAsync();

                return await CmdServices.SettingsService.GetAsync(argName, GetManifestPath());
            });

    private bool IsCommandActive()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var workspaceRoot = CmdServices.GetWorkspaceRoot();
        return (workspaceRoot + Constants.ManifestFileName2).FileExists();
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

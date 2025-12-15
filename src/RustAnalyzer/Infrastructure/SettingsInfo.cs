using System;
using System.Collections.Generic;
using KS.RustAnalyzer.NodeEnhancements;

namespace KS.RustAnalyzer.Infrastructure;

public class SettingsInfo
{
    public const string KindGeneral = "General";
    public const string KindDebugger = "Debugger";
    public const string KindBuild = "Build";
    public const string KindTest = "Test";

    // Workspace-scoped settings (stored in VS Open Folder settings store).
    // These are not shown in the Node Browse Object UI (toolbar combo is the UX).
    public const string TypeTargetSystem = "TargetSystem";
    public const string TypeWslDistroName = "WslDistroName";
    public const string TypeCommandLineArguments = nameof(NodeBrowseObject.CommandLineArguments);
    public const string TypeDebuggerEnvironment = nameof(NodeBrowseObject.DebuggerEnvironment);
    public const string TypeDebuggerWorkingDirectory = nameof(NodeBrowseObject.WorkingDirectory);
    public const string TypeAdditionalBuildArguments = nameof(NodeBrowseObject.AdditionalBuildArguments);
    public const string TypeAdditionalTestDiscoveryArguments = nameof(NodeBrowseObject.AdditionalTestDiscoveryArguments);
    public const string TypeAdditionalTestExecutionArguments = nameof(NodeBrowseObject.AdditionalTestExecutionArguments);
    public const string TypeTestExecutionEnvironment = nameof(NodeBrowseObject.TestExecutionEnvironment);

    public static readonly IReadOnlyDictionary<string, SettingsInfo> Store =
        new Dictionary<string, SettingsInfo>
        {
            [TypeTargetSystem] =
                new SettingsInfo
                {
                    Kind = KindGeneral,
                    Getter = x => x,
                    ShouldDisplay = (_, _, _) => false,
                },
            [TypeWslDistroName] =
                new SettingsInfo
                {
                    Kind = KindGeneral,
                    Getter = x => x,
                    ShouldDisplay = (_, _, _) => false,
                },
            [TypeCommandLineArguments] =
                new SettingsInfo
                {
                    Kind = KindDebugger,
                    Getter = x => x,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isExe,
                },
            [TypeDebuggerEnvironment] =
                new SettingsInfo
                {
                    Kind = KindDebugger,
                    Getter = StringExtensions.GetEnvironmentBlock,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isExe,
                },
            [TypeDebuggerWorkingDirectory] =
                new SettingsInfo
                {
                    Kind = KindDebugger,
                    Getter = x => x,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isExe,
                },
            [TypeAdditionalBuildArguments] =
                new SettingsInfo
                {
                    Kind = KindBuild,
                    Getter = x => x,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isExe,
                },
            [TypeAdditionalTestDiscoveryArguments] =
                new SettingsInfo
                {
                    Kind = KindTest,
                    Getter = StringExtensions.ToNullSeparatedArray,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isManifest && hasTargets,
                },
            [TypeAdditionalTestExecutionArguments] =
                new SettingsInfo
                {
                    Kind = KindTest,
                    Getter = StringExtensions.ToNullSeparatedArray,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isManifest && hasTargets,
                },
            [TypeTestExecutionEnvironment] =
                new SettingsInfo
                {
                    Kind = KindTest,
                    Getter = StringExtensions.GetEnvironmentBlock,
                    ShouldDisplay = (hasTargets, isExe, isManifest) => isManifest && hasTargets,
                },
        };

    public string Kind { get; set; }

    public Func<string, string> Getter { get; set; }

    public Func<bool, bool, bool, bool> ShouldDisplay { get; set; }
}

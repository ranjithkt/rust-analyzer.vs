using System.Collections.Generic;
using Newtonsoft.Json;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

/// <summary>
/// Raw DTOs for cargo metadata JSON deserialization.
/// These preserve paths as strings to avoid PathEx corruption of Linux paths.
/// Use <see cref="WorkspaceFactory"/> to convert to <see cref="Workspace"/> with proper path mapping.
/// </summary>
internal sealed class RawWorkspace
{
    [JsonProperty("version")]
    public int Version { get; set; }

    [JsonProperty("workspace_root")]
    public string WorkspaceRoot { get; set; }

    [JsonProperty("target_directory")]
    public string TargetDirectory { get; set; }

    [JsonProperty("packages")]
    public List<RawPackage> Packages { get; set; }
}

/// <summary>
/// Raw package data from cargo metadata.
/// </summary>
internal sealed class RawPackage
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("manifest_path")]
    public string ManifestPath { get; set; }

    [JsonProperty("targets")]
    public List<RawTarget> Targets { get; set; }
}

/// <summary>
/// Raw target data from cargo metadata.
/// </summary>
internal sealed class RawTarget
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("src_path")]
    public string SourcePath { get; set; }

    [JsonProperty("kind")]
    public Workspace.Kind[] Kinds { get; set; }

    [JsonProperty("crate_types")]
    public Workspace.CrateType[] CrateTypes { get; set; }
}

using System;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Service for translating paths between VS-visible (Windows/UNC) paths and remote (Linux) paths.
/// </summary>
public interface IPathMapper
{
    /// <summary>
    /// Gets the target kind this mapper handles.
    /// </summary>
    TargetKind Kind { get; }

    /// <summary>
    /// Convert a VS-visible path (Windows/UNC) to a remote path.
    /// Example: \\wsl$\Ubuntu\home\user\proj → /home/user/proj
    /// </summary>
    /// <param name="vsPath">The VS-visible path.</param>
    /// <returns>The corresponding remote path.</returns>
    RemotePath MapToRemote(PathEx vsPath);

    /// <summary>
    /// Convert a remote path to a VS-visible path (Windows/UNC).
    /// Example: /home/user/proj → \\wsl$\Ubuntu\home\user\proj
    /// </summary>
    /// <param name="remotePath">The remote path.</param>
    /// <returns>The corresponding VS-visible path.</returns>
    PathEx MapToLocal(RemotePath remotePath);

    /// <summary>
    /// Convert a VS file URI to a remote URI for LSP.
    /// Example: file:///\\wsl$\Ubuntu\home\user\proj\src\main.rs → file:///home/user/proj/src/main.rs
    /// </summary>
    /// <param name="vsUri">The VS file URI.</param>
    /// <returns>The corresponding remote URI.</returns>
    Uri MapUriToRemote(Uri vsUri);

    /// <summary>
    /// Convert a remote URI to a VS-visible URI for LSP.
    /// Example: file:///home/user/proj/src/main.rs → file:///\\wsl$\Ubuntu\home\user\proj\src\main.rs
    /// </summary>
    /// <param name="remoteUri">The remote file URI.</param>
    /// <returns>The corresponding VS-visible URI.</returns>
    Uri MapUriToLocal(Uri remoteUri);

    /// <summary>
    /// Check if a path is for this target system.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if the path belongs to this target system.</returns>
    bool IsPathForTarget(string path);
}


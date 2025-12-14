namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Defines the two workspace modes supported for SSH remote development.
/// </summary>
public enum SshWorkspaceMode
{
    /// <summary>
    /// Local Sync Mode (C++ Style): Source code is on Windows, synced to remote for builds.
    /// The user edits files locally, and the extension syncs changes to the remote machine
    /// before building. Build errors are mapped back to local paths.
    /// </summary>
    LocalSync,

    /// <summary>
    /// Remote Cache Mode: Source code is on the remote machine, cached locally for editing.
    /// The user browses to a project via Remote File Explorer, and the extension downloads
    /// files to a local cache. Edits are uploaded back to remote.
    /// </summary>
    RemoteCache,
}

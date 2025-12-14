using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// Service for synchronizing files between local Windows filesystem and remote SSH host.
/// Used in Local Sync mode (Mode 1) to upload source files before remote builds.
/// </summary>
public interface ISshFileSyncService
{
    /// <summary>
    /// Synchronizes all files from local workspace to remote destination.
    /// </summary>
    /// <param name="localRoot">Local workspace root path.</param>
    /// <param name="remoteRoot">Remote destination root path.</param>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sync result with statistics.</returns>
    Task<SyncResult> SyncToRemoteAsync(
        PathEx localRoot,
        RemotePath remoteRoot,
        SshConnectionInfo connectionInfo,
        IProgress<SyncProgress> progress,
        CancellationToken ct);

    /// <summary>
    /// Synchronizes specific files from local to remote.
    /// </summary>
    /// <param name="localFiles">Local file paths to sync.</param>
    /// <param name="localRoot">Local workspace root path.</param>
    /// <param name="remoteRoot">Remote destination root path.</param>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sync result with statistics.</returns>
    Task<SyncResult> SyncFilesToRemoteAsync(
        IEnumerable<PathEx> localFiles,
        PathEx localRoot,
        RemotePath remoteRoot,
        SshConnectionInfo connectionInfo,
        CancellationToken ct);

    /// <summary>
    /// Downloads files from remote to local cache.
    /// Used for Remote Cache mode (Mode 2).
    /// </summary>
    /// <param name="remoteRoot">Remote source root path.</param>
    /// <param name="localCacheRoot">Local cache destination path.</param>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sync result with statistics.</returns>
    Task<SyncResult> SyncFromRemoteAsync(
        RemotePath remoteRoot,
        PathEx localCacheRoot,
        SshConnectionInfo connectionInfo,
        IProgress<SyncProgress> progress,
        CancellationToken ct);

    /// <summary>
    /// Synchronizes a project and all its path dependencies to the remote.
    /// Parses Cargo.toml to find path dependencies and syncs them recursively.
    /// </summary>
    /// <param name="localRoot">Local workspace root path (containing Cargo.toml).</param>
    /// <param name="remoteRoot">Remote destination root path for the main project.</param>
    /// <param name="connectionInfo">SSH connection information.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sync result with statistics including dependencies.</returns>
    Task<SyncResult> SyncProjectWithDependenciesAsync(
        PathEx localRoot,
        RemotePath remoteRoot,
        SshConnectionInfo connectionInfo,
        IProgress<SyncProgress> progress,
        CancellationToken ct);
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public sealed class SyncResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the sync was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if sync failed.
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the number of files synced.
    /// </summary>
    public int FilesSynced { get; set; }

    /// <summary>
    /// Gets or sets the number of files skipped (already up to date).
    /// </summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the total bytes transferred.
    /// </summary>
    public long BytesTransferred { get; set; }

    /// <summary>
    /// Gets or sets the time taken for sync.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Creates a successful sync result.
    /// </summary>
    public static SyncResult Succeeded(int filesSynced, int filesSkipped, long bytesTransferred, TimeSpan duration)
    {
        return new SyncResult
        {
            Success = true,
            FilesSynced = filesSynced,
            FilesSkipped = filesSkipped,
            BytesTransferred = bytesTransferred,
            Duration = duration,
        };
    }

    /// <summary>
    /// Creates a failed sync result.
    /// </summary>
    public static SyncResult Failed(string errorMessage)
    {
        return new SyncResult
        {
            Success = false,
            ErrorMessage = errorMessage,
        };
    }
}

/// <summary>
/// Progress information for sync operations.
/// </summary>
public sealed class SyncProgress
{
    /// <summary>
    /// Gets or sets the current file being synced.
    /// </summary>
    public string CurrentFile { get; set; }

    /// <summary>
    /// Gets or sets the number of files processed so far.
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total number of files to process.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Gets or sets the bytes transferred so far.
    /// </summary>
    public long BytesTransferred { get; set; }

    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    public int PercentComplete => TotalFiles > 0 ? (int)(FilesProcessed * 100.0 / TotalFiles) : 0;
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Remote;

/// <summary>
/// SFTP-based implementation of file synchronization for SSH targets.
/// Uses OpenSSH sftp/scp commands for file transfers.
/// </summary>
public sealed class SshFileSyncService : ISshFileSyncService
{
    // Files/directories to exclude from sync
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "target",
        ".git",
        ".vs",
        ".vscode",
        "node_modules",
        ".cursor",
        "*.rusttests",
    };

    // File extensions to include (Rust project files)
    private static readonly HashSet<string> IncludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rs",
        ".toml",
        ".lock",
        ".md",
        ".txt",
        ".json",
        ".yaml",
        ".yml",
        ".sh",
    };

    /// <inheritdoc/>
    public async Task<SyncResult> SyncToRemoteAsync(
        PathEx localRoot,
        RemotePath remoteRoot,
        SshConnectionInfo connectionInfo,
        IProgress<SyncProgress> progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // First, ensure the remote directory exists
            await EnsureRemoteDirectoryAsync(remoteRoot, connectionInfo, ct).ConfigureAwait(false);

            // Get list of files to sync
            var filesToSync = GetFilesToSync(localRoot).ToList();

            if (filesToSync.Count == 0)
            {
                return SyncResult.Succeeded(0, 0, 0, stopwatch.Elapsed);
            }

            int filesSynced = 0;
            int filesSkipped = 0;
            long bytesTransferred = 0;

            // Sync files in batches using scp
            foreach (var localFile in filesToSync)
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = GetRelativePath(localRoot, localFile);
                var remoteFile = new RemotePath($"{remoteRoot}/{relativePath.Replace(@"\", "/")}", TargetKind.Ssh);

                progress?.Report(new SyncProgress
                {
                    CurrentFile = relativePath,
                    FilesProcessed = filesSynced + filesSkipped,
                    TotalFiles = filesToSync.Count,
                    BytesTransferred = bytesTransferred,
                });

                // Ensure remote directory exists
                var remoteDir = GetRemoteDirectory(remoteFile);
                await EnsureRemoteDirectoryAsync(remoteDir, connectionInfo, ct).ConfigureAwait(false);

                // Copy file using scp
                var fileInfo = new FileInfo((string)localFile);
                var success = await CopyFileToRemoteAsync(localFile, remoteFile, connectionInfo, ct).ConfigureAwait(false);

                if (success)
                {
                    filesSynced++;
                    bytesTransferred += fileInfo.Length;
                }
                else
                {
                    filesSkipped++;
                }
            }

            stopwatch.Stop();
            return SyncResult.Succeeded(filesSynced, filesSkipped, bytesTransferred, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return SyncResult.Failed("Sync was cancelled.");
        }
        catch (Exception ex)
        {
            return SyncResult.Failed($"Sync failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<SyncResult> SyncFilesToRemoteAsync(
        IEnumerable<PathEx> localFiles,
        PathEx localRoot,
        RemotePath remoteRoot,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var files = localFiles.ToList();
            int filesSynced = 0;
            long bytesTransferred = 0;

            foreach (var localFile in files)
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = GetRelativePath(localRoot, localFile);
                var remoteFile = new RemotePath($"{remoteRoot}/{relativePath.Replace(@"\", "/")}", TargetKind.Ssh);

                // Ensure remote directory exists
                var remoteDir = GetRemoteDirectory(remoteFile);
                await EnsureRemoteDirectoryAsync(remoteDir, connectionInfo, ct).ConfigureAwait(false);

                var fileInfo = new FileInfo((string)localFile);
                var success = await CopyFileToRemoteAsync(localFile, remoteFile, connectionInfo, ct).ConfigureAwait(false);

                if (success)
                {
                    filesSynced++;
                    bytesTransferred += fileInfo.Length;
                }
            }

            stopwatch.Stop();
            return SyncResult.Succeeded(filesSynced, 0, bytesTransferred, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return SyncResult.Failed($"Sync failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<SyncResult> SyncFromRemoteAsync(
        RemotePath remoteRoot,
        PathEx localCacheRoot,
        SshConnectionInfo connectionInfo,
        IProgress<SyncProgress> progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Ensure local cache directory exists
            Directory.CreateDirectory((string)localCacheRoot);

            // Use recursive scp to download entire directory
            var success = await CopyDirectoryFromRemoteAsync(remoteRoot, localCacheRoot, connectionInfo, ct).ConfigureAwait(false);

            stopwatch.Stop();

            if (success)
            {
                // Count files after download
                var fileCount = Directory.GetFiles((string)localCacheRoot, "*", SearchOption.AllDirectories).Length;
                return SyncResult.Succeeded(fileCount, 0, 0, stopwatch.Elapsed);
            }
            else
            {
                return SyncResult.Failed("Failed to download remote directory.");
            }
        }
        catch (Exception ex)
        {
            return SyncResult.Failed($"Sync failed: {ex.Message}");
        }
    }

    private static IEnumerable<PathEx> GetFilesToSync(PathEx localRoot)
    {
        var rootDir = (string)localRoot;

        if (!Directory.Exists(rootDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = file.Substring(rootDir.Length).TrimStart('\\', '/');
            var pathParts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            // Check if any path component should be excluded
            var shouldExclude = pathParts.Any(part => ExcludedNames.Contains(part));
            if (shouldExclude)
            {
                continue;
            }

            // Check if file extension is in included list, or if it's a known file
            var extension = Path.GetExtension(file);
            var fileName = Path.GetFileName(file);

            if (IncludedExtensions.Contains(extension) ||
                fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(".cargo", StringComparison.OrdinalIgnoreCase))
            {
                yield return (PathEx)file;
            }
        }
    }

    private static string GetRelativePath(PathEx root, PathEx fullPath)
    {
        var rootStr = (string)root;
        var fullStr = (string)fullPath;

        if (fullStr.StartsWith(rootStr, StringComparison.OrdinalIgnoreCase))
        {
            return fullStr.Substring(rootStr.Length).TrimStart('\\', '/');
        }

        return fullStr;
    }

    private static RemotePath GetRemoteDirectory(RemotePath filePath)
    {
        var path = (string)filePath;
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash > 0)
        {
            return new RemotePath(path.Substring(0, lastSlash), TargetKind.Ssh);
        }

        return filePath;
    }

    private static async Task EnsureRemoteDirectoryAsync(
        RemotePath remoteDir,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        var sshArgs = new List<string>();

        // Add identity file if specified
        if (!string.IsNullOrEmpty(connectionInfo.IdentityFile))
        {
            sshArgs.Add("-i");
            sshArgs.Add(connectionInfo.IdentityFile);
        }

        // Add port if not default
        if (connectionInfo.Port != 22)
        {
            sshArgs.Add("-p");
            sshArgs.Add(connectionInfo.Port.ToString());
        }

        // Disable strict host key checking for automation (user should have verified host before)
        sshArgs.Add("-o");
        sshArgs.Add("StrictHostKeyChecking=accept-new");

        // Add batch mode
        sshArgs.Add("-o");
        sshArgs.Add("BatchMode=yes");

        sshArgs.Add($"{connectionInfo.Username}@{connectionInfo.Host}");

        // Use eval to expand $HOME in the path, and don't quote the entire path if it contains $HOME
        var remoteDirStr = (string)remoteDir;
        if (remoteDirStr.Contains("$HOME"))
        {
            sshArgs.Add($"mkdir -p {remoteDirStr}");
        }
        else
        {
            sshArgs.Add($"mkdir -p \"{remoteDirStr}\"");
        }

        using var proc = ProcessRunner.Run("ssh", sshArgs.ToArray(), workingDirectory: null, env: null, ct);
        await proc;
    }

    private static async Task<bool> CopyFileToRemoteAsync(
        PathEx localFile,
        RemotePath remoteFile,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        var scpArgs = new List<string>();

        // Add identity file if specified
        if (!string.IsNullOrEmpty(connectionInfo.IdentityFile))
        {
            scpArgs.Add("-i");
            scpArgs.Add(connectionInfo.IdentityFile);
        }

        // Add port if not default
        if (connectionInfo.Port != 22)
        {
            scpArgs.Add("-P");
            scpArgs.Add(connectionInfo.Port.ToString());
        }

        // Disable strict host key checking
        scpArgs.Add("-o");
        scpArgs.Add("StrictHostKeyChecking=accept-new");

        // Add batch mode
        scpArgs.Add("-o");
        scpArgs.Add("BatchMode=yes");

        // Quiet mode
        scpArgs.Add("-q");

        // Source file
        scpArgs.Add((string)localFile);

        // Destination - convert $HOME back to ~ for scp (which expands ~ on the remote side)
        var remoteFileStr = ConvertHomeForScp((string)remoteFile);
        scpArgs.Add($"{connectionInfo.Username}@{connectionInfo.Host}:{remoteFileStr}");

        using var proc = ProcessRunner.Run("scp", scpArgs.ToArray(), workingDirectory: null, env: null, ct);
        var exitCode = await proc;
        return exitCode == 0;
    }

    /// <summary>
    /// Converts $HOME to ~ for use with scp, which expands ~ on the remote side.
    /// </summary>
    private static string ConvertHomeForScp(string path)
    {
        if (path.StartsWith("$HOME/", StringComparison.Ordinal))
        {
            return "~" + path.Substring(5);
        }

        if (path.StartsWith("$HOME", StringComparison.Ordinal) && path.Length == 5)
        {
            return "~";
        }

        return path;
    }

    private static async Task<bool> CopyDirectoryFromRemoteAsync(
        RemotePath remoteDir,
        PathEx localDir,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        var scpArgs = new List<string>();

        // Add identity file if specified
        if (!string.IsNullOrEmpty(connectionInfo.IdentityFile))
        {
            scpArgs.Add("-i");
            scpArgs.Add(connectionInfo.IdentityFile);
        }

        // Add port if not default
        if (connectionInfo.Port != 22)
        {
            scpArgs.Add("-P");
            scpArgs.Add(connectionInfo.Port.ToString());
        }

        // Disable strict host key checking
        scpArgs.Add("-o");
        scpArgs.Add("StrictHostKeyChecking=accept-new");

        // Recursive copy
        scpArgs.Add("-r");

        // Quiet mode
        scpArgs.Add("-q");

        // Source (remote)
        scpArgs.Add($"{connectionInfo.Username}@{connectionInfo.Host}:{remoteDir}/*");

        // Destination (local)
        scpArgs.Add((string)localDir);

        using var proc = ProcessRunner.Run("scp", scpArgs.ToArray(), workingDirectory: null, env: null, ct);
        var exitCode = await proc;
        return exitCode == 0;
    }
}

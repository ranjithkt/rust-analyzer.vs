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
/// File synchronization service for SSH targets.
/// Uses the best available method: rsync > tar+ssh > scp
/// </summary>
public sealed class SshFileSyncService : ISshFileSyncService
{
    // Cached tool availability (checked once per session)
    private static bool? _rsyncAvailable;
    private static bool? _tarAvailable;
    private static readonly object _toolCheckLock = new();

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

    /// <summary>
    /// Gets which sync method is available (for diagnostics).
    /// </summary>
    public static string GetAvailableSyncMethod()
    {
        EnsureToolsChecked();
        if (_rsyncAvailable == true) return "rsync (incremental, fast)";
        if (_tarAvailable == true) return "tar+ssh (compressed, single connection)";
        return "scp (basic recursive copy)";
    }

    private static void EnsureToolsChecked()
    {
        lock (_toolCheckLock)
        {
            if (_rsyncAvailable == null)
            {
                _rsyncAvailable = IsToolAvailable("rsync", "--version");
                Debug.WriteLine($"[SshFileSyncService] rsync available: {_rsyncAvailable}");
            }

            if (_tarAvailable == null)
            {
                _tarAvailable = IsToolAvailable("tar", "--version");
                Debug.WriteLine($"[SshFileSyncService] tar available: {_tarAvailable}");
            }
        }
    }

    private static bool IsToolAvailable(string tool, string testArg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = testArg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

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

            // Count files that would be synced (for reporting)
            var filesToSync = GetFilesToSync(localRoot).ToList();
            var syncMethod = GetAvailableSyncMethod();

            progress?.Report(new SyncProgress
            {
                CurrentFile = $"Syncing via {syncMethod}...",
                FilesProcessed = 0,
                TotalFiles = filesToSync.Count,
                BytesTransferred = 0,
            });

            // Use the best available method
            var success = await CopyDirectoryToRemoteAsync(localRoot, remoteRoot, connectionInfo, ct).ConfigureAwait(false);

            stopwatch.Stop();

            if (success)
            {
                // Calculate total bytes transferred
                long bytesTransferred = filesToSync.Sum(f => new FileInfo((string)f).Length);
                return SyncResult.Succeeded(filesToSync.Count, 0, bytesTransferred, stopwatch.Elapsed);
            }
            else
            {
                return SyncResult.Failed($"Failed to sync directory to remote using {syncMethod}.");
            }
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

    /// <inheritdoc/>
    public async Task<SyncResult> SyncProjectWithDependenciesAsync(
        PathEx localRoot,
        RemotePath remoteRoot,
        SshConnectionInfo connectionInfo,
        IProgress<SyncProgress> progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        int totalFilesSynced = 0;
        int totalFilesSkipped = 0;
        long totalBytesTransferred = 0;
        var syncedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Find the Cargo.toml file
            var cargoTomlPath = localRoot + "Cargo.toml";
            if (!File.Exists((string)cargoTomlPath))
            {
                // No Cargo.toml, just sync the main project
                var mainResult = await SyncToRemoteAsync(localRoot, remoteRoot, connectionInfo, progress, ct).ConfigureAwait(false);
                stopwatch.Stop();
                return mainResult;
            }

            // Get all path dependencies recursively
            var dependencies = CargoTomlParser.GetAllPathDependenciesRecursive(cargoTomlPath);

            // Collect all paths we need to sync (project + dependencies)
            var allPaths = new List<string> { (string)localRoot };
            allPaths.AddRange(dependencies.Select(d => (string)d.AbsoluteLocalPath));

            // Find the common ancestor of all paths - this preserves relative path structure
            var commonRoot = FindCommonAncestor(allPaths);

            // Calculate remote sync base - use $HOME/vs-sync as base
            var remoteBase = "$HOME/vs-sync";

            // Sync the main project first
            var projectRelativePath = GetRelativePathFromRoot(commonRoot, (string)localRoot);
            var projectRemotePath = new RemotePath($"{remoteBase}/{projectRelativePath}", TargetKind.Ssh);

            var mainSyncResult = await SyncToRemoteAsync(localRoot, projectRemotePath, connectionInfo, progress, ct).ConfigureAwait(false);
            if (!mainSyncResult.Success)
            {
                return mainSyncResult;
            }

            totalFilesSynced += mainSyncResult.FilesSynced;
            totalFilesSkipped += mainSyncResult.FilesSkipped;
            totalBytesTransferred += mainSyncResult.BytesTransferred;
            syncedPaths.Add((string)localRoot);

            // Sync each dependency, preserving the relative structure from common root
            foreach (var dependency in dependencies)
            {
                ct.ThrowIfCancellationRequested();

                var depLocalPath = (string)dependency.AbsoluteLocalPath;

                // Skip if already synced
                if (syncedPaths.Contains(depLocalPath))
                {
                    continue;
                }

                // Calculate remote path by preserving structure relative to common root
                var depRelativePath = GetRelativePathFromRoot(commonRoot, depLocalPath);
                var depRemotePath = new RemotePath($"{remoteBase}/{depRelativePath}", TargetKind.Ssh);

                // Sync the dependency
                var depResult = await SyncToRemoteAsync(
                    dependency.AbsoluteLocalPath,
                    depRemotePath,
                    connectionInfo,
                    progress,
                    ct).ConfigureAwait(false);

                if (!depResult.Success)
                {
                    totalFilesSkipped++;
                    continue;
                }

                totalFilesSynced += depResult.FilesSynced;
                totalFilesSkipped += depResult.FilesSkipped;
                totalBytesTransferred += depResult.BytesTransferred;
                syncedPaths.Add(depLocalPath);
            }

            stopwatch.Stop();
            return SyncResult.Succeeded(totalFilesSynced, totalFilesSkipped, totalBytesTransferred, stopwatch.Elapsed);
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

    /// <summary>
    /// Finds the common ancestor directory of all given paths.
    /// </summary>
    private static string FindCommonAncestor(IList<string> paths)
    {
        if (paths == null || paths.Count == 0)
        {
            return string.Empty;
        }

        if (paths.Count == 1)
        {
            return Path.GetDirectoryName(paths[0]) ?? paths[0];
        }

        // Normalize all paths
        var normalizedPaths = paths.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).ToList();

        // Split the first path into parts
        var firstPath = normalizedPaths[0];
        var commonParts = firstPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Compare with each other path
        foreach (var path in normalizedPaths.Skip(1))
        {
            var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            // Find how many parts match
            int matchCount = 0;
            for (int i = 0; i < Math.Min(commonParts.Count, parts.Length); i++)
            {
                if (string.Equals(commonParts[i], parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    matchCount++;
                }
                else
                {
                    break;
                }
            }

            // Trim common parts to the matching length
            if (matchCount < commonParts.Count)
            {
                commonParts = commonParts.Take(matchCount).ToList();
            }
        }

        // Reconstruct the common path
        if (commonParts.Count == 0)
        {
            return string.Empty;
        }

        // Handle drive letter on Windows
        var result = commonParts[0];
        if (result.Length == 2 && result[1] == ':')
        {
            result += "\\";
        }

        for (int i = 1; i < commonParts.Count; i++)
        {
            result = Path.Combine(result, commonParts[i]);
        }

        return result;
    }

    /// <summary>
    /// Gets the relative path from a root directory to a target path.
    /// </summary>
    private static string GetRelativePathFromRoot(string root, string targetPath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd('\\', '/');

        if (normalizedTarget.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedTarget.Substring(normalizedRoot.Length).TrimStart('\\', '/');
            return relative.Replace('\\', '/');
        }

        // Fallback: just use the directory name
        return Path.GetFileName(normalizedTarget);
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

    /// <summary>
    /// Copies an entire local directory to the remote using the best available method.
    /// Priority: rsync (incremental) > tar+ssh (compressed) > scp (basic)
    /// </summary>
    private static async Task<bool> CopyDirectoryToRemoteAsync(
        PathEx localDir,
        RemotePath remoteDir,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        EnsureToolsChecked();

        // Try rsync first (best - incremental, supports excludes, compressed)
        if (_rsyncAvailable == true)
        {
            Debug.WriteLine("[SshFileSyncService] Using rsync for sync");
            var result = await TryRsyncToRemoteAsync(localDir, remoteDir, connectionInfo, ct).ConfigureAwait(false);
            if (result)
            {
                return true;
            }

            // rsync failed, try next method
            Debug.WriteLine("[SshFileSyncService] rsync failed, trying tar+ssh");
        }

        // Try tar+ssh (good - compressed, single connection, supports excludes)
        if (_tarAvailable == true)
        {
            Debug.WriteLine("[SshFileSyncService] Using tar+ssh for sync");
            var result = await TryTarSshToRemoteAsync(localDir, remoteDir, connectionInfo, ct).ConfigureAwait(false);
            if (result)
            {
                return true;
            }

            // tar failed, try next method
            Debug.WriteLine("[SshFileSyncService] tar+ssh failed, falling back to scp");
        }

        // Fall back to scp -r (basic - works everywhere but slower)
        Debug.WriteLine("[SshFileSyncService] Using scp -r for sync (fallback)");
        return await ScpDirectoryToRemoteAsync(localDir, remoteDir, connectionInfo, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Syncs using tar piped over SSH. Very efficient - single connection, compressed, handles excludes.
    /// Works with tar from Git for Windows or Windows 10+.
    /// </summary>
    private static async Task<bool> TryTarSshToRemoteAsync(
        PathEx localDir,
        RemotePath remoteDir,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        try
        {
            // Build exclude arguments for tar
            var excludeArgs = new StringBuilder();
            foreach (var exclude in ExcludedNames)
            {
                if (!exclude.Contains("*"))
                {
                    excludeArgs.Append($" --exclude=\"{exclude}\"");
                }
            }

            // Build SSH command
            var sshCmd = new StringBuilder();
            sshCmd.Append($"ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes");

            if (!string.IsNullOrEmpty(connectionInfo.IdentityFile))
            {
                sshCmd.Append($" -i \"{connectionInfo.IdentityFile}\"");
            }

            if (connectionInfo.Port != 22)
            {
                sshCmd.Append($" -p {connectionInfo.Port}");
            }

            sshCmd.Append($" {connectionInfo.Username}@{connectionInfo.Host}");

            // Remote directory with $HOME expanded
            var remoteDirStr = (string)remoteDir;
            string remoteExtractCmd;
            if (remoteDirStr.Contains("$HOME"))
            {
                remoteExtractCmd = $"\"mkdir -p {remoteDirStr} && tar -xzf - -C {remoteDirStr}\"";
            }
            else
            {
                remoteExtractCmd = $"\"mkdir -p '{remoteDirStr}' && tar -xzf - -C '{remoteDirStr}'\"";
            }

            // Full command: tar -czf - [excludes] -C localDir . | ssh user@host "mkdir -p remotedir && tar -xzf - -C remotedir"
            var localPath = (string)localDir;

            // Use cmd.exe to run the piped command on Windows
            var cmdArgs = $"/c tar -czf -{excludeArgs} -C \"{localPath}\" . | {sshCmd} {remoteExtractCmd}";

            Debug.WriteLine($"[SshFileSyncService] tar+ssh command: cmd.exe {cmdArgs}");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            // Read output asynchronously
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10)); // 10 minute timeout

            try
            {
                await Task.Run(() => proc.WaitForExit(), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                Debug.WriteLine($"[SshFileSyncService] tar+ssh failed with exit code {proc.ExitCode}: {error}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SshFileSyncService] tar+ssh error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Tries to use rsync for efficient directory sync. Returns false if rsync is not available.
    /// </summary>
    private static async Task<bool> TryRsyncToRemoteAsync(
        PathEx localDir,
        RemotePath remoteDir,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        // Build rsync command
        var rsyncArgs = new List<string>
        {
            "-avz",           // archive, verbose, compress
            "--delete",       // delete files on remote that don't exist locally
            "--progress",     // show progress
        };

        // Add excludes
        foreach (var exclude in ExcludedNames)
        {
            rsyncArgs.Add($"--exclude={exclude}");
        }

        // Build SSH command for rsync
        var sshCmd = new StringBuilder("ssh");
        if (!string.IsNullOrEmpty(connectionInfo.IdentityFile))
        {
            sshCmd.Append($" -i \"{connectionInfo.IdentityFile}\"");
        }

        if (connectionInfo.Port != 22)
        {
            sshCmd.Append($" -p {connectionInfo.Port}");
        }

        sshCmd.Append(" -o StrictHostKeyChecking=accept-new -o BatchMode=yes");

        rsyncArgs.Add("-e");
        rsyncArgs.Add(sshCmd.ToString());

        // Source - add trailing slash to copy contents, not directory itself
        var localPath = (string)localDir;
        if (!localPath.EndsWith("\\") && !localPath.EndsWith("/"))
        {
            localPath += "/";
        }

        // Convert Windows path to rsync-compatible format (for Git Bash rsync)
        // C:\path\to\dir -> /c/path/to/dir
        if (localPath.Length > 2 && localPath[1] == ':')
        {
            localPath = "/" + char.ToLower(localPath[0]) + localPath.Substring(2).Replace('\\', '/');
        }

        rsyncArgs.Add(localPath);

        // Destination
        var remoteDirStr = ConvertHomeForScp((string)remoteDir);
        rsyncArgs.Add($"{connectionInfo.Username}@{connectionInfo.Host}:{remoteDirStr}/");

        try
        {
            using var proc = ProcessRunner.Run("rsync", rsyncArgs.ToArray(), workingDirectory: null, env: null, ct);
            var exitCode = await proc;
            return exitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // rsync not found
            return false;
        }
    }

    /// <summary>
    /// Copies directory using scp -r (fallback when rsync is not available).
    /// </summary>
    private static async Task<bool> ScpDirectoryToRemoteAsync(
        PathEx localDir,
        RemotePath remoteDir,
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

        // Batch mode
        scpArgs.Add("-o");
        scpArgs.Add("BatchMode=yes");

        // Recursive copy
        scpArgs.Add("-r");

        // Quiet mode (reduces overhead)
        scpArgs.Add("-q");

        // Source - we need to sync contents, so use wildcard
        // But first, let's get all top-level items that aren't excluded
        var localPath = (string)localDir;
        var itemsToSync = new List<string>();

        foreach (var dir in Directory.GetDirectories(localPath))
        {
            var dirName = Path.GetFileName(dir);
            if (!ExcludedNames.Contains(dirName))
            {
                itemsToSync.Add(dir);
            }
        }

        foreach (var file in Directory.GetFiles(localPath))
        {
            itemsToSync.Add(file);
        }

        if (itemsToSync.Count == 0)
        {
            return true; // Nothing to sync
        }

        // For scp, we add all sources
        scpArgs.AddRange(itemsToSync);

        // Destination
        var remoteDirStr = ConvertHomeForScp((string)remoteDir);
        scpArgs.Add($"{connectionInfo.Username}@{connectionInfo.Host}:{remoteDirStr}/");

        using var proc = ProcessRunner.Run("scp", scpArgs.ToArray(), workingDirectory: null, env: null, ct);
        var exitCode = await proc;

        if (exitCode != 0)
        {
            return false;
        }

        // Clean up excluded directories on remote (they might exist from previous syncs)
        await CleanExcludedDirectoriesOnRemoteAsync(remoteDir, connectionInfo, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Removes excluded directories on the remote if they exist.
    /// </summary>
    private static async Task CleanExcludedDirectoriesOnRemoteAsync(
        RemotePath remoteDir,
        SshConnectionInfo connectionInfo,
        CancellationToken ct)
    {
        var sshArgs = new List<string>();

        if (!string.IsNullOrEmpty(connectionInfo.IdentityFile))
        {
            sshArgs.Add("-i");
            sshArgs.Add(connectionInfo.IdentityFile);
        }

        if (connectionInfo.Port != 22)
        {
            sshArgs.Add("-p");
            sshArgs.Add(connectionInfo.Port.ToString());
        }

        sshArgs.Add("-o");
        sshArgs.Add("StrictHostKeyChecking=accept-new");
        sshArgs.Add("-o");
        sshArgs.Add("BatchMode=yes");

        sshArgs.Add($"{connectionInfo.Username}@{connectionInfo.Host}");

        // Build command to remove excluded directories (ignore errors if they don't exist)
        var remoteDirStr = (string)remoteDir;
        var rmCommands = string.Join(" ; ", ExcludedNames
            .Where(n => !n.Contains("*")) // Skip patterns with wildcards
            .Select(n => remoteDirStr.Contains("$HOME")
                ? $"rm -rf {remoteDirStr}/{n} 2>/dev/null"
                : $"rm -rf \"{remoteDirStr}/{n}\" 2>/dev/null"));

        sshArgs.Add(rmCommands);

        try
        {
            using var proc = ProcessRunner.Run("ssh", sshArgs.ToArray(), workingDirectory: null, env: null, ct);
            await proc; // Ignore exit code - directories might not exist
        }
        catch
        {
            // Ignore errors during cleanup
        }
    }
}

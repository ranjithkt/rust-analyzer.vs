using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KS.RustAnalyzer.TestAdapter;
using KS.RustAnalyzer.TestAdapter.Cargo;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Maintains a WSL-native mirror (ext4) of a Windows workspace + external path dependencies.
/// Uses rsync on-demand (sync-on-build) and lightweight Windows file watchers to avoid unnecessary sync.
/// </summary>
public sealed class WslMirrorInstance : IDisposable
{
    private static readonly Regex TomlPathCracker =
        new("\\bpath\\s*=\\s*(['\\\"])(?<path>[^'\\\"]+)\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PathEx _workspaceRootWindows;
    private readonly string _distroName;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly object _watcherLock = new();

    private bool _initialized;
    private bool _disposed;

    private volatile bool _workspaceDirty = true;
    private volatile bool _depsDirty = true;

    private FileSystemWatcher _workspaceWatcher;
    private readonly Dictionary<string, FileSystemWatcher> _depWatchers = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> _externalRoots = new(StringComparer.OrdinalIgnoreCase);

    public WslMirrorInstance(PathEx workspaceRootWindows, string distroName)
    {
        _workspaceRootWindows = workspaceRootWindows;
        _distroName = (distroName ?? string.Empty).Trim();
    }

    public WslMirrorConfig Config { get; private set; }

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            var linuxHome = await GetLinuxHomeAsync(_distroName, ct);
            var mirrorBase = WslMirrorConfig.CombineLinuxPaths(linuxHome, ".cache/rust-analyzer.vs/mirrors");
            var workspaceId = ComputeWorkspaceId(_workspaceRootWindows, _distroName);

            var cfg = new WslMirrorConfig(_workspaceRootWindows.GetFullPath(), _distroName, linuxHome, mirrorBase, workspaceId);
            cfg.MirrorTargetDirUnc = LinuxPathToUnc(_distroName, cfg.MirrorTargetDirLinux);
            Config = cfg;

            EnsureWorkspaceWatcher();
            await RefreshExternalRootsAsync(ct);

            // Do not eagerly rsync here; keep init lightweight.
            // First actual tool invocation will call EnsureSynchronizedAsync which will rsync as needed.

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task EnsureSynchronizedAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        if (_depsDirty)
        {
            await RefreshExternalRootsAsync(ct);
        }

        if (_workspaceDirty)
        {
            await RsyncWindowsRootToMirrorAsync(_workspaceRootWindows, ct);
            _workspaceDirty = false;
        }

        // Sync external roots (outside workspace root) only if marked dirty.
        if (_externalRoots.Count > 0)
        {
            foreach (var root in _externalRoots.ToArray())
            {
                if (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                }

                // If deps dirty we resync all external roots (safe).
                if (_depsDirty)
                {
                    await RsyncWindowsRootToMirrorAsync((PathEx)root, ct);
                }
            }
        }

        _depsDirty = false;
    }

    private void EnsureWorkspaceWatcher()
    {
        if (_workspaceWatcher != null)
        {
            return;
        }

        var path = (string)_workspaceRootWindows;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var w = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };

        w.Changed += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Created += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Deleted += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Renamed += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Error += (_, __) => { _workspaceDirty = true; _depsDirty = true; };

        _workspaceWatcher = w;
    }

    private void EnsureDepWatcher(string root)
    {
        lock (_watcherLock)
        {
            if (_depWatchers.ContainsKey(root))
            {
                return;
            }

            if (!Directory.Exists(root))
            {
                return;
            }

            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };

            w.Changed += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Created += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Deleted += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Renamed += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Error += (_, __) => { _depsDirty = true; };

            _depWatchers[root] = w;
        }
    }

    private void OnAnyWorkspaceChanged(string fullPath)
    {
        if (ShouldIgnore(fullPath))
        {
            return;
        }

        _workspaceDirty = true;

        if (IsManifestOrLock(fullPath))
        {
            _depsDirty = true;
        }
    }

    private void OnAnyDepChanged(string root, string fullPath)
    {
        if (ShouldIgnore(fullPath))
        {
            return;
        }

        _depsDirty = true;

        if (IsManifestOrLock(fullPath))
        {
            _depsDirty = true;
        }
    }

    private bool ShouldIgnore(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || Config == null)
        {
            return false;
        }

        try
        {
            var excluded = Config.ExcludedDirectoryNames ?? Array.Empty<string>();
            foreach (var dir in excluded)
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                // Cheap substring ignore: "\\target\\" etc.
                var needle = "\\" + dir.Trim('\\', '/') + "\\";
                if (fullPath.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore.
        }

        return false;
    }

    private static bool IsManifestOrLock(string fullPath)
    {
        try
        {
            var name = Path.GetFileName(fullPath);
            return string.Equals(name, Constants.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Cargo.lock", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task RefreshExternalRootsAsync(CancellationToken ct)
    {
        // Discover external path deps by:
        // 1) get workspace package manifests via `cargo metadata --no-deps`
        // 2) parse those manifests (and recursively discovered path dep manifests) for `path = "..."`

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in await DiscoverExternalPathDependencyRootsAsync(ct))
        {
            roots.Add(r);
        }

        // Update watchers.
        foreach (var root in roots)
        {
            EnsureDepWatcher(root);
        }

        // Best-effort cleanup of old watchers.
        lock (_watcherLock)
        {
            foreach (var kv in _depWatchers.Keys.ToArray())
            {
                if (!roots.Contains(kv))
                {
                    try
                    {
                        _depWatchers[kv].Dispose();
                    }
                    catch
                    {
                    }

                    _depWatchers.Remove(kv);
                }
            }
        }

        _externalRoots = roots;
    }

    private async Task<IEnumerable<string>> DiscoverExternalPathDependencyRootsAsync(CancellationToken ct)
    {
        var wsManifest = _workspaceRootWindows.Combine(Constants.ManifestFileName2);
        var rootManifest = wsManifest.FileExists() ? wsManifest : FindAnyManifestUnderWorkspace();
        if (!rootManifest.FileExists())
        {
            return Array.Empty<string>();
        }

        // Run cargo metadata --no-deps in WSL against /mnt/... (no mirror needed).
        if (!WslPathMapper.TryWindowsToWslPath(rootManifest, out var linuxManifest) ||
            !WslPathMapper.TryWindowsToWslPath(rootManifest.GetDirectoryName(), out var linuxCwd))
        {
            return Array.Empty<string>();
        }

        var args = new[] { "metadata", "--no-deps", "--format-version", "1", "--manifest-path", linuxManifest, "--offline" };
        using var proc = ToolchainServiceExtensions.RunCargoInWsl(_distroName, args, linuxCwd, ct);
        var ec = await proc;
        if (ec != 0)
        {
            return Array.Empty<string>();
        }

        var json = string.Join(string.Empty, proc.StandardOutputLines);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            if (obj["packages"] is Newtonsoft.Json.Linq.JArray packages)
            {
                foreach (var p in packages)
                {
                    var mp = (string)p["manifest_path"];
                    if (string.IsNullOrWhiteSpace(mp))
                    {
                        continue;
                    }

                    if (WslPathMapper.TryWslToWindowsPath(mp, out var winMp) && !string.IsNullOrWhiteSpace(winMp))
                    {
                        manifests.Add(winMp);
                    }
                }
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        // BFS through manifests for path deps.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<string>(manifests);
        var externals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (q.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var manifest = q.Dequeue();
            if (!visited.Add(manifest))
            {
                continue;
            }

            foreach (var depRoot in FindPathDepsFromManifest(manifest))
            {
                if (string.IsNullOrWhiteSpace(depRoot))
                {
                    continue;
                }

                // Skip dependencies inside the workspace root (workspace sync covers them).
                try
                {
                    var depRootEx = (PathEx)depRoot;
                    if (depRootEx.IsContainedIn(_workspaceRootWindows))
                    {
                        continue;
                    }
                }
                catch
                {
                }

                if (externals.Add(depRoot))
                {
                    // Recurse into dep's Cargo.toml.
                    var depManifest = Path.Combine(depRoot, Constants.ManifestFileName);
                    if (File.Exists(depManifest))
                    {
                        q.Enqueue(depManifest);
                    }
                }
            }
        }

        return externals;
    }

    private PathEx FindAnyManifestUnderWorkspace()
    {
        // Conservative: only check the workspace root.
        // If no root Cargo.toml exists, we won't attempt deep scanning here.
        return (PathEx)string.Empty;
    }

    private static IEnumerable<string> FindPathDepsFromManifest(string manifestPath)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var baseDir = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(baseDir) || !File.Exists(manifestPath))
            {
                return results;
            }

            foreach (var line in File.ReadLines(manifestPath))
            {
                var m = TomlPathCracker.Match(line);
                if (!m.Success)
                {
                    continue;
                }

                var raw = m.Groups["path"].Value?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                // Ignore Linux absolute paths.
                if (raw.StartsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                string depDir;
                if (Path.IsPathRooted(raw))
                {
                    depDir = raw;
                }
                else
                {
                    depDir = Path.GetFullPath(Path.Combine(baseDir, raw));
                }

                // Normalize to the directory that actually contains Cargo.toml.
                if (File.Exists(depDir) && depDir.EndsWith(Constants.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    depDir = Path.GetDirectoryName(depDir);
                }

                if (string.IsNullOrWhiteSpace(depDir))
                {
                    continue;
                }

                var depManifest = Path.Combine(depDir, Constants.ManifestFileName);
                if (File.Exists(depManifest))
                {
                    results.Add(depDir);
                }
            }
        }
        catch
        {
        }

        return results;
    }

    private async Task RsyncWindowsRootToMirrorAsync(PathEx windowsRoot, CancellationToken ct)
    {
        if (Config == null)
        {
            return;
        }

        var winRoot = (string)windowsRoot;
        if (string.IsNullOrWhiteSpace(winRoot) || !Directory.Exists(winRoot))
        {
            return;
        }

        if (!WslPathMapper.TryWindowsToWslPath(winRoot, out var linuxSrc))
        {
            return;
        }

        if (!WslMirrorPathMapper.TryWindowsToMirrorLinuxPath(winRoot, Config, out var linuxDst))
        {
            return;
        }

        // Ensure destination directory exists.
        using (var mkdir = ToolchainServiceExtensions.RunInWsl(_distroName, "/bin/mkdir", new[] { "-p", linuxDst }, linuxWorkingDir: null, env: ImmutableDictionary<string, string>.Empty, ct))
        {
            await mkdir;
        }

        var args = new List<string>();
        args.AddRange(Config.RsyncArgsPrefix ?? Array.Empty<string>());

        foreach (var ex in Config.ExcludedDirectoryNames ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(ex))
            {
                continue;
            }

            // rsync expects trailing slash to exclude directory.
            var pat = ex.Trim('\\', '/');
            args.Add("--exclude");
            args.Add(pat + "/");
        }

        // Copy directory contents (trailing slash matters).
        args.Add(linuxSrc.TrimEnd('/') + "/");
        args.Add(linuxDst.TrimEnd('/') + "/");

        using var rsync = ToolchainServiceExtensions.RunInWsl(_distroName, "rsync", args.ToArray(), linuxWorkingDir: null, env: null, ct);
        var ec = await rsync;
        if (ec != 0)
        {
            var output = string.Join("\n", rsync.StandardOutputLines.Concat(rsync.StandardErrorLines));
            throw new InvalidOperationException($"rsync failed with exit code {ec}. Output:\n{output}");
        }
    }

    private static string LinuxPathToUnc(string distroName, string linuxAbsPath)
    {
        return LinuxPathToUncInternal(distroName, linuxAbsPath);
    }

    private static string LinuxPathToUncInternal(string distroName, string linuxAbsPath)
    {
        if (string.IsNullOrWhiteSpace(distroName) || string.IsNullOrWhiteSpace(linuxAbsPath) || !linuxAbsPath.StartsWith("/"))
        {
            return linuxAbsPath;
        }

        // Create a WslInfo instance by parsing the distro UNC root.
        // We standardize on \wsl.localhost for generated paths.
        var uncRoot = $"\\\\wsl.localhost\\{distroName}\\";
        if (!WslInfo.TryParse(uncRoot, out var info) || info == null)
        {
            return linuxAbsPath;
        }

        return info.ToUncPath(linuxAbsPath);
    }

    private static async Task<string> GetLinuxHomeAsync(string distroName, CancellationToken ct)
    {
        var wslExe = WslInfo.GetWslExePath();
        // Use printf to avoid trailing newline.
        var args = new[] { "-d", distroName, "--exec", "/bin/bash", "-lc", "printf %s \"$HOME\"" };
        using var proc = ProcessRunner.Run(wslExe, args, Environment.SystemDirectory, ImmutableDictionary<string, string>.Empty, ct);
        var ec = await proc;
        if (ec != 0)
        {
            throw new InvalidOperationException($"Unable to query $HOME in WSL distro '{distroName}'.");
        }

        var stdout = string.Join(string.Empty, proc.StandardOutputLines);
        if (!string.IsNullOrEmpty(stdout) && stdout.IndexOf('\0') >= 0)
        {
            stdout = stdout.Replace("\0", string.Empty);
        }

        stdout = stdout.Trim();
        if (string.IsNullOrWhiteSpace(stdout) || !stdout.StartsWith("/"))
        {
            throw new InvalidOperationException($"Unexpected $HOME from WSL distro '{distroName}': '{stdout}'.");
        }

        return stdout;
    }

    private static string ComputeWorkspaceId(PathEx workspaceRootWindows, string distroName)
    {
        var input = (((string)workspaceRootWindows.GetFullPath()) + "|" + (distroName ?? string.Empty)).ToLowerInvariant();
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        // 16 bytes hex is enough and keeps paths short.
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16 && i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { _workspaceWatcher?.Dispose(); } catch { }

        lock (_watcherLock)
        {
            foreach (var w in _depWatchers.Values)
            {
                try { w.Dispose(); } catch { }
            }

            _depWatchers.Clear();
        }

        _initLock.Dispose();
    }
}

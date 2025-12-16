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
using KS.RustAnalyzer.TestAdapter.Cargo;
using Tomlyn;
using Tomlyn.Model;

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

    private readonly object _watcherLock = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly SemaphoreSlim _rsyncProbeLock = new(1, 1);
    private readonly Dictionary<string, FileSystemWatcher> _depWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _externalRootsDirty = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private int _disposed;

    // Dirty flags: use Interlocked to avoid lost updates between watcher threads and sync thread.
    // 0 = clean, 1 = dirty
    private int _workspaceDirty = 1;

    // Dependency graph dirty: manifests / lock files changed; requires re-discovery of external roots.
    private int _depsGraphDirty = 1;

    // Dependency content dirty: files changed under one or more external roots; requires rsync only.
    private int _depsContentDirty = 1;

    // Watchers can overflow; when they do, recreate them on the next sync.
    private int _watchersNeedRecreate;

    // 0 = unchecked, 1 = checking, 2 = available, 3 = missing
    private int _rsyncState;
    private long _rsyncLastCheckUtcTicks;

    private FileSystemWatcher _workspaceWatcher;
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

            // We just refreshed the dep graph; avoid re-running discovery on the first sync.
            Interlocked.Exchange(ref _depsGraphDirty, 0);

            // But do ensure all discovered external roots get synced at least once.
            MarkDepsContentDirtyAll();

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

        // Multiple VS operations can trigger WSL tool invocations concurrently (build + test discovery + debug, etc.).
        // Serialize all sync work per instance to avoid concurrent rsync into the same destination.
        await _syncLock.WaitAsync(ct);
        try
        {
            if (Interlocked.Exchange(ref _watchersNeedRecreate, 0) == 1)
            {
                // After an overflow, assume the watcher may have lost events; we already marked dirty in Error handlers.
                // Recreate watchers so we continue to get events reliably going forward.
                try
                {
                    RecreateWatchers();
                }
                catch
                {
                    // Best-effort.
                }
            }

            var depsGraphDirty = Interlocked.Exchange(ref _depsGraphDirty, 0) == 1;
            var depsContentDirty = Interlocked.Exchange(ref _depsContentDirty, 0) == 1;
            var workspaceDirty = Interlocked.Exchange(ref _workspaceDirty, 0) == 1;

            if (depsGraphDirty)
            {
                try
                {
                    await RefreshExternalRootsAsync(ct);
                }
                catch
                {
                    MarkDepsGraphDirty();
                    throw;
                }
            }

            if (workspaceDirty)
            {
                try
                {
                    await RsyncWindowsRootToMirrorAsync(_workspaceRootWindows, ct);
                }
                catch
                {
                    MarkWorkspaceDirty();
                    throw;
                }
            }

            // External roots:
            // - graph dirty => sync all roots once (new roots may exist)
            // - content dirty => sync only roots that were dirtied
            if (depsGraphDirty || depsContentDirty)
            {
                string[] rootsToSync;
                lock (_watcherLock)
                {
                    rootsToSync = depsGraphDirty ? _externalRoots.ToArray() : _externalRootsDirty.ToArray();
                }

                if (rootsToSync.Length > 0)
                {
                    foreach (var root in rootsToSync)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            await RsyncWindowsRootToMirrorAsync((PathEx)root, ct);
                            lock (_watcherLock)
                            {
                                _externalRootsDirty.Remove(root);
                            }
                        }
                        catch
                        {
                            MarkDepsContentDirty(root);
                            throw;
                        }
                    }
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _workspaceWatcher?.Dispose();
        }
        catch
        {
        }

        lock (_watcherLock)
        {
            foreach (var w in _depWatchers.Values)
            {
                try
                {
                    w.Dispose();
                }
                catch
                {
                }
            }

            _depWatchers.Clear();
        }

        _initLock.Dispose();
        _syncLock.Dispose();
        _rsyncProbeLock.Dispose();
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
        };

        w.Changed += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Created += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Deleted += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Renamed += (_, e) => OnAnyWorkspaceChanged(e.FullPath);
        w.Error += (_, __) =>
        {
            MarkWorkspaceDirty();
            MarkDepsGraphDirty();
            MarkDepsContentDirtyAll();
            Interlocked.Exchange(ref _watchersNeedRecreate, 1);

            try
            {
                System.Diagnostics.Trace.WriteLine("WslMirrorInstance: workspace FileSystemWatcher error (possible buffer overflow). Marking mirror dirty for full resync.");
            }
            catch
            {
            }
        };

        // Attach handlers before enabling events to avoid missing a change window at startup.
        w.EnableRaisingEvents = true;
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
            };

            w.Changed += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Created += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Deleted += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Renamed += (_, e) => OnAnyDepChanged(root, e.FullPath);
            w.Error += (_, __) =>
            {
                MarkDepsContentDirty(root);
                Interlocked.Exchange(ref _watchersNeedRecreate, 1);

                try
                {
                    System.Diagnostics.Trace.WriteLine("WslMirrorInstance: dependency FileSystemWatcher error (possible buffer overflow). Marking mirror deps dirty for full resync.");
                }
                catch
                {
                }
            };

            // Attach handlers before enabling events to avoid missing a change window at startup.
            w.EnableRaisingEvents = true;
            _depWatchers[root] = w;
        }
    }

    private void RecreateWatchers()
    {
        string[] roots;
        lock (_watcherLock)
        {
            roots = _externalRoots.ToArray();

            try
            {
                _workspaceWatcher?.Dispose();
            }
            catch
            {
            }

            _workspaceWatcher = null;

            foreach (var kv in _depWatchers.Values)
            {
                try
                {
                    kv.Dispose();
                }
                catch
                {
                }
            }

            _depWatchers.Clear();
        }

        EnsureWorkspaceWatcher();
        foreach (var r in roots)
        {
            EnsureDepWatcher(r);
        }
    }

    private void OnAnyWorkspaceChanged(string fullPath)
    {
        if (ShouldIgnore(fullPath))
        {
            return;
        }

        MarkWorkspaceDirty();

        if (IsManifestOrLock(fullPath))
        {
            MarkDepsGraphDirty();
        }
    }

    private void OnAnyDepChanged(string root, string fullPath)
    {
        if (ShouldIgnore(fullPath))
        {
            return;
        }

        MarkDepsContentDirty(root);

        if (IsManifestOrLock(fullPath))
        {
            MarkDepsGraphDirty();
        }
    }

    private void MarkWorkspaceDirty()
    {
        Interlocked.Exchange(ref _workspaceDirty, 1);
    }

    private void MarkDepsGraphDirty()
    {
        Interlocked.Exchange(ref _depsGraphDirty, 1);
    }

    private void MarkDepsContentDirty(string root)
    {
        Interlocked.Exchange(ref _depsContentDirty, 1);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        lock (_watcherLock)
        {
            _externalRootsDirty.Add(root);
        }
    }

    private void MarkDepsContentDirtyAll()
    {
        Interlocked.Exchange(ref _depsContentDirty, 1);
        lock (_watcherLock)
        {
            foreach (var r in _externalRoots)
            {
                _externalRootsDirty.Add(r);
            }
        }
    }

    // Back-compat helper: graph + content dirty.
    private void MarkDepsDirty()
    {
        MarkDepsGraphDirty();
        MarkDepsContentDirtyAll();
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

                if (ContainsPathSegment(fullPath, dir))
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

    private static bool ContainsPathSegment(string fullPath, string segment)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var seg = segment.Trim('\\', '/');
        if (seg.Length == 0)
        {
            return false;
        }

        // Look for occurrences of seg and ensure it is a full path segment (bounded by separators or ends).
        var idx = 0;
        while (true)
        {
            idx = fullPath.IndexOf(seg, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            var beforeOk = idx == 0 || fullPath[idx - 1] == '\\' || fullPath[idx - 1] == '/';
            var afterIdx = idx + seg.Length;
            var afterOk = afterIdx >= fullPath.Length || fullPath[afterIdx] == '\\' || fullPath[afterIdx] == '/';
            if (beforeOk && afterOk)
            {
                return true;
            }

            idx = afterIdx;
        }
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
        // Discover external path deps.
        //
        // Preferred (robust): use `cargo metadata` (with deps) and collect all packages with `source == null`
        // that resolve outside the workspace root.
        //
        // Fallback (best-effort): seed with `cargo metadata --no-deps` + scan manifests for `path = "..."`
        // when full metadata is unavailable (e.g. offline resolution failures).
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

        lock (_watcherLock)
        {
            _externalRoots = roots;
            try
            {
                _externalRootsDirty.RemoveWhere(r => !_externalRoots.Contains(r));
            }
            catch
            {
                // best-effort
            }
        }
    }

    private async Task<IEnumerable<string>> DiscoverExternalPathDependencyRootsAsync(CancellationToken ct)
    {
        var wsManifest = _workspaceRootWindows.Combine(Constants.ManifestFileName2);
        var rootManifest = wsManifest.FileExists() ? wsManifest : FindAnyManifestUnderWorkspace();
        if (!rootManifest.FileExists())
        {
            return Array.Empty<string>();
        }

        // Run cargo metadata in WSL against /mnt/... (no mirror needed).
        if (!WslPathMapper.TryWindowsToWslPath(rootManifest, out var linuxManifest) ||
            !WslPathMapper.TryWindowsToWslPath(rootManifest.GetDirectoryName(), out var linuxCwd))
        {
            return Array.Empty<string>();
        }

        // Preferred: full metadata (captures all local path deps without TOML parsing).
        try
        {
            var argsFull = new[] { "metadata", "--format-version", "1", "--manifest-path", linuxManifest, "--offline" };
            using var procFull = ToolchainServiceExtensions.RunCargoInWsl(_distroName, argsFull, linuxCwd, ct);
            var ecFull = await procFull;
            if (ecFull == 0)
            {
                var jsonFull = string.Join(string.Empty, procFull.StandardOutputLines);
                if (!string.IsNullOrWhiteSpace(jsonFull))
                {
                    var objFull = Newtonsoft.Json.Linq.JObject.Parse(jsonFull);
                    if (objFull["packages"] is Newtonsoft.Json.Linq.JArray packagesFull)
                    {
                        var roots = new List<PathEx>();

                        foreach (var p in packagesFull)
                        {
                            // Path/workspace crates have source == null.
                            if ((string)p["source"] != null)
                            {
                                continue;
                            }

                            var mp = (string)p["manifest_path"];
                            if (string.IsNullOrWhiteSpace(mp))
                            {
                                continue;
                            }

                            if (!WslPathMapper.TryWslToWindowsPath(mp, out var winMp) || string.IsNullOrWhiteSpace(winMp))
                            {
                                continue;
                            }

                            var dir = Path.GetDirectoryName(winMp);
                            if (string.IsNullOrWhiteSpace(dir))
                            {
                                continue;
                            }

                            try
                            {
                                var dirEx = (PathEx)dir;
                                if (dirEx.IsContainedIn(_workspaceRootWindows))
                                {
                                    continue;
                                }

                                roots.Add(dirEx);
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        // Consolidate roots to avoid many rsyncs: if a root is contained in another root, keep only the outer root.
                        var consolidated = new List<PathEx>();
                        foreach (var r in roots
                                     .OrderBy(r => ((string)r).Length))
                        {
                            if (!consolidated.Any(x => r.IsContainedIn(x)))
                            {
                                consolidated.Add(r);
                            }
                        }

                        return consolidated.Select(x => (string)x).ToArray();
                    }
                }
            }
        }
        catch
        {
            // fall back
        }

        // Fallback: use --no-deps for workspace packages, then scan manifests for `path = ...` (best-effort).
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
        // Cheap-ish scan up to 2 levels deep for Cargo.toml when workspace root is not itself a Cargo root.
        // This avoids expensive full recursion on large repos while supporting common layouts.
        try
        {
            var root = (string)_workspaceRootWindows;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return (PathEx)string.Empty;
            }

            var direct = Path.Combine(root, Constants.ManifestFileName);
            if (File.Exists(direct))
            {
                return (PathEx)direct;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var p1 = Path.Combine(dir, Constants.ManifestFileName);
                if (File.Exists(p1))
                {
                    return (PathEx)p1;
                }

                foreach (var dir2 in Directory.EnumerateDirectories(dir))
                {
                    var p2 = Path.Combine(dir2, Constants.ManifestFileName);
                    if (File.Exists(p2))
                    {
                        return (PathEx)p2;
                    }
                }
            }
        }
        catch
        {
        }

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

            // Preferred fallback parsing: Tomlyn (handles inline tables, dotted keys, etc.).
            try
            {
                var text = File.ReadAllText(manifestPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var model = Toml.ToModel(text) as TomlTable;
                    if (model != null)
                    {
                        var rawPaths = new List<string>();
                        CollectTomlPathValues(model, rawPaths);

                        foreach (var raw in rawPaths)
                        {
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

                        // If Tomlyn found anything, treat it as authoritative for the fallback.
                        if (results.Count > 0)
                        {
                            return results;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to best-effort regex scan.
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

    private static void CollectTomlPathValues(object node, List<string> results)
    {
        if (node == null)
        {
            return;
        }

        if (node is TomlTable table)
        {
            foreach (var kv in table)
            {
                if (string.Equals(kv.Key, "path", StringComparison.OrdinalIgnoreCase) && kv.Value is string s)
                {
                    results.Add(s.Trim());
                    continue;
                }

                CollectTomlPathValues(kv.Value, results);
            }

            return;
        }

        if (node is TomlArray arr)
        {
            foreach (var e in arr)
            {
                CollectTomlPathValues(e, results);
            }

            return;
        }

        // Tomlyn represents table arrays as TomlTableArray (also IEnumerable).
        if (node is TomlTableArray ta)
        {
            foreach (var t in ta)
            {
                CollectTomlPathValues(t, results);
            }
        }
    }

    private async Task RsyncWindowsRootToMirrorAsync(PathEx windowsRoot, CancellationToken ct)
    {
        if (Config == null)
        {
            return;
        }

        await EnsureRsyncAvailableAsync(ct);

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
        using (var mkdir = ToolchainServiceExtensions.RunInWsl(_distroName, "mkdir", new[] { "-p", linuxDst }, linuxWorkingDir: null, env: ImmutableDictionary<string, string>.Empty, ct))
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

    private async Task EnsureRsyncAvailableAsync(CancellationToken ct)
    {
        var state = Volatile.Read(ref _rsyncState);
        if (state == 2)
        {
            return;
        }

        // If we previously failed, allow periodic retries: WSL can be temporarily unavailable (or mid-upgrade).
        if (state == 3)
        {
            var now = DateTime.UtcNow.Ticks;
            var last = Volatile.Read(ref _rsyncLastCheckUtcTicks);
            if (last != 0 && (now - last) < TimeSpan.FromSeconds(30).Ticks)
            {
                throw new InvalidOperationException(GetRsyncMissingMessage());
            }

            Interlocked.CompareExchange(ref _rsyncState, 0, 3);
        }

        await _rsyncProbeLock.WaitAsync(ct);
        try
        {
            state = Volatile.Read(ref _rsyncState);
            if (state == 2)
            {
                return;
            }

            try
            {
                using var probe = ToolchainServiceExtensions.RunInWsl(_distroName, "rsync", new[] { "--version" }, linuxWorkingDir: null, env: null, ct);
                var ec = await probe;
                Volatile.Write(ref _rsyncLastCheckUtcTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref _rsyncState, ec == 0 ? 2 : 3);
            }
            catch
            {
                Volatile.Write(ref _rsyncLastCheckUtcTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref _rsyncState, 3);
            }
        }
        finally
        {
            _rsyncProbeLock.Release();
        }

        if (Volatile.Read(ref _rsyncState) != 2)
        {
            throw new InvalidOperationException(GetRsyncMissingMessage());
        }
    }

    private string GetRsyncMissingMessage()
    {
        return $"WSL Mirror Sync requires 'rsync' to be installed in the WSL distro '{_distroName}'. " +
               "Install it inside WSL, e.g. 'sudo apt update && sudo apt install rsync'.";
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
}

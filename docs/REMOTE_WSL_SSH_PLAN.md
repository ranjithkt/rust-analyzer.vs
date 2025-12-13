# Remote Development Plan (WSL + SSH) for `rust-analyzer.vs`

This document proposes an implementation plan to add **WSL** and **SSH remote** development support to this Visual Studio (not VS Code) extension:

- Connect to WSL/SSH targets
- Open remote folders (as VS "Open Folder" workspaces or via a local cache model)
- Build/test using remote toolchains
- Run and debug remote binaries
- Run rust-analyzer remotely while keeping VS editor navigation and diagnostics working

It is intentionally **design-only** (no implementation).

---

## Table of Contents

1. [Current Architecture](#current-architecture-what-exists-today)
2. [Goals and Constraints](#goals-and-constraints)
3. [Proposed Architecture](#proposed-architecture-target-system--execution-context)
4. [Interface Contracts](#interface-contracts)
5. [WSL Plan](#wsl-plan-phased)
6. [SSH Plan](#ssh-plan-two-approaches)
7. [Refactors Required](#refactors-required-surgical-not-a-rewrite)
8. [Error Handling Strategy](#error-handling-strategy)
9. [Rollout Plan](#rollout-plan-risk-controlled)
10. [Testing Strategy](#testing-strategy)
11. [Risk Assessment](#risk-assessment)

---

## Current Architecture (What Exists Today)

The extension is built around Visual Studio **Open Folder** extensibility and assumes a **Windows-local workspace**.

### Build/Test Pipeline

- Build & clean actions are provided through `IFileContextProvider` / `IFileScanner` and call into `IToolchainService` (`ToolchainService`) to run `cargo`.
- Process execution is local via `ProcessRunner` and some probes go through `cmd.exe`.
- Cargo JSON output parsing (`BuildJsonOutputParser`) and workspace metadata (`cargo metadata`) assume paths can be mapped to Windows files.

### Debug Pipeline

- `DebugLaunchTargetProvider` launches a **local Windows `.exe`** using `VsDebugTargetInfo` and `DebugEnginesGuids.NativeOnly_guid`.
- It verifies `File.Exists(processName)` and sets `bstrRemoteMachine = null`.

### Language Server (LSP)

- `LanguageClient` starts a **local** `rust-analyzer.exe` and talks over stdio.
- It is constrained to host context: `[RunOnContext(RunningContext.RunOnHost)]`.
- Working directory is derived from `WorkspaceService.CurrentWorkspace.Location` (assumed local).
- `MiddleLayer` property returns `null` - no URI rewriting.

### Test Adapter

- `TestDiscoverer` runs `cargo test --no-run` to discover test executables.
- `TestExecutor` runs compiled test binaries directly via `ProcessRunner`.
- Test source paths are parsed from cargo output and assumed to be Windows paths.

### Target System UI Exists But Is a Stub

- `TargetSystemCommands.cs` contains `TemporaryTargetSystemStore` with only `"Local Machine"`.
- It does not currently influence build/debug/LSP behavior.

### rust-analyzer Installation

- `RlsInstallerService` downloads `rust-analyzer-x86_64-pc-windows-msvc.zip` from GitHub releases.
- No support for Linux binaries or remote rust-analyzer discovery.

### Key Limitations to Call Out Early

#### `PathEx` is Windows-Centric

```csharp
public PathEx(string path)
{
    _path = path.Replace("/", @"\");  // Corrupts Linux paths!
}
```

- `PathEx` normalizes `/` into `\`, which **corrupts Linux paths immediately**.
- Many `PathExExtensions` methods call `System.IO.File/Directory/Path`, which operate on the host filesystem.
- **Solution**: Do NOT modify `PathEx`. Create a separate `RemotePath` type for remote paths.

#### `cmd.exe` Hardcoded in ToolchainServiceExtensions

```csharp
using var proc = ProcessRunner.Run("cmd.exe", new[] { "/c", $"{toolName} {args}" }, ...);
```

- Used for `rustup show`, `cargo --version`, etc.
- Must be abstracted through execution context.

#### Cargo Metadata Deserializes into PathEx

```csharp
[JsonProperty("workspace_root")]
public PathEx WorkspaceRoot { get; set; }
```

- When cargo runs in WSL, paths are Linux format.
- Deserializing into `PathEx` corrupts them.

### Existing Remote Infrastructure (Untapped)

- `ContentDefinition` already uses `CodeRemoteContentDefinition.CodeRemoteContentTypeName` as base.
- `Microsoft.VisualStudio.Linux.ConnectionManager.Store` is referenced in project dependencies.
- These may provide capabilities that can be leveraged.

---

## Goals and Constraints

### Goals

- **WSL**: Build, run, debug, and run rust-analyzer **inside WSL**, while the workspace is opened in VS via `\\wsl$\<distro>\...` paths.
- **SSH**: Connect to a remote host over SSH, open a remote folder, build/run/debug remotely, and run rust-analyzer remotely.
- **Test Adapter**: Discover and execute tests in remote environments with results mapped back to VS.

### Constraints

- Must remain compatible with Visual Studio Open Folder extensibility.
- Must preserve the extension's current behavior for local Windows workspaces.
- Must handle remote↔local path and URI translation for:
  - Build diagnostics (Error List navigation)
  - LSP locations/diagnostics (Go to definition, references, etc.)
  - Debug launch targets and source mapping
  - Test discovery and execution results

---

## Proposed Architecture: Target System + Execution Context

Introduce new primary abstractions and route build/test/debug/LSP through them.

### Core Types

#### `TargetKind` Enumeration

```csharp
public enum TargetKind
{
    Local,      // Windows local machine
    Wsl,        // WSL distro
    Ssh         // Remote SSH host
}
```

#### `RemotePath` Struct (NEW - Do Not Modify PathEx)

```csharp
/// <summary>
/// Represents a path on a remote system (WSL or SSH).
/// Unlike PathEx, this preserves forward slashes for Linux paths.
/// </summary>
[DebuggerDisplay("{_path} ({Kind})")]
public readonly struct RemotePath : IEquatable<RemotePath>
{
    private readonly string _path;

    public TargetKind Kind { get; }

    public RemotePath(string path, TargetKind kind)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Kind = kind;

        // Normalize to forward slashes for WSL/SSH
        if (kind != TargetKind.Local)
        {
            _path = _path.Replace(@"\", "/");
        }
    }

    public static implicit operator string(RemotePath p) => p._path;

    public RemotePath Combine(string segment) =>
        new RemotePath($"{_path}/{segment.TrimStart('/')}", Kind);

    public string GetFileName() =>
        _path.Split('/').LastOrDefault() ?? string.Empty;

    public RemotePath GetDirectoryName() =>
        new RemotePath(string.Join("/", _path.Split('/').SkipLast(1)), Kind);
}
```

### `ITargetSystemService` (Per-Workspace)

Responsibilities:

- Determine and persist the active target system for a workspace:
  - Local (default)
  - WSL distro(s)
  - SSH profile(s)
- Populate the **Target System** combo UI (replacing `TemporaryTargetSystemStore`).
- Persist selection via existing workspace-scoped settings (`ISettingsService`).
- Raise events when target system changes.

### `IExecutionContext` (The Key Seam)

Each target system provides an `IExecutionContext` that encapsulates all remote operations.

### `IPathMapper` (Path Translation Service)

Dedicated service for path translation between VS-visible paths and remote paths.

### `IRustAnalyzerLocator` (Remote RA Discovery)

Service to locate or install rust-analyzer for the target system.

---

## Interface Contracts

### `ITargetSystemService`

```csharp
public interface ITargetSystemService
{
    /// <summary>Current target for the workspace.</summary>
    ITargetSystem CurrentTarget { get; }

    /// <summary>All available targets (Local + WSL distros + SSH profiles).</summary>
    IReadOnlyList<ITargetSystem> AvailableTargets { get; }

    /// <summary>Raised when current target changes. Consumers should refresh state.</summary>
    event EventHandler<TargetChangedEventArgs> TargetChanged;

    /// <summary>Change the active target system.</summary>
    Task SetCurrentTargetAsync(ITargetSystem target, CancellationToken ct);

    /// <summary>Refresh available targets (re-enumerate WSL distros, SSH profiles).</summary>
    Task RefreshAvailableTargetsAsync(CancellationToken ct);

    /// <summary>Auto-detect appropriate target for a workspace path.</summary>
    ITargetSystem DetectTargetForWorkspace(PathEx workspacePath);
}

public interface ITargetSystem
{
    string Id { get; }           // e.g., "local", "wsl:Ubuntu", "ssh:myserver"
    string DisplayName { get; }  // e.g., "Local Machine", "WSL: Ubuntu", "SSH: myserver"
    TargetKind Kind { get; }

    /// <summary>Get execution context for this target.</summary>
    IExecutionContext GetExecutionContext();

    /// <summary>Get path mapper for this target.</summary>
    IPathMapper GetPathMapper();
}

public class TargetChangedEventArgs : EventArgs
{
    public ITargetSystem OldTarget { get; }
    public ITargetSystem NewTarget { get; }
}
```

### `IExecutionContext`

```csharp
public interface IExecutionContext
{
    TargetKind Kind { get; }

    /// <summary>
    /// Execute a command in the target environment.
    /// </summary>
    /// <param name="command">Command to execute (e.g., "cargo")</param>
    /// <param name="arguments">Command arguments</param>
    /// <param name="workingDirectory">Working directory on target system</param>
    /// <param name="environment">Environment variables to set</param>
    /// <param name="outputSink">Sink for stdout/stderr streaming</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Process result with exit code and captured output</returns>
    Task<ProcessResult> ExecuteAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        IDictionary<string, string> environment,
        IProcessOutputSink outputSink,
        CancellationToken ct);

    /// <summary>
    /// Execute a command and return output lines (for simple queries).
    /// </summary>
    Task<string[]> ExecuteAndCaptureAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        CancellationToken ct);

    /// <summary>
    /// Check if a file exists on the target system.
    /// </summary>
    Task<bool> FileExistsAsync(RemotePath path, CancellationToken ct);

    /// <summary>
    /// Check if a directory exists on the target system.
    /// </summary>
    Task<bool> DirectoryExistsAsync(RemotePath path, CancellationToken ct);

    /// <summary>
    /// Read file contents from target system.
    /// </summary>
    Task<string> ReadFileAsync(RemotePath path, CancellationToken ct);

    /// <summary>
    /// Get the path to rust-analyzer on the target system.
    /// </summary>
    Task<RemotePath> GetRustAnalyzerPathAsync(CancellationToken ct);

    /// <summary>
    /// Start rust-analyzer process and return streams for LSP communication.
    /// </summary>
    Task<(Stream Input, Stream Output)> StartRustAnalyzerAsync(
        RemotePath workingDirectory,
        CancellationToken ct);

    /// <summary>
    /// Capabilities of this execution context.
    /// </summary>
    ExecutionCapabilities Capabilities { get; }
}

public class ProcessResult
{
    public int ExitCode { get; set; }
    public IReadOnlyList<string> StandardOutput { get; set; }
    public IReadOnlyList<string> StandardError { get; set; }
    public TimeSpan Duration { get; set; }
}

[Flags]
public enum ExecutionCapabilities
{
    None = 0,
    CanBuild = 1,
    CanDebug = 2,
    CanRunLsp = 4,
    CanRunTests = 8,
    CanOpenRemoteFolder = 16,
    All = CanBuild | CanDebug | CanRunLsp | CanRunTests | CanOpenRemoteFolder
}
```

### `IPathMapper`

```csharp
public interface IPathMapper
{
    TargetKind Kind { get; }

    /// <summary>
    /// Convert a VS-visible path (Windows/UNC) to a remote path.
    /// Example: \\wsl$\Ubuntu\home\user\proj → /home/user/proj
    /// </summary>
    RemotePath MapToRemote(PathEx vsPath);

    /// <summary>
    /// Convert a remote path to a VS-visible path (Windows/UNC).
    /// Example: /home/user/proj → \\wsl$\Ubuntu\home\user\proj
    /// </summary>
    PathEx MapToLocal(RemotePath remotePath);

    /// <summary>
    /// Convert a VS file URI to a remote URI for LSP.
    /// Example: file:///\\wsl$\Ubuntu\home\user\proj\src\main.rs → file:///home/user/proj/src/main.rs
    /// </summary>
    Uri MapUriToRemote(Uri vsUri);

    /// <summary>
    /// Convert a remote URI to a VS-visible URI for LSP.
    /// Example: file:///home/user/proj/src/main.rs → file:///\\wsl$\Ubuntu\home\user\proj\src\main.rs
    /// </summary>
    Uri MapUriToLocal(Uri remoteUri);

    /// <summary>
    /// Check if a path is for this target system.
    /// </summary>
    bool IsPathForTarget(string path);
}
```

### `IProcessOutputSink`

```csharp
public interface IProcessOutputSink
{
    void OnStdout(string line);
    void OnStderr(string line);
    void OnProcessStarted(int? processId);
    void OnProcessExited(int exitCode);
}
```

---

## WSL Plan (Phased)

WSL should be implemented first because VS already supports opening `\\wsl$` folders and the extension roadmap mentions WSL2.

### Phase W0 — Infrastructure Foundation (NEW)

Before any WSL functionality, create the foundational abstractions.

**Deliverables:**

1. `RemotePath` struct (as defined above)
2. `IPathMapper` interface and `WslPathMapper` implementation
3. `IExecutionContext` interface and `LocalExecutionContext` implementation (refactored from current code)
4. `ITargetSystemService` interface and base implementation
5. Refactor `ToolchainServiceExtensions.GetCommandOutput()` to use `IExecutionContext`
6. Add feature flags in `Options`:
   - `EnableWslSupport` (default: false during development)
   - `EnableSshSupport` (default: false during development)

**WslPathMapper Implementation:**

```csharp
public class WslPathMapper : IPathMapper
{
    private readonly string _distroName;
    private readonly string _uncPrefix;

    public WslPathMapper(string distroName)
    {
        _distroName = distroName;
        _uncPrefix = $@"\\wsl$\{distroName}";
    }

    public TargetKind Kind => TargetKind.Wsl;

    public RemotePath MapToRemote(PathEx vsPath)
    {
        string path = vsPath;
        if (path.StartsWith(_uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var linuxPath = path.Substring(_uncPrefix.Length).Replace(@"\", "/");
            return new RemotePath(linuxPath, TargetKind.Wsl);
        }
        throw new ArgumentException($"Path '{path}' is not a WSL path for distro '{_distroName}'");
    }

    public PathEx MapToLocal(RemotePath remotePath)
    {
        if (remotePath.Kind != TargetKind.Wsl)
            throw new ArgumentException("Expected WSL path");

        return (PathEx)$"{_uncPrefix}{((string)remotePath).Replace("/", @"\")}";
    }

    public Uri MapUriToRemote(Uri vsUri)
    {
        if (vsUri.Scheme != "file")
            return vsUri;

        var localPath = vsUri.LocalPath;
        if (localPath.StartsWith(_uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var linuxPath = localPath.Substring(_uncPrefix.Length).Replace(@"\", "/");
            return new Uri($"file://{linuxPath}");
        }
        return vsUri;
    }

    public Uri MapUriToLocal(Uri remoteUri)
    {
        if (remoteUri.Scheme != "file")
            return remoteUri;

        var path = remoteUri.AbsolutePath;
        if (path.StartsWith("/") && !path.StartsWith("//"))
        {
            var windowsPath = $"{_uncPrefix}{path.Replace("/", @"\")}";
            return new Uri($"file:///{windowsPath}");
        }
        return remoteUri;
    }

    public bool IsPathForTarget(string path)
    {
        return path.StartsWith(_uncPrefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/");
    }
}
```

**Success Criteria:**
- [ ] `RemotePath` type created with unit tests
- [ ] `WslPathMapper` created with unit tests covering all path variants
- [ ] `LocalExecutionContext` works identically to current behavior
- [ ] Feature flags wired into Options UI
- [ ] No regressions in local Windows functionality

---

### Phase W1 — Build/Clean/Fmt/Clippy in WSL

**Target Selection:**

- Auto-detect WSL workspace roots: `workspaceContext.Location` begins with `\\wsl$\`.
- Default the target system to the corresponding distro; allow user override via combo.
- Parse distro name from path: `\\wsl$\{distroName}\...`

**WslExecutionContext Implementation:**

```csharp
public class WslExecutionContext : IExecutionContext
{
    private readonly string _distroName;
    private readonly IPathMapper _pathMapper;

    public TargetKind Kind => TargetKind.Wsl;

    public async Task<ProcessResult> ExecuteAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        IDictionary<string, string> environment,
        IProcessOutputSink outputSink,
        CancellationToken ct)
    {
        // Build wsl.exe command line
        var wslArgs = new List<string>
        {
            "-d", _distroName,
            "--cd", (string)workingDirectory,
            "--"
        };

        // Add environment variables as env command prefix if needed
        if (environment?.Any() == true)
        {
            wslArgs.Add("env");
            foreach (var kv in environment)
            {
                wslArgs.Add($"{kv.Key}={kv.Value}");
            }
        }

        wslArgs.Add(command);
        wslArgs.AddRange(arguments);

        using var proc = ProcessRunner.Run(
            "wsl.exe",
            wslArgs.ToArray(),
            workingDirectory: null,  // Not used for wsl.exe
            env: null,
            cancellationToken: ct);

        outputSink?.OnProcessStarted(proc.ProcessId);

        // Stream output
        // ... implementation details ...

        var exitCode = await proc;
        outputSink?.OnProcessExited(exitCode);

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = proc.StandardOutputLines.ToList(),
            StandardError = proc.StandardErrorLines.ToList()
        };
    }

    public async Task<RemotePath> GetRustAnalyzerPathAsync(CancellationToken ct)
    {
        // Try to find rust-analyzer in WSL
        var result = await ExecuteAndCaptureAsync(
            "which", new[] { "rust-analyzer" },
            new RemotePath("/", TargetKind.Wsl), ct);

        if (result.Length > 0 && !string.IsNullOrWhiteSpace(result[0]))
        {
            return new RemotePath(result[0].Trim(), TargetKind.Wsl);
        }

        // Fall back to ~/.cargo/bin/rust-analyzer
        var homeResult = await ExecuteAndCaptureAsync(
            "sh", new[] { "-c", "echo $HOME" },
            new RemotePath("/", TargetKind.Wsl), ct);

        if (homeResult.Length > 0)
        {
            var raPath = $"{homeResult[0].Trim()}/.cargo/bin/rust-analyzer";
            if (await FileExistsAsync(new RemotePath(raPath, TargetKind.Wsl), ct))
            {
                return new RemotePath(raPath, TargetKind.Wsl);
            }
        }

        throw new FileNotFoundException(
            "rust-analyzer not found in WSL. Install it with: rustup component add rust-analyzer");
    }
}
```

**Diagnostics Path Mapping:**

- Cargo JSON output contains:
  - Absolute Linux paths (`/home/...`)
  - Relative paths (`src/main.rs`)
- Update `BuildJsonOutputParser.CreateBuildMessage()`:

```csharp
private static DetailedBuildMessage CreateBuildMessage(
    PathEx workspaceRoot,
    IPathMapper pathMapper,  // NEW PARAMETER
    dynamic obj,
    dynamic fileInfo = null,
    dynamic lineInfo = null,
    dynamic colInfo = null)
{
    var msg = new DetailedBuildMessage { /* ... */ };

    // Handle file path - may be Linux path from cargo
    string filePath = fileInfo?.Value ?? obj.target.src_path.Value;

    if (pathMapper != null && pathMapper.Kind != TargetKind.Local)
    {
        // Convert Linux path to VS-visible path
        if (filePath.StartsWith("/"))
        {
            var remotePath = new RemotePath(filePath, pathMapper.Kind);
            msg.File = (string)pathMapper.MapToLocal(remotePath);
        }
        else
        {
            // Relative path - combine with workspace root
            var remoteWorkspace = pathMapper.MapToRemote(workspaceRoot);
            var fullRemotePath = remoteWorkspace.Combine(filePath);
            msg.File = (string)pathMapper.MapToLocal(fullRemotePath);
        }
    }
    else
    {
        msg.File = Path.Combine(workspaceRoot, filePath);
    }

    // ... rest of method
}
```

**Prerequisites Updates:**

- In WSL mode, prereq checks should validate:
  - `wsl.exe` availability on Windows
  - Selected distro exists and is running
  - `cargo` and `rustc` exist within the selected distro
  - `rust-analyzer` exists within the selected distro

```csharp
private static async Task<(bool Success, string Message)> CheckWslPrerequisitesAsync(
    WslExecutionContext ctx,
    CancellationToken ct)
{
    // Check wsl.exe exists
    if (!File.Exists(@"C:\Windows\System32\wsl.exe"))
    {
        return (false, "WSL is not installed. Install WSL from Microsoft Store or run 'wsl --install'.");
    }

    // Check distro is running
    try
    {
        var result = await ctx.ExecuteAndCaptureAsync(
            "echo", new[] { "test" },
            new RemotePath("/", TargetKind.Wsl), ct);
    }
    catch (Exception ex)
    {
        return (false, $"WSL distro is not accessible: {ex.Message}");
    }

    // Check cargo exists in WSL
    var cargoResult = await ctx.ExecuteAndCaptureAsync(
        "which", new[] { "cargo" },
        new RemotePath("/", TargetKind.Wsl), ct);

    if (cargoResult.Length == 0 || string.IsNullOrWhiteSpace(cargoResult[0]))
    {
        return (false, "Cargo not found in WSL. Install Rust with: curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh");
    }

    return (true, string.Empty);
}
```

**Success Criteria:**
- [ ] Open a WSL folder in VS (`\\wsl$\Ubuntu\...`)
- [ ] Build/Clean/Fmt/Clippy execute in WSL via `wsl.exe`
- [ ] Build errors appear in Error List with correct file paths
- [ ] Double-clicking errors navigates to correct source location
- [ ] Output window shows cargo output correctly

---

### Phase W1.5 — Test Adapter for WSL (NEW)

**Problem:** Test binaries compiled in WSL are ELF format, not Windows PE. They cannot run directly on Windows.

**Test Discovery Updates:**

```csharp
public class TestDiscoverer : BaseTestDiscoverer, ITestDiscoverer
{
    public override void DiscoverTests(
        IEnumerable<PathEx> sources,
        IDiscoveryContext discoveryContext,
        IMessageLogger logger,
        ITestCaseDiscoverySink discoverySink)
    {
        var tl = logger.CreateTL();

        // Get execution context for each source
        var tasks = sources
            .GroupBy(s => s)
            .Select(async g =>
            {
                var tc = await g.Key.ReadTestContainerAsync(default);
                var targetService = GetTargetSystemService(tc);
                var ctx = targetService.CurrentTarget.GetExecutionContext();
                var pathMapper = targetService.CurrentTarget.GetPathMapper();

                await DiscoverAndReportTestsFromOneSource(tc, ctx, pathMapper, discoverySink, tl, default);
            });

        Task.WaitAll(tasks.ToArray());
    }
}
```

**Test Execution Updates:**

```csharp
private static async Task RunTestsFromOneExe(
    RemotePath exe,           // Changed from PathEx
    IExecutionContext ctx,    // NEW
    IPathMapper pathMapper,   // NEW
    string[] args,
    IReadOnlyDictionary<string, TestCase> testCasesMap,
    IDictionary<string, string> envDict,
    TL tl,
    bool isBeingDebugged,
    IFrameworkHandle fh,
    CancellationToken ct)
{
    if (isBeingDebugged && ctx.Kind != TargetKind.Local)
    {
        // Debugging remote tests requires special handling (Phase W3)
        tl.L.WriteError("Debugging tests in WSL/SSH is not yet supported.");
        return;
    }

    if (ctx.Kind == TargetKind.Local)
    {
        // Existing local execution path
        using var testExeProc = await ProcessRunner.RunWithLogging(
            (PathEx)(string)exe, args,
            ((PathEx)(string)exe).GetDirectoryName(),
            envDict, ct, tl.L, @throw: false);
        // ... process results
    }
    else
    {
        // Remote execution via context
        var result = await ctx.ExecuteAsync(
            (string)exe,
            args,
            exe.GetDirectoryName(),
            envDict,
            null,  // No streaming needed for test execution
            ct);

        // Parse results - paths need mapping
        var trs = result.StandardOutput
            .Skip(1)
            .Take(result.StandardOutput.Count - 2)
            .Select(JsonConvert.DeserializeObject<TestRunInfo>)
            .Where(x => x.Event != TestRunInfo.EventType.Started)
            .Select(x => ToTestResult(exe, x, testCasesMap, pathMapper));

        foreach (var tr in trs)
        {
            fh.RecordResult(tr);
        }
    }
}
```

**Success Criteria:**
- [ ] Tests discovered from WSL projects appear in Test Explorer
- [ ] Running tests executes via WSL and reports results correctly
- [ ] Test failure locations link to correct source files

---

### Phase W2 — rust-analyzer in WSL (LSP over stdio) with URI Rewriting

**Problem:**

- VS sends LSP `file://` URIs pointing at Windows/UNC paths.
- rust-analyzer in WSL expects Linux paths.

**LSP Middle Layer Implementation:**

```csharp
public class RemotePathMiddleLayer : ILanguageClientMiddleLayer
{
    private readonly IPathMapper _pathMapper;

    public RemotePathMiddleLayer(IPathMapper pathMapper)
    {
        _pathMapper = pathMapper;
    }

    public bool CanHandle(string methodName)
    {
        // Handle all methods that contain document URIs
        return _methodsWithUris.Contains(methodName);
    }

    private static readonly HashSet<string> _methodsWithUris = new()
    {
        // Outgoing (VS → RA) - need to convert local → remote
        "textDocument/didOpen",
        "textDocument/didChange",
        "textDocument/didClose",
        "textDocument/didSave",
        "textDocument/completion",
        "textDocument/hover",
        "textDocument/definition",
        "textDocument/typeDefinition",
        "textDocument/implementation",
        "textDocument/references",
        "textDocument/documentHighlight",
        "textDocument/documentSymbol",
        "textDocument/codeAction",
        "textDocument/codeLens",
        "textDocument/formatting",
        "textDocument/rangeFormatting",
        "textDocument/rename",
        "textDocument/signatureHelp",

        // Incoming (RA → VS) - need to convert remote → local
        "textDocument/publishDiagnostics",
        "workspace/applyEdit",
    };

    public async Task<JToken> HandleRequestAsync(
        string methodName,
        JToken methodParam,
        Func<JToken, Task<JToken>> sendRequest)
    {
        // Rewrite outgoing URIs
        var rewrittenParam = RewriteUrisInToken(methodParam, toRemote: true);

        // Send request
        var response = await sendRequest(rewrittenParam);

        // Rewrite incoming URIs in response
        return RewriteUrisInToken(response, toRemote: false);
    }

    public async Task HandleNotificationAsync(
        string methodName,
        JToken methodParam,
        Func<JToken, Task> sendNotification)
    {
        var rewrittenParam = RewriteUrisInToken(methodParam, toRemote: true);
        await sendNotification(rewrittenParam);
    }

    private JToken RewriteUrisInToken(JToken token, bool toRemote)
    {
        if (token == null) return null;

        switch (token.Type)
        {
            case JTokenType.Object:
                var obj = (JObject)token.DeepClone();
                foreach (var prop in obj.Properties().ToList())
                {
                    if (prop.Name == "uri" || prop.Name == "targetUri" || prop.Name == "originSelectionRange")
                    {
                        if (prop.Value.Type == JTokenType.String)
                        {
                            var uri = new Uri(prop.Value.ToString());
                            var mappedUri = toRemote
                                ? _pathMapper.MapUriToRemote(uri)
                                : _pathMapper.MapUriToLocal(uri);
                            prop.Value = mappedUri.ToString();
                        }
                    }
                    else
                    {
                        prop.Value = RewriteUrisInToken(prop.Value, toRemote);
                    }
                }
                return obj;

            case JTokenType.Array:
                var arr = new JArray();
                foreach (var item in (JArray)token)
                {
                    arr.Add(RewriteUrisInToken(item, toRemote));
                }
                return arr;

            default:
                return token;
        }
    }
}
```

**Updated LanguageClient:**

```csharp
[ContentType(Constants.RustLanguageContentType)]
[Export(typeof(ILanguageClient))]
[RunOnContext(RunningContext.RunOnHost)]
public class LanguageClient : ILanguageClient, ILanguageClientCustomMessage2
{
    [Import]
    public ITargetSystemService TargetSystemService { get; set; }

    private ILanguageClientMiddleLayer _middleLayer;

    public object MiddleLayer => _middleLayer;

    public async Task<Connection> ActivateAsync(CancellationToken token)
    {
        var target = TargetSystemService.CurrentTarget;
        var ctx = target.GetExecutionContext();

        if (ctx.Kind == TargetKind.Local)
        {
            // Existing local behavior
            _middleLayer = null;
            var rlsPath = await RADownloader.GetExePathAsync();
            // ... start local process
        }
        else
        {
            // Remote behavior
            var pathMapper = target.GetPathMapper();
            _middleLayer = new RemotePathMiddleLayer(pathMapper);

            var remoteWorkspace = pathMapper.MapToRemote(
                (PathEx)WorkspaceService.CurrentWorkspace.Location);

            var (input, output) = await ctx.StartRustAnalyzerAsync(remoteWorkspace, token);

            return new Connection(output, input);
        }
    }
}
```

**Success Criteria:**
- [ ] IntelliSense (completion, hover) works for WSL workspaces
- [ ] Go-to-definition navigates to correct files
- [ ] Diagnostics appear in Error List with correct locations
- [ ] Find All References returns results with correct paths
- [ ] Code Actions work correctly

---

### Phase W3 — Debugging in WSL (MI/gdbserver)

Current debug code is Windows-native-only; WSL debug must use a different integration.

**Approach:**

1. Build produces an ELF binary in WSL
2. Start `gdbserver` inside WSL listening on a port or stdio
3. Attach using VS's MIEngine debug adapter

**Required Spikes (Before Implementation):**

- [ ] Spike 1: Determine correct debug engine GUID for Linux/MI debugging in Open Folder contexts
- [ ] Spike 2: Verify MIEngine configuration options for WSL
- [ ] Spike 3: Test source mapping behavior with UNC paths

**Debug Launch Provider Updates:**

```csharp
[ExportLaunchDebugTarget(...)]
public sealed class DebugLaunchTargetProvider : ILaunchDebugTargetProvider
{
    [Import]
    public ITargetSystemService TargetSystemService { get; set; }

    public void LaunchDebugTarget(
        IWorkspace workspaceContext,
        IServiceProvider serviceProvider,
        DebugLaunchActionContext debugLaunchActionContext)
    {
        var target = TargetSystemService.CurrentTarget;

        if (target.Kind == TargetKind.Local)
        {
            LaunchLocalDebugTarget(workspaceContext, serviceProvider, debugLaunchActionContext);
        }
        else if (target.Kind == TargetKind.Wsl)
        {
            LaunchWslDebugTarget(workspaceContext, serviceProvider, debugLaunchActionContext);
        }
    }

    private void LaunchWslDebugTarget(
        IWorkspace workspaceContext,
        IServiceProvider serviceProvider,
        DebugLaunchActionContext ctx)
    {
        var pathMapper = TargetSystemService.CurrentTarget.GetPathMapper();
        var lcw = new LaunchConfigWrapper(ctx.LaunchConfiguration, _tl);

        // Get the Linux path to the executable
        var windowsExePath = (PathEx)lcw[LaunchConfigurationConstants.ProgramKey];
        var linuxExePath = pathMapper.MapToRemote(windowsExePath);

        var info = new VsDebugTargetInfo4
        {
            dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
            bstrExe = (string)linuxExePath,
            bstrCurDir = (string)linuxExePath.GetDirectoryName(),
            bstrOptions = CreateMIDebuggerOptions(linuxExePath),
            guidLaunchDebugEngine = MIDebugEngineGuid,  // Need to determine correct GUID
            LaunchFlags = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_Silent,
        };

        // Configure source mapping
        // Maps Linux paths back to UNC paths for VS
        // ...

        // Launch
        VsShellUtilities.LaunchDebugger(serviceProvider, info);
    }

    private string CreateMIDebuggerOptions(RemotePath exePath)
    {
        // Create MI debugger options for WSL debugging
        // This might involve:
        // - Setting up gdbserver in WSL
        // - Configuring source mappings
        // - Setting working directory

        return $@"
            <LocalLaunchOptions xmlns=""http://schemas.microsoft.com/vstudio/MDDDebuggerOptions/2014""
                MIDebuggerPath=""wsl.exe""
                MIDebuggerArgs=""-d {_distroName} -- gdb""
                ExePath=""{exePath}""
                WorkingDirectory=""{exePath.GetDirectoryName()}"">
                <SourceMap>
                    <SourceMapEntry LocalPath=""{_uncPrefix}"" RemotePath=""/""/>
                </SourceMap>
            </LocalLaunchOptions>";
    }
}
```

**Success Criteria:**
- [ ] F5 starts debugging a WSL-built Rust binary
- [ ] Breakpoints hit correctly
- [ ] Variables/watch expressions work
- [ ] Stack traces show correct source locations (UNC paths)
- [ ] Step debugging (F10/F11) works correctly

---

## SSH Plan (Two Approaches)

SSH must solve "open remote folder" and "remote toolchain + remote debug + remote LSP".
There are two approaches; pick based on desired UX and available supported VS APIs.

### Pre-Implementation Spike Required

Before choosing between S1 and S2, investigate:

1. **Does `Microsoft.VisualStudio.Linux.ConnectionManager` provide usable APIs?**
   - Check if it exposes SSH connection management
   - Check if it provides file system access

2. **Does VS have a remote workspace filesystem provider API?**
   - Research VS extensibility for remote "Open Folder"
   - Check if `IVsHierarchy` can be implemented for remote files

### Option S1 — Integrate with VS-supported Remote Workspace Filesystem (Preferred if Supported)

If Visual Studio exposes a supported API for remote Open Folder workspaces:

- Implement or plug into a remote filesystem provider so VS can open `ssh://host/path` as a workspace.
- Use `SshExecutionContext` to:
  - Run cargo/rust-analyzer/debug on the remote host
  - Translate paths/URIs between remote files and VS workspace items

**Pros:**

- True remote open-folder UX; minimal duplication/caching logic
- Seamless integration with VS features

**Cons:**

- Depends on availability/supportability of the VS remote filesystem APIs for extensions

### Option S2 — Local Cache + Sync (Always Feasible)

If VS does not provide a supported remote filesystem surface:

**Architecture:**

```
┌─────────────────────────────────────────────────────────┐
│                     Visual Studio                        │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐  │
│  │   Editor    │    │  Error List │    │   Debugger  │  │
│  └──────┬──────┘    └──────┬──────┘    └──────┬──────┘  │
│         │                  │                  │          │
│         └────────────┬─────┴──────────────────┘          │
│                      ▼                                   │
│              ┌───────────────┐                           │
│              │  Path Mapper  │                           │
│              │ (cache ↔ SSH) │                           │
│              └───────┬───────┘                           │
│                      │                                   │
│         ┌────────────┴────────────┐                     │
│         ▼                         ▼                     │
│  ┌─────────────┐          ┌─────────────┐              │
│  │ Local Cache │          │ SSH Context │              │
│  │ (file ops)  │          │ (execution) │              │
│  └──────┬──────┘          └──────┬──────┘              │
│         │                        │                      │
└─────────┼────────────────────────┼──────────────────────┘
          │                        │
          │    ┌─────────────┐     │
          └───►│  File Sync  │◄────┘
               │   Service   │
               └──────┬──────┘
                      │ SFTP
                      ▼
              ┌───────────────┐
              │  Remote Host  │
              │  (SSH/SFTP)   │
              └───────────────┘
```

**Implementation:**

```csharp
public interface ISshFileSyncService
{
    /// <summary>Initialize cache for a remote folder.</summary>
    Task<PathEx> InitializeCacheAsync(
        string sshHost,
        RemotePath remoteFolder,
        CancellationToken ct);

    /// <summary>Upload local changes to remote.</summary>
    Task UploadAsync(PathEx localFile, CancellationToken ct);

    /// <summary>Download remote changes to local.</summary>
    Task DownloadAsync(RemotePath remoteFile, CancellationToken ct);

    /// <summary>Sync entire folder (for refresh).</summary>
    Task SyncFolderAsync(SyncDirection direction, CancellationToken ct);

    /// <summary>Watch for local file changes and auto-upload.</summary>
    IDisposable WatchLocalChanges();
}

public enum SyncDirection { LocalToRemote, RemoteToLocal, Bidirectional }
```

**User Flow:**

1. User invokes **"Open SSH Folder…"** command
2. SSH connection dialog appears:
   - Host/port selection (from saved profiles or new entry)
   - Authentication (key or password)
   - Remote folder path browser
3. Extension creates local cache: `%LOCALAPPDATA%\rust-analyzer.vs\ssh-cache\{host}\{path-hash}\`
4. Initial sync: SFTP download of remote files to local cache
5. VS opens the **local cache** as a standard workspace
6. File watcher uploads changes on save
7. Build/LSP/debug execute on remote via SSH

**SshPathMapper:**

```csharp
public class SshPathMapper : IPathMapper
{
    private readonly string _sshHost;
    private readonly RemotePath _remoteRoot;
    private readonly PathEx _localCacheRoot;

    public TargetKind Kind => TargetKind.Ssh;

    public RemotePath MapToRemote(PathEx vsPath)
    {
        if (!((string)vsPath).StartsWith((string)_localCacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Path is not in SSH cache: {vsPath}");
        }

        var relativePath = ((string)vsPath)
            .Substring(((string)_localCacheRoot).Length)
            .Replace(@"\", "/");

        return _remoteRoot.Combine(relativePath);
    }

    public PathEx MapToLocal(RemotePath remotePath)
    {
        var remoteStr = (string)remotePath;
        var rootStr = (string)_remoteRoot;

        if (!remoteStr.StartsWith(rootStr))
        {
            throw new ArgumentException($"Path is not under remote root: {remotePath}");
        }

        var relativePath = remoteStr.Substring(rootStr.Length);
        return _localCacheRoot.Combine((PathEx)relativePath.Replace("/", @"\"));
    }
}
```

**SSH Profile Management:**

```csharp
public interface ISshProfileService
{
    IReadOnlyList<SshProfile> GetProfiles();
    Task<SshProfile> CreateProfileAsync(string name, SshConnectionInfo info);
    Task DeleteProfileAsync(string profileId);
    Task<bool> TestConnectionAsync(SshProfile profile, CancellationToken ct);
}

public class SshProfile
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Host { get; set; }
    public int Port { get; set; } = 22;
    public string Username { get; set; }
    public SshAuthMethod AuthMethod { get; set; }
    public string PrivateKeyPath { get; set; }  // For key auth
}

public enum SshAuthMethod { Password, PrivateKey, Agent }
```

**Pros:**

- Fully under extension control; compatible with existing code's local file assumptions
- Works with any VS version

**Cons:**

- Must implement sync correctness, perf optimizations, and conflict UX
- Additional latency for file operations
- Disk space usage for cache

---

## Refactors Required (Surgical, Not a Rewrite)

### 1. Stop Embedding Windows-Only Assumptions into Shared Logic

- Keep `PathEx` for Windows paths (DO NOT MODIFY)
- Create `RemotePath` for Linux/remote paths
- Add `IPathMapper` at system boundaries
- All path creation must go through appropriate type

### 2. Route Tool Execution Through `IExecutionContext`

Replace all direct process execution:

| Current Code | New Code |
|--------------|----------|
| `ProcessRunner.Run("cmd.exe", ...)` | `context.ExecuteAsync(command, args, ...)` |
| `ProcessRunner.RunWithLogging(...)` | `context.ExecuteAsync(...) + logging wrapper` |
| `ToolchainServiceExtensions.GetCommandOutput(...)` | `context.ExecuteAndCaptureAsync(...)` |

### 3. Make Diagnostics Parsing Path-Aware

Update `BuildJsonOutputParser`:

```csharp
// Before
public static BuildMessage[] Parse(PathEx workspaceRoot, string jsonLine, TL tl)

// After
public static BuildMessage[] Parse(
    PathEx workspaceRoot,
    IPathMapper pathMapper,  // NEW
    string jsonLine,
    TL tl)
```

### 4. Make Debug Target Generation Target-Aware

`FileScanner` currently emits launch settings for `.exe` targets:

```csharp
// Add target-aware launch settings
var launchSettings = new PropertySettings
{
    [LaunchConfigurationConstants.NameKey] = target.QualifiedTargetFileName,
    [LaunchConfigurationConstants.DebugTypeKey] = GetDebugType(targetKind),  // NEW
    ["TargetKind"] = targetKind.ToString(),  // NEW - for debug provider routing
    // ...
};

private static string GetDebugType(TargetKind kind) => kind switch
{
    TargetKind.Local => LaunchConfigurationConstants.NativeOptionKey,
    TargetKind.Wsl => "MIEngine",  // or appropriate value
    TargetKind.Ssh => "MIEngine",
    _ => LaunchConfigurationConstants.NativeOptionKey
};
```

### 5. Update Test Containers for Remote

`TestContainer` needs target system information:

```csharp
public sealed class TestContainer
{
    // ... existing properties ...

    // NEW
    public TargetKind TargetKind { get; set; }
    public string TargetId { get; set; }  // e.g., "wsl:Ubuntu"
}
```

### 6. LSP URI Rewriting (Covered in W2)

Use `ILanguageClientMiddleLayer` to translate URIs for WSL and SSH.

---

## Error Handling Strategy

### Error Categories

```csharp
public enum RemoteErrorCategory
{
    ConnectionFailed,      // Can't reach target (WSL not running, SSH unreachable)
    AuthenticationFailed,  // SSH auth failed
    ToolNotFound,          // cargo/rustc/rust-analyzer not installed on target
    PathMappingFailed,     // Can't map path between local and remote
    ExecutionFailed,       // Command ran but failed
    Timeout,               // Operation timed out
    Cancelled,             // User cancelled
    Unknown                // Unexpected error
}

public class RemoteException : Exception
{
    public RemoteErrorCategory Category { get; }
    public TargetKind TargetKind { get; }
    public string TargetId { get; }

    public RemoteException(
        RemoteErrorCategory category,
        TargetKind kind,
        string targetId,
        string message,
        Exception innerException = null)
        : base(message, innerException)
    {
        Category = category;
        TargetKind = kind;
        TargetId = targetId;
    }
}
```

### Error Handling by Category

#### Connection Failures

```csharp
// WSL not running
catch (RemoteException ex) when (ex.Category == RemoteErrorCategory.ConnectionFailed && ex.TargetKind == TargetKind.Wsl)
{
    await VsCommon.ShowMessageBoxAsync(
        $"Cannot connect to WSL distro '{ex.TargetId}'.",
        "Make sure WSL is running. Try 'wsl --list --running' in a terminal.\n\n" +
        "Press OK to switch to Local target.");

    await TargetSystemService.SetCurrentTargetAsync(
        TargetSystemService.AvailableTargets.First(t => t.Kind == TargetKind.Local),
        ct);
}

// SSH unreachable
catch (RemoteException ex) when (ex.Category == RemoteErrorCategory.ConnectionFailed && ex.TargetKind == TargetKind.Ssh)
{
    await VsCommon.ShowMessageBoxAsync(
        $"Cannot connect to SSH host '{ex.TargetId}'.",
        "Check that:\n" +
        "• The host is reachable\n" +
        "• SSH service is running\n" +
        "• Firewall allows connection\n\n" +
        "Press OK to open connection settings.");

    // Open SSH profile editor
}
```

#### Tool Not Found

```csharp
catch (RemoteException ex) when (ex.Category == RemoteErrorCategory.ToolNotFound)
{
    var tool = ex.Data["ToolName"] as string ?? "required tool";

    await VsCommon.ShowMessageBoxAsync(
        $"'{tool}' not found on {ex.TargetKind} target '{ex.TargetId}'.",
        ex.TargetKind == TargetKind.Wsl
            ? $"Install Rust in WSL with:\ncurl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh"
            : $"Install Rust on the remote host with rustup.");
}
```

#### Path Mapping Failures

```csharp
catch (RemoteException ex) when (ex.Category == RemoteErrorCategory.PathMappingFailed)
{
    L.WriteError("Path mapping failed: {0}", ex.Message);
    T.TrackException(ex);

    // Log but don't crash - continue with unmapped path
    // This allows partial functionality even with mapping issues
}
```

### Graceful Degradation

When remote target is unavailable, offer fallback options:

```csharp
public async Task<bool> TryExecuteWithFallbackAsync(
    Func<IExecutionContext, Task<bool>> action,
    CancellationToken ct)
{
    var target = TargetSystemService.CurrentTarget;

    try
    {
        return await action(target.GetExecutionContext());
    }
    catch (RemoteException ex) when (ex.Category == RemoteErrorCategory.ConnectionFailed)
    {
        if (target.Kind != TargetKind.Local)
        {
            var fallback = await VsCommon.ShowMessageBoxAsync(
                $"Remote target '{target.DisplayName}' is unavailable.",
                "Would you like to:\n" +
                "• OK: Retry the operation\n" +
                "• Cancel: Switch to Local target",
                MessageBoxButton.OKCancel);

            if (fallback == MessageBoxResult.OK)
            {
                return await TryExecuteWithFallbackAsync(action, ct);
            }
            else
            {
                await TargetSystemService.SetCurrentTargetAsync(
                    TargetSystemService.AvailableTargets.First(t => t.Kind == TargetKind.Local),
                    ct);
                return false;
            }
        }

        throw;
    }
}
```

### Timeout Handling

```csharp
public async Task<ProcessResult> ExecuteWithTimeoutAsync(
    IExecutionContext ctx,
    string command,
    IEnumerable<string> args,
    RemotePath workingDir,
    TimeSpan timeout,
    CancellationToken ct)
{
    using var timeoutCts = new CancellationTokenSource(timeout);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

    try
    {
        return await ctx.ExecuteAsync(command, args, workingDir, null, null, linkedCts.Token);
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        throw new RemoteException(
            RemoteErrorCategory.Timeout,
            ctx.Kind,
            "", // TODO: get target ID
            $"Operation timed out after {timeout.TotalSeconds} seconds");
    }
}
```

### Telemetry for Remote Errors

```csharp
public void TrackRemoteError(RemoteException ex)
{
    T.TrackException(ex, new[]
    {
        ("Category", ex.Category.ToString()),
        ("TargetKind", ex.TargetKind.ToString()),
        ("TargetId", ex.TargetId),
    });
}
```

---

## Rollout Plan (Risk-Controlled)

### Phase R0 — Target System Plumbing

**Scope:**
- `RemotePath` type with unit tests
- `IPathMapper` interface + `LocalPathMapper` + `WslPathMapper` with unit tests
- `IExecutionContext` interface + `LocalExecutionContext` (refactored from current code)
- `ITargetSystemService` with target enumeration (Local + WSL distro detection)
- Replace `TemporaryTargetSystemStore` with real implementation
- Feature flags in Options

**Success Criteria:**
- [ ] Target System combo shows Local + detected WSL distros
- [ ] Selecting a target persists per-workspace
- [ ] Changing target fires `TargetChanged` event
- [ ] All existing local functionality works identically
- [ ] Unit tests pass for path mapping

### Phase R1 — WSL Build/Clean/Fmt/Clippy + Diagnostics Path Mapping

**Scope:**
- `WslExecutionContext` implementation
- Build/Clean/Fmt/Clippy routed through execution context
- `BuildJsonOutputParser` updated for path mapping
- WSL prerequisites checking

**Success Criteria:**
- [ ] Build WSL project with errors in Error List
- [ ] Error List items navigate to correct UNC paths
- [ ] Output window shows cargo output
- [ ] Clean/Fmt/Clippy work correctly

### Phase R1.5 — WSL Test Adapter

**Scope:**
- Test discovery via WSL
- Test execution via WSL
- Test result path mapping

**Success Criteria:**
- [ ] Tests appear in Test Explorer
- [ ] Running tests works
- [ ] Test failures link to correct source

### Phase R2 — WSL rust-analyzer + LSP URI Rewriting

**Scope:**
- `RemotePathMiddleLayer` for LSP
- rust-analyzer discovery in WSL
- `LanguageClient` updates for remote

**Success Criteria:**
- [ ] IntelliSense works for WSL projects
- [ ] Go-to-definition works
- [ ] Diagnostics appear correctly

### Phase R3 — WSL Debug (Spike First)

**Scope:**
- Spike: MIEngine configuration for WSL
- Spike: Source mapping verification
- Implementation based on spike findings

**Success Criteria:**
- [ ] F5 launches debugger
- [ ] Breakpoints work
- [ ] Source navigation works

### Phase R4 — SSH "Open Folder" Model + Build Support

**Scope:**
- Decide S1 vs S2 based on spike results
- SSH connection management UI
- `SshExecutionContext` implementation
- File sync (if S2)
- Build support

**Success Criteria:**
- [ ] Can open SSH folder
- [ ] Build works on remote
- [ ] Errors navigate correctly

### Phase R5 — SSH rust-analyzer + Debug + Polish

**Scope:**
- SSH LSP support
- SSH debugging (gdbserver)
- Performance optimization
- Documentation

**Success Criteria:**
- [ ] Full feature parity with WSL
- [ ] Acceptable latency for common operations
- [ ] User documentation complete

---

## Testing Strategy

### Unit Tests

#### Path Mapping Tests

```csharp
public class WslPathMapperTests
{
    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\user\proj", "/home/user/proj")]
    [InlineData(@"\\wsl$\Ubuntu\home\user\proj\src\main.rs", "/home/user/proj/src/main.rs")]
    [InlineData(@"\\wsl$\Ubuntu-20.04\opt\rust", "/opt/rust")]
    public void MapToRemote_ConvertsUncToLinux(string input, string expected)
    {
        var mapper = new WslPathMapper("Ubuntu");
        var result = mapper.MapToRemote((PathEx)input);
        Assert.Equal(expected, (string)result);
    }

    [Theory]
    [InlineData("/home/user/proj", @"\\wsl$\Ubuntu\home\user\proj")]
    [InlineData("/home/user/proj/src/main.rs", @"\\wsl$\Ubuntu\home\user\proj\src\main.rs")]
    public void MapToLocal_ConvertsLinuxToUnc(string input, string expected)
    {
        var mapper = new WslPathMapper("Ubuntu");
        var result = mapper.MapToLocal(new RemotePath(input, TargetKind.Wsl));
        Assert.Equal(expected, (string)result);
    }

    [Theory]
    [InlineData("file:///\\\\wsl$\\Ubuntu\\home\\user\\proj\\src\\main.rs", "file:///home/user/proj/src/main.rs")]
    public void MapUriToRemote_ConvertsFileUri(string input, string expected)
    {
        var mapper = new WslPathMapper("Ubuntu");
        var result = mapper.MapUriToRemote(new Uri(input));
        Assert.Equal(expected, result.ToString());
    }
}
```

#### Cargo JSON Parsing Tests

```csharp
public class BuildJsonOutputParserRemoteTests
{
    [Fact]
    public void Parse_WithWslPathMapper_MapsLinuxPathsToUnc()
    {
        var json = @"{
            ""reason"": ""compiler-message"",
            ""message"": {
                ""message"": ""unused variable"",
                ""level"": ""warning"",
                ""spans"": [{
                    ""file_name"": ""/home/user/proj/src/main.rs"",
                    ""line_start"": 10,
                    ""column_start"": 5
                }]
            },
            ""target"": { ""src_path"": ""/home/user/proj/src/main.rs"" }
        }";

        var mapper = new WslPathMapper("Ubuntu");
        var workspaceRoot = (PathEx)@"\\wsl$\Ubuntu\home\user\proj";

        var messages = BuildJsonOutputParser.Parse(workspaceRoot, mapper, json, new TL());

        Assert.Single(messages);
        var msg = messages[0] as DetailedBuildMessage;
        Assert.Equal(@"\\wsl$\Ubuntu\home\user\proj\src\main.rs", msg.File);
    }
}
```

#### Command Execution Tests

```csharp
public class WslExecutionContextTests
{
    [Fact]
    public async Task ExecuteAsync_BuildsCorrectWslCommand()
    {
        var mockProcessRunner = new Mock<IProcessRunner>();
        var ctx = new WslExecutionContext("Ubuntu", mockProcessRunner.Object);

        await ctx.ExecuteAsync(
            "cargo",
            new[] { "build", "--release" },
            new RemotePath("/home/user/proj", TargetKind.Wsl),
            null, null, CancellationToken.None);

        mockProcessRunner.Verify(p => p.Run(
            "wsl.exe",
            It.Is<string[]>(args =>
                args.Contains("-d") &&
                args.Contains("Ubuntu") &&
                args.Contains("cargo") &&
                args.Contains("build")),
            It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(),
            It.IsAny<CancellationToken>()));
    }
}
```

### Integration Tests

#### WSL Integration Tests (Requires WSL)

```csharp
[Collection("WSL Integration")]
public class WslIntegrationTests : IClassFixture<WslTestFixture>
{
    private readonly WslTestFixture _fixture;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildInWsl_SimpleProject_Succeeds()
    {
        // Setup: Copy test project to WSL
        var projectPath = await _fixture.SetupTestProjectAsync("hello_world");

        // Act: Build via WSL execution context
        var ctx = new WslExecutionContext(_fixture.DistroName);
        var result = await ctx.ExecuteAsync(
            "cargo", new[] { "build" },
            projectPath, null, null, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RustAnalyzerInWsl_RespondsToInitialize()
    {
        var ctx = new WslExecutionContext(_fixture.DistroName);
        var raPath = await ctx.GetRustAnalyzerPathAsync(CancellationToken.None);

        var (input, output) = await ctx.StartRustAnalyzerAsync(
            new RemotePath("/tmp", TargetKind.Wsl),
            CancellationToken.None);

        // Send initialize request
        // Verify response
    }
}
```

### Manual Test Checklist

#### WSL Testing

- [ ] Open `\\wsl$\Ubuntu\...` folder in VS
- [ ] Target System combo shows WSL distro
- [ ] Build project → errors appear in Error List
- [ ] Double-click error → opens correct file
- [ ] IntelliSense works (completion, hover)
- [ ] Go to Definition works
- [ ] Find All References works
- [ ] Tests appear in Test Explorer
- [ ] Run tests → results reported correctly
- [ ] F5 debugging works (after R3)

#### SSH Testing (After R4/R5)

- [ ] Create SSH connection profile
- [ ] Open remote folder via SSH
- [ ] Files sync correctly
- [ ] Build works
- [ ] IntelliSense works
- [ ] Debugging works
- [ ] Latency is acceptable for typical operations

### Performance Benchmarks

| Operation | Local Target | WSL Target | SSH Target (LAN) | SSH Target (WAN) |
|-----------|-------------|------------|------------------|------------------|
| Build (hello_world) | < 2s | < 5s | < 10s | < 30s |
| cargo metadata | < 1s | < 2s | < 5s | < 15s |
| LSP hover | < 100ms | < 300ms | < 500ms | < 2s |
| Go to Definition | < 200ms | < 500ms | < 1s | < 3s |

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| PathEx refactoring breaks existing local functionality | Medium | High | Create separate RemotePath type; don't modify PathEx; extensive regression testing |
| VS remote filesystem API doesn't exist for extensions | Medium | High | Design SSH cache mode (S2) as fallback; spike early |
| MIEngine WSL debugging doesn't work cleanly | Medium | Medium | Spike early in R3; consider alternative debug approaches |
| Performance issues with SSH sync | Medium | Medium | Incremental sync; lazy loading; caching; progress UI |
| LSP latency makes IntelliSense unusable | Low | High | Request batching; local caching; timeout handling |
| WSL distro detection fails on some systems | Low | Medium | Manual distro entry fallback; clear error messages |
| Community adoption confusion | Low | Medium | Clear documentation; feature flags; gradual rollout |

---

## Appendix: File Changes Summary

### New Files

| File | Purpose |
|------|---------|
| `RustAnalyzer.Remote/RemotePath.cs` | Remote path type |
| `RustAnalyzer.Remote/IPathMapper.cs` | Path mapping interface |
| `RustAnalyzer.Remote/WslPathMapper.cs` | WSL path mapping |
| `RustAnalyzer.Remote/SshPathMapper.cs` | SSH path mapping |
| `RustAnalyzer.Remote/IExecutionContext.cs` | Execution abstraction |
| `RustAnalyzer.Remote/LocalExecutionContext.cs` | Local execution (refactored) |
| `RustAnalyzer.Remote/WslExecutionContext.cs` | WSL execution |
| `RustAnalyzer.Remote/SshExecutionContext.cs` | SSH execution |
| `RustAnalyzer.Remote/ITargetSystemService.cs` | Target management |
| `RustAnalyzer.Remote/TargetSystemService.cs` | Target implementation |
| `RustAnalyzer/LanguageService/RemotePathMiddleLayer.cs` | LSP URI rewriting |
| `RustAnalyzer.Remote/ISshFileSyncService.cs` | SSH file sync |
| `RustAnalyzer.Remote/SshFileSyncService.cs` | SSH sync implementation |

### Modified Files

| File | Changes |
|------|---------|
| `TargetSystemCommands.cs` | Use ITargetSystemService instead of stub |
| `ToolChainService.cs` | Route through IExecutionContext |
| `ToolChainServiceExtensions.cs` | Route through IExecutionContext |
| `BuildJsonOutputParser.cs` | Add IPathMapper parameter |
| `LanguageClient.cs` | Add middle layer, remote RA support |
| `DebugLaunchTargetProvider.cs` | Target-aware debug launch |
| `FileScanner.cs` | Target-aware launch settings |
| `TestDiscoverer.cs` | Remote test discovery |
| `TestExecutor.cs` | Remote test execution |
| `PreReqsCheckService.cs` | Remote prerequisites |
| `Options.cs` | Feature flags |

---

## Appendix: Glossary

| Term | Definition |
|------|------------|
| UNC Path | Universal Naming Convention path, e.g., `\\wsl$\Ubuntu\...` |
| PathEx | Existing Windows-centric path type in codebase |
| RemotePath | New Linux-compatible path type for remote systems |
| Execution Context | Abstraction for running processes on local/remote targets |
| Path Mapper | Service to translate paths between VS-visible and remote formats |
| MI Engine | Machine Interface debug engine used for Linux debugging in VS |
| gdbserver | Remote debugging stub for GDB, runs on target system |

---

*Document Version: 2.0*
*Last Updated: December 2024*
*Status: Design Complete - Ready for Implementation*

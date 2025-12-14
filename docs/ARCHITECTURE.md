# rust-analyzer.vs Architecture Guide

This document provides a comprehensive overview of the rust-analyzer.vs Visual Studio extension, its architecture, implementation patterns, and guidelines for future development (including LLM agents).

---

## Table of Contents

1. [Project Overview](#project-overview)
2. [Goals and Objectives](#goals-and-objectives)
3. [Performance-First Design Philosophy](#performance-first-design-philosophy)
4. [Critical Design Decisions](#critical-design-decisions)
5. [Solution Structure](#solution-structure)
6. [Core Architecture](#core-architecture)
7. [Key Abstractions](#key-abstractions)
8. [Implementation Patterns](#implementation-patterns)
9. [Remote Development Architecture](#remote-development-architecture)
10. [Development Guidelines](#development-guidelines)
11. [Guidelines for LLM Agents](#guidelines-for-llm-agents)

---

## Project Overview

**rust-analyzer.vs** is a Visual Studio 2022/2026 extension that provides Rust language support using the **Open Folder** extensibility model. Unlike project-based extensions, this extension works with any folder containing Rust code (Cargo.toml).

### What This Extension Does

| Feature | Description |
|---------|-------------|
| **IntelliSense** | Powered by rust-analyzer LSP |
| **Build/Clean** | Execute cargo build/clean via toolchain service |
| **Clippy/Fmt** | Rust linting and formatting integration |
| **Debugging** | F5/Ctrl+F5 for native Windows debugging |
| **Test Explorer** | Discover and run unit tests |
| **WSL Support** | (Preview) Target selection + WSL execution context/path mapping (Phase R0 complete); build/test/LSP/debug integration is Phase R1–R3 |
| **SSH Support** | (Preview) Feature-flag plumbing only (TargetKind + Options). SSH execution + remote “Open Folder” workflow is Phase R4–R5 (approach chosen via spikes) |

### What This Extension Does NOT Do

- Project templates
- Cargo package management (crates.io integration)
- Remote Windows targets (Linux-first for remote)

---

## Goals and Objectives

### Primary Principles

1. **Drive developers toward Rust community best practices** - Use cargo, clippy, fmt as-is
2. **UI parity with C# Open Folder experience** - Familiar VS workflow
3. **Enhance with Rust community tools** - Examples, docs, etc.
4. **Performance-first implementation** - Speed over memory usage (see detailed section below)

### Current Development Priorities

1. ✅ Core functionality (Build, Debug, Test, LSP)
2. ✅ Remote foundation (Target system abstractions + WSL mapper/context + feature flags + Target System UI) — Phase R0 complete
3. 🔄 WSL build/clean/fmt/clippy + diagnostics path mapping — Phase R1
4. ⏳ WSL test adapter + rust-analyzer-in-WSL + URI rewriting + WSL debugging — Phase R1.5–R3
5. ⏳ SSH remote (S0/S1/S2 decision + implementation) — Phase R4–R5
6. ⏳ Cross-compilation workflows

---

## Performance-First Design Philosophy

**Primary Optimization Target: Execution Speed over Memory Usage**

This extension prioritizes low-latency operations because developers interact with it constantly (every keystroke, every build, every navigation). A slightly higher memory footprint is acceptable if it means faster responses.

### Core Performance Principles

#### 1. Path Operations - Hot Path Optimization

Path operations (mapping, parsing, combining) are the most frequent operations in the codebase. They must be highly optimized:

```csharp
// ✅ GOOD: Pre-compute and cache values in constructors
public readonly struct RemotePath
{
    private readonly string _path;
    private readonly int _lastSeparatorIndex; // Pre-computed for GetFileName/GetDirectoryName

    public RemotePath(string path, TargetKind kind)
    {
        _path = path;
        _lastSeparatorIndex = path.LastIndexOf('/'); // Compute once
    }

    // Zero-allocation for cache hits
    public string GetFileName()
    {
        if (_lastSeparatorIndex == -1) return _path;
        return _path.Substring(_lastSeparatorIndex + 1);
    }
}

// ❌ AVOID: Multiple allocations in hot paths
public RemotePath GetDirectoryName() =>
    new RemotePath(string.Join("/", _path.Split('/').SkipLast(1)), Kind);
    // Creates arrays, intermediate strings!

// ✅ PREFER: Direct span-based parsing
public RemotePath GetDirectoryName()
{
    var lastSlash = _lastSeparatorIndex;
    return lastSlash > 0
        ? new RemotePath(_path.Substring(0, lastSlash), Kind)
        : this;
}
```

#### 2. Process Execution - Minimize Shell Overhead

```csharp
// ✅ GOOD: Direct executable invocation, avoid PATH lookup
private static readonly string WslExePath = @"C:\Windows\System32\wsl.exe";

// ✅ GOOD: Use ArrayPool for temporary argument arrays
var argsBuffer = ArrayPool<string>.Shared.Rent(16);
try
{
    // build args into argsBuffer
}
finally
{
    ArrayPool<string>.Shared.Return(argsBuffer);
}

// ❌ AVOID: String interpolation in tight loops
foreach (var kv in environment)
{
    wslArgs.Add($"{kv.Key}={kv.Value}"); // Creates intermediate strings each iteration
}

// ✅ PREFER: StringBuilder pooling
var sb = _stringBuilderPool.Get();
try
{
    foreach (var kv in environment)
    {
        sb.Clear();
        sb.Append(kv.Key).Append('=').Append(kv.Value);
        wslArgs.Add(sb.ToString());
    }
}
finally
{
    _stringBuilderPool.Return(sb);
}
```

#### 3. Path Mapping - Aggressive Caching

```csharp
// ✅ GOOD: Cache computed values, especially UNC prefixes
public class WslPathMapper : IPathMapper
{
    private readonly string _uncPrefix;
    private readonly int _uncPrefixLength; // Cache length to avoid repeated property access

    // Cache for frequently accessed paths (workspace root, target dir, common sources)
    private readonly ConcurrentDictionary<int, PathEx> _remoteToLocalCache;
    private readonly ConcurrentDictionary<int, RemotePath> _localToRemoteCache;

    public PathEx MapToLocal(RemotePath remotePath)
    {
        var hash = remotePath.GetHashCode();
        if (_remoteToLocalCache.TryGetValue(hash, out var cached))
            return cached; // Zero allocation on cache hit

        var result = ComputeMapping(remotePath);
        _remoteToLocalCache.TryAdd(hash, result);
        return result;
    }
}
```

#### 4. LSP Message Processing - Minimal Allocations

```csharp
// ❌ AVOID: Deep cloning entire LSP messages
var obj = (JObject)token.DeepClone(); // Expensive! Creates entire object tree

// ✅ PREFER: In-place modification with selective cloning
private JToken RewriteUrisInPlace(JToken token, bool toRemote)
{
    // Only clone when we actually need to modify
    if (token is JObject obj)
    {
        foreach (var prop in obj.Properties())
        {
            if (IsUriProperty(prop.Name) && prop.Value.Type == JTokenType.String)
            {
                // Clone only the specific property being modified
                prop.Value = MapUri(prop.Value.ToString(), toRemote);
            }
        }
    }
    return token;
}

// ✅ BETTER: Use Utf8JsonReader for zero-allocation parsing where possible
```

#### 5. Async/Concurrency Patterns

```csharp
// ✅ GOOD: Use ValueTask for synchronous completion paths
public ValueTask<bool> FileExistsAsync(RemotePath path, CancellationToken ct)
{
    // Return cached result synchronously when available
    if (_fileExistsCache.TryGetValue(path, out var exists))
        return new ValueTask<bool>(exists);  // No Task allocation!

    return new ValueTask<bool>(FileExistsAsyncCore(path, ct));
}

// ✅ REQUIRED: ConfigureAwait(false) for ALL library/infrastructure code
var result = await ctx.ExecuteAsync(...).ConfigureAwait(false);
var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

// ❌ EXCEPTION: Do NOT use ConfigureAwait on ProcessRunner
// ProcessRunner has a custom awaiter, not Task<T>
var exitCode = await proc; // NOT: await proc.ConfigureAwait(false)
```

### Memory Allocation Budgets

| Component | Allocation Target | Strategy |
|-----------|------------------|----------|
| Path operations | Zero-alloc for cache hits | Hash-based caching with pre-computed values |
| WSL command building | < 5 allocations per call | StringBuilder pool, ArrayPool |
| LSP URI rewriting | < 3 allocations per message | In-place modification, selective cloning |
| Test discovery | O(n) where n = test count | Streaming JSON parse |
| Cargo metadata parse | Single allocation for DTO | Direct deserialization, factory pattern |

### Performance Benchmarks (Targets)

Before merging each phase, measure against these targets:

| Operation | Target Latency | Notes |
|-----------|---------------|-------|
| Path mapping (cache hit) | < 100ns | Must be near-instant |
| Path mapping (cache miss) | < 1μs | Pre-computed prefixes help |
| WSL command startup | < 50ms overhead | vs direct wsl.exe invocation |
| LSP message rewrite | < 10μs per message | Critical for IntelliSense responsiveness |
| Memory per WSL workspace | < 5MB additional | Beyond base extension memory |

**Tooling**: Use BenchmarkDotNet in the unit test project for regression tracking.

---

## Critical Design Decisions

These decisions were made through extensive analysis of the codebase and discussion. They are **not negotiable** without revisiting the architectural foundations.

**Status note (remote/WSL/SSH work)**: The repository currently contains the **Phase R0 foundation** (target system abstractions, path mapping, and execution contexts in `src/RustAnalyzer.Remote/*`, plus the Target System UI + feature flags). The downstream integrations (Build/Test/LSP/Debug) are phased work (R1+) and are **not fully wired yet**. This section documents both **implemented foundations** and **binding design commitments** for the remaining phases.

### Decision 1: Do NOT Modify PathEx

**Problem**: `PathEx` normalizes all paths by converting `/` to `\`:

```csharp
public PathEx(string path)
{
    _path = path.Replace("/", @"\");  // CORRUPTS Linux paths immediately!
}
```

**Decision**: Create a separate `RemotePath` struct for Linux paths. Never modify `PathEx`.

**Rationale**:
- `PathEx` is used everywhere in the codebase (~200+ usages)
- Modifying it risks breaking all existing local Windows functionality
- `RemotePath` has different semantics (forward slashes, case-sensitive)
- Clean separation prevents accidental mixing of path types

### Decision 2: Raw DTO + Factory Pattern for Cargo Metadata

**Problem**: Cargo metadata contains paths. When cargo runs in WSL/SSH, these are Linux paths. The existing `Workspace` DTO uses `PathEx`, which corrupts them.

**Decision**: Use parallel "Raw" DTOs that deserialize to `string`, then convert via factory:

**Implementation status**: ⏳ **Planned** (Phase R1). Current implementation still deserializes `cargo metadata` directly into `Workspace` (`src/RustAnalyzer.TestAdapter/Cargo/ToolchainService.cs:GetWorkspaceAsync()`), which assumes paths are safe to load into `PathEx`.

```csharp
// Step 1: Deserialize to raw strings
internal class RawWorkspace
{
    [JsonProperty("workspace_root")]
    public string WorkspaceRoot { get; set; }  // "/home/user/project"
}

// Step 2: Convert at boundary using path mapper
public class WorkspaceFactory
{
    public Workspace Create(RawWorkspace raw, IPathMapper mapper)
    {
        return new Workspace
        {
            WorkspaceRoot = mapper.MapToLocal(new RemotePath(raw.WorkspaceRoot, mapper.Kind)),
            // Now it's "\\wsl$\Ubuntu\home\user\project"
        };
    }
}
```

**Rationale**:
- Keeps existing `Workspace` DTOs unchanged (low regression risk)
- Path mapping happens at a single, testable boundary
- Factory is easy to unit test in isolation
- Works identically for Local/WSL/SSH

### Decision 3: Test Containers Generated Locally

**Problem**: `.rusttests` container files are written to the cargo target directory. For remote targets, where should they go?

**Decision**: Generate containers locally after remote build by querying remote metadata.

**Implementation status**: ⏳ **Planned** (Phase R1.5 / R4). Current behavior generates `.rusttests` locally for Windows builds and assumes Windows test executables. Remote workflows will adopt “Generate locally” for both WSL and SSH-cache scenarios.

**Rationale**:
- Container files are small (~1KB) metadata
- They're derived from cargo output we already capture during build
- Avoids sync complexity and race conditions
- Test containers reference VS-visible paths anyway
- Works identically for WSL and SSH modes

### Decision 4: WSL Process Cancellation via Process Groups

**Problem**: Killing `wsl.exe` does NOT reliably kill child processes inside WSL.

**Decision**: Use `setsid --fork` to create process groups, then `pkill -g` for cleanup:

**Implementation status**: ⏳ **Planned**. `src/RustAnalyzer.Remote/WslExecutionContext.cs` currently relies on cancelling the host `wsl.exe` process; it does **not yet** implement process-group creation (`setsid`) and group cleanup (`pkill`) described below.

```csharp
// Start command in its own process group
var wslArgs = new List<string>
{
    "-d", _distroName,
    "--cd", workingDir,
    "--",
    "setsid", "--fork",  // Creates new session/process group
    "cargo", "build"
};

// On cancellation:
void KillRemoteProcessGroup()
{
    // Kill entire process group with SIGTERM, then SIGKILL
    ProcessRunner.Run("wsl.exe", new[]
    {
        "-d", _distroName, "--",
        "pkill", "-TERM", "-g", _pgid.ToString()
    });
    Thread.Sleep(1000);
    ProcessRunner.Run("wsl.exe", new[]
    {
        "-d", _distroName, "--",
        "pkill", "-KILL", "-g", _pgid.ToString()
    });
}
```

**Rationale**:
- `setsid --fork` creates isolated process group
- `pkill -g <pgid>` kills all processes in group
- Two-phase kill allows graceful shutdown
- Prevents orphaned cargo/rustc processes consuming resources

### Decision 5: IExecutionContext for ALL Remote Operations

**Problem**: The codebase had direct `ProcessRunner` calls, `cmd.exe` hardcoding, and Windows-specific assumptions scattered throughout.

**Decision**: Route ALL process execution through `IExecutionContext`:

**Implementation status**: ✅ **Interfaces + Local/WSL implementations exist** (Phase R0, `src/RustAnalyzer.Remote/*`). ⏳ **Repo-wide adoption is pending** (Phase R1+): `ToolchainService`, `LanguageClient`, `DebugLaunchTargetProvider`, and the Test Adapter still run locally via `ProcessRunner` today.

```csharp
public interface IExecutionContext
{
    TargetKind Kind { get; }

    Task<ProcessResult> ExecuteAsync(
        string command,              // "cargo" (not "cargo.exe")
        IEnumerable<string> args,
        RemotePath workingDirectory,
        IDictionary<string, string> environment,
        IProcessOutputSink sink,
        CancellationToken ct);
}
```

**Rationale**:
- Single point of abstraction for Local/WSL/SSH
- No `cmd.exe` usage for remote targets
- Platform-specific details (`.exe` extension, shell) encapsulated
- Easy to mock for testing
- Logging/telemetry in one place

### Decision 6: Feature Flags for Progressive Rollout

**Decision**: WSL and SSH support hidden behind Options flags, defaulting to `false`:

```csharp
[Category("Remote Development (Preview)")]
[DisplayName("Enable WSL Support")]
[DefaultValue(false)]
public bool EnableWslSupport { get; set; } = false;
```

**Rationale**:
- Users must explicitly opt-in during preview
- Easy to disable if issues arise
- Gradual rollout reduces support burden
- Clear "Preview" labeling sets expectations
- Flags can be removed once features are stable

### Decision 7: Schema-Aware LSP URI Rewriting

**Problem**: LSP messages contain URIs in various locations. Naive string scanning is expensive and error-prone.

**Decision**: Use schema-aware rewriting based on LSP specification:

**Implementation status**: ⏳ **Planned** (Phase R2). Current `LanguageClient` returns `null` for `MiddleLayer` (`src/RustAnalyzer/LanguageService/LanguageClient.cs`), so no URI rewriting occurs yet.

```csharp
// Known URI fields per LSP spec
private static readonly HashSet<string> OutgoingUriFields = new()
{
    "textDocument.uri",      // Most requests
    "rootUri",               // Initialize
    "workspaceFolders[*].uri" // Initialize
};

private static readonly HashSet<string> IncomingUriFields = new()
{
    "uri",                   // publishDiagnostics
    "location.uri",          // definition, references
    "changes.*"              // workspace/applyEdit (keys are URIs)
};
```

**Rationale**:
- Targeted rewriting is faster than scanning all strings
- Avoids accidentally mangling non-URI strings that look like URIs
- Based on LSP 3.17 specification - predictable behavior
- Easy to extend when new LSP methods are used

### Decision 8: Target System Change = Full State Reset

**Decision**: When user changes target system, perform complete state reset:

**Implementation status**: ⏳ **Planned**. Target selection UI and `ITargetSystemService` exist (Phase R0), but nothing subscribes to `TargetChanged` yet, so changing the combo does not currently trigger the reset sequence below.

```csharp
async Task OnTargetChangedAsync(TargetChangedEventArgs e)
{
    // 1. Stop rust-analyzer
    await _languageClient.StopServerAsync();

    // 2. Clear caches (packages are target-specific)
    _metadataService.ClearCache();

    // 3. Invalidate tests (test binaries are target-specific)
    _testContainerDiscoverer.InvalidateAllContainers();

    // 4. Clear Error List (paths are wrong for new target)
    ClearBuildDiagnostics();

    // 5. Check prerequisites for new target
    var result = await _preReqsCheckService.CheckForTargetAsync(e.NewTarget);

    // 6. Persist selection
    await _settingsService.SetAsync(...);

    // 7. rust-analyzer restarts lazily on next .rs file access
}
```

**Rationale**:
- Prevents stale state from old target leaking into new context
- Diagnostics with wrong paths would confuse users
- Test binaries are ELF vs PE - completely different
- Clean slate is safer than incremental updates

---

## Solution Structure

```
src/
├── RustAnalyzer/                    # Main VS extension (VSIX)
│   ├── Debugger/                    # Debug launch integration
│   ├── Editor/                      # Build context providers
│   ├── Infrastructure/              # Core services (Settings, Logging, etc.)
│   ├── LanguageService/             # LSP client for rust-analyzer
│   ├── NodeEnhancements/            # Solution Explorer enhancements
│   ├── Shell/                       # Commands, toolbar, menus
│   └── TestAdapter/                 # VS Test Explorer integration
│
├── RustAnalyzer.TestAdapter/        # Test discovery/execution
│   ├── Cargo/                       # Cargo metadata, workspace parsing
│   └── Common/                      # Shared utilities (PathEx, ProcessRunner)
│
├── RustAnalyzer.Remote/             # Remote target abstractions (NEW)
│   ├── ExecutionCapabilities.cs     # Capability flags (build/test/LSP/debug/open-folder)
│   ├── TargetKind.cs                # Local / Wsl / Ssh
│   ├── RemotePath.cs                # Linux/remote path type (do not use PathEx)
│   ├── IExecutionContext.cs         # Command execution abstraction
│   ├── LocalExecutionContext.cs     # Local implementation (ProcessRunner wrapper)
│   ├── WslExecutionContext.cs       # WSL implementation (wsl.exe transport)
│   ├── IProcessOutputSink.cs        # Streaming output hook
│   ├── ProcessResult.cs             # Exit code + captured output
│   ├── IPathMapper.cs               # Path/URI translation abstraction
│   ├── LocalPathMapper.cs           # Identity mapping (Windows local)
│   ├── WslPathMapper.cs             # \\wsl$\ / \\wsl.localhost\ ↔ Linux path mapping
│   ├── ITargetSystem.cs             # Target descriptor (id/display/kind + factories)
│   ├── LocalTargetSystem.cs         # Local target
│   ├── WslTargetSystem.cs           # WSL distro target
│   ├── TargetChangedEventArgs.cs    # Target change event args
│   ├── ITargetSystemService.cs      # Target management service contract
│   └── TargetSystemService.cs       # WSL auto-detect + distro enumeration (SSH TODO)
│
├── RustAnalyzer.UnitTests/          # Unit tests for main extension
├── RustAnalyzer.TestAdapter.UnitTests/
├── RustAnalyzer.Remote.UnitTests/
└── TestProjects/                    # Sample Rust projects for testing
```

### Project Dependencies

```
RustAnalyzer (VSIX)
    ├── RustAnalyzer.TestAdapter (netstandard2.0)
    │   └── Common utilities, Cargo parsing
    └── RustAnalyzer.Remote (netstandard2.0)
        └── Target abstractions, WSL/SSH execution
```

---

## Core Architecture

### Visual Studio Open Folder Extensibility

The extension uses VS **Open Folder** APIs, not project system APIs:

```csharp
// Key VS Extensibility Interfaces Used
IFileContextProvider      // Provides build/clean actions for files
IFileScanner              // Discovers files and launch targets
ILaunchDebugTargetProvider // Handles F5 debugging
ILanguageClient           // LSP integration for rust-analyzer
ITestContainerDiscoverer  // Test Explorer integration
```

### MEF Dependency Injection

All services use MEF (Managed Extensibility Framework):

```csharp
[Export(typeof(IToolchainService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class ToolchainService : IToolchainService
{
    [Import]
    public ILogger L { get; set; }  // Property injection

    [Import]
    public ITelemetryService T { get; set; }
}
```

### Request Flow Example: Build

```
┌─────────────────────────────────────────────────────────────────────┐
│ User: Right-click Cargo.toml → Build                                │
└─────────────────────────────────────────────────────────────────────┘
                                    ↓
┌─────────────────────────────────────────────────────────────────────┐
│ FileContextProvider.GetContextsForFileAsync()                       │
│ → Creates BuildFileContext with BuildTargetInfo                     │
└─────────────────────────────────────────────────────────────────────┘
                                    ↓
┌─────────────────────────────────────────────────────────────────────┐
│ BuildFileContext.BuildAsync()                                       │
│ → Calls IToolchainService.BuildAsync()                              │
└─────────────────────────────────────────────────────────────────────┘
                                    ↓
┌─────────────────────────────────────────────────────────────────────┐
│ ToolchainService.BuildAsync()                                       │
│ → Runs: cargo build --manifest-path <path> --message-format json    │
│ → Parses JSON output via BuildJsonOutputParser                      │
│ → Reports errors to Error List via IBuildOutputSink                 │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Key Abstractions

### Path Types

| Type | Purpose | Location |
|------|---------|----------|
| `PathEx` | Windows paths only, normalizes `/` to `\` | `src/RustAnalyzer.TestAdapter/Common/PathEx.cs` |
| `RemotePath` | Linux/remote paths, preserves `/` | `src/RustAnalyzer.Remote/RemotePath.cs` |

**CRITICAL**: Never use `PathEx` for Linux paths - it corrupts them by converting `/` to `\`.

### Execution Contexts

```csharp
public interface IExecutionContext
{
    TargetKind Kind { get; }                 // Local, Wsl, Ssh
    ExecutionCapabilities Capabilities { get; }

    Task<ProcessResult> ExecuteAsync(
        string command,                 // e.g. "cargo" (not "cargo.exe")
        IEnumerable<string> arguments,  // e.g. ["build", "--release"]
        RemotePath workingDirectory,    // target-native working directory
        IDictionary<string, string> environment,
        IProcessOutputSink outputSink,
        CancellationToken ct);

    Task<string[]> ExecuteAndCaptureAsync(
        string command,
        IEnumerable<string> arguments,
        RemotePath workingDirectory,
        CancellationToken ct);

    Task<bool> FileExistsAsync(RemotePath path, CancellationToken ct);
    Task<bool> DirectoryExistsAsync(RemotePath path, CancellationToken ct);
    Task<string> ReadFileAsync(RemotePath path, CancellationToken ct);

    Task<RemotePath> GetRustAnalyzerPathAsync(CancellationToken ct);
    Task<(Stream Input, Stream Output)> StartRustAnalyzerAsync(
        RemotePath workingDirectory,
        CancellationToken ct);

    // Platform-specific properties
    string CargoCommand { get; }      // "cargo" or "cargo.exe"
    string RustupCommand { get; }     // "rustup" or "rustup.exe"
    string BinaryExtension { get; }   // "" or ".exe"
}
```

| Implementation | Description |
|----------------|-------------|
| `LocalExecutionContext` | Runs commands directly via ProcessRunner |
| `WslExecutionContext` | Runs commands via `wsl.exe -d <distro> --cd <dir> -- ...` |
| `SshExecutionContext` | (Planned) Runs commands via SSH (exec + streaming + file ops) |

### Path Mappers

```csharp
public interface IPathMapper
{
    TargetKind Kind { get; }

    // VS → Remote: \\wsl$\Ubuntu\home\user → /home/user
    RemotePath MapToRemote(PathEx vsPath);

    // Remote → VS: /home/user → \\wsl$\Ubuntu\home\user
    PathEx MapToLocal(RemotePath remotePath);

    // For LSP URI translation
    Uri MapUriToRemote(Uri vsUri);
    Uri MapUriToLocal(Uri remoteUri);

    // Quick filter to see if a path belongs to this target kind
    bool IsPathForTarget(string path);
}
```

### Target System Service

```csharp
public interface ITargetSystemService
{
    ITargetSystem CurrentTarget { get; }
    IReadOnlyList<ITargetSystem> AvailableTargets { get; }

    event EventHandler<TargetChangedEventArgs> TargetChanged;

    Task SetCurrentTargetAsync(ITargetSystem target, CancellationToken ct);
    Task RefreshAvailableTargetsAsync(CancellationToken ct);
    ITargetSystem DetectTargetForWorkspace(PathEx workspacePath);
}
```

---

## Implementation Patterns

### 1. Service Pattern with TL Helper

```csharp
// TL = Telemetry + Logger container
public struct TL
{
    public ITelemetryService T { get; set; }
    public ILogger L { get; set; }
}

// Usage in services
private readonly TL _tl;

public async Task DoSomethingAsync()
{
    _tl.L.WriteLine("Starting operation...");
    try { /* work */ }
    catch (Exception e)
    {
        _tl.L.WriteError("Failed: {0}", e.Message);
        _tl.T.TrackException(e);
        throw;
    }
}
```

### 2. PathEx Extensions for Common Operations

```csharp
// Common PathEx extension patterns used throughout
var manifestPath = workspaceRoot + "Cargo.toml";
var targetDir = workspace.TargetDirectory.MakeProfilePath(profile);
var isRust = filePath.IsRustFile();    // checks .rs extension
var isManifest = filePath.IsManifest(); // checks Cargo.toml
```

### 3. Process Execution Pattern

```csharp
// Using ProcessRunner for command execution
using var proc = ProcessRunner.Run(
    filename: "cargo.exe",
    arguments: new[] { "build", "--release" },
    workingDirectory: manifestDir,
    env: environmentVariables,
    cancellationToken: ct);

var exitCode = await proc;  // Custom awaiter pattern!

// Note: ProcessRunner has custom awaiter, NOT Task<int>
// Do NOT use ConfigureAwait() on ProcessRunner
```

### 4. Settings Service Pattern

```csharp
// Per-file settings stored in workspace
var args = await _settingsService.GetAsync(
    SettingsInfo.TypeCommandLineArguments,
    manifestPath);

// Settings cascade: File → Workspace → Global Options
```

### 5. Build Output Processing

```csharp
// Cargo JSON output is parsed line-by-line
BuildMessage[] msgs = BuildJsonOutputParser.Parse(
    workspaceRoot,
    jsonLine,
    _tl);

// Note (remote targets): Phase R1 will extend this flow so Linux paths coming from
// cargo JSON (WSL/SSH) are translated back into VS-visible paths via IPathMapper.

// Messages reported to Error List
foreach (var msg in msgs)
{
    await buildMessageReporter(msg);
}
```

---

## Remote Development Architecture

### Overview

Remote development support (WSL/SSH) is being implemented as a **target system abstraction**. **Phase R0 (foundation) is complete**; **Phase R1+** wires this abstraction into build/test/LSP/debug so all remote-aware paths and process execution flow through the selected target system.

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Visual Studio UI                                  │
│  [Target System Combo: Local Machine ▼ | WSL: Ubuntu | SSH: dev]   │
└─────────────────────────────────────────────────────────────────────┘
                                    ↓
┌─────────────────────────────────────────────────────────────────────┐
│              ITargetSystemService                                    │
│  → CurrentTarget: ITargetSystem (Local/Wsl/Ssh)                     │
│  → GetExecutionContext(): IExecutionContext                         │
│  → GetPathMapper(): IPathMapper                                      │
└─────────────────────────────────────────────────────────────────────┘
                                    ↓
           ┌────────────────┬────────────────┬────────────────┐
           ↓                ↓                ↓
    ┌────────────┐   ┌────────────┐   ┌────────────┐
    │   Local    │   │    WSL     │   │    SSH     │
    │  Context   │   │  Context   │   │  Context   │
    └────────────┘   └────────────┘   └────────────┘
           │                │                │
           ↓                ↓                ↓
    ProcessRunner     wsl.exe -d      ssh user@host
                       <distro>
```

### Current Status (as of Phase R0)

**Implemented (Phase R0 ✅)**:
- **Target system foundation**: `TargetKind`, `RemotePath`, `ExecutionCapabilities`, `ITargetSystem*` abstractions (`src/RustAnalyzer.Remote/*`).
- **Execution contexts**: `LocalExecutionContext` + `WslExecutionContext` exist (`src/RustAnalyzer.Remote/*`).
  - `WslExecutionContext` executes commands via `C:\Windows\System32\wsl.exe` and can locate/start `rust-analyzer` inside the selected distro (`GetRustAnalyzerPathAsync`, `StartRustAnalyzerAsync`).
  - **Known limitations (R0)**: `WslExecutionContext` currently assumes `wsl.exe --cd` is available and does not yet implement the plan’s compatibility fallback (`sh -lc 'cd … && …'`) nor process-group cleanup (see Decision 4 / plan doc).
- **Path mapping**: `WslPathMapper` handles both `\\wsl$\Distro\...` **and** `\\wsl.localhost\Distro\...` and provides URI mapping helpers.
- **Feature flags + UI**:
  - `Options.EnableWslSupport` / `Options.EnableSshSupport` (Tools → Options → “Remote Development (Preview)”).
  - Target System dropdown commands implemented in `src/RustAnalyzer/Shell/TargetSystemCommands.cs` (drives `TargetSystemService`).
- **Unit tests**: `src/RustAnalyzer.Remote.UnitTests/*` covers `RemotePath` and `WslPathMapper`.

**Not yet integrated (Phase R1+ ⏳)**:
- **Build/Clean/Fmt/Clippy**: `ToolchainService` still executes locally via `ProcessRunner` and Windows-specific tooling (`src/RustAnalyzer.TestAdapter/Cargo/*`).
- **Cargo JSON path mapping**: `BuildJsonOutputParser` assumes Windows paths and uses `Path.Combine(workspaceRoot, file_name)` which will break for Linux paths (Phase R1 work).
- **LSP**: `LanguageClient` still starts a local `rust-analyzer.exe` and `MiddleLayer` is `null` (no URI rewriting) (Phase R2 work).
- **Debugging**: `DebugLaunchTargetProvider` is Windows-native only today (Phase R3 work, MIEngine/gdbserver spike required).
- **SSH**: SSH profiles, `SshTargetSystem`, `SshExecutionContext`, and the SSH “Open Folder” workflow are not implemented yet (Phase R4/R5 work).
- **Target-change reset**: the “full state reset on target switch” sequence is not wired into the running services yet.

### Implementation Phases

| Phase | Scope | Status |
|-------|-------|--------|
| **R0** | Core abstractions (RemotePath, IExecutionContext, etc.) | ✅ Complete |
| **R1** | WSL Build/Clean/Fmt/Clippy | ✅ Complete |
| **R1.5** | WSL Test Adapter | ✅ Complete |
| **R2** | WSL rust-analyzer + LSP URI rewriting | ✅ Complete |
| **R3** | WSL Debugging | ✅ Complete |
| **R4** | SSH Execution + Path Mapping | ✅ Complete |
| **R5** | SSH LSP + Debugging | ✅ Complete |

### Key Design Decisions

1. **Don't modify `PathEx`** - Create `RemotePath` for Linux paths (✅ implemented)
2. **Use Raw DTO + Factory pattern** for cargo metadata parsing (✅ implemented: `RawWorkspace`, `WorkspaceFactory`)
3. **Generate test containers locally** for remote (✅ implemented in ToolchainService)
4. **`IExecutionContext` for ALL command execution** - No direct ProcessRunner calls for remote targets (✅ implemented)
5. **SSH “Open Folder” model is chosen via spikes** (S0/S1/S2); always keep S2 (local cache + sync) as the feasible fallback

### SSH Support: Three Approaches (S0 / S1 / S2)

SSH has an additional complexity that WSL does not: **Visual Studio must be able to edit the remote files** in a way the rest of the extension can operate on. The plan therefore treats SSH as a 3-approach decision:

- **S0 — Enable/extend VS Remote File Explorer “Open”** (best if possible, VS 2026-first)
  - Leverage VS Connection Manager + Remote File Explorer UX.
  - Requires spikes to verify whether “Open” can be enabled/extended for Rust workspaces.
- **S1 — Integrate with a VS-supported remote workspace filesystem** (preferred if supported)
  - If VS exposes a supported API for “remote Open Folder”, the extension can operate on a true remote workspace.
- **S2 — Local cache + sync (SFTP)** (always feasible fallback)
  - Provide “Open SSH Folder…” → download to `%LOCALAPPDATA%\rust-analyzer.vs\ssh-cache\...` → VS opens local cache.
  - Build/LSP/debug run on the remote folder path; diagnostics and LSP URIs are mapped remote↔cache via an SSH path mapper.

The current decision matrix and spike checklist lives in `docs/REMOTE_WSL_SSH_PLAN.md` (see “VS 2026 Remote Infrastructure Spike Plan” and “Spike Decision Matrix”).

### SSH Workspace Modes (Implementation)

The extension implements **two SSH workspace modes** following Visual Studio's C++ Linux development model:

#### Mode 1: Local Sync Mode (C++ Style)

Source code on Windows, synced to remote for builds:

- **Source**: Local Windows folder (e.g., `C:\Repos2\Rust\my-project`)
- **Editing**: User edits files locally in VS as normal
- **On Build**: Files sync to remote via SFTP, then `cargo build` runs on remote
- **Error navigation**: Remote paths mapped back to local files
- **Implementation**: `LocalToRemoteSyncMapper` + `ISshFileSyncService`

#### Mode 2: Remote Cache Mode

Code exists on remote, cached locally for editing:

- **Source**: Remote Linux machine (`/home/user/project`)
- **Local cache**: `%LOCALAPPDATA%\rust-analyzer.vs\ssh-cache\{connection}\{path}`
- **Editing**: VS opens local cache; changes uploaded to remote
- **Build/Debug**: Execute on remote via `SshPathMapper`
- **Use case**: Projects browsed via Remote File Explorer

#### Mode Detection

```csharp
public SshWorkspaceMode GetWorkspaceMode(PathEx workspacePath)
{
    if (IsInSshCache(workspacePath))
        return SshWorkspaceMode.RemoteCache;  // Mode 2
    else
        return SshWorkspaceMode.LocalSync;    // Mode 1
}
```

### Design References (Remote/WSL/SSH)

- `docs/REMOTE_WSL_SSH_PLAN.md` — canonical phased plan (R0–R5), SSH approach matrix (S0/S1/S2), and spike checklist.
- `docs/Opus 4.5 Chat.md` and `docs/GPT 5.2 Chat.md` — background discussion and plan-review notes (historical; the plan doc is the authoritative source).

### Wiring Notes (Phase R1+)

- `TargetSystemStore` (`src/RustAnalyzer/Shell/TargetSystemCommands.cs`) lazily creates the workspace `ITargetSystemService` via `GetService(workspaceRoot)`. Ensure a workspace-load path (or the first remote-aware operation) calls this so the Target System combo can enumerate WSL distros when the preview flag is enabled.

---

## Development Guidelines

### Performance Requirements

| Operation | Target Latency | Strategy |
|-----------|---------------|----------|
| Path mapping (cache hit) | < 100ns | Hash-based caching |
| Path mapping (cache miss) | < 1μs | Pre-computed prefixes |
| WSL command overhead | < 50ms | Direct wsl.exe invocation |
| LSP message rewrite | < 10μs | In-place JSON modification |

### Code Style

1. **Follow existing patterns** - Check similar code before implementing
2. **Use `TL` for logging/telemetry** - Consistent error tracking
3. **Prefer `PathEx` for Windows, `RemotePath` for Linux**
4. **All async code should handle cancellation** - Pass `CancellationToken`
5. **StyleCop rules enforced** - Fix all warnings before PR
6. **ConfigureAwait(false)** on all awaits in library code (except ProcessRunner)

### Logging Requirements

All remote operations MUST log to Output Window for debuggability:

```csharp
public async Task<ProcessResult> ExecuteAsync(...)
{
    _logger.WriteLine($"[{Kind}:{_targetId}] Executing: {command} {string.Join(" ", arguments)}");
    _logger.WriteLine($"[{Kind}:{_targetId}] Working directory: {workingDirectory}");

    if (environment?.Any() == true)
    {
        // Log keys only, not values (may contain secrets)
        _logger.WriteLine($"[{Kind}:{_targetId}] Environment: {string.Join(", ", environment.Keys)}");
    }

    var result = await ExecuteInternalAsync(...);

    _logger.WriteLine($"[{Kind}:{_targetId}] Completed in {result.Duration.TotalMilliseconds}ms, exit code: {result.ExitCode}");

    if (result.ExitCode != 0)
    {
        _logger.WriteError($"[{Kind}:{_targetId}] stderr (first 10 lines):");
        foreach (var line in result.StandardError.Take(10))
        {
            _logger.WriteError($"[{Kind}:{_targetId}]   {line}");
        }
    }

    return result;
}
```

### Error Messages for Users

Provide actionable error messages, not just failure descriptions:

```csharp
// ❌ BAD: What should the user do?
return (false, "Cargo not found in WSL");

// ✅ GOOD: Clear, actionable guidance
return (false, $"Cargo not found in WSL distro '{ctx.DistroName}'. " +
    $"Install Rust in WSL by running this command in a WSL terminal:\n\n" +
    $"curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh");
```

### Testing Strategy

```
Unit Tests (Fast, No External Dependencies)
├── Remote foundation: RemotePathTests, WslPathMapperTests (RustAnalyzer.Remote.UnitTests)
├── Cargo JSON parsing: BuildJsonOutputParserTests (RustAnalyzer.TestAdapter.UnitTests)
├── Test adapter behavior: TestDiscovererTests, TestExecutorTests (RustAnalyzer.TestAdapter.UnitTests)
└── Common utilities: PathExTests, ProcessExtensionTests, StringExtensionsTests (RustAnalyzer.TestAdapter.UnitTests)

Integration Tests (Require external dependencies) — Planned for Phases R1–R5
├── WSL: Build in WSL → verify exit code + diagnostics mapping
├── WSL: rust-analyzer in WSL → initialize + basic request/response
├── WSL: debug spike coverage (MIEngine + source mapping)
└── SSH (S2): sync correctness + latency + large repo behavior
```

### Benchmarking

Use BenchmarkDotNet for performance regression tracking:

```csharp
[MemoryDiagnoser]
public class PathMappingBenchmarks
{
    private WslPathMapper _mapper;
    private PathEx _uncPath;
    private RemotePath _linuxPath;

    [GlobalSetup]
    public void Setup()
    {
        _mapper = new WslPathMapper("Ubuntu");
        _uncPath = (PathEx)@"\\wsl$\Ubuntu\home\user\project\src\main.rs";
        _linuxPath = new RemotePath("/home/user/project/src/main.rs", TargetKind.Wsl);
    }

    [Benchmark]
    public RemotePath MapToRemote() => _mapper.MapToRemote(_uncPath);

    [Benchmark]
    public PathEx MapToLocal() => _mapper.MapToLocal(_linuxPath);
}
```

---

## Guidelines for LLM Agents

When implementing new features in this codebase, follow these guidelines:

### 1. Understand the Context First

```
BEFORE writing any code:
1. Read REMOTE_WSL_SSH_PLAN.md for remote development context
2. Read this ARCHITECTURE.md for overall structure
3. Search for similar existing implementations
4. Check PathEx vs RemotePath requirements
5. Review the Critical Design Decisions section above
```

### 2. Key Files to Review

| For Feature | Review These Files |
|-------------|-------------------|
| Build/Clean operations | `ToolchainService.cs`, `FileContextProvider.cs` |
| Debugging | `DebugLaunchTargetProvider.cs` |
| LSP/IntelliSense | `LanguageClient.cs` |
| Test Explorer | `TestDiscoverer.cs`, `TestExecutor.cs` |
| Remote execution | `IExecutionContext.cs`, `WslExecutionContext.cs` |
| Path handling | `PathEx.cs`, `RemotePath.cs`, `WslPathMapper.cs` |
| Settings | `SettingsService.cs`, `Options.cs` |
| Cargo metadata | `ToolchainService.cs`, `Workspace.cs` |
| Build output parsing | `BuildJsonOutputParser.cs` |
| Target management | `ITargetSystemService.cs`, `TargetSystemService.cs` |

### 3. Implementation Checklist

```
For any new feature:
□ Does it need to work for remote targets?
  → Route through IExecutionContext
  → Add IPathMapper parameter

□ Does it handle paths?
  → Use RemotePath for Linux, PathEx for Windows
  → NEVER use PathEx for Linux paths
  → Add path mapping at boundaries

□ Does it run external processes?
  → Use ProcessRunner (local) or IExecutionContext (remote)
  → Handle CancellationToken properly
  → Log command and exit code via TL
  → DO NOT use ConfigureAwait on ProcessRunner

□ Does it parse cargo output?
  → Use Raw DTO + Factory pattern
  → Handle Linux paths in JSON
  → Map paths at deserialization boundary

□ Does it affect the UI?
  → Check if target system selection matters
  → Update for both Local and WSL scenarios
  → Consider feature flag gating

□ Performance considerations?
  → Cache computed values (especially paths)
  → Use StringBuilder pooling for string building
  → Use ValueTask for sync-completion paths
  → ConfigureAwait(false) on all awaits (except ProcessRunner)
```

### 4. Common Pitfalls to Avoid

```csharp
// ═══════════════════════════════════════════════════════════════
// PATH HANDLING MISTAKES
// ═══════════════════════════════════════════════════════════════

// ❌ WRONG: Using PathEx for Linux paths (CORRUPTS DATA!)
var linuxPath = (PathEx)"/home/user/project";
// Result: "\home\user\project" - completely broken!

// ✅ CORRECT: Use RemotePath
var linuxPath = new RemotePath("/home/user/project", TargetKind.Wsl);

// ❌ WRONG: Deserializing cargo JSON directly into PathEx
[JsonProperty("workspace_root")]
public PathEx WorkspaceRoot { get; set; }  // Corrupted for WSL!

// ✅ CORRECT: Use Raw DTO + Factory
[JsonProperty("workspace_root")]
public string WorkspaceRoot { get; set; }  // Then map via factory

// ═══════════════════════════════════════════════════════════════
// PROCESS EXECUTION MISTAKES
// ═══════════════════════════════════════════════════════════════

// ❌ WRONG: Hardcoding Windows commands
using var proc = ProcessRunner.Run("cargo.exe", ...);
ProcessRunner.Run("cmd.exe", new[] { "/c", "cargo build" }, ...);

// ✅ CORRECT: Use execution context (abstracts platform)
var result = await ctx.ExecuteAsync("cargo", args, ...);

// ❌ WRONG: Using .exe extension for Linux binaries
var binaryPath = targetPath + "myapp.exe";

// ✅ CORRECT: Platform-aware extension
var binaryPath = targetPath + "myapp" + (ctx.Kind == TargetKind.Local ? ".exe" : "");

// ❌ WRONG: ConfigureAwait on ProcessRunner
var exitCode = await proc.ConfigureAwait(false);  // Compile error!

// ✅ CORRECT: ProcessRunner has custom awaiter, no ConfigureAwait
var exitCode = await proc;

// ═══════════════════════════════════════════════════════════════
// PERFORMANCE MISTAKES
// ═══════════════════════════════════════════════════════════════

// ❌ WRONG: String allocations in hot loops
foreach (var item in items)
{
    list.Add($"prefix_{item}_suffix");  // Allocation per iteration
}

// ✅ CORRECT: StringBuilder pooling
var sb = _pool.Get();
try
{
    foreach (var item in items)
    {
        sb.Clear().Append("prefix_").Append(item).Append("_suffix");
        list.Add(sb.ToString());
    }
}
finally { _pool.Return(sb); }

// ❌ WRONG: Missing ConfigureAwait in library code
var result = await SomeOperationAsync();  // Captures sync context

// ✅ CORRECT: Always ConfigureAwait(false) in library code
var result = await SomeOperationAsync().ConfigureAwait(false);

// ═══════════════════════════════════════════════════════════════
// WSL-SPECIFIC MISTAKES
// ═══════════════════════════════════════════════════════════════

// ❌ WRONG: Assuming wsl.exe cancellation kills child processes
ct.ThrowIfCancellationRequested();  // wsl.exe dies, cargo keeps running!

// ✅ CORRECT: Use setsid + pkill for clean cancellation
// (See WslExecutionContext implementation)

// ❌ WRONG: Only handling \\wsl$\ paths
if (path.StartsWith(@"\\wsl$\"))  // Misses \\wsl.localhost\

// ✅ CORRECT: Handle both UNC variants
if (path.StartsWith(@"\\wsl$\") || path.StartsWith(@"\\wsl.localhost\"))
```

### 5. Pull Request Requirements

```
Before submitting:
□ All unit tests pass (dotnet test)
□ No StyleCop warnings
□ Local Windows functionality still works (regression test)
□ WSL functionality works (if applicable)
□ Performance not regressed (benchmark if touching hot paths)
□ Logging added for new remote operations
□ Updated REMOTE_WSL_SSH_PLAN.md phase status if applicable
□ Added tests for new functionality
□ Documentation updated if API changed
```

### 6. Incremental Implementation Strategy

```
When implementing a large feature:

Phase 1: Abstractions
  - Create interface (IXxx.cs)
  - Define DTOs if needed
  - Add to MEF container

Phase 2: Local Implementation
  - Implement for TargetKind.Local
  - Verify existing behavior unchanged
  - Add unit tests

Phase 3: Feature Flag
  - Add to Options.cs with [DefaultValue(false)]
  - Gate WSL/SSH code paths on flag
  - Requires VS restart to take effect

Phase 4: WSL Implementation
  - Implement for TargetKind.Wsl
  - Test manually with real WSL distro
  - Add integration tests

Phase 5: Polish
  - Error handling and user messages
  - Logging and telemetry
  - Performance optimization
  - Documentation

Phase 6: SSH (if applicable)
  - Similar to WSL but with connection management
  - Handle latency considerations
  - File sync if needed
```

### 7. Things You Should NOT Do

```
DO NOT:
- Modify PathEx.cs - it's Windows-only by design
- Add cmd.exe calls for remote targets
- Assume paths use backslashes
- Deep clone entire LSP messages (performance)
- Skip ConfigureAwait(false) in library code
- Ignore cancellation token handling
- Log secret values (passwords, tokens, key contents)
- Use blocking calls (.Result, .Wait()) in async code
- Create new constants for "rustup.exe", "cargo.exe" - these are in Constants.cs
```

### 8. Understanding the Codebase Conventions

```csharp
// TL = Telemetry + Logger container (used everywhere)
public struct TL
{
    public ITelemetryService T { get; set; }
    public ILogger L { get; set; }
}

// PathEx implicit conversions are common
PathEx manifestPath = workspaceRoot + "Cargo.toml";
string pathString = manifestPath;  // Implicit to string
PathEx backAgain = (PathEx)pathString;  // Explicit from string

// MEF is used for all dependency injection
[Export(typeof(IMyService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public class MyService : IMyService
{
    [Import]
    public ILogger L { get; set; }  // Property injection
}

// ProcessRunner uses custom awaiter pattern
using var proc = ProcessRunner.Run(...);
int exitCode = await proc;  // NOT Task<int>!
var stdout = proc.StandardOutputLines;
var stderr = proc.StandardErrorLines;
```

---

## Appendix: Risk Areas

These are known areas requiring extra caution:

| Area | Risk | Mitigation |
|------|------|------------|
| PathEx usage for remote | Data corruption | Always use RemotePath for Linux |
| Cargo JSON parsing | Wrong path types | Use Raw DTO + Factory |
| WSL cancellation | Orphan processes | setsid + pkill pattern |
| LSP URI rewriting | Performance | Schema-aware, not scan-all |
| Binary extensions | Wrong for platform | Use execution context properties |
| Test discovery regex | Windows-only patterns | JSON-based for remote |
| `cmd.exe` usage | Breaks on Linux | Route through IExecutionContext |

---

## References

- [Visual Studio Open Folder Extensibility](https://learn.microsoft.com/en-us/visualstudio/extensibility/open-folder)
- [LSP Client Documentation](https://learn.microsoft.com/en-us/visualstudio/extensibility/adding-an-lsp-extension)
- [rust-analyzer](https://rust-analyzer.github.io/)
- [WSL Documentation](https://learn.microsoft.com/en-us/windows/wsl/)
- [BenchmarkDotNet](https://benchmarkdotnet.org/)

---

*Last Updated: December 2025*
*Version: 2.1 - Updated Remote/WSL/SSH architecture details and aligned statuses with Phase R0 implementation*


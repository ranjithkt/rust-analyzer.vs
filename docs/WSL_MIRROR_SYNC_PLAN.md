### Summary
Mode 2 (“Windows workspace, WSL execution” by building directly on `/mnt/<drive>/...`) turned out to be **not acceptable**: `cargo build` rebuilds the selected crate every time even when nothing changed.

This document proposes a new strategy:

- The **workspace stays on Windows** (VS opens `C:\...`).
- We create a **WSL-native mirror** (ext4) of the workspace **and** all reachable local `path` dependencies.
- We run `cargo build/clippy/fmt/test/metadata` **inside WSL** against the **mirror**, not under `/mnt`.

This is a **plan only** (no implementation here).

> **Important (repo build tooling)**: This repo is a **Visual Studio / MSBuild** codebase (classic `.csproj` + VSIX). Use **MSBuild** (e.g. from a “Developer Command Prompt for VS”) to build/test it. Do **not** use `dotnet build` / `dotnet test` for repo validation — it can fail or behave differently.

### Recommendation: choose sync-on-build (Option 1)
Between the two options:

1. **Sync only the modified files at the start of each build**, then build in the WSL mirror.
2. Sync modified files every ~5s in the background.

Recommend **Option 1** as the default because it is:

- **Deterministic**: the build always uses a known-good “mirror is up to date” state.
- **Lower overhead**: no idle-time churn and fewer chances of conflicting with builds.
- **Simpler to implement correctly** (renames/deletes + batching + “wait for sync” semantics are easier).

We can add Option 2 later as an optimization (background sync feeding the same “dirty file” queue), but the baseline should be sync-on-build.

---

### Goals / constraints
- **Fix the `/mnt` rebuild issue** by building on WSL’s native filesystem.
- **Keep repo on Windows**: VS Open Folder stays stable and `.vs/` remains on NTFS.
- **Mirror exact relative structure** so `path = "../dep"` keeps working.
- **Support local `path` dependencies outside the workspace root**.
- **Do not change Windows-native behavior** when Target System is Local.

---

### High-level design
When Target System is `WSL: <distro>` **and** WSL build mode is “mirror”:

- Compute a **mirror root** inside WSL, e.g.
  - `~/.cache/rust-analyzer.vs/mirrors/<workspaceId>/`
- Mirror Windows paths into WSL **preserving drive/absolute structure**, e.g.
  - `C:\Repos\proj\src\main.rs`
  - → `/home/<user>/.cache/rust-analyzer.vs/mirrors/<id>/win/c/Repos/proj/src/main.rs`

This ensures that relative `path` dependencies work naturally inside the mirror:

- Workspace mirror: `.../win/c/Repos/proj`
- Dependency at `C:\Repos\dep` mirrors to `.../win/c/Repos/dep`
- Then `path = "../dep"` still resolves.

Cargo runs in WSL with:

- **Linux working dir** = mirror workspace root
- **--manifest-path** = mirror manifest path
- Default `target/` outputs under the mirror workspace (ext4)

Implication:
- In mirror mode, the build outputs (including `target/`) live in the **WSL mirror**. The Windows workspace will **not** naturally have a `target/` folder unless we add an optional “copy artifacts back / junction” feature (not recommended by default).

---

### How this fits into the current repo
Relevant existing pieces we will reuse:

- **WSL selection + distro name**
  - UI combo + persistence: `src/RustAnalyzer/Shell/TargetSystemCommands.cs`
  - Cross-layer read (process env): `src/RustAnalyzer.TestAdapter/Common/TargetSystemSelection.cs`

- **WSL invocation helpers**
  - `wsl.exe` execution wrappers: `src/RustAnalyzer.TestAdapter/Cargo/ToolChainServiceExtensions.cs`
  - WSL detection + UNC↔Linux mapping: `src/RustAnalyzer.TestAdapter/Common/WslInfo.cs`
  - Windows↔/mnt mapping (legacy Mode 2): `src/RustAnalyzer.TestAdapter/Common/WslPathMapper.cs`

- **Chokepoints for tool execution**
  - Build/clean/clippy/fmt and `cargo metadata`: `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`

- **Path rewrite / navigation**
  - JSON diagnostics parsing: `src/RustAnalyzer.TestAdapter/Cargo/BuildJsonOutputParser.cs`
  - String diagnostics parsing: `src/RustAnalyzer/Infrastructure/StringBuildMessagePreprocessor.cs`
  - Test discovery/exe-path parsing + test-json `source_path` mapping: `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
  - Test execution: `src/RustAnalyzer.TestAdapter/TestExecutor.cs`
  - Debug launch (already supports WSL debug transport): `src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`

- **Existing VS workspace watcher** (for metadata cache invalidation inside workspace)
  - `workspaceContext.GetFileWatcherService().OnBatchFileSystemChanged`: `src/RustAnalyzer/Infrastructure/MetadataServiceFactory.cs`

What we **don’t** currently have (and this plan adds):

- A Windows-side watcher that covers **external** `path` dependency roots
- A WSL mirror directory concept + mapping
- A sync engine (initial full copy + incremental updates)

---

### Key components we need to add
#### 1) Mirror path mapper (Windows <-> mirror Linux <-> UNC)
Add a new mapper (suggested location: `src/RustAnalyzer.TestAdapter/Common/`):

- `WslMirrorPathMapper` (new)
  - `TryWindowsToMirrorLinux(string windowsPath, MirrorConfig cfg, out string linuxPath)`
  - `TryMirrorLinuxToWindows(string linuxPath, MirrorConfig cfg, out string windowsPath)`
  - `TryMirrorLinuxToUnc(string linuxPath, string distro, out string uncPath)`
  - `TryUncToMirrorLinux(string uncPath, out string linuxPath)` (can reuse `WslInfo`)

Notes:
- For debug + test execution, storing mirror outputs as **UNC paths** (`\\wsl.localhost\<distro>\home\...`) is convenient because the existing code already knows how to map UNC ↔ Linux via `WslInfo`.
- For diagnostics navigation, we want **Windows-local** source paths (`C:\...`). So we must map mirror Linux source paths back to Windows.

#### 2) Mirror manager/service (per workspace)
Add a new service that owns:

- Mirror location (base dir, workspace id)
- Discovered local path-dependency roots
- Windows file watchers + dirty-file queue
- Sync routines (initial full copy, incremental sync)

Suggested shape:

- `WslMirrorManager` (process-wide singleton)
  - keyed by `(workspaceRootWindows, distroName)`
  - returns a `WslMirrorInstance`

- `WslMirrorInstance`
  - `EnsureInitializedAsync()`
    - resolves `~` / `$HOME` and creates mirror base dir
    - discovers `path` dependencies
    - does initial full copy for workspace + deps
    - starts watchers
  - `EnsureSynchronizedAsync()`
    - applies dirty file set (copy/delete/rename)
  - `GetMirrorWorkingDirLinux()`
  - `GetMirrorManifestPathLinux(PathEx manifestWindows)`

Where to call it:
- From `ToolchainService` before any WSL cargo command.

#### 3) Dependency discovery: find *local path* dependencies
We need to watch and mirror **all local path deps** the workspace depends on.

Most robust approach:

- Run `cargo metadata` **in WSL** against the original workspace under `/mnt/<drive>/...` (this can be slow, but it’s only for discovery and can be cached).
- Parse JSON and collect packages where:
  - `manifest_path` is a local path (`/mnt/...`)
  - `source` is `null` (path crates / workspace crates)
  - and `manifest_path` is **outside** the workspace root

This avoids writing our own TOML resolver and correctly handles `[patch]`, workspace inheritance, etc.

Important: the extension’s existing `Workspace` model uses `--no-deps` for performance; we should keep that for the core UX and add a separate “full metadata parse for local path deps only”.

#### 4) Windows file watchers (workspace + path-dep roots)
Add watchers for:

- Workspace root (Windows)
- Each discovered path-dep root (Windows)

Implementation notes:
- Use `.NET FileSystemWatcher` with `IncludeSubdirectories = true`.
- Track `Created/Changed/Renamed/Deleted`.
- Debounce/coalesce events into a dirty set.
- Handle overflow (buffer overrun) by marking the root “needs rescan” and triggering a one-time `robust resync` for that root.

The existing VS workspace watcher (`workspaceContext.GetFileWatcherService().OnBatchFileSystemChanged`) is useful for metadata cache invalidation **inside the workspace**, but it will not cover external path-dep roots. We will keep it as-is and add extra watchers for mirroring.

---

### Sync mechanics
#### Initial full copy
On first run (or when mirror is missing/corrupted):

- Copy workspace root into its mirror location.
- Copy each path-dependency root into its mirror location.

Copy tool options (pick one for implementation):

- **Primary (default)**: invoke `wsl.exe` and use `rsync` from `/mnt/<drive>/...` into the mirror (ext4).
  - Recommended baseline: `rsync -a --delete` (plus excludes).
  - Pro: fast incremental sync, preserves mtimes, handles deletes/renames robustly.
  - Note: `rsync` is expected to be installed in the target distro (you already installed it).

- **Fallbacks (only if needed)**:
  - `tar` pipeline (`tar -cf - . | tar -xf -`) to mirror, plus manual delete handling.
  - Windows copy to UNC (`\\wsl.localhost\...`) using `File.Copy` + recursive enumeration.
    - Use only if `rsync` is unavailable or shows unacceptable performance in a particular environment.

Exclusions we should apply by default:
- `target/`
- `.git/`
- `.vs/`
- `**/*.pdb`, `**/*.obj` (optional)

#### Reference: what VS C++ WSL toolset appears to do (useful patterns)
From your VS 2026 Linux C++ build logs, the WSL toolset pipeline effectively does:

- **ResolveWSLTarget / ResolveRemoteDir**: determine *which* WSL distro + *where* the remote build root lives.
- **ConsolidateSourcesToCopy + ValidateSources**: compute and validate the set of inputs that should be mirrored.
- **PrepareUpToDateChecks**: prepare a mechanism so “copy sources” can be skipped/fast when inputs are unchanged.
- **CopySources**: perform the copy/sync step (this is where `rsync` fits; in your log it’s ~663ms for the sample).
- **Build on WSL**: compile/link happens on the remote side after copy.
- **(Optional) produce a Windows-visible output path**: the log line `... -> C:\\...\\bin\\...\\WslCppTest.out` suggests the toolset may also place/copy the final output somewhere on Windows for the VS project system. For our Rust flow we can avoid copying artifacts back by using UNC (`\\\\wsl.localhost\\...`) for debug/test paths.

How we reuse these concepts:
- `ResolveWSLTarget/ResolveRemoteDir` ⇒ our `WslMirrorInstance` resolves distro + mirror base + per-workspace mirror root.
- `PrepareUpToDateChecks` ⇒ we keep a simple “last sync stamp” + dirty tracking to avoid unnecessary rsyncs, while still allowing a forced rsync when needed.
- `CopySources` ⇒ our rsync invocation(s) for workspace root and external `path` dependency roots.

#### Incremental sync (Option 1: on-build)
At the start of every WSL cargo operation (build/test/clippy/fmt/metadata):

- `EnsureInitializedAsync()` (no-op if already initialized)
- `EnsureSynchronizedAsync()`
  - For each dirty file:
    - If file exists on Windows: copy to corresponding mirror path
    - If deleted: delete mirror file
    - If rename: delete old + copy new

Batching (for performance):
- group dirty items by root (workspace root vs dep root)
- flush in a single batch per root

After sync, run cargo in WSL with:
- `--cd <mirrorWorkspaceRootLinux>`
- `--manifest-path <mirrorManifestLinux>`

---

### Required changes in existing code (no implementation yet)
#### Cargo execution: use mirror paths (not `/mnt`)
Files:
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainServiceExtensions.cs`

Changes:
- Add a new WSL execution mode for Windows workspaces:
  - `Mode 2 (legacy): /mnt` (current)
  - `Mode 3 (new): mirror (ext4)`
- Before WSL operations, call mirror sync.
- Compute:
  - linux working dir = mirror workspace root
  - linux manifest path = mirror manifest
- Keep Mode 1 (UNC workspace) behavior unchanged.

#### Metadata rewriting: handle mirror
File:
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs` (`RewriteCargoMetadataPathsFromLinuxToWindows`)

Today:
- Mode 1 maps Linux `/home/...` → UNC workspace
- Mode 2 maps `/mnt/<drive>/...` → `X:\...`

For mirror mode:
- `workspace_root`, `packages[].manifest_path`, `targets[].src_path` should map back to **Windows paths** (`C:\...`) for navigation/scanning.
- `target_directory` should map to **UNC** path pointing to the mirror’s `target/` directory, so that debug/test container paths reference real files.

This is the key “split mapping” difference vs Mode 2.

#### Diagnostics output mapping: mirror Linux paths -> Windows
Files:
- `src/RustAnalyzer.TestAdapter/Cargo/BuildJsonOutputParser.cs`
- `src/RustAnalyzer/Infrastructure/StringBuildMessagePreprocessor.cs`
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs` (test JSON `source_path` mapping)

Add: when WSL is selected and a path is a Linux absolute path under the mirror prefix, map it to the corresponding Windows path.

#### Tests: store exe paths correctly
Files:
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs` (test exe parsing + conversion)
- `src/RustAnalyzer.TestAdapter/TestExecutor.cs` (already runs WSL exes)

For mirror mode:
- `cargo test --no-run` will emit Linux paths under the mirror.
- Store test exe paths as **UNC** (mirror) paths so that:
  - they can be persisted in `.rusttests`
  - they can be converted back to Linux paths for execution via existing UNC→Linux logic.

#### Debugging
File:
- `src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`

No new debug transport is required beyond what already exists:
- If the binary path is UNC into WSL, `WslPathMapper` already maps it to Linux, and MIEngine + `SSH:wsl+<distro>` should work.

We must ensure metadata rewriting produces target paths that exist (UNC into mirror target dir).

---

### New settings (workspace-scoped)
Add settings to control this behavior (via `ISettingsService` / `.vs` workspace settings):

- `Target System` (already exists): `Local` or `WSL: <distro>`
- `WSL Build Location` (new):
  - `mirror` (default)
  - `mnt` (legacy)
- `WSL Mirror Base Directory` (optional advanced): default `~/.cache/rust-analyzer.vs/mirrors`
- `WSL Mirror Excludes` (optional): default excludes listed above

---

### File-by-file implementation checklist (what will change)
New files (suggested locations):

- `src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs`
  - Implements the mirror mapping (Windows ↔ mirror Linux ↔ UNC).
- `src/RustAnalyzer.TestAdapter/Common/WslMirrorManager.cs`
  - Process-wide cache of mirror instances keyed by workspace root + distro.
- `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`
  - Holds watchers, dirty queue, sync logic, and computed mirror roots.
- `src/RustAnalyzer.TestAdapter/Common/WslMirrorConfig.cs`
  - Mirror base dir, workspace id, excludes, sync mode (on-build), etc.

Files to modify:

- `src/RustAnalyzer/Infrastructure/SettingsInfo.cs`
  - Add new workspace-scoped setting keys for mirror mode (e.g. `TypeWslBuildLocation`, `TypeWslMirrorBaseDir`, `TypeWslMirrorExcludes`).
- `src/RustAnalyzer.TestAdapter/Common/TargetSystemSelection.cs`
  - No functional change required, but mirror mode will likely want a helper that also reads the new “WSL Build Location” setting (or an env var for early prototyping).
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
  - Before WSL operations: `await mirror.EnsureInitializedAsync(); await mirror.EnsureSynchronizedAsync();`
  - Replace `/mnt` manifest + working dir with mirror equivalents.
  - Extend metadata path rewriting to support “mirror” split-mapping (sources → Windows, target dir → UNC).
  - Extend test exe path conversion to store UNC mirror paths.
  - Extend test JSON `source_path` mapping for mirror linux paths → Windows.
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainServiceExtensions.cs`
  - Add a “resolve linux working dir” path that can return mirror paths (not just `/mnt` / UNC workspace).
  - Keep the existing `RunInWsl`/`RunCargoInWsl` machinery.
- `src/RustAnalyzer.TestAdapter/Cargo/BuildJsonOutputParser.cs`
  - Teach `ResolvePathForVs(...)` to map mirror linux absolute paths back to Windows.
- `src/RustAnalyzer/Infrastructure/StringBuildMessagePreprocessor.cs`
  - Teach the WSL processors to map mirror linux absolute paths back to Windows (in addition to existing UNC and `/mnt`).

No expected changes (but validate during implementation):

- `src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`
  - Should work if `target_directory` is rewritten to UNC in mirror mode.
- `src/RustAnalyzer/Infrastructure/MetadataServiceFactory.cs`
  - Keep VS workspace watcher for cache invalidation; mirror adds extra watchers for external roots.

Lifecycle decision (implementation detail):
- Start mirror initialization **on-demand** (first WSL tool invocation) rather than eagerly on workspace open, to avoid doing large copies unexpectedly.

---

### Validation plan
- **Repro baseline**: build twice on `/mnt/<drive>` and confirm the unwanted rebuild behavior.
- **Mirror mode**:
  - First build: mirror initialized + full copy + build.
  - Second build (no changes): should be a true no-op (cargo outputs `fresh=true` artifacts, no recompilation).
  - Change a single `.rs` file: only that file is synced; incremental build recompiles only necessary crates.
  - Change a `Cargo.toml` impacting a path dependency: path deps list updates + watcher coverage expands.
  - Rename/delete files: mirror updates match Windows tree.

---

### Open questions / risks
- Best default mirror base path and workspace id scheme (hash vs sanitized path).
- Copy mechanism choice (`rsync` vs UNC copy vs tar).
- File watcher overflow strategy on very large repos.
- How often to recompute path-dependency roots (on manifest changes vs periodic).
- Whether to also mirror `Cargo.lock` and `.cargo/config.toml` (likely yes; they’re within workspace root anyway).

---

### Related docs
- The original WSL plan: see [`WSL_SUPPORT_PLAN.md`](WSL_SUPPORT_PLAN.md) (Mode 2 describes the `/mnt` approach that this replaces).

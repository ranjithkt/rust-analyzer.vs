---
name: WSL Full Support Implementation
overview: |
  Implement WSL-first support for rust-analyzer.vs so a repo living in WSL can be opened in Visual Studio (Open Folder), edited via UNC paths, and all Rust tooling (cargo build/clean/test, clippy, fmt, rust-analyzer, debugging) executes inside WSL.

  SSH support that already exists (current: remote build via SSH, plus additional plumbing) MUST remain supported.

status: planning
last_reviewed: 2025-12-14
assumptions:
  - Visual Studio opens WSL repos via UNC paths (\\wsl$\<distro>\... or \\wsl.localhost\<distro>\...).
  - For WSL-first, the source of truth is the Linux filesystem (ext4 in WSL2 is the primary target).
  - Windows-local Rust toolchain must NOT be required for WSL-first scenarios.
non_goals:
  - Implementing new SSH UX (remote open-folder) in this plan.
  - Removing or redesigning existing SSH build support.

# NOTE: This frontmatter is used as a planning tracker.
# Status values: pending | in_progress | completed | cancelled

todos:
  - id: wsl-metadata-remote
    content: Make workspace/package metadata discovery run cargo metadata in WSL (MetadataService must use IExecutionContext/IPathMapper)
    status: pending
  - id: startup-prereqs-gating
    content: Stop enforcing Windows cargo/rustup/rust-analyzer download at VS startup for WSL-first workspaces (make prereqs target-aware + lazy)
    status: pending
  - id: wsl-exec-streaming
    content: Fix WslExecutionContext to stream output to IProcessOutputSink (required for JSON build parsing and UX)
    status: pending
  - id: wsl-exec-fileops
    content: Fix WslExecutionContext file/dir ops (no shell operators without a shell) and add --cd fallback
    status: pending
  - id: wsl-process-cancel
    content: Implement robust WSL cancellation (setsid/pkill process group cleanup) in WslExecutionContext
    status: pending
  - id: uri-mapping-hardening
    content: Harden WslPathMapper (and SshPathMapper) URI mapping (file:///… vs file://…) and add unit tests for real VS URI forms
    status: pending
  - id: file-scanner-target-aware
    content: Make FileScanner/launch target generation target-aware (no .exe assumptions for WSL)
    status: pending
  - id: test-executor-remote
    content: Add WSL test execution support in TestExecutor (run ELF tests in WSL via IExecutionContext)
    status: pending
  - id: testcontainer-debug-engine
    content: Make TestContainer debug engine selection target-aware (NativeOnly is wrong for WSL)
    status: pending
  - id: targetstore-lifecycle
    content: Fix TargetSystemStore lifecycle (per-workspace service; clear on workspace change; avoid cross-workspace bleed)
    status: pending
  - id: target-change-reset
    content: Implement full state reset on target change (stop LSP, clear caches, invalidate tests, clear diagnostics)
    status: pending
  - id: debug-miengine-spikes
    content: Run MIEngine/gdbserver spikes for Open Folder WSL debugging (engine GUIDs, required options, source mapping, networking)
    status: pending
  - id: debug-gdbserver
    content: Implement real WSL debugging using gdbserver + MIEngine (not NativeOnly over wsl.exe)
    status: pending
  - id: debug-export-fix
    content: Fix DebugLaunchTargetProvider export/SupportsContext to handle WSL binaries without .exe extension
    status: pending
  - id: open-wsl-terminal
    content: Add right-click context menu "Open WSL Terminal Here" for WSL folders (nice-to-have, not a blocker)
    status: pending
  - id: integration-test
    content: Manual integration testing (open WSL folder, build, clippy, fmt, LSP, test discover/execute, debug)
    status: pending
  - id: regression-ssh-build
    content: Regression test: existing SSH build LocalSync remains intact and unchanged
    status: pending
---

# WSL Full Support Implementation Plan (WSL-first)

## Goal / user story

**Primary user story**: “My Rust repo lives in WSL. I open it in VS 2026 via `\\wsl$\…`, edit files, and VS runs `cargo build`, `cargo test`, `cargo clippy`, `cargo fmt`, rust-analyzer, and debugging entirely inside WSL.”

**Constraints**:
- WSL-first must work **without requiring a Windows Rust toolchain**.
- Do **not** remove existing SSH support (current: remote build after syncing to remote).

## Architectural invariants (do not violate)

- **Do not modify** `PathEx` (it normalizes `/` to `\` and is Windows-only).
- For Linux paths use `RemotePath`.
- All target-specific process execution must go through `IExecutionContext`.
- All path/URI translation across boundaries must go through `IPathMapper`.

## Current state (verified in code, 2025-12-14)

The repo has a meaningful remote foundation, but it is not yet “WSL-first complete”.

### Implemented (usable building blocks)

- **Target system foundation**: `ITargetSystem`, `TargetSystemService`, `TargetKind`, `IExecutionContext`, `IPathMapper`, `RemotePath`.
  - Files: `../src/RustAnalyzer.Remote/*`
- **WSL plumbing**:
  - `WslTargetSystem`, `WslPathMapper`, `WslExecutionContext`.
  - Files: `../src/RustAnalyzer.Remote/Wsl*.cs`
- **SSH plumbing** (must keep):
  - `SshTargetSystem`, `SshExecutionContext`, `SshPathMapper`, `LocalToRemoteSyncMapper`.
  - Files: `../src/RustAnalyzer.Remote/Ssh*.cs`, `../src/RustAnalyzer.Remote/LocalToRemoteSyncMapper.cs`
- **Build/clean/clippy/fmt call path is remote-aware**:
  - `BuildFileContext` and `CmdServices` pass `(IExecutionContext, IPathMapper)` into `ToolchainService`.
  - Files: `../src/RustAnalyzer/Editor/BuildFileContext.cs`, `../src/RustAnalyzer/Shell/CmdServices.cs`, `../src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
- **LSP has remote startup + middleware**:
  - `LanguageClient` can start remote rust-analyzer via `IExecutionContext.StartRustAnalyzerAsync()`.
  - `RustAnalyzerMiddleLayer` rewrites URIs.
  - Files: `../src/RustAnalyzer/LanguageService/LanguageClient.cs`, `../src/RustAnalyzer/LanguageService/RustAnalyzerMiddleLayer.cs`
- **Target dropdown exists + persisted selection**:
  - `TargetSystemStore` persists selection under `.vs/rust-analyzer/workspace-settings.json`.
  - File: `../src/RustAnalyzer/Shell/TargetSystemCommands.cs`

### Known missing / incorrect pieces (WSL-first blockers)

These are the items that prevent WSL-first from being reliable and/or from working without Windows cargo:

1) **Metadata/package discovery still uses local Windows cargo**
- `MetadataService` calls `IToolchainService.GetWorkspaceAsync(manifestPath, ct)` with **no execution context**, so it will require Windows cargo and will not work when the repo is only in WSL.
- File: `../src/RustAnalyzer.TestAdapter/Cargo/MetadataService.cs`

2) **Startup prereqs & installer are Windows-centric and always run**
- On package load, VS runs prerequisite checks and downloads Windows rust-analyzer.
- `PreReqsCheckService` currently checks Windows `cargo.exe`/`rustup.exe`.
- Files:
  - `../src/RustAnalyzer/RustAnalyzerPackage.cs`
  - `../src/RustAnalyzer/Infrastructure/PreReqsCheckService.cs`
  - `../src/RustAnalyzer/Infrastructure/RlsInstallerService.cs`

3) **WSL execution context correctness issues**
- `WslExecutionContext` currently:
  - Does not stream output to `IProcessOutputSink` (critical for build JSON parsing and good UX).
  - Uses shell operators (e.g. `&&`) without running through a shell, making `FileExistsAsync` / `DirectoryExistsAsync` logically wrong.
- File: `../src/RustAnalyzer.Remote/WslExecutionContext.cs`

4) **Test execution is host-only**
- `TestExecutor` runs test binaries using `ProcessRunner` (cannot run ELF tests via UNC path).
- File: `../src/RustAnalyzer.TestAdapter/TestExecutor.cs`

5) **Debugging is not the desired WSL MIEngine/gdbserver workflow**
- Current WSL debug path wraps `wsl.exe` and uses `NativeOnly_guid`. This is not the “VS gdbserver on WSL” model.
- Export is restricted to `.exe`.
- `TestContainer.DebugEngines` is hard-coded to `NativeOnly_guid`.
- Files:
  - `../src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`
  - `../src/RustAnalyzer/TestAdapter/TestContainer.cs`

6) **Launch target generation still contains `.exe` assumptions**
- `FileScanner` uses `target.GetPath(profile)` without passing the current `TargetKind`, which returns `.exe` paths.
- File: `../src/RustAnalyzer/Editor/FileScanner.cs`

7) **Target service lifecycle is static and not per-workspace**
- `TargetSystemStore` keeps a single static `_service` and is not cleared on workspace change.
- File: `../src/RustAnalyzer/Shell/TargetSystemCommands.cs`

## Important note about doc drift

`docs/REMOTE_WSL_SSH_PLAN.md` contains an “Implementation Complete” status section. The codebase does have many of those components, but **WSL-first is not complete yet** due to the blockers above (metadata/prereqs/WSL execution correctness/tests/debug). Treat the WSL plan in *this* document as the authoritative WSL-first execution plan.

## Implementation strategy (phased, WSL-first, SSH-safe)

### Phase 0 — Make target context + lifecycle correct (foundation for everything)

**Goal**: Ensure every component can reliably query “what target am I on?” and that switching workspaces/targets does not leak state.

**Tasks**
- Make `TargetSystemStore` **per-workspace**:
  - Maintain a dictionary keyed by workspace root (or a stable workspace identity) → `ITargetSystemService`.
  - Clear on active workspace change.
  - Keep persistence as-is, but ensure it’s keyed per workspace.
  - File: `../src/RustAnalyzer/Shell/TargetSystemCommands.cs`
- Wire “target changed” → “full state reset”:
  - Stop rust-analyzer
  - Clear metadata cache
  - Invalidate tests
  - Clear error list diagnostics
  - Re-run (lazy) prereq checks for the new target

**Acceptance criteria**
- Switching folders in one VS session does not reuse a stale target list/selection.
- Switching target in the combo visibly restarts/rebinds state (no stale LSP/diagnostics/tests).

### Phase 1 — Fix startup prerequisites and installer gating (WSL-first cannot require Windows cargo)

**Goal**: Don’t block VS startup for WSL-first users and don’t require Windows cargo/rustup.

**Tasks**
- Refactor prereqs into **target-aware checks**:
  - Local target: current checks are fine.
  - WSL target: use `WslExecutionContext` to check `cargo`, `rustup`, `rust-analyzer`, and (for debug later) `gdbserver`.
  - SSH target: keep existing behavior (at minimum, build-only scenario).
  - File: `../src/RustAnalyzer/Infrastructure/PreReqsCheckService.cs`
- Make prereqs **lazy**:
  - Don’t run “cargo.exe exists” at package load if the workspace is WSL-first.
  - Instead, run when the user triggers an operation (build/LSP/test/debug) for that target.
  - Files: `../src/RustAnalyzer/RustAnalyzerPackage.cs`, plus the call sites for operations.
- Gate Windows rust-analyzer auto-install:
  - If the active flow uses remote rust-analyzer (WSL), do not force-install `rust-analyzer.exe` on Windows.
  - Keep Windows installer for local target scenarios.
  - File: `../src/RustAnalyzer/Infrastructure/RlsInstallerService.cs` and call site in `RustAnalyzerPackage`.

**Acceptance criteria**
- A machine with only WSL Rust installed (no Windows cargo) can open a WSL repo in VS without being forced into Windows prereq failures.

### Phase 2 — Make WSL execution context correct (streaming, file ops, cancellation)

**Goal**: Ensure WSL commands behave correctly and provide the same streaming semantics used by build parsing.

**Tasks**
- Implement streaming behavior in `WslExecutionContext.ExecuteAsync`:
  - Mirror `SshExecutionContext` pattern: attach a `ProcessOutputRedirector` that forwards to `IProcessOutputSink`.
- Fix `FileExistsAsync` / `DirectoryExistsAsync`:
  - Do not use `&&` unless executing under `sh -lc`.
  - Prefer robust patterns:
    - `sh -lc "test -f 'path' && echo 1"`
    - or `stat`/exit-code based checks.
- Add `wsl.exe --cd` compatibility fallback:
  - If `--cd` not available, use `sh -lc 'cd … && …'` (with careful quoting).
- Implement robust cancellation:
  - Add planned `setsid --fork` + `pkill -g` cleanup.

**Files**
- `../src/RustAnalyzer.Remote/WslExecutionContext.cs`

**Acceptance criteria**
- Remote build output appears in the Output pane progressively, and cargo JSON diagnostics are parsed correctly.
- Cancellation reliably stops runaway cargo/rustc processes in WSL.

### Phase 3 — Make metadata + target/launch discovery WSL-first

**Goal**: Package discovery, targets, and test containers must work with cargo running in WSL.

**Tasks**
- Make `MetadataService` call the remote-aware overload of `GetWorkspaceAsync`:
  - It must obtain `(IExecutionContext, IPathMapper)` from current target.
  - Recommended approach: inject `IWorkspaceContextAccessor` (or a narrow “current target provider”) into `MetadataServiceFactory` and `MetadataService`.
  - Files:
    - `../src/RustAnalyzer/Infrastructure/MetadataServiceFactory.cs`
    - `../src/RustAnalyzer.TestAdapter/Cargo/MetadataService.cs`
- Make `FileScanner` target-aware when producing output references / launch settings:
  - Use `target.GetPath(profile, currentTarget.Kind)`.
  - Ensure “launch target export” does not assume `.exe` for WSL.
  - File: `../src/RustAnalyzer/Editor/FileScanner.cs`

**Acceptance criteria**
- Opening a WSL repo shows runnable/debuggable targets in Solution Explorer.
- Build contexts appear and work for WSL repo without Windows cargo.

### Phase 4 — WSL build/clean/clippy/fmt end-to-end validation

**Goal**: WSL builds produce navigable diagnostics and do not regress SSH build.

**Tasks**
- Validate the current remote build integration end-to-end after Phase 2+3.
- Review remaining Windows-only probes:
  - `ToolchainServiceExtensions.GetCommandOutput(...)` still uses `cmd.exe` and should be local-only or have a remote equivalent for remote scenarios.
  - File: `../src/RustAnalyzer.TestAdapter/Cargo/ToolChainServiceExtensions.cs`

**Acceptance criteria**
- WSL: Build/Clean/Clippy/Fmt run inside WSL and diagnostics navigate to files under `\\wsl$\…`.
- SSH LocalSync build continues to work exactly as before.

### Phase 5 — LSP hardening (remote rust-analyzer in WSL)

**Goal**: Remote rust-analyzer + URI rewriting works reliably for WSL UNC paths.

**Tasks**
- Fix URI mapping correctness:
  - Avoid constructing URIs via string concatenation like `"file://" + "/home/..."`.
  - Ensure Unix absolute paths become `file:///home/...`.
  - Ensure VS-understood URIs for WSL UNC are emitted correctly.
  - Files:
    - `../src/RustAnalyzer.Remote/WslPathMapper.cs`
    - `../src/RustAnalyzer.Remote/SshPathMapper.cs`
    - `../src/RustAnalyzer/LanguageService/RustAnalyzerMiddleLayer.cs`
- Add tests for URI and path variants:
  - `\\wsl$\Distro\...` and `\\wsl.localhost\Distro\...`
  - file URI forms that VS emits for UNC paths (capture from real VS session during spike).
- Ensure remote rust-analyzer process lifecycle is managed:
  - stop on target change
  - stop on workspace close

**Acceptance criteria**
- Completion/hover/diagnostics/go-to-definition work for WSL repo.
- No “file not found” when navigating to diagnostics.

### Phase 6 — Tests (discover + execute in WSL)

**Goal**: VS Test Explorer can both discover and execute tests when binaries are built in WSL.

**Tasks**
- Update `TestExecutor` to execute tests via `IExecutionContext` when `TargetKind != Local`.
  - Replace `ProcessRunner.RunWithLogging(exe, ...)` with `executionContext.ExecuteAsync(remoteExePath, ...)`.
  - Decide how to handle “debug tests” (likely MIEngine attach path, not `LaunchProcessWithDebuggerAttached`).
  - File: `../src/RustAnalyzer.TestAdapter/TestExecutor.cs`
- Make `TestContainer.DebugEngines` target-aware (NativeOnly is wrong for WSL).
  - File: `../src/RustAnalyzer/TestAdapter/TestContainer.cs`

**Acceptance criteria**
- “Run All Tests” works for WSL repo.
- Test results and failure navigation work.

### Phase 7 — Debugging (MIEngine + gdbserver in WSL)

**Goal**: Implement the debugging model you want: VS uses MIEngine and gdbserver in WSL.

**Spikes (do first)**
- Determine correct debug engine GUID and required VS workload/components for Open Folder.
- Determine the exact `VsDebugTargetInfo*` shape and options needed.
- Determine source mapping configuration for UNC ↔ Linux paths.
- Confirm WSL2 networking behavior (localhost port forwarding vs using WSL IP).

**Implementation tasks**
- Update `DebugLaunchTargetProvider`:
  - For WSL target: start `gdbserver` in WSL and connect MIEngine.
  - Keep local Windows debugging path unchanged.
  - Keep SSH behavior as-is (currently shows a message).
  - Fix export to not be `.exe`-only.
  - File: `../src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`

**Acceptance criteria**
- F5 hits breakpoints in WSL-built ELF.
- Stack/locals work.
- Stepping works.

### Phase 8 — UX polish (optional)

- “Open WSL Terminal Here” context menu (only for WSL folders).
- Better error messages and guided “Install in WSL” instructions.
- Document expected performance characteristics (UNC + WSL2).

## Regression / validation matrix (must be maintained)

- **Local Windows repo + Local target**: build, LSP, tests, debug.
- **WSL repo (UNC) + WSL target**: build, LSP, tests, debug.
- **Local Windows repo + SSH target (LocalSync)**: build must keep working (do not remove sync).

## Gotchas / sharp edges (call out early)

- **UNC path performance**: `\\wsl$\...` IO can be slower than Linux native; but builds running in WSL are fine.
- **`.vs` folder on UNC**: persisting settings under `.vs` in WSL UNC path can be slower and occasionally flaky; keep an escape hatch if needed.
- **URI correctness is non-negotiable**: incorrect `file://` forms will silently break LSP navigation.
- **WSL command quoting**: if you must fall back to `sh -lc`, quoting/escaping must be correct.
- **WSL2 networking**: gdbserver port forwarding usually works, but have a fallback to WSL IP (`hostname -I`).
- **Nightly requirement**: test discovery/execution currently relies on `-Zunstable-options` JSON output; this may require nightly in WSL.

## Notes on SSH support (do not break)

- Keep SSH LocalSync behavior:
  - source is local Windows
  - build executes remotely after sync
  - LSP stays local for LocalSync workspaces (this is intentional and already implemented in `LanguageClient`).


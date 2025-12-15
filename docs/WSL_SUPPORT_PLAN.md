## Goal
Add **WSL support** to this Visual Studio (VS) extension so that when a user opens a Rust workspace located under `\\wsl.localhost\…` (or `\\wsl$\…`) in **Visual Studio (not VS Code)**, the extension can:

- Run **build** (cargo build)
- Run **cargo clippy**
- Run **cargo fmt**
- Run **debug (F5 / Ctrl+F5)**
- Run **tests in Test Explorer** (discovery + execution) *(new: needed for parity)*

…with the actual tool execution happening **inside the selected WSL distribution**, while keeping existing **Windows-native behavior unchanged**.

This document is a **plan only**: it proposes a minimal-change implementation strategy and highlights the “gotchas” and required verification steps.

---

## Non-goals / constraints (explicit)
- **Do not change Windows behavior**: Windows workspaces must keep their current `.exe` target naming, debug dropdown population, and native debugging launch behavior.
- **No broad refactors**: prefer small, WSL-gated shims at chokepoints (tool invocation, metadata path rewriting, output path rewriting).
- **WSL detection is workspace-scoped**: WSL behavior must activate only for WSL UNC workspaces (`\\wsl.localhost\...`, `\\wsl$\...`) unless a future optional “target system” UI overrides it.

---

## Current behavior (Windows) – how the pieces fit

### How build/clippy/fmt are invoked
- VS commands (`Tools > Rust Tools` and context menus) are implemented in `src/RustAnalyzer/Shell/ToolChainCommands.cs`.
- These commands call `CmdServices.ExecuteToolchainOperationAsync(...)` (`src/RustAnalyzer/Shell/CmdServices.cs`), which delegates to `IToolchainService` implementations.
- The concrete `IToolchainService` is `ToolchainService` (`src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`).

### How debug targets appear in the VS debug dropdown
- Debug targets are produced by the folder-workspace scanning pipeline:
  - `FileScanner` (`src/RustAnalyzer/Editor/FileScanner.cs`) creates `DebugLaunchActionContext` entries and associates them with cargo targets.
  - Target executable paths are currently derived from cargo metadata (`Workspace.TargetDirectory`, target name, and profile mapping) via `WorkspaceExtensions.GetPath(...)` (`src/RustAnalyzer.TestAdapter/Cargo/WorkspaceExtensions.cs`).

### How debugging is launched
- `DebugLaunchTargetProvider` (`src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`) is registered for file extension **`.exe`**:
  - `[ExportLaunchDebugTarget(..., new[] { ".exe" }, ...)]`
- It resolves the cargo target and calls `VsShellUtilities.LaunchDebugger(...)` with:
  - `VsDebugTargetInfo.dlo = DLO_CreateProcess`
  - `bstrExe = <path-to-exe>`
  - `clsidCustom = VSConstants.DebugEnginesGuids.NativeOnly_guid`

---

## Why it is Windows-only today (hard assumptions)

### Hardcoded Windows executables + tool discovery
- `src/RustAnalyzer.TestAdapter/Constants.cs`
  - `CargoExe = "cargo.exe"`
  - `RustUpExe = "rustup.exe"`
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
  - `GetCargoExePath()` uses `Constants.CargoExe.FindInPath()`.
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainServiceExtensions.cs`
  - `GetCommandOutput(...)` runs via **`cmd.exe /c`**:
    - `ProcessRunner.Run("cmd.exe", new[] { "/c", $"{toolName} {args}" }, ...)`

### Path model forces Windows separators
- `src/RustAnalyzer.TestAdapter/Common/PathEx.cs`
  - Constructor replaces `/` with `\` unconditionally.
  - This breaks Linux paths if they ever enter the system.

### Target executable naming assumes `.exe`
- `src/RustAnalyzer.TestAdapter/Cargo/WorkspaceExtensions.cs`
  - `CrateTypeInfos[CrateType.Bin] = ("", ".exe")`

### Output parsing assumes Windows paths
- `src/RustAnalyzer.TestAdapter/Cargo/BuildJsonOutputParser.cs`
  - Consumes `cargo build --message-format json` output.
  - Assumes file paths can be combined into Windows paths.
- `src/RustAnalyzer/Infrastructure/StringBuildMessagePreprocessor.cs`
  - Regexes for clippy/rustfmt output assume Windows-style relative paths and combine with a Windows workspace root.

### Debug provider is `.exe`-only
- `src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`
  - Registered for `new[] { ".exe" }` only.
  - Uses Windows “native only” debug engine.

---

## WSL support definition

### What “WSL workspace” means
A workspace is considered WSL-backed if `workspaceContext.Location` (or the solution/workspace root) starts with:

- `\\wsl.localhost\<DistroName>\...`
- `\\wsl$\<DistroName>\...`

Examples:
- `\\wsl.localhost\Ubuntu-22.04\home\ranji\trader-one\`
- `\\wsl$\Ubuntu\mnt\c\Repos\myproj\`

### WSL1 vs WSL2 (requirements note)
- Prefer **WSL2** for reliability/performance. WSL1 may behave differently for filesystem events and I/O performance.
- The implementation should not hard-require WSL2, but the plan should document “WSL2 recommended” if we hit watcher/perf limits.

### Path mapping requirement
For WSL execution and output processing we need a **reversible mapping**:

- **Windows UNC → Linux path**
  - `\\wsl.localhost\Ubuntu-22.04\home\u\p\Cargo.toml` → `/home/u/p/Cargo.toml`
- **Linux absolute path → Windows UNC (same distro)**
  - `/home/u/p/src/main.rs` → `\\wsl.localhost\Ubuntu-22.04\home\u\p\src\main.rs`

This mapping must be:
- deterministic
- fast
- safe for spaces
- not dependent on fragile regexes

---

## Minimal-change strategy (high level)

### Principle
**Do not change the Windows path**. Add an *additional* execution + mapping path that activates **only** when the workspace root is WSL UNC.

### Core idea
Introduce a small **WSL adapter layer**:

1. **Detect** WSL workspaces (based on root path prefix)
2. **Run** cargo/rustup inside WSL using `wsl.exe` (Windows host tool)
3. **Translate** paths:
   - Before executing commands: UNC → Linux paths in arguments
   - After command output: Linux paths → UNC for VS navigation
4. **Debug** using Visual Studio’s WSL/SSH transport convention (`bstrPortName = "SSH:wsl+<Distro>"`) where possible, while keeping current Windows debugging unchanged.

### Staged rollout (add explicit feature flags)
To reduce risk and keep Windows stable, implement WSL support in stages with explicit feature flags:

- **Stage A (EnableWslBuildTools)**: build + clippy + fmt + clean + metadata + diagnostics path mapping
- **Stage B (EnableWslDebug)**: debug dropdown + launch in WSL (`SSH:wsl+<distro>`)
- **Stage C (EnableWslTests)**: Test Explorer discovery/execution in WSL
- **Stage D (EnableWslLsp)** *(optional)*: rust-analyzer LSP sourced from WSL distro instead of Windows bundle

Feature flags should default to:
- Stage A: **on** when WSL workspace detected (after prereq checks)
- Stage B/C/D: **off** by default initially, until validated on supported VS versions

Where to store flags:
- Prefer per-workspace settings via VS “Open Folder” settings mechanism (aligns with the VSSDK Open Folder sample), or a hidden global option if workspace settings aren’t available in our context.

---

## Proposed technical design

### 1) Add `WslInfo` + path mapper (new small utility)
Create a new helper in a low-level shared location (suggestion: `src/RustAnalyzer.TestAdapter/Common/`):

- `WslInfo.TryParseFromUnc(PathEx workspaceRoot)`
  - Detects `\\wsl.localhost\` and `\\wsl$\`
  - Extracts:
    - `HostPrefix` (either `\\wsl.localhost` or `\\wsl$`)
    - `DistroName`
    - `UncDistroRoot` = `\\wsl.localhost\<Distro>\`

- `string ToLinuxPath(PathEx uncPath)`
  - Preconditions:
    - `uncPath` begins with `UncDistroRoot`
  - Implementation:
    - strip prefix
    - replace `\` with `/`
    - ensure it starts with `/`

- `PathEx ToUncPath(string linuxAbsPath)`
  - Preconditions:
    - linuxAbsPath is absolute and starts with `/`
  - Implementation:
    - prefix with `UncDistroRoot`
    - replace `/` with `\`

Why this is minimal:
- Doesn’t change `PathEx` behavior.
- Avoids letting Linux paths leak into `PathEx`.

Optional alignment with existing stubs:
- There are existing (currently stubby) remote target interfaces under `src/RustAnalyzer.Remote/` (`IRemoteTarget`, `IRemoteTargets`).
- **Optional**: model `WslInfo` as (or alongside) an `IRemoteTarget` implementation to keep the “target system” concept extensible (WSL today, SSH later). This should remain a thin type alias/wrapper, not a refactor.

### 2) Introduce a single “command invocation” abstraction
The current code mixes:
- “which executable to run” (cargo.exe)
- “how to invoke it” (direct vs `cmd.exe /c`)
- “how to pass working directory”

Add a minimal abstraction (no major refactor):

- `struct ToolInvocation { string FileName; string[] Args; string WorkingDirectory; }`
- `ToolInvocation BuildCargoInvocation(BuildTargetInfo bti, string cargoArgs)`
  - If **Windows workspace**:
    - `FileName = <cargo.exe>`
    - `Args = split(cargoArgs)` (or keep existing string-based `RunAsync` path)
    - `WorkingDirectory = <manifest dir>`
  - If **WSL workspace**:
    - `FileName = <path-to-wsl.exe>`
    - `Args = ["-d", distro, "--cd", <linuxWorkingDir>, "--exec", "cargo", <cargoArgs...>]`
    - `WorkingDirectory = Environment.SystemDirectory` (or null)

Notes:
- Prefer `--exec` + argument array to avoid quoting bugs.
- If VS/.NET quoting makes `--exec` hard, fall back to: `wsl.exe -d <distro> --cd <linuxWorkingDir> -- sh -lc "cargo ..."` and implement careful escaping.
- **Environment handling difference**: Windows uses a null-terminated environment block for debugging; for WSL command invocation we should pass env differently (see section “Environment variables”). Do not reuse Windows `ToEnvironmentBlock()` for WSL tool execution.

Where to apply:
- `ToolchainService.BuildAsync / RunClippyAsync / RunFmtAsync / CleanAsync`
- `ToolchainService.GetWorkspaceAsync` (cargo metadata)
- `ToolchainServiceExtensions.GetCommandOutput*` (rustup/cargo version queries, toolchain switching)

### 3) WSL-aware prerequisites checks
Update `PreReqsCheckService` to be workspace-aware:

- If workspace is **Windows-native**: keep existing checks (`cargo.exe`, `rustup.exe` in PATH)
- If workspace is **WSL**:
  - verify `wsl.exe` exists
  - verify distro exists / is reachable
  - verify `cargo` and `rustup` exist **inside the distro** by running:
    - `wsl.exe -d <distro> --exec cargo --version`
    - `wsl.exe -d <distro> --exec rustup --version`

Why:
- Current checks will always fail in WSL workspaces because `cargo.exe`/`rustup.exe` aren’t expected.

Additional prereq items to validate:
- **`wsl.exe` location**: don’t assume it’s in PATH; prefer `%SystemRoot%\\System32\\wsl.exe` and verify existence.
- **Distro selection**: for `\\wsl.localhost\\<Distro>\\...` the distro is explicit; for robustness allow an override if the user’s distro does not have rust installed.

### 4) Make cargo metadata usable without changing `PathEx`
Problem:
- `cargo metadata` in WSL returns Linux paths (e.g., `/home/u/p`).
- `Workspace` model uses `PathEx`, which forcibly converts `/` to `\` and assumes Windows.

Plan:
- In `ToolchainService.GetWorkspaceAsync(...)`, when in WSL mode:
  1. Run `cargo metadata` in WSL.
  2. Parse JSON using `JObject` (already using Newtonsoft elsewhere).
  3. Rewrite the path-bearing fields to UNC before deserializing into `Workspace`:
     - `workspace_root`
     - `target_directory`
     - `packages[].manifest_path`
     - `packages[].targets[].src_path`
     - any other path-like fields consumed by the extension
  4. Then `JsonConvert.DeserializeObject<Workspace>(...)` works unchanged.

This keeps changes localized and avoids a full “Linux path type” introduction.

Critical note: avoid `Path.Combine` with Linux absolute paths
- `Path.Combine(<UNC-root>, \"/home/.../file.rs\")` returns the Linux path and discards the UNC root.\
  Therefore **any** code that combines a UNC root with a Linux absolute path must use the explicit UNC mapper (`WslInfo.ToUncPath`) instead of `Path.Combine`/`PathEx.Combine`.

### 5) WSL-aware build/clippy/fmt output mapping

#### 5a) `cargo build --message-format json`
Problem:
- JSON contains Linux paths.
- VS error list navigation must point to UNC paths.

Plan:
- Extend `BuildJsonOutputParser.Parse(...)`:
  - Detect WSL mode based on `workspaceRoot` (UNC prefix).
  - Before producing `DetailedBuildMessage.File`, translate any Linux absolute paths to UNC.
  - Specifically handle:
    - `obj.target.src_path`
    - `spans[].file_name`
    - `manifest_path` (project file)
  - Ensure we never call `Path.Combine(workspaceRoot, linuxAbsPath)`; use `WslInfo.ToUncPath(linuxAbsPath)` for absolute Linux paths.

#### 5b) clippy/fmt string output
Problem:
- `StringBuildMessagePreprocessor` combines root path with captured file paths.
- On WSL, clippy often prints absolute Linux paths.

Plan:
- Add WSL mode branch:
  - if captured path starts with `/`, convert to UNC using `WslInfo`
  - if captured path is relative, keep current behavior (combine with root)
  - Add WSL variants for Windows-specific regexes (e.g., fmt output like `Diff in /home/... at line N:`)
  - Avoid combining UNC root with captured Linux absolute paths via `Combine`/`Path.Combine`.

### 5c) Test discovery output parsing (new)
The test discovery path relies on parsing `cargo test --no-run` stderr to find produced test executables.

Problem:
- `ToolchainService` currently uses a Windows-specific regex expecting backslashes and `.exe`.\
  For WSL, cargo prints Linux paths and the test binaries are extensionless ELF paths.

Plan:
- Add a WSL-specific regex (or a parameterized regex) that handles:\
  `/.../target/<profile>/deps/<name>-<hash>` and `/.../target/<profile>/deps/<name>-<hash>.d` patterns.\
  Keep the existing Windows regex unchanged.

### 6) Debugging in WSL

#### Key constraint
Debugging a Linux binary is not the same as debugging a Windows `.exe`.

We must keep existing Windows behavior:
- Windows workspaces keep the current `.exe` provider and native debugger engine.

#### 6a) Make debug targets appear for WSL binaries
Today, bin targets are named with `.exe` via `WorkspaceExtensions`.

Plan:
- Update `WorkspaceExtensions.CreateTargetFileName(...)` to choose extension by workspace type:
  - If workspace root is WSL UNC → **no extension** for `CrateType.Bin`
  - Else → keep `.exe`

This is a minimal change because it affects only the filename calculation and is gated by workspace path.

#### 6b) Ensure `DebugLaunchTargetProvider` can be selected
Today the provider is registered for `.exe` only.

Plan:
- Register an additional provider (or broaden the existing one) to include extensionless binaries when the workspace is WSL.

Options (investigate which VS supports):
- Add a second `[ExportLaunchDebugTarget(..., new[] { "" }, ...)]`
- Or change existing registration to `new[] { ".exe", "" }`
- If empty-extension matching is not supported by VS, fall back to registering for a synthetic extension (requires adjusting target naming) – avoid unless necessary.

#### 6c) Launch WSL debugging using VS’s WSL port transport
There is a known internal convention used by VS for WSL debugging:

- `VsDebugTargetInfo(4).bstrPortName = "SSH:wsl+<DistroName>"`

Reference:
- Microsoft Q&A discussion: `https://learn.microsoft.com/en-us/answers/questions/2280428/attaching-to-wsl-process-from-a-visual-studio-exte`

Plan:
- In `DebugLaunchTargetProvider.LaunchDebugTargetAsync(...)`, add a WSL branch:
  - Convert `processName` (UNC) → Linux path `/...`
  - Convert `workingDirectory` similarly
  - Set:
    - `bstrExe = <linux path>`
    - `bstrCurDir = <linux working dir>`
    - `bstrPortName = "SSH:wsl+<DistroName>"`
    - Keep engine GUID as `NativeOnly_guid` initially (VS may route to MIEngine based on port)
  - If `VsDebugTargetInfo` is insufficient, switch to `IVsDebugger4.LaunchDebugTargets4(...)` with `VsDebugTargetInfo4`.

Important: this should be implemented such that:
- On non-WSL workspaces, the code path is identical to today.

#### 6d) Debug environment variables
Today `bstrEnv` is composed using Windows paths (`bin/lib` from rustup toolchain on Windows).

WSL plan:
- For WSL debugging, do not attempt to construct a Windows environment block.
- Instead pass environment via the Linux debugger configuration if supported (or omit initially).
- Keep the existing Windows env behavior for local debugging.

#### 6e) Rust toolchain paths for debug (WSL-specific)
Current Windows code uses `ToolchainServiceExtensions.GetBinAndLibPathsAsync(...)`, which is Windows/rustup-layout specific:
- it locates `.rustup\\toolchains\\...\\bin` and `.rustup\\toolchains\\...\\lib\\rustlib\\x86_64-pc-windows-msvc\\lib`
- it assumes a Windows target triple (`x86_64-pc-windows-msvc`)

For WSL debugging:
- If we keep environment minimal initially (Stage B), we can omit toolchain bin/lib injection entirely and rely on the debugger/WSL environment to locate dependencies.
- If we need parity with Windows env behavior, compute the relevant paths **inside WSL** instead:
  - Determine the active toolchain/sysroot via `rustc --print sysroot` (run in WSL)
  - Derive bin/lib locations from that sysroot (Linux target triple, e.g. `x86_64-unknown-linux-gnu`), or query with `rustc -vV` and `rustup show` inside WSL
  - Map any paths that must flow back into VS (e.g., for logging) to UNC via `WslInfo`

Keep this logic WSL-gated; do not change the existing Windows `GetBinAndLibPathsAsync` behavior.

### 7) Test Explorer (discovery + execution) in WSL (new, critical)
This repo includes a test adapter that executes test binaries.\
For WSL workspaces these binaries are Linux ELF files and cannot be executed on Windows directly.

Plan:
- **Discovery**:\
  Keep the discovery flow, but ensure that any parsed “test exe” paths are mapped correctly (Linux → UNC for storage/VS; UNC → Linux for execution).\
  Update the test executable path parsing regex as described in section 5c.
- **Execution**:\
  When executing tests in WSL mode, run the test binaries via `wsl.exe`:\
  `wsl.exe -d <distro> --cd <linuxWorkingDir> --exec <linuxTestExePath> <args>`.\
  Preserve the existing Windows execution path.
- **Debugging tests**:\
  If `LaunchProcessWithDebuggerAttached` is used, WSL requires the same remote transport concept as app debugging.\
  Gate under `EnableWslTests` (and possibly a separate `EnableWslTestDebug`) until validated.

### 8) rust-analyzer LSP behavior in WSL (new, likely needed for good UX)
The extension currently launches a bundled Windows `rust-analyzer.exe`.\
For WSL workspaces, this can cause path/URI mismatches and inconsistent diagnostics.

Plan options (stage-gated):
- **Stage D (recommended)**: use `rust-analyzer` inside the WSL distro, launched via `wsl.exe`.\
  This aligns the language server with the workspace OS and paths.\
  Add prereq check: `wsl.exe -d <distro> --exec rust-analyzer --version`.\
  Provide a setting to choose between “Windows RA” and “WSL RA” for WSL workspaces.
- **Fallback**: keep Windows `rust-analyzer.exe` for WSL initially if it works “well enough”, but document limitations.

### 9) Workspace file watching / cache invalidation on WSL UNC (make this explicit)
`MetadataService` relies on VS workspace file watcher events to invalidate cached packages and refresh targets.

Plan:
- Validate whether `IFileWatcherService` events fire reliably for `\\wsl.localhost\\...`.\
  If unreliable, add a WSL-only fallback strategy:
  - conservative approach: refresh metadata on-demand at operation boundaries (before build/debug/test discovery)
  - optional polling approach: periodically re-run `cargo metadata` for WSL workspaces (guarded by a feature flag and with backoff)

Keep the Windows watcher path untouched.

---

## File-by-file change plan (minimal touch points)

### New code (small)
- Add `WslInfo` + mapper utility (suggested location):
  - `src/RustAnalyzer.TestAdapter/Common/WslInfo.cs`

### Modify (WSL-gated changes)
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
  - Make `GetWorkspaceAsync`, `BuildAsync`, `RunClippyAsync`, `RunFmtAsync`, `CleanAsync` choose Windows vs WSL invocation.
- `src/RustAnalyzer.TestAdapter/Cargo/ToolChainServiceExtensions.cs`
  - Replace `cmd.exe /c` usage when in WSL.
  - Add WSL-aware `GetCommandOutput*`.
- `src/RustAnalyzer/Infrastructure/PreReqsCheckService.cs`
  - Make checks conditional based on workspace type.
- `src/RustAnalyzer.TestAdapter/Cargo/WorkspaceExtensions.cs`
  - Bin extension: `.exe` for Windows, empty for WSL.
- `src/RustAnalyzer.TestAdapter/Cargo/BuildJsonOutputParser.cs`
  - Map Linux paths to UNC in WSL mode.
- `src/RustAnalyzer/Infrastructure/StringBuildMessagePreprocessor.cs`
  - Support Linux absolute paths and map them to UNC.
- `src/RustAnalyzer/Debugger/DebugLaunchTargetProvider.cs`
  - Add WSL debug launch branch using `bstrPortName = SSH:wsl+<Distro>`.
  - Update provider registration to cover extensionless binaries (investigate VS support).

- **Test Adapter / Test Explorer path** (new):
  - `src/RustAnalyzer.TestAdapter/TestExecutor.cs`: execute test binaries via `wsl.exe` for WSL workspaces.
  - `src/RustAnalyzer.TestAdapter/TestDiscoverer.cs` and/or `src/RustAnalyzer.TestAdapter/TestDiscovererCommon.cs`: ensure discovery paths and stored container paths work for WSL UNC.
  - `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`: update `TestExecutablePathCracker` parsing for WSL output.

- **LSP / rust-analyzer** (optional Stage D):
  - `src/RustAnalyzer/LanguageService/LanguageClient.cs`: allow launching rust-analyzer via WSL for WSL workspaces.
  - `src/RustAnalyzer/Infrastructure/RlsInstallerService.cs`: consider skipping Windows RA install/use for WSL workspaces if WSL RA is enabled.

- **Environment handling**:
  - `src/RustAnalyzer.TestAdapter/Common/EnvironmentExtensions.cs`: keep Windows env block, but introduce separate WSL env passing strategy for WSL tool/test execution (do not reuse `ToEnvironmentBlock`).

### Optional / later (if needed)
- `src/RustAnalyzer/Shell/TargetSystemCommands.cs`
  - Populate a target-system dropdown with:
    - `Local Machine`
    - `WSL: <distro>`
  - This would allow using WSL even when workspace isn’t under `\\wsl...`.

---

## Testing plan (what to validate before shipping)

### Environment setup
- Windows 11 + Visual Studio 2022 (17.12+) + WSL2 distro (Ubuntu recommended).
- Ensure the distro has:
  - `rustup`, `cargo`, `rustc`, `clippy`, `rustfmt`

### Core scenarios

#### 1) Windows workspace regression
- Open a normal Windows folder (e.g., `C:\Repos\myproj`).
- Verify:
  - Build/clippy/fmt run exactly as before.
  - Debug dropdown shows `.exe` targets.
  - F5 launches with native debugger.

#### 2) WSL workspace – build
- Open `\\wsl.localhost\<distro>\home\...\myproj`.
- Run Build.
- Verify:
  - cargo executes inside WSL
  - build output appears in VS Output pane
  - errors are clickable and open the correct file under `\\wsl.localhost\...`

#### 3) WSL workspace – clippy/fmt
- Run clippy/fmt.
- Verify:
  - output parsing doesn’t garble paths
  - warnings/errors are clickable

#### 4) WSL workspace – debug
- Ensure a bin target exists.
- Verify:
  - debug dropdown entries appear
  - F5 starts a WSL debug session (breakpoints hit)
  - stdout/stderr behavior is acceptable

### Edge cases
- Paths with spaces.
- Multiple distros.
- `\\wsl$\` vs `\\wsl.localhost\`.
- `\r\n` handling in output streams.
- WSL file watcher behavior (does VS fire workspace change events for UNC WSL paths?).
- Very deep directory structures (Windows long path support; WSL UNC paths can be long).
- Unicode paths.
- Slow/laggy `File.Exists` on UNC WSL paths (avoid tight loops).

### Test Explorer scenarios (new)
- **Discovery**: open Test Explorer, verify tests are discovered for WSL workspace.
- **Run tests**: run from Test Explorer; verify execution happens in WSL.
- **Debug tests** (if supported): verify WSL debug transport works or is explicitly disabled behind a feature flag.

---

## Risk management / rollback

### How we keep Windows behavior safe
- Every WSL change is gated behind `IsWslWorkspaceRoot(...)` checks.
- Windows code paths remain byte-for-byte identical where possible.

### If WSL debug is unstable
Ship in stages:
- Stage A: build/clippy/fmt on WSL (most valuable + lowest risk)
- Stage B: debug on WSL after validating `bstrPortName` + engine selection across VS versions
- Stage C: tests on WSL after validating execution + (optional) debug transport
- Stage D: LSP on WSL if needed for a good editing experience

---

## VS SDK sample repo notes (VSSDK-Extensibility-Samples)

You have the official samples at `C:\\Repos3\\VSSDK-Extensibility-Samples`. I searched that tree for WSL + debug-target-provider patterns (e.g., `ILaunchDebugTargetProvider`, `ExportLaunchDebugTarget`, `VsDebugTargetInfo(4)`, `IVsDebugger4`, `bstrPortName`, and `SSH:wsl+...`) and did **not** find an example that directly covers:

- creating debug dropdown entries for folder-workspaces, or
- launching a WSL/Linux debug session from an extension using the Workspace debug APIs.

What *is* directly helpful is the **Open Folder** extensibility sample, because it demonstrates the same “folder workspace” extensibility model we’re already using in this repo:

- **Workspace-scoped providers**: Providers are created per-workspace via `IWorkspaceProviderFactory<T>.CreateProvider(IWorkspace workspaceContext)`.
  - See `Open_Folder_Extensibility\\C#\\SymbolScannerSample\\TxtFileSymbolScanner.cs` (scanner) and `...\\FileActionSample\\TxtFileContextProviderFactory.cs` (context provider).
  - Takeaway for our WSL work: WSL detection should be workspace-scoped (based on `workspaceContext.Location`), and the WSL distro + UNC root should be cached per-workspace/provider instance rather than as global static state.

- **Folder/workspace settings persistence**: The sample shows how to store per-workspace settings into the `.vs` workspace settings file.
  - See `Open_Folder_Extensibility\\C#\\SettingsSample\\WordCountSettings.cs` using `workspaceContext.GetSettingsManager().GetAggregatedSettings(...)` and `GetPersistanceAsync(true)`.
  - Takeaway for our WSL work: if we later add a “Target System” dropdown or allow overriding the detected distro, this settings mechanism is a good, SDK-aligned way to persist it *per opened folder*.

Net: the sample repo reinforces the **right extension points for Open Folder mode** and **how to persist workspace settings**, but it doesn’t provide a ready-made WSL debugging implementation. Our plan’s WSL debugging portion still needs a small experimental spike to validate `bstrPortName = \"SSH:wsl+<distro>\"` and engine selection behavior on the VS versions we support.

---

## Notes / open investigation items (resolve during implementation)

- Does `[ExportLaunchDebugTarget(..., new[] { "" }, ...)]` work for extensionless binaries?
  - If not, what is VS’s mechanism for selecting a provider in folder-workspace scenarios?
- For WSL debugging, is `VsDebugTargetInfo` sufficient or must we use `IVsDebugger4` + `VsDebugTargetInfo4`?
- Exact requirements for Linux debugging engine selection (is `NativeOnly_guid` acceptable when `bstrPortName` targets WSL?).
- Do VS file watcher events fire reliably for `\\wsl.localhost\\...` workspaces? If not, do we need a polling fallback for metadata refresh?
- Can `wsl.exe --cd` be relied upon for all supported Windows/WSL versions, or do we need a fallback `cd` wrapper via `sh -lc`?
- Long path requirements: do we need to document enabling Windows long paths (Group Policy / registry) for deep WSL UNC workspaces?

These questions should be answered with a small experimental branch before integrating deeply.

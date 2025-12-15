## Goal
Add **WSL support** to this Visual Studio (VS) extension so that a user can work on Rust code in Visual Studio and still have **all Rust tool execution happen inside a selected WSL distribution**, while keeping existing **Windows-native behavior unchanged**.

This plan covers two “workspace location” modes:

- **Mode 1 (WSL UNC workspace)**: user opens a folder under `\\wsl.localhost\…` (or `\\wsl$\…`).
- **Mode 2 (Windows workspace, WSL execution)** *(Option 1 requested)*: user opens a folder under `C:\…` (or other Windows local drive), but **build/clippy/fmt/test/debug** are executed in WSL.

> Important: **Mode 2 is the recommended path for VS 2026 / 18.x** because VS Open Folder has shown instability when its workspace DB lives under `\\wsl.localhost\...` (see “VS 2026 note” below).

In both modes, the extension can:

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
- **WSL detection is workspace-scoped by default**: WSL behavior activates for WSL UNC workspaces (`\\wsl.localhost\...`, `\\wsl$\...`).
- **Target-system override**: we will also support an explicit override (per-workspace) that forces WSL execution even for a Windows-local workspace (Mode 2). This override must be opt-in and must not affect Windows-native workflows unless selected.

---

## VS 2026 / 18.x note: Open Folder + WSL UNC instability (why Mode 2 exists)
During investigation on VS 2026 (18.1.0), opening a folder under `\\wsl.localhost\...` sometimes produces an ActivityLog error like:

- `VS/Workspace/BrowseOpenWorkspace`
- `System.IO.IOException: The process cannot access the file '\\wsl.localhost\...\<workspace>\.vs\slnx.sqlite' because it is being used by another process`
- followed by failures inside `DebugTargetsManager.DeserializeAsync`

When this happens, Visual Studio’s **Open Folder debug-target DB is not initialized**, and **debug dropdown population / build context enablement becomes unreliable** regardless of extension correctness.

This plan therefore treats:
- **Mode 1 (WSL UNC workspace)** as “best effort” (and likely a VS bug to track upstream), and
- **Mode 2 (Windows workspace + WSL execution)** as the primary supported workflow for VS 2026.

Mode 2 also aligns with the user’s intent to avoid Windows tooling requirements while still using VS UI, and it allows the user to add Defender exclusions for the Windows folder if needed.

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
For WSL execution and output processing we need a **reversible mapping**. There are two mapping categories:

#### Category A (WSL UNC workspace, Mode 1)
- **Windows UNC → Linux path**
  - `\\wsl.localhost\Ubuntu-22.04\home\u\p\Cargo.toml` → `/home/u/p/Cargo.toml`
- **Linux absolute path → Windows UNC (same distro)**
  - `/home/u/p/src/main.rs` → `\\wsl.localhost\Ubuntu-22.04\home\u\p\src\main.rs`

#### Category B (Windows workspace, Mode 2 / Option 1)
- **Windows local path → Linux path (under /mnt)**
  - `C:\Repos\trader-one\Cargo.toml` → `/mnt/c/Repos/trader-one/Cargo.toml`
- **Linux path (under /mnt) → Windows local path**
  - `/mnt/c/Repos/trader-one/src/main.rs` → `C:\Repos\trader-one\src\main.rs`

> For Category B, prefer using `wslpath` (via `wsl.exe`) for correctness when paths are not simple `X:\...` forms.

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

---

## Mode 2 (Option 1): Windows workspace + WSL execution (recommended for VS 2026)

### Summary
User opens a workspace under a **Windows-local path** (e.g. `C:\Repos\trader-one`) to keep VS Open Folder stable. The extension then runs:

- `cargo metadata / build / clippy / fmt / test` via `wsl.exe`
- debugging via VS’s WSL debug transport (`SSH:wsl+<distro>`) against the **Linux ELF** binaries produced under the workspace’s `target/` folder (which lives on NTFS).

This avoids the Open Folder `\\wsl.localhost\...\.vs\slnx.sqlite` instability, at the cost of compiling on `/mnt/c` (which can be mitigated with Defender exclusions; see “Performance notes”).

### How this differs from “tapping into the C++ WSL toolset flow”
Both approaches can “execute in WSL”. The difference is **where the build artifacts and filesystem writes happen**:

- **This plan (Mode 2)**: build runs in WSL **against `/mnt/<drive>`** paths that are backed by NTFS. This is simplest and keeps our existing extension architecture (Open Folder + file scanner + debug provider).
- **C++ Linux/WSL toolset**: often copies/syncs sources into WSL’s Linux filesystem and builds there, which tends to avoid NTFS/AV overhead. However it requires a project system that drives that sync/build pipeline.

This plan intentionally implements the simplest WSL execution first. If `/mnt/c` performance is problematic, add “mirror-to-WSL build root” as a later stage (see “Performance notes”).

### UX / configuration
Add a per-workspace setting (“Target System”) with values like:
- `Local Machine` (default; current behavior)
- `WSL: <DistroName>` (forces WSL execution even when workspace root is Windows)

Implementation detail:
- There is already a `Target System` combo in the UI (`src/RustAnalyzer/Shell/TargetSystemCommands.cs`). For Mode 2, we must persist the selection per workspace and make it readable from both the `RustAnalyzer` and `RustAnalyzer.TestAdapter` assemblies.

Recommended persistence:
- **Workspace settings** (preferred): VS Open Folder settings service (`ISettingsService`) keyed by workspace root.
- **Fallback**: a process environment variable for the current VS session (works for prototyping; less ideal for multi-workspace scenarios).

### Core technical changes (gated behind “Target System = WSL”)

#### A) WSL target selection / distro discovery
Add a minimal, shared “target selection” helper accessible from both projects:
- `TargetSystem` model: `Local` or `Wsl(distroName)`
- `TargetSystemProvider.GetCurrent(workspaceRoot)`:
  - returns WSL only if the user selected WSL for that workspace
  - otherwise returns Local

Also allow Mode 1 (WSL UNC workspace) to infer distro from the UNC root (existing `WslInfo.TryParse(...)`).

#### B) Windows ↔ WSL path mapper (Category B)
Add a new helper (suggested location: `src/RustAnalyzer.TestAdapter/Common/`) that can map:
- `C:\...` ↔ `/mnt/c/...`
- and (optionally) use `wslpath` for corner cases

Proposed API:
- `bool TryMapWindowsToWsl(string windowsPath, out string linuxPath)`
- `bool TryMapWslToWindows(string linuxPath, out string windowsPath)`
- `Task<string> WindowsToWslViaWslpathAsync(string windowsPath, string distro, CancellationToken ct)` (fallback)
- `Task<string> WslToWindowsViaWslpathAsync(string linuxPath, string distro, CancellationToken ct)` (fallback)

Rules:
- Never push raw Linux paths through `PathEx` (it rewrites `/` to `\`).
- Do mapping at the string layer, then only convert to `PathEx` once the path is in Windows form.

#### C) Route tool execution to WSL even when workspace root is Windows
Update the chokepoints that currently decide “Windows vs WSL” based solely on UNC prefix:
- `ToolChainService.GetWorkspaceAsync` (cargo metadata)
- `ToolChainService.BuildAsync / CleanAsync / RunFmtAsync / RunClippyAsync`
- `ToolChainServiceExtensions.GetCommandOutput*` and any toolchain detection
- `PreReqsCheckService` (workspace-aware prereqs)

New decision logic:
- If `workspaceRoot` is WSL UNC → WSL mode (Mode 1)
- Else if `Target System == WSL:<distro>` → WSL mode (Mode 2)
- Else → Windows mode (current behavior)

Execution in Mode 2:
- Linux working dir = mapped Windows folder, e.g. `/mnt/c/Repos/trader-one`
- Use `wsl.exe -d <distro> --cd <linuxWorkingDir> --exec ...`
- When invoking `cargo`, prefer `/bin/bash -lc` wrapper if PATH is minimal (same fix as Mode 1).

#### D) Cargo metadata rewriting for Mode 2
When running `cargo metadata` in WSL on a Windows workspace, paths in JSON will typically be:
- `/mnt/c/...`

Before deserializing into the `Workspace` model (which uses `PathEx`), rewrite:
- `workspace_root`, `target_directory`, `packages[].manifest_path`, `packages[].targets[].src_path`

from Linux `/mnt/...` to Windows `C:\...`.

#### E) Diagnostics path rewriting for Mode 2
Update `StringBuildMessagePreprocessor` and JSON output parsing so that:
- absolute Linux paths under `/mnt/<drive>/...` are mapped to `X:\...`
- then VS navigation works normally (click errors → open Windows file)

#### F) Debugging for Mode 2
Debug dropdown entries should still be generated by `FileScanner` (Open Folder scanning) from Windows-visible files. For Mode 2:
- Cargo produces Linux binaries under `C:\...\target\debug\<bin>` (extensionless ELF file)
- We must ensure target filename rules are extensionless when `Target System == WSL`

Required changes:
- `WorkspaceExtensions.CreateTargetFileNameForWorkspace`:
  - if Mode 2 WSL: return extensionless for bin targets (like Mode 1)
  - else keep `.exe` (Windows)
- `DebugLaunchTargetProvider`:
  - If Mode 2 WSL:
    - Convert `bstrExe` from Windows path `C:\...\target\debug\bin` to Linux `/mnt/c/.../target/debug/bin`
    - Set `bstrPortName = SSH:wsl+<distro>`
    - Set `bstrCurDir` to Linux working dir (`/mnt/c/...` or resolved)
    - Keep existing Windows path unchanged otherwise

Env handling:
- In WSL debug, omit `bstrEnv` initially (use WSL environment).

#### G) Tests for Mode 2
Test discovery/execution should reuse the existing WSL execution path, but now mappings are `/mnt/...` instead of `\\wsl.localhost\...`.
- Store test container and source file paths as Windows paths.
- Convert to Linux paths only at execution time.

### Performance notes (user concern)
Mode 2 builds on `/mnt/c`, which can be slower and can be affected by Defender/AV.

Mitigations:
- Document recommended Defender exclusions for the workspace folder and `target/`.
- Optional future stage (Mode 2b): “mirror-to-WSL build root”:
  - Sync workspace to a WSL-local directory (e.g. `/home/<user>/.cache/rustanalyzer-vs/<workspace-id>`)
  - Run cargo there (fast, avoids AV)
  - Map diagnostics back to the Windows source tree (requires stable source mapping strategy)

This “mirror” stage is conceptually closer to the C++ WSL toolset behavior, but it is intentionally postponed until Mode 2 works end-to-end.

### Verification checklist for Mode 2
- Open a Windows-local folder (e.g. `C:\Repos\trader-one`) in VS Open Folder.
- Select `Target System = WSL: Debian`.
- **Build**:
  - `cargo build` runs via WSL and produces Linux binary under `target/debug/<bin>` (no `.exe`).
- **Clippy / Fmt**:
  - run via WSL and error paths open the correct Windows file.
- **Debug dropdown**:
  - runnable targets appear in the dropdown.
- **F5**:
  - launches WSL debug session using `SSH:wsl+Debian` and breakpoints hit.
- **Test Explorer**:
  - discovery and execution happen via WSL, and test navigation opens Windows files.

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

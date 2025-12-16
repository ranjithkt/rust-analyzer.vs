# WSL build strategy with filesystem watcher
_Exported on 12/15/2025 at 23:29:29 CST from Cursor (2.2.20)_

---

**User**

Go through @docs/WSL_SUPPORT_PLAN.md on what we have been doing in this repo. Mode 2 turned out ot be not successful. When we build (from experimental VS 2026 or even from WSL command prompt), it rebuilds the selected crate every time, even though nothing is changed. That is not acceptable. 

So, we need to implement another strategy. Still repo will be in windows, but add a filesystem watcher for the entire workspace and also for all local "path" dependencies which the current workspace depends on. The very first time it has to copy all folders, including path dependencies with exact structure onto WSL, may be under /home and some sub folder relevant. 

Now we have two options:
1. For each build, we sync (only) the modified files onto WSL native folder (not under /mnt) and build there.
and/or
2. Sync modfied files every 5 seconds or so in the background

You recommend one of the above two options. Now create a new MD file for this implementation under docs folder and link it to the original md file. Go over the entire code structure and see what needs to be done to be implemented. Do not implement anything, just plan for now.

---

**Cursor**

Got it — I’ll (a) re-read `docs/WSL_SUPPORT_PLAN.md` to anchor what “Mode 2” was, (b) scan the repo for the existing WSL/remote-build plumbing to see where a sync+build strategy would hook in, then (c) write a new design doc under `docs/` describing the new approach (file-watcher + initial full copy + incremental sync) and add a link to it from the original plan.

Nice: the repo already has WSL plumbing (`WslInfo`, `WslPathMapper`, target-system selection, etc.), but the current Mode 2 assumes building directly on `/mnt/<drive>` (NTFS). Next I’ll inspect the existing WSL-related code paths to identify exactly where a “mirror to WSL native FS + incremental sync” strategy would plug in, then I’ll write the new design doc and link it from `WSL_SUPPORT_PLAN.md`.

---

**User**

Continue to plan

---

**Cursor**

I’m going to map out where the new “WSL mirror” layer needs to hook in: command execution (so cargo runs in the mirror folder), path rewriting (mirror Linux paths → Windows paths), and a new per-workspace sync service (watchers + dirty-file queue + initial full copy). Then I’ll write the new doc and link it from `WSL_SUPPORT_PLAN.md`.

```markdown
### Summary
Mode 2 (“Windows workspace, WSL execution” by building directly on `/mnt/<drive>/...`) turned out to be **not acceptable**: `cargo build` rebuilds the selected crate every time even when nothing changed.

This document proposes a new strategy:

- The **workspace stays on Windows** (VS opens `C:\...`).
- We create a **WSL-native mirror** (ext4) of the workspace **and** all reachable local `path` dependencies.
- We run `cargo build/clippy/fmt/test/metadata` **inside WSL** against the **mirror**, not under `/mnt`.

This is a **plan only** (no implementation here).

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

- **Preferred (fast, accurate)**: invoke `wsl.exe` and use `rsync -a --delete` from `/mnt/<drive>/...` into the mirror (ext4).
  - Pro: very fast, preserves mtimes, handles deletes.
  - Con: `rsync` may not be installed by default.

- **Fallback (no extra deps)**: `tar` pipeline (`tar -cf - . | tar -xf -`) to mirror, plus manual delete handling.

- **Simplest (no WSL tools)**: Windows copy to UNC (`\\wsl.localhost\...`) using `File.Copy` + recursive enumeration.
  - Pro: no WSL tool requirements.
  - Con: may be slower for large trees.

Exclusions we should apply by default:
- `target/`
- `.git/`
- `.vs/`
- `**/*.pdb`, `**/*.obj` (optional)

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
- The original WSL plan: see `docs/WSL_SUPPORT_PLAN.md` (Mode 2 describes the `/mnt` approach that this replaces).

```

### Recommendation
Use **Option 1 (sync-on-build)** as the default. It’s deterministic (build always sees a consistent mirror state) and avoids constant background I/O.

### New plan doc + link
- Created: `docs/WSL_MIRROR_SYNC_PLAN.md`
- Linked from the original plan: `docs/WSL_SUPPORT_PLAN.md` now points to [`WSL_MIRROR_SYNC_PLAN.md`](WSL_MIRROR_SYNC_PLAN.md) in the Mode 2 section.

### What needs to be done (planned, not implemented)
The new doc spells out the full design, but the core repo touchpoints are:

- **Add a WSL mirror layer** (new mapper + per-workspace mirror manager + Windows watchers for workspace + external `path` deps).
- **Change WSL Mode 2 execution** (`ToolchainService` / `ToolchainServiceExtensions`) to run cargo in the **mirror (ext4)** instead of `/mnt`.
- **Update path rewriting** so diagnostics map **mirror Linux paths → Windows paths**, while `target_directory` maps to **UNC in WSL** so debug/tests can reference real build outputs.
- **Extend tests/debug plumbing** to tolerate mirror paths (mostly by storing outputs as UNC and reusing existing UNC↔Linux conversion).

---

**User**

Can you check what concepts from the below link we can use for the new functionality. 

I already installed the below in WSL as per the instructions on the link. Can you check if using File system watcher is better or just using rsync (and any other tools mentioned in the link) are helpful?

Already on my WSL:
sudo apt update
sudo apt install g++ gdb make ninja-build rsync zip

https://learn.microsoft.com/en-us/cpp/build/walkthrough-build-debug-wsl2?view=msvc-170 

---

**Cursor**

### Concepts we can reuse from the VS WSL2 toolset article
From Microsoft’s description, the key idea is: **keep sources in Windows, but build in WSL2’s Linux filesystem by automatically copying/syncing with `rsync`** (so builds don’t run on `/mnt/...`). That’s exactly the direction of our “mirror” plan. See: [Walkthrough: Build and Debug C++ with WSL 2 and Visual Studio 2022](https://learn.microsoft.com/en-us/cpp/build/walkthrough-build-debug-wsl2?view=msvc-170).

Practical takeaways we can apply:

- **Rsync-based mirroring**: VS uses `rsync` to copy from Windows FS to WSL FS when targeting WSL2, specifically to get Linux FS performance and avoid `/mnt` penalties. We can do the same: `rsync` from `/mnt/<drive>/<workspace>` → `<mirrorRoot>/win/<drive>/<workspace>`.
- **Configurable mirror location + rsync args**: the article mentions “advanced settings” (copy directory, copy options, rsync args). For us: workspace settings like mirror base dir, excludes, and optional extra rsync args.
- **Build artifacts live in WSL FS**: for us that means `target/` is under the mirror, which is the critical fix for repeated rebuilds.
- **Debugging is WSL-native**: the article relies on WSL debugging tooling (`gdb`). Your installed `gdb` aligns with our current MIEngine + `SSH:wsl+<distro>` debug approach.

### FileSystemWatcher vs rsync: which is “better” for our scenario?
They solve different problems:

- **File watchers** answer: *“What changed?”* (event-driven hints).
- **rsync** answers: *“Make destination match source correctly.”* (robust, handles deletes/renames, preserves mtimes, incremental transfer).

For the mirror strategy, **rsync is the more valuable/robust building block**. A watcher alone can’t perform correct syncing; it only tells you “something changed”, and it can miss events (buffer overflow, rename edge-cases, editor temp-file patterns). Meanwhile `rsync` already does fast incremental sync *without needing exact per-file events*.

### Recommendation for the new functionality
- **Use `rsync` as the primary sync mechanism** (both initial copy and incremental updates), because it matches what VS’s WSL2 toolset does conceptually and it’s the most reliable way to guarantee the mirror is correct.
- **Treat FileSystemWatcher as optional optimization**:
  - Good for avoiding unnecessary sync attempts when nothing changed, or for deciding *which roots* to sync (workspace + external path-dep roots).
  - But still do a final `rsync` **at build start** (Option 1) so correctness doesn’t depend on watcher fidelity.

So the best hybrid is: **Option 1 (sync-on-build) using `rsync`**, optionally gated by watcher “dirty” state. Background syncing (Option 2) can be added later, but you’ll still typically keep a cheap “sync-if-dirty” step at build start to guarantee correctness.

### Are the tools you installed helpful?
Yes:
- **`rsync`**: directly useful for mirroring (core).
- **`gdb`**: useful/needed for WSL debugging (matches the article’s toolchain expectations).
- **`zip` / `make` / `ninja-build` / `g++`**: not directly required for Rust/cargo, but harmless; `zip` can be handy for optional packaging workflows, and `ninja`/`make` are irrelevant unless you build native components.

If you want, I can update `WSL_MIRROR_SYNC_PLAN.md` with a short “aligns with VS WSL2 toolset (rsync mirror)” section and a concrete recommended `rsync` command shape (with excludes) consistent with this approach.

---

**User**

Lets go ahead with rsync option, keep other options as "needed when rsync performance is slow". But one question though, if binaries live in the mirror WSL machine, then it means we will not have "target" folder in our windows file system, is that right?

---

**Cursor**

I’ll update `WSL_MIRROR_SYNC_PLAN.md` to make **rsync the primary sync mechanism** (with other methods as fallback), and I’ll add a short note clarifying where the `target/` directory lives in mirror mode.

Yes — **by default, `target/` would live inside the WSL mirror**, so you would **not** have a `target/` folder under your Windows workspace (`C:\...`) for that build.

A couple important nuances:

- **VS can still work** because we’ll point debug/test paths to the mirror via **UNC** (e.g. `\\wsl.localhost\<distro>\home\...\target\...`), so existence checks and launches can still succeed.
- If we *force* `target/` back onto Windows (via `CARGO_TARGET_DIR` → `/mnt/c/...` or copying artifacts back), we risk reintroducing the very NTFS/`/mnt` behavior we’re trying to avoid — so it’s **not recommended by default**.

I’ve updated `WSL_MIRROR_SYNC_PLAN.md` to make **rsync the primary option** and note the `target/` implication.

---

**User**

Just so that you can get more context, below are some rundowns of diagnostic messages when I build VS 2026 Linux c++ project. See if any flow is useful for implementing our plan. First output is for Clean, then build output for build and Cross Platform output for build

1. Clean:
@Untitled-1 (1-132) 

2. Build output for build:
@Untitled-2 (1-639) 

3. Cross platform output for build:
@Untitled-2 (1-639) 

---

**Cursor**

I’ll pull the key “WSL C++ toolset” steps from your build logs (CopySources / up-to-date checks / remote dir resolution) and map them to our Rust mirror plan, then I’ll add a short section to `WSL_MIRROR_SYNC_PLAN.md` capturing the useful flow patterns.

Yes — in **mirror mode**, binaries (and thus the whole `target/` tree) live in the **WSL mirror**, so you typically **won’t have `C:\...\target\...`** for that build.

- **What you *will* have**: a Windows-accessible path via UNC, e.g. `\\wsl.localhost\<distro>\home\...\mirrors\...\win\c\...\target\...`, which VS can use for file existence checks, Test Explorer containers, and debug launching.
- **What we should *not* do by default**: force `target/` back onto Windows (via `/mnt/c/...` or copying artifacts back), because that tends to reintroduce the NTFS `/mnt` pain we’re explicitly avoiding.

### What your VS 2026 C++ WSL logs tell us (useful flow patterns)
Your build log shows a very relevant pipeline we can mirror conceptually:

- **ResolveWSLTarget / ResolveRemoteDir**: pick distro + remote build root.
- **PrepareUpToDateChecks + ConsolidateSourcesToCopy**: compute inputs and make “copy sources” incremental.
- **CopySources**: do the sync step (took ~**663ms** in your log).
- Then compile/link runs remotely.

This is basically “**rsync-on-build with up-to-date checks**”, not “background watcher-driven syncing”. I added a short section to `WSL_MIRROR_SYNC_PLAN.md` capturing these patterns so we can use them directly in our implementation plan.

---

**User**

Ok, now I want you to implement the plan we just created. Make sure to prioritize utmost performance, no to minimum lag in all build/parse compiler messages and or rsync things.

---

**Cursor**

```csharp
using System;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Configuration for WSL mirror mode (Mode 2: Windows workspace + WSL execution on ext4).
/// </summary>
public sealed class WslMirrorConfig
{
    public WslMirrorConfig(PathEx workspaceRootWindows, string distroName, string linuxHome, string mirrorBaseLinux, string workspaceId)
    {
        WorkspaceRootWindows = workspaceRootWindows;
        DistroName = distroName;
        LinuxHome = linuxHome;
        MirrorBaseLinux = mirrorBaseLinux;
        WorkspaceId = workspaceId;

        MirrorRootLinux = CombineLinuxPaths(MirrorBaseLinux, WorkspaceId);
        MirrorWindowsRootLinux = CombineLinuxPaths(MirrorRootLinux, "win");

        // Map the Windows workspace root into the mirror.
        if (!WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)WorkspaceRootWindows, this, out var mirrorWorkspaceRoot))
        {
            throw new InvalidOperationException($"Unable to map workspace root '{WorkspaceRootWindows}' to mirror linux path.");
        }

        MirrorWorkspaceRootLinux = mirrorWorkspaceRoot;
        MirrorTargetDirLinux = CombineLinuxPaths(MirrorWorkspaceRootLinux, "target");
    }

    /// <summary>Windows workspace root (e.g. C:\Repos\proj).</summary>
    public PathEx WorkspaceRootWindows { get; }

    /// <summary>WSL distro name (e.g. Ubuntu-22.04).</summary>
    public string DistroName { get; }

    /// <summary>Linux $HOME for the distro (e.g. /home/user).</summary>
    public string LinuxHome { get; }

    /// <summary>Mirror base directory (e.g. /home/user/.cache/rust-analyzer.vs/mirrors).</summary>
    public string MirrorBaseLinux { get; }

    /// <summary>Stable workspace id used as a subfolder under MirrorBaseLinux.</summary>
    public string WorkspaceId { get; }

    /// <summary>Mirror root directory (e.g. .../mirrors/&lt;id&gt;).</summary>
    public string MirrorRootLinux { get; }

    /// <summary>Root under which Windows drive trees are mirrored (e.g. .../mirrors/&lt;id&gt;/win).</summary>
    public string MirrorWindowsRootLinux { get; }

    /// <summary>Mirror path for the workspace root (e.g. .../win/c/Repos/proj).</summary>
    public string MirrorWorkspaceRootLinux { get; }

    /// <summary>Mirror target dir (e.g. .../win/c/Repos/proj/target).</summary>
    public string MirrorTargetDirLinux { get; }

    /// <summary>UNC path to mirror target dir (e.g. \\wsl.localhost\Ubuntu\home\user\...\target).</summary>
    public string MirrorTargetDirUnc { get; set; }

    /// <summary>
    /// Excluded directory names (case-insensitive) for sync and watcher ignore.
    /// NOTE: Excluding "target" is required so rsync doesn't delete cargo outputs in the mirror.
    /// </summary>
    public string[] ExcludedDirectoryNames { get; set; } = new[]
    {
        ".git",
        ".vs",
        "target",
    };

    /// <summary>rsync flags tuned for correctness + speed.</summary>
    public string[] RsyncArgsPrefix { get; set; } = new[]
    {
        "-rt",
        "--delete",
        "--modify-window=1",
    };

    public static string CombineLinuxPaths(string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right ?? string.Empty;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        if (left.EndsWith("/", StringComparison.Ordinal))
        {
            return left + right.TrimStart('/');
        }

        return left + "/" + right.TrimStart('/');
    }
}

```

```csharp
using System;
using System.Text.RegularExpressions;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Maps Windows-local paths (C:\...) to WSL mirror paths under a WSL-native directory.
/// Mirror layout:
///   &lt;MirrorWindowsRootLinux&gt;/&lt;drive&gt;/&lt;path...&gt;
/// Example:
///   C:\Repos\proj\src\main.rs -> /home/u/.cache/rust-analyzer.vs/mirrors/&lt;id&gt;/win/c/Repos/proj/src/main.rs
/// </summary>
public static class WslMirrorPathMapper
{
    private static readonly Regex WindowsDrivePath = new(@"^(?<drive>[a-zA-Z]):[\\/](?<rest>.*)$", RegexOptions.Compiled);

    public static bool TryWindowsToMirrorLinuxPath(string windowsPath, WslMirrorConfig cfg, out string mirrorLinuxPath)
    {
        mirrorLinuxPath = null;

        if (cfg == null || string.IsNullOrWhiteSpace(cfg.MirrorWindowsRootLinux))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(windowsPath))
        {
            return false;
        }

        // Already a Linux path.
        if (windowsPath.StartsWith("/", StringComparison.Ordinal))
        {
            mirrorLinuxPath = windowsPath;
            return true;
        }

        // If this is a WSL UNC path, keep it as-is (caller should convert via WslInfo instead).
        if (WslInfo.IsWslPath(windowsPath))
        {
            mirrorLinuxPath = windowsPath;
            return false;
        }

        var m = WindowsDrivePath.Match(windowsPath.Trim());
        if (!m.Success)
        {
            return false;
        }

        var drive = char.ToLowerInvariant(m.Groups["drive"].Value[0]);
        var rest = m.Groups["rest"].Value.Replace('\\', '/');

        mirrorLinuxPath = $"{cfg.MirrorWindowsRootLinux}/{drive}/{rest}";
        return true;
    }

    public static bool TryMirrorLinuxToWindowsPath(string linuxPath, WslMirrorConfig cfg, out string windowsPath)
    {
        windowsPath = null;
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.MirrorWindowsRootLinux))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        var trimmed = linuxPath.Trim();
        if (!trimmed.StartsWith(cfg.MirrorWindowsRootLinux, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = trimmed.Substring(cfg.MirrorWindowsRootLinux.Length).TrimStart('/');
        if (suffix.Length < 2)
        {
            return false;
        }

        // Expect: "c/Repos/proj/..."
        var drive = suffix[0];
        if (!char.IsLetter(drive) || suffix[1] != '/')
        {
            return false;
        }

        var rest = suffix.Substring(2).Replace('/', '\\');
        windowsPath = char.ToUpperInvariant(drive) + ":\\" + rest;
        return true;
    }

    /// <summary>
    /// Converts an argument that may contain a Windows path into a mirror Linux path.
    /// Handles raw paths and patterns like "--foo=C:\path".
    /// </summary>
    public static string ConvertArgumentWindowsPathsToMirror(string arg, WslMirrorConfig cfg)
    {
        if (string.IsNullOrEmpty(arg) || cfg == null)
        {
            return arg;
        }

        // Fast-path: raw "C:\..." token.
        if (TryWindowsToMirrorLinuxPath(arg, cfg, out var linux) && linux != arg)
        {
            return linux;
        }

        // Handle "--foo=C:\..." and similar.
        var idx = arg.IndexOf(":\\", StringComparison.Ordinal);
        if (idx > 0)
        {
            var driveChar = arg[idx - 1];
            if (char.IsLetter(driveChar))
            {
                var start = idx - 1;
                var prefix = arg.Substring(0, start);
                var pathPart = arg.Substring(start);
                if (TryWindowsToMirrorLinuxPath(pathPart, cfg, out var linuxPath))
                {
                    return prefix + linuxPath;
                }
            }
        }

        return arg;
    }
}

```

```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Process-wide cache of WSL mirror instances keyed by (workspace root, distro).
/// </summary>
public static class WslMirrorManager
{
    private static readonly ConcurrentDictionary<string, WslMirrorInstance> Instances = new(StringComparer.OrdinalIgnoreCase);

    public static WslMirrorInstance GetOrCreate(PathEx workspaceRootWindows, string distroName)
    {
        var key = MakeKey(workspaceRootWindows, distroName);
        return Instances.GetOrAdd(key, _ => new WslMirrorInstance(workspaceRootWindows, distroName));
    }

    public static bool TryGet(PathEx workspaceRootWindows, string distroName, out WslMirrorInstance instance)
    {
        return Instances.TryGetValue(MakeKey(workspaceRootWindows, distroName), out instance);
    }

    public static async Task<WslMirrorConfig> EnsureInitializedAsync(PathEx workspaceRootWindows, string distroName, CancellationToken ct)
    {
        var inst = GetOrCreate(workspaceRootWindows, distroName);
        await inst.EnsureInitializedAsync(ct);
        return inst.Config;
    }

    public static bool TryMapMirrorLinuxToWindows(PathEx workspaceRootWindows, string distroName, string linuxPath, out string windowsPath)
    {
        windowsPath = null;
        if (!TryGet(workspaceRootWindows, distroName, out var inst) || inst.Config == null)
        {
            return false;
        }

        return WslMirrorPathMapper.TryMirrorLinuxToWindowsPath(linuxPath, inst.Config, out windowsPath);
    }

    public static bool TryGetMirrorTargetDirUnc(PathEx workspaceRootWindows, string distroName, out string uncTargetDir)
    {
        uncTargetDir = null;
        if (!TryGet(workspaceRootWindows, distroName, out var inst) || inst.Config == null)
        {
            return false;
        }

        uncTargetDir = inst.Config.MirrorTargetDirUnc;
        return !string.IsNullOrWhiteSpace(uncTargetDir);
    }

    private static string MakeKey(PathEx workspaceRootWindows, string distroName)
    {
        return $"{workspaceRootWindows.GetFullPath()}|{(distroName ?? string.Empty).Trim()}";
    }
}

```

```csharp
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

namespace KS.RustAnalyzer.TestAdapter.Common;

/// <summary>
/// Maintains a WSL-native mirror (ext4) of a Windows workspace + external path dependencies.
/// Uses rsync on-demand (sync-on-build) and lightweight Windows file watchers to avoid unnecessary sync.
/// </summary>
public sealed class WslMirrorInstance : IDisposable
{
    private static readonly Regex TomlPathCracker = new(@"\bpath\s*=\s*(['\"])(?<path>[^'\"]+)\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var input = (workspaceRootWindows.GetFullPath() + "|" + (distroName ?? string.Empty)).ToLowerInvariant();
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

```

### What’s implemented (WSL Mirror Sync mode)

- **Mode 2 is now “mirror by default”**: when Target System is `WSL: <distro>` *and the workspace is on Windows*, all cargo operations (build/clippy/fmt/clean/test discovery) run **inside WSL on an ext4 mirror**, not under `/mnt/...`.
- **Rsync sync-on-build with minimal lag**:
  - A **Windows `FileSystemWatcher`** marks the workspace (and external `path`-dep roots) dirty.
  - On each WSL operation we **skip rsync entirely when nothing changed**.
  - When dirty, we run **one rsync per dirty root** with mtime-preserving flags and excludes so cargo incremental builds stay fast.
- **Correct “no Windows `target/`” behavior**:
  - In mirror mode, build outputs live in the **WSL mirror target dir**.
  - `cargo metadata` is rewritten so **`target_directory` becomes a UNC path** into WSL (e.g. `\\wsl.localhost\<distro>\home\...`) so **debug + test execution can still find binaries**.
- **Fast path remapping for diagnostics/navigation**:
  - Compiler/Clippy/Fmt output paths emitted as mirror Linux paths are mapped back to **Windows `C:\...`** for editor navigation.
  - This mapping is designed to be **cheap string-prefix/sentinel logic** (no extra WSL calls during parsing).

### Key code additions/changes

- **New mirror infra** (rsync + mapping + watchers):
  - `src/RustAnalyzer.TestAdapter/Common/WslMirrorConfig.cs`
  - `src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs`
  - `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`
  - `src/RustAnalyzer.TestAdapter/Common/WslMirrorManager.cs`
- **Tool execution now syncs + runs on mirror (Mode 2)**:
  - `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
- **Metadata rewrite emits UNC target dir for mirror outputs**:
  - `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
- **Diagnostics/test output remapping understands mirror paths**:
  - `src/RustAnalyzer.TestAdapter/Cargo/BuildJsonOutputParser.cs`
  - `src/RustAnalyzer/Infrastructure/StringBuildMessagePreprocessor.cs`
- **Workspace root is exposed process-wide for correct mirror keying**:
  - `src/RustAnalyzer/Infrastructure/MetadataServiceFactory.cs` sets `RAVsWorkspaceRoot`
  - `src/RustAnalyzer.TestAdapter/Common/TargetSystemSelection.cs` reads it
  - `src/RustAnalyzer.TestAdapter/Constants.cs` adds `RAVsWorkspaceRoot`
- **API update**:
  - `IToolchainService.GetWorkspaceAsync(...)` now includes `workspaceRoot` so mirror/target-dir rewriting is correct and cheap.

### How to use / expected behavior

- Select **Target System → `WSL: <distro>`**.
- First WSL build:
  - Creates mirror root under WSL home cache
  - Runs initial rsync
  - Builds on ext4 (no `/mnt` rebuild pathology)
- Second build with no changes:
  - Watchers keep it “clean” → **no rsync** → **cargo should be fully incremental/no-op**
- `target/`:
  - Not created under `C:\...` in mirror mode; binaries are accessed via **UNC into WSL** for debug/tests.

If you want, I can add a small “verbose mirror logging” toggle (timestamps + rsync duration + whether sync was skipped) so you can verify the “no-lag when clean” behavior while you test it in VS 2026.

---

**User**

Make sure rsync does not use delta transfer method and instead syncs whole files (only those modified files), that is --whole-file option. I have been told that is the default behavior of rsync when the sync is within the same disk, even if file partitions are in different format). Just want to make sure you are aware of this and make sure it does not use delta transfer method for modified files.

---

**Cursor**

Got it—I'll hard-force rsync to use **whole-file transfers** for modified files by adding `--whole-file` to the default rsync argument set used for mirror sync. Then I’ll verify we don’t have any other rsync call sites and do a quick build of the TestAdapter project.

### Done: force rsync whole-file transfers

I updated the default rsync flags for mirror sync to **always include `--whole-file`**, so rsync will **not** use the delta-transfer algorithm for modified files (it will copy the entire changed file).

- Updated: `src/RustAnalyzer.TestAdapter/Common/WslMirrorConfig.cs`
  - `RsyncArgsPrefix` now includes `--whole-file`.

This applies to **all mirror rsync invocations**, because `WslMirrorInstance` builds its rsync command from `Config.RsyncArgsPrefix`.

If you want, next I can also add an explicit `--no-whole-file` guard check (to prevent accidental override if someone edits args later), but right now it’s hard-forced in the default config.

---

**User**

Verify these issues exist and fix them:

Bug 1:
When `source_path` is missing from test JSON, `test.SourcePath` becomes an uninitialized `PathEx` struct whose `_path` field is null. Attempting `workspaceRoot + test.SourcePath` causes `Path.Combine` to receive a null string argument and throws `ArgumentNullException`. The code should check for uninitialized or null paths before combining.

 @src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs:761-772 @src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs:737-767 

---

**Cursor**

I’m going to inspect the test JSON deserialization path in `ToolChainService.cs`, reproduce the null/default `PathEx` scenario in code, then patch it so missing `source_path` can never crash path combining.

Now I’ll verify the fix by rebuilding the TestAdapter project; if anything else still combines an uninitialized `PathEx`, I’ll patch those too.

Bug 1 **does exist** in the code you pointed at:

- If `source_path` is missing, JSON deserialization leaves `test.SourcePath` as **default(PathEx)** (its internal `_path` is null).
- The old logic then did `workspaceRoot + test.SourcePath`, which ultimately feeds a **null string** into `Path.Combine(...)` and throws `ArgumentNullException`.

### Fix applied

I fixed this in `DeserializeTest(...)` by:

1) **Guarding all “combine with workspace root” paths** so we only combine when `test.SourcePath` is **non-null, non-empty, and relative**.

2) **Rewriting/removing Linux `source_path` before deserialization** so `PathEx` never tries to hold a Linux absolute path (and so WSL test JSON can’t blow up deserialization).

Relevant updated code:

```705:816:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Cargo\ToolChainService.cs
private static TestSuiteInfo.TestInfo DeserializeTest(PathEx workspaceRoot, string serializedVal, bool isWsl, WslInfo wslInfo)
{
    string rawSourcePath = null;
    var jsonForDeserialization = serializedVal;
    try
    {
        var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(serializedVal);
        rawSourcePath = (string)jsonObj["source_path"];

        if (isWsl && !string.IsNullOrWhiteSpace(rawSourcePath) && WslInfo.IsLinuxAbsolutePath(rawSourcePath))
        {
            string mapped = null;
            if (wslInfo != null) mapped = wslInfo.ToUncPath(rawSourcePath);
            else if (WslPathMapper.TryWslToWindowsPath(rawSourcePath, out var winPath)) mapped = winPath;
            else if (WslMirrorPathMapper.TryMirrorLinuxToWindowsPathBySentinel(rawSourcePath, out var mirrorWin)) mapped = mirrorWin;

            if (!string.IsNullOrWhiteSpace(mapped)) jsonObj["source_path"] = mapped;
            else { jsonObj.Remove("source_path"); rawSourcePath = null; }
        }

        jsonForDeserialization = jsonObj.ToString(Newtonsoft.Json.Formatting.None);
    }
    catch { }

    var test = JsonConvert.DeserializeObject<TestSuiteInfo.TestInfo>(jsonForDeserialization);

    // ...
    // When combining, guard against missing/uninitialized SourcePath:
    var pathStr = (string)test.SourcePath;
    if (!string.IsNullOrEmpty(pathStr) && !Path.IsPathRooted(pathStr) && !pathStr.StartsWith(@"\\", StringComparison.Ordinal))
    {
        test.SourcePath = workspaceRoot + test.SourcePath;
    }

    return test;
}
```

I also rebuilt `RustAnalyzer.TestAdapter.csproj` successfully after the change.

```plaintext
private static TestSuiteInfo.TestInfo DeserializeTest(PathEx workspaceRoot, string serializedVal, bool isWsl, WslInfo wslInfo)
{
    string rawSourcePath = null;
    var jsonForDeserialization = serializedVal;
    try
    {
        var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(serializedVal);
        rawSourcePath = (string)jsonObj["source_path"];

        if (isWsl && !string.IsNullOrWhiteSpace(rawSourcePath) && WslInfo.IsLinuxAbsolutePath(rawSourcePath))
        {
            string mapped = null;
            if (wslInfo != null) mapped = wslInfo.ToUncPath(rawSourcePath);
            else if (WslPathMapper.TryWslToWindowsPath(rawSourcePath, out var winPath)) mapped = winPath;
            else if (WslMirrorPathMapper.TryMirrorLinuxToWindowsPathBySentinel(rawSourcePath, out var mirrorWin)) mapped = mirrorWin;

            if (!string.IsNullOrWhiteSpace(mapped)) jsonObj["source_path"] = mapped;
            else { jsonObj.Remove("source_path"); rawSourcePath = null; }
        }

        jsonForDeserialization = jsonObj.ToString(Newtonsoft.Json.Formatting.None);
    }
    catch { }

    var test = JsonConvert.DeserializeObject<TestSuiteInfo.TestInfo>(jsonForDeserialization);

    // ...
    // When combining, guard against missing/uninitialized SourcePath:
    var pathStr = (string)test.SourcePath;
    if (!string.IsNullOrEmpty(pathStr) && !Path.IsPathRooted(pathStr) && !pathStr.StartsWith(@"\\", StringComparison.Ordinal))
    {
        test.SourcePath = workspaceRoot + test.SourcePath;
    }

    return test;
}
```

---

**User**

Another LLM did a code review and suggested the below. See if you need to change or fix anything?

[11 tools called]

The code implementation closely follows `docs/WSL_MIRROR_SYNC_PLAN.md` but has several significant flaws, particularly regarding TOML parsing reliability, path casing, and consistency between execution modes.

### Critical Issues

1.  **Fragile Dependency Discovery (Regex parsing of TOML)**
    *   **File:** `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`
    *   **Issue:** The `TomlPathCracker` regex (`\bpath\s*=\s*...`) is too simplistic. It will fail to detect `path` dependencies defined in:
        *   Inline tables: `dep = { path = "../foo", version = "1.0" }`
        *   Dotted keys: `[dependencies.dep] path = "../foo"`
    *   **Consequence:** Dependencies defined this way will **not be mirrored**, causing the build to fail in WSL with "crate not found" errors.
    *   **Recommendation:** Use `cargo metadata` output (which is already being run partially) to discover *all* local dependencies instead of parsing TOML manually, or use a proper TOML parser if manual scanning is strictly required for performance.

2.  **Path Casing Mismatch Risk**
    *   **File:** `src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs`
    *   **Issue:** `TryWindowsToMirrorLinuxPath` constructs the mirror path using the string casing provided by the caller (VS).
        *   If VS provides `c:\repos\proj` but the filesystem is `C:\Repos\Proj`, `rsync` will copy contents to `/win/c/repos/proj`.
        *   Linux is case-sensitive. If `Cargo.toml` contains `path = "../Dep"`, but we mirrored it to `../dep` (because of the input string), `cargo` will fail to resolve the path.
    *   **Consequence:** Broken builds due to "file not found" on valid projects.
    *   **Recommendation:** Ensure `windowsPath` is canonicalized to match the on-disk casing before generating the mirror path.

### Major Issues

3.  **Test Execution Working Directory Inconsistency**
    *   **File:** `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs` (`GetTestSuiteInfoFromOneTestExeAsync`)
    *   **Issue:** The code executes the test binary (which lives in the Mirror) but sets the working directory to the `/mnt/c/...` path (lines 657-659).
    *   **Consequence:**
        *   Tests relying on `env::current_dir()` will see `/mnt/c/...` (slow, NTFS) instead of the expected Mirror path.
        *   Tests using `env!("CARGO_MANIFEST_DIR")` (baked at compile time) will see the Mirror path.
        *   This mismatch between CWD and Manifest Dir can cause tests to fail if they look for files relative to the manifest.
    *   **Recommendation:** When in Mirror mode, the working directory for test execution should be resolved to the Mirror path, not the `/mnt` path.

4.  **Metadata Rewriting Missing Mirror Support**
    *   **File:** `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs` (`RewritePathProperty`)
    *   **Issue:** The rewriting logic handles `WslInfo` (UNC) and `WslPathMapper` (/mnt), but skips `WslMirrorPathMapper`.
    *   **Context:** Currently `cargo metadata` runs against `/mnt` (in `GetWorkspaceMetadataWslForWindowsWorkspaceAsync`), so the output paths are `/mnt/...`, which `WslPathMapper` handles. However, if any path resolves to the Mirror (or if you switch metadata to run in Mirror for consistency), this function will fail to map paths back to Windows.
    *   **Recommendation:** Add `WslMirrorPathMapper.TryMirrorLinuxToWindowsPath` logic to `RewritePathProperty` to future-proof the implementation and handle cases where `src_path` might point to the mirror.

### Minor Issues & Observations

5.  **Argument Tokenization Fragility**
    *   **File:** `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs` (`ParseArgumentsForWsl`)
    *   **Issue:** The manual string splitting loop to handle quotes is primitive. It may break on complex nested quoting often found in `AdditionalBuildArgs` (e.g., `--config "foo='bar'"`).
    *   **Recommendation:** Use `CommandLineToArgvW` or a robust command-line parser to split arguments reliably.

6.  **Running Metadata in `/mnt` vs Mirror**
    *   **File:** `src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs`
    *   **Observation:** `GetWorkspaceAsync` runs `cargo metadata` against `/mnt`, while `BuildAsync` runs against the Mirror.
    *   **Risk:** While likely an optimization to avoid sync-for-metadata, it creates a "split brain" situation where metadata resolution (features, dependencies) happens on the Windows FS (via `/mnt`) while the build happens on the Linux FS. Any subtle differences in FS behavior or file visibility could lead to inconsistent states.

7.  **`rsync` Permissions**
    *   **File:** `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`
    *   **Observation:** `rsync` uses `-rt` (recursive, times). This is generally good for `/mnt` -> `ext4`, but ensure that file permissions (read/write/exec) on the created files in `~/.cache` are sufficient for `cargo` to read/execute them. `rsync` without `-p` (perms) defaults to destination umask, which is usually fine, but worth verifying.

---

**Cursor**

I’m going to address the reviewer’s high-impact points by updating: (1) dependency discovery to use `cargo metadata` (no fragile TOML regex), (2) mirror-mode test execution working dir consistency, (3) future-proof metadata path rewriting for mirror paths, and (4) more robust argument tokenization. I’ll keep changes performance-conscious (cache + only do heavier work when Cargo.toml changes).

Now I’ll rebuild `RustAnalyzer.TestAdapter` to ensure the dependency-discovery change + mirror test CWD fix + metadata rewrite + improved arg tokenization all compile cleanly together.

### Yes — I made changes based on that review (and kept them performance-minded)

#### 1) Fragile dependency discovery (TOML regex)
- **Fixed** in `WslMirrorInstance`: external `path` dependency roots are now discovered via **`cargo metadata` (with deps)** first (robust), and we **consolidate roots** to avoid lots of rsync calls.
- **Fallback kept**: if full metadata fails (e.g. offline resolution issues), it falls back to the prior “`--no-deps` + best-effort manifest scan” logic.

#### 2) Test execution working directory inconsistency (mirror vs `/mnt`)
- **Fixed** in `ToolChainService.GetTestSuiteInfoFromOneTestExeAsync`: when WSL is selected but `wslInfo == null`, it now:
  - tries to derive a `WslInfo` from the **UNC test exe / target dir**, and
  - otherwise uses the **mirror mapping** for `linuxWorkingDir` (so CWD is mirror, not `/mnt`), with `/mnt` only as a last fallback.

#### 3) Metadata rewriting missing mirror support
- **Fixed** in `RewritePathProperty`: if a Linux path isn’t `/mnt/...` but looks like a mirror path, we now map it back to Windows using the **`/win/<drive>/...` sentinel**.

#### 4) Argument tokenization fragility
- **Fixed**: `ParseArgumentsForWsl` now uses **`CommandLineToArgvW`** (Windows-native parsing), which handles complex quoting (e.g. `--config "foo='bar'"`) correctly.

#### 5) Path casing mismatch risk
- I did a **low-risk stabilization**: `TryWindowsToMirrorLinuxPath` now normalizes Windows paths via `Path.GetFullPath(...)` before mapping, which prevents “same path, different spelling” from generating different mirror locations.
- Full “match on-disk casing” is **not fully solvable** if a project relies on Windows’ case-insensitivity (e.g. Cargo.toml refers to `../Dep` but the directory is actually `dep`). Supporting that would require **rewriting Cargo.toml in the mirror or adding case-variant symlinks**, which we can discuss if you hit it in practice.

All changes compile cleanly (`RustAnalyzer.TestAdapter` builds).

---

**User**

Now another LLM did code review and has below suggestions. See if you have to fix anything. Also consider any performance improvements suggested:

I'll review both plan documents and then critically examine the implementation code for the mirror sync feature. Let me start by reading the relevant documents and code.

[2 tools called]

Now let me find and review the implementation code for the mirror sync feature:

[6 tools called]

Now let me read the related files that integrate with the mirror sync:

[3 tools called]

Let me continue reviewing the remaining integration files:

[3 tools called]

Let me check the target system selection mechanism:

[2 tools called]

Based on my thorough review of both plan documents and all the implementation code, here is my critical code review of the WSL Mirror Sync implementation:

---

## Critical Code Review: WSL Mirror Sync Implementation

### 🔴 Critical Issues

#### 1. Race Condition with Dirty Flags (`WslMirrorInstance.cs`)

```34:35:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
    private volatile bool _workspaceDirty = true;
    private volatile bool _depsDirty = true;
```

The `volatile` keyword does not provide atomicity for read-modify-write operations. In `EnsureSynchronizedAsync`:

```96:100:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
        if (_workspaceDirty)
        {
            await RsyncWindowsRootToMirrorAsync(_workspaceRootWindows, ct);
            _workspaceDirty = false;
        }
```

Between checking `_workspaceDirty` and setting it to `false`, a file watcher could set it to `true` and that update would be lost. This could result in **files not being synced**.

**Recommendation**: Use a lock or `Interlocked.Exchange` pattern.

---

#### 2. Memory Leak in `WslMirrorManager` (`WslMirrorManager.cs`)

```13:13:src/RustAnalyzer.TestAdapter/Common/WslMirrorManager.cs
    private static readonly ConcurrentDictionary<string, WslMirrorInstance> Instances = new(StringComparer.OrdinalIgnoreCase);
```

`WslMirrorInstance` objects are never removed from this dictionary. Each instance holds `FileSystemWatcher` objects with active subscriptions. Over time (especially during development with multiple workspace opens/closes), this will:
- Leak memory
- Keep file handles open
- Keep watching directories that are no longer relevant

**Recommendation**: Add an eviction mechanism or tie instance lifetime to workspace lifecycle.

---

#### 3. Test Execution Doesn't Use Mirror Paths (`TestExecutor.cs`)

```154:159:src/RustAnalyzer.TestAdapter/TestExecutor.cs
                var linuxExe = wslInfo != null
                    ? wslInfo.ToLinuxPath(exe)
                    : (WslPathMapper.TryWindowsToWslPath(exe, out var lex) ? lex : null);
                var linuxWorkingDir = wslInfo != null
                    ? wslInfo.ToLinuxPath(exe.GetDirectoryName())
                    : (WslPathMapper.TryWindowsToWslPath(exe.GetDirectoryName(), out var lwd) ? lwd : null);
```

For Mode 2 (Windows workspace + WSL execution), test execution falls back to `/mnt` path mapping instead of using the mirror paths. This is **inconsistent** with the build operations which use mirror paths, and may result in the `/mnt` rebuild issue for tests.

---

### 🟠 Important Issues

#### 4. Cargo Metadata Not Run on Mirrored Paths (`ToolChainService.cs`)

```292:298:src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs
        if (!WslPathMapper.TryWindowsToWslPath(manifestPath, out var linuxManifestPath) ||
            !WslPathMapper.TryWindowsToWslPath(manifestPath.GetDirectoryName(), out var linuxWorkingDir))
        {
            throw new InvalidOperationException($"Unable to map manifest path '{manifestPath}' to a WSL /mnt path.").AddExitCode(-1);
        }
```

`GetWorkspaceMetadataWslForWindowsWorkspaceAsync` runs `cargo metadata` against `/mnt/...` paths, not mirror paths. While this may be intentional (metadata discovery before sync), it means:
- The first metadata query hits `/mnt` (slow)
- `target_directory` returned will initially point to `/mnt/...` before being overwritten

---

#### 5. File Watcher Buffer Overflow Handling (`WslMirrorInstance.cs`)

```149:149:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
        w.Error += (_, __) => { _workspaceDirty = true; _depsDirty = true; };
```

When `FileSystemWatcher` raises an error (typically buffer overflow), the code only sets dirty flags. However:
- The watcher may not automatically recover
- There's no logging to alert users
- A large number of file changes could repeatedly overflow the buffer

**Recommendation**: Log the error, consider increasing `InternalBufferSize` dynamically or trigger a full resync.

---

#### 6. TOML Parser Limitations (`WslMirrorInstance.cs`)

```22:23:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
    private static readonly Regex TomlPathCracker =
        new("\\bpath\\s*=\\s*(['\\\"])(?<path>[^'\\\"]+)\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

This regex won't correctly parse:
- Inline tables: `dep = { path = "../foo", version = "0.1" }`
- Multi-line strings
- Commented-out dependencies
- `path` keys in other tables (not just dependencies)

This could cause **missing external path dependencies** in the mirror.

---

#### 7. rsync Flags Differ from Plan (`WslMirrorConfig.cs`)

```73:79:src/RustAnalyzer.TestAdapter/Common/WslMirrorConfig.cs
    public string[] RsyncArgsPrefix { get; set; } = new[]
    {
        "-rt",
        "--whole-file",
        "--delete",
        "--modify-window=1",
    };
```

The plan specified `rsync -a --delete`, but implementation uses `-rt`. The `-a` flag includes `-rlptgoD` (recursive, links, permissions, times, group, owner, devices). Missing `-p`, `-g`, `-o` means:
- Executable permissions may not be preserved
- Could break shell scripts or git hooks in the repo

---

#### 8. Silent Exception Swallowing (`WslMirrorInstance.cs`)

Multiple empty catch blocks throughout:

```398:402:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
        catch
        {
            // fall back
        }
```

```597:600:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
        catch
        {
        }
```

This makes debugging issues very difficult. At minimum, these should log at debug/trace level.

---

### 🟡 Moderate Issues

#### 9. Regex Performance in String Preprocessor (`StringBuildMessagePreprocessor.cs`)

```29:30:src/RustAnalyzer.Infrastructure/StringBuildMessagePreprocessor.cs
            var match = Regex.Match(x, @"^Diff in (?<path>\/.*?) at line (?<line>\d+)\:");
```

```65:65:src/RustAnalyzer.Infrastructure/StringBuildMessagePreprocessor.cs
            var match = Regex.Match(x, @"^( )*\-\-\> (?<path>\/.*?):(?<line>\d+):(?<col>\d+)");
```

These regexes are **not compiled** and are called on every line of build output. For large builds, this creates significant GC pressure and CPU overhead.

**Recommendation**: Move to static compiled `Regex` fields like `TestExecutablePathCracker`.

---

#### 10. Inconsistent Path Passthrough (`WslMirrorPathMapper.cs`)

```33:37:src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs
        // Already a Linux path.
        if (windowsPath.StartsWith("/", StringComparison.Ordinal))
        {
            mirrorLinuxPath = windowsPath;
            return true;
        }
```

`TryWindowsToMirrorLinuxPath` returns `true` when given a Linux path, which masks bugs where callers accidentally pass Linux paths. Similar issue in `WslPathMapper.TryWindowsToWslPath`.

---

#### 11. NUL Character Workarounds (`TargetSystemSelection.cs`)

```25:28:src/RustAnalyzer.TestAdapter/Common/TargetSystemSelection.cs
        if (!string.IsNullOrEmpty(distro) && distro.IndexOf('\0') >= 0)
        {
            distro = distro.Replace("\0", string.Empty);
        }
```

This suggests environment variables are being set with NUL characters somewhere upstream. This should be fixed at the source rather than worked around.

---

#### 12. O(N) Lookup in Manager (`WslMirrorManager.cs`)

```56:85:src/RustAnalyzer.TestAdapter/Common/WslMirrorManager.cs
        foreach (var inst in Instances.Values)
        {
            // ... iterates all instances
        }
```

`TryMapMirrorLinuxToWindowsFromWorkspacePath` iterates all mirror instances on every call. With multiple workspaces, this could become a performance bottleneck during build output processing.

---

#### 13. `/bin/mkdir` Direct Invocation (`WslMirrorInstance.cs`)

```629:632:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
        using (var mkdir = ToolchainServiceExtensions.RunInWsl(_distroName, "/bin/mkdir", new[] { "-p", linuxDst }, linuxWorkingDir: null, env: ImmutableDictionary<string, string>.Empty, ct))
        {
            await mkdir;
        }
```

Uses direct `--exec /bin/mkdir` which doesn't go through the shell wrapper. On some distros, `mkdir` might be in `/usr/bin` or aliased differently.

---

#### 14. Test Executable Regex Inconsistency (`ToolChainService.cs`)

```23:26:src/RustAnalyzer.TestAdapter/Cargo/ToolChainService.cs
    // Windows regex: matches paths like ...\target\debug\deps\name-hash.exe
    private static readonly Regex TestExecutablePathCracker = new(@"^\s*Executable( unittests)? (.*) \((.*\\(.*)\-[\da-f]{16}.exe)\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // WSL/Linux regex: matches paths like .../target/debug/deps/name-hash (no .exe extension)
    private static readonly Regex TestExecutablePathCrackerWsl = new(@"^\s*Executable( unittests)? (.*) \((.*/(.*)(?:-[\da-f]{16}))\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

The Windows regex requires the hash (`-[\da-f]{16}`), but the WSL regex makes it optional with `(?:...)`. This could match unexpected paths.

---

### 🔵 Minor Issues / Suggestions

#### 15. Missing `target/.rustc_info.json` in Excludes

The `.rustc_info.json` file and other cargo-generated metadata might cause sync issues. Consider adding to exclusions.

#### 16. `_disposed` Should Be Volatile

```33:33:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
    private bool _disposed;
```

Without `volatile` or proper locking, concurrent dispose calls and operations could have race conditions.

#### 17. No Telemetry for Mirror Operations

Mirror sync timing would be valuable for performance analysis and troubleshooting user issues.

#### 18. Hard-coded Cache Path

```66:66:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
            var mirrorBase = WslMirrorConfig.CombineLinuxPaths(linuxHome, ".cache/rust-analyzer.vs/mirrors");
```

Users cannot configure the mirror location via settings as mentioned in the plan.

#### 19. `ShouldIgnore` String Matching

```235:235:src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs
                var needle = "\\" + dir.Trim('\\', '/') + "\\";
```

`fullPath.IndexOf(needle)` could match `\target_backup\` when trying to exclude `target`. Should use path segment matching.

---

### Summary Table

| Severity | Count | Key Issues |
|----------|-------|------------|
| 🔴 Critical | 3 | Race conditions, memory leak, test execution path mismatch |
| 🟠 Important | 5 | Metadata paths, TOML parsing, rsync flags, exception handling |
| 🟡 Moderate | 6 | Regex performance, path passthrough, O(N) lookup |
| 🔵 Minor | 5 | Configuration, telemetry, edge cases |

---

Would you like me to elaborate on any specific issue or discuss potential fixes for the critical issues?

---

**Cursor**

I’m going to fix the truly risky items from that review (race conditions + leaks + mirror consistency + hot-path perf), while keeping the design fast: no extra syncs, no extra WSL calls during parsing. Next I’ll patch `WslMirrorInstance` dirty flags with `Interlocked`, add a cleanup hook in `MetadataService.Dispose` to release mirror watchers, and tighten a few hot-path utilities.

I’m going to implement fixes for the real correctness/perf risks from that review: atomic dirty-flag handling (no lost updates), deterministic cleanup of mirror instances (no watcher leaks), mirror-aware WSL test execution (avoid `/mnt`), and compile the hot-path regexes in the build output preprocessor. Then I’ll rebuild the TestAdapter project.

Good catch from the build: `ReleaseForWorkspace` uses `ToArray()` but `WslMirrorManager` didn’t import LINQ. I’ll add the missing `using System.Linq;` and rebuild to confirm everything is clean again.

One more small safety/perf tweak from that review: I’ll make `TryWindowsToMirrorLinuxPath` strictly reject Linux paths (instead of returning “success”), so accidental Linux inputs don’t get silently accepted and routed through mirror logic. Then I’ll rebuild once more.

Yes — several of those review points were real issues (or good perf wins). I went ahead and fixed the important ones:

- **Race condition on dirty flags (`WslMirrorInstance`)**: replaced `volatile bool` with **`Interlocked.Exchange`-based int flags** so watcher updates can’t be lost during `EnsureSynchronizedAsync`.
- **Mirror instance leak (`WslMirrorManager`)**: added **`ReleaseForWorkspace(...)`** that disposes instances/watchers, and wired it into **`MetadataService.Dispose`** so closing/reopening workspaces won’t leak watchers/handles.
- **Mode2 test execution using `/mnt` (`TestExecutor.cs`)**: when `wslInfo == null`, we now **prefer mirror mapping** (when a mirror instance/config already exists) and only fall back to `/mnt` mapping as a last resort.
- **Hot-path regex perf (`StringBuildMessagePreprocessor`)**: moved the per-line `Regex.Match/Replace` patterns to **static compiled `Regex`** fields to reduce CPU + GC on large outputs.
- **O(N) lookup in manager**: `TryMapMirrorLinuxToWindowsFromWorkspacePath` now does a **direct lookup using the process workspace root** first (avoids scanning all instances during output parsing).
- **File ignore correctness**: `ShouldIgnore` now matches **path segments** (so excluding `target` won’t accidentally match `target_backup`).
- **`/bin/mkdir` portability**: switched to calling `mkdir` via PATH in WSL.
- **rsync flags vs plan**: updated to **`-a --whole-file --no-owner --no-group --delete`** (keeps whole-file transfers, adds symlink/perms behavior without needing elevated owner/group).

What I *didn’t* fully “fix” because it’s not reliably solvable without rewriting manifests:
- **Windows path casing vs Linux case sensitivity**: we can normalize paths, but if a workspace relies on Windows’ case-insensitive behavior (e.g. `../Dep` vs actual `dep`), Linux will still treat that as different. Solving that requires **rewriting `Cargo.toml` paths in the mirror or creating case-variant links**, which is heavier and riskier.

Everything above compiles cleanly in `RustAnalyzer.TestAdapter` after the changes.

---

**User**

One more review done by another LLM. Please check if you need to fix anything. Also, How much functionality are we depending on TOML parsing? Can we use Tomlyn nuget package for reliable and fast parsing of TOML files?

I'll review both plans and then critically examine the code implementation for the mirror sync feature. Let me start by reading the documents and understanding what was implemented.

[2 tools called]

Now let me examine the actual implementation to find potential issues and flaws:

[8 tools called]

Now let me examine how these are integrated into the actual code:

[15 tools called]

Now let me check one more key file to understand the complete picture:

[3 tools called]

Based on my thorough review of both plan documents and the code implementation, here is my critical code review of the WSL Mirror Sync implementation:

## Critical Code Review: WSL Mirror Sync Implementation

### **HIGH PRIORITY ISSUES**

#### 1. **Missing Mirror Path Mapping in Debug Launch Provider** ⚠️ CRITICAL
**Location**: `DebugLaunchTargetProvider.cs` (lines 259-273, 275-317)

**Problem**: The debug launch provider's `ResolveLinuxPathForDebugExe` and `ResolveLinuxWorkingDirectory` methods do **NOT** handle mirror mode properly. They only handle:
- Mode 1 (WSL UNC via `WslInfo`)
- Mode 2 legacy (`/mnt` via `WslPathMapper`)

But they **completely miss** Mode 2 mirror paths!

**Impact**: When debugging in mirror mode, the debugger will try to use `/mnt/c/...` paths instead of the mirror paths like `/home/user/.cache/rust-analyzer.vs/mirrors/<id>/win/c/...`, causing debug launch failures.

**Fix Required**: Add mirror path resolution similar to how `TestExecutor.cs` does it (lines 167-182):
```csharp
if (TargetSystemSelection.TryGetWorkspaceRoot(out var wsRoot) &&
    WslMirrorManager.TryGet(wsRoot, distroName, out var mirror) &&
    mirror?.Config != null &&
    WslMirrorPathMapper.TryWindowsToMirrorLinuxPath(...))
```

---

#### 2. **Race Condition in Mirror Initialization** ⚠️ HIGH
**Location**: `WslMirrorInstance.cs` - `EnsureSynchronizedAsync` (lines 89-122)

**Problem**: The dirty flags are reset **before** the sync completes:
```csharp
var workspaceDirty = Interlocked.Exchange(ref _workspaceDirty, 0) == 1;
if (workspaceDirty)
{
    await RsyncWindowsRootToMirrorAsync(_workspaceRootWindows, ct);
}
```

If a file changes **after** the flag is cleared but **before** rsync completes, that change will be lost until the next build.

**Fix Required**: Clear dirty flags **after** successful sync, or wrap in try-finally to re-mark dirty on failure.

---

#### 3. **Potential Deadlock in Discovery** ⚠️ MEDIUM-HIGH
**Location**: `WslMirrorInstance.cs` - `DiscoverExternalPathDependencyRootsAsync` (lines 377-469)

**Problem**: This method runs `cargo metadata` **against `/mnt` paths** (line 397) which can trigger a rebuild every time (the exact problem mirror mode was supposed to solve!). This is called during `RefreshExternalRootsAsync` which is triggered on manifest changes.

**Impact**: Every time `Cargo.toml` is edited, this could cause unexpected rebuilds on `/mnt`, defeating the purpose of the mirror.

**Fix Required**: Either:
- Accept this cost as one-time discovery overhead
- Cache the dependency discovery results more aggressively
- Use the mirror paths for metadata queries too

---

#### 4. **Missing Settings Persistence** ⚠️ HIGH
**Location**: `SettingsInfo.cs`

**Problem**: The plan document mentions (line 293-297 in `WSL_MIRROR_SYNC_PLAN.md`):
```
- `WSL Build Location` (new): `mirror` (default) or `mnt` (legacy)
- `WSL Mirror Base Directory` (optional advanced)
- `WSL Mirror Excludes` (optional)
```

These settings are **NOT implemented** in `SettingsInfo.cs`. The code hardcodes mirror behavior with no way to fall back to legacy `/mnt` mode or customize the mirror location/excludes per workspace.

**Impact**: Users cannot:
- Disable mirror mode if it causes issues
- Customize excluded directories
- Override the mirror base directory

---

#### 5. **Unsafe Path Concatenation in Mirror Config** ⚠️ MEDIUM
**Location**: `WslMirrorConfig.cs` constructor (lines 22-24)

**Problem**: The constructor calls `WslMirrorPathMapper.TryWindowsToMirrorLinuxPath` inside the constructor and throws if it fails. This can happen if `MirrorWindowsRootLinux` is not set yet (chicken-and-egg problem).

**Current Code**:
```csharp
if (!WslMirrorPathMapper.TryWindowsToMirrorLinuxPath((string)WorkspaceRootWindows, this, out var mirrorWorkspaceRoot))
{
    throw new InvalidOperationException(...);
}
```

This uses `this` before the object is fully constructed, which could fail if the mapper needs `MirrorWindowsRootLinux` to be set first.

---

#### 6. **FileSystemWatcher Buffer Overflow Handling is Incomplete** ⚠️ MEDIUM
**Location**: `WslMirrorInstance.cs` (lines 150-162, 194-205)

**Problem**: When a watcher error occurs (typically buffer overflow), the code marks everything dirty and traces an error, but it does **NOT**:
- Attempt to recreate the watcher
- Provide any user feedback
- Track how often this happens

**Impact**: In large projects with rapid file changes, watchers will silently fail and the user won't know until a build uses stale mirrored files.

**Fix Required**: Add telemetry, consider recreating watchers, or increase buffer sizes (`InternalBufferSize = 64 * 1024` might be too small for large workspaces).

---

### **MEDIUM PRIORITY ISSUES**

#### 7. **Inefficient Full Resync on Every External Dep Change**
**Location**: `WslMirrorInstance.cs` - `EnsureSynchronizedAsync` (lines 106-121)

**Problem**: When `depsDirty` is true, ALL external roots are re-synced:
```csharp
if (depsDirty)
{
    await RsyncWindowsRootToMirrorAsync((PathEx)root, ct);
}
```

Even if only one dependency changed, this rsyncs all dependencies. For projects with many path dependencies, this is wasteful.

---

#### 8. **Missing Cleanup/Disposal Hook**
**Location**: `WslMirrorManager.cs` - `ReleaseForWorkspace` (lines 111-128)

**Problem**: `ReleaseForWorkspace` is defined but I don't see it being called anywhere. The plan mentions it should be called when workspaces are closed, but there's no integration with VS workspace lifecycle events.

**Impact**: FileSystemWatchers will leak when users close/reopen folders, potentially causing file handle exhaustion over time.

---

#### 9. **No Validation of rsync Availability**
**Location**: `WslMirrorInstance.cs` - `RsyncWindowsRootToMirrorAsync` (line 721)

**Problem**: The code assumes `rsync` is installed in the distro. If it's not, the error message (line 726) will be cryptic:
```csharp
throw new InvalidOperationException($"rsync failed with exit code {ec}. Output:\n{output}");
```

**Fix Required**: Add a prerequisite check similar to how `cargo` and `rustup` are checked, and provide a friendly error message telling users to install rsync.

---

#### 10. **Sentinel Fallback is Fragile**
**Location**: `WslMirrorPathMapper.cs` - `TryMirrorLinuxToWindowsPathBySentinel` (lines 107-141)

**Problem**: This "fast path" heuristic looks for `/win/` in the path:
```csharp
var idx = normalized.IndexOf(WinSentinel, StringComparison.Ordinal);
```

This will break if:
- A user has a directory named `win` anywhere in their path (e.g., `/home/darwin/win/project`)
- The mirror base path is customized to something other than `<base>/win/<drive>/...`

This is a **silent failure** scenario—the mapping will incorrectly trigger and produce wrong paths.

---

### **LOW PRIORITY / CODE QUALITY ISSUES**

#### 11. **Regex Path Dependency Discovery is Best-Effort Only**
**Location**: `WslMirrorInstance.cs` - `FindPathDepsFromManifest` (lines 605-670)

The TOML parsing using regex (line 22-23) is explicitly best-effort:
```csharp
new("\\bpath\\s*=\\s*(['\"])(?<path>[^'\"]+)\\1", ...)
```

This will miss:
- Multi-line TOML values
- Comments that contain `path = `
- Workspace inheritance patterns

**Note**: The plan acknowledges this is a fallback, but it's worth documenting limitations.

---

#### 12. **Hard-coded Distro Name Format in UNC Path Generation**
**Location**: `WslMirrorInstance.cs` - line 744

```csharp
var uncRoot = $"\\\\wsl.localhost\\{distroName}\\";
```

The code standardizes on `\\wsl.localhost` but users might be using `\\wsl$`. While this likely works (Windows redirects), it could cause confusion if paths displayed to the user don't match what they typed.

---

#### 13. **No Telemetry for Mirror Sync Performance**
**Location**: Throughout `WslMirrorInstance.cs`

There's no telemetry tracking:
- How long rsync takes
- How many files are synced
- How often dirty flags are set
- Watcher error rates

This makes it impossible to diagnose performance issues in the field.

---

#### 14. **Metadata Path Rewriting Fallback Order**
**Location**: `BuildJsonOutputParser.cs` and `StringBuildMessagePreprocessor.cs`

The path resolution tries multiple strategies in this order:
1. WSL UNC (Mode 1)
2. `/mnt` legacy mapping (Mode 2 legacy)
3. Mirror mapping via `WslMirrorManager`
4. Sentinel-based heuristic

**Problem**: If #3 fails (mirror not initialized), it falls back to #4 (heuristic) which could succeed with a **wrong** mapping. The heuristic should probably be the last resort or removed entirely.

---

### **ARCHITECTURAL CONCERNS**

#### 15. **No Explicit Mode Selection** 
The code has three modes but no clear way to see which mode is active:
- Mode 1: WSL UNC workspace
- Mode 2 legacy: Windows workspace + `/mnt` 
- Mode 2 mirror: Windows workspace + mirror

Users and even the code itself have to infer the mode from various checks. Consider adding an explicit `enum WslExecutionMode` and a single source of truth.

---

#### 16. **Environment Variable Communication is Fragile**
`TargetSystemSelection` communicates via environment variables (`Constants.RAVsTargetSystem`, `Constants.RAVsWslDistroName`, `Constants.RAVsWorkspaceRoot`).

**Problem**: This assumes:
- Only one workspace per VS process
- Environment variables are set before any TestAdapter code runs
- No multi-threaded race conditions

For VS scenarios with multiple workspace roots (rare but possible), this could cause cross-contamination.

---

### **POSITIVE OBSERVATIONS** ✅

1. **Good use of Interlocked for dirty flags** - Thread-safe without locks
2. **Proper disposal pattern** - Watchers are disposed correctly
3. **Comprehensive path mapping fallbacks** - Multiple strategies reduce failure scenarios
4. **Rsync flag choices are sensible** - `--no-owner --no-group --modify-window=1` handle NTFS→ext4 correctly
5. **SHA256 workspace ID** - Collision-resistant and stable

---

### **RECOMMENDATIONS**

1. **Fix debug launch provider immediately** (Issue #1) - This is a showstopper
2. **Implement missing settings** (Issue #4) - Users need escape hatches
3. **Add rsync prerequisite check** (Issue #9) - Better error messages
4. **Address race condition** (Issue #2) - Data integrity issue
5. **Add telemetry** (Issue #13) - Critical for field diagnostics
6. **Review discovery metadata cost** (Issue #3) - May negate mirror benefits
7. **Document limitations** - Especially around external dependencies and the `/mnt` fallback during discovery

---

**Overall Assessment**: The implementation is **functionally sound** for the happy path but has **several critical gaps** around debugging, error handling, and configurability. The architecture correctly separates concerns and handles the complex path mapping requirements, but needs hardening for production use.

---

**Cursor**

I’ll focus on the real gaps from that last review: fix **debug launch mirror path mapping**, harden **dirty tracking** so changes during rsync aren’t missed, and add a **friendly rsync prerequisite check**. Then I’ll answer the TOML/Tomlyn question with what we actually rely on today.

Next I’ll harden mirror sync correctness (don’t “forget” dirty flags on rsync failure), add a one-time rsync availability check with a friendly error, and optionally upgrade the fallback TOML parsing with Tomlyn (the main path already uses cargo metadata).

### Fixes applied from that review

#### 1) **Mirror path mapping in Debug Launch Provider (critical)**
Yes — that was a real gap. I updated `DebugLaunchTargetProvider.cs` so WSL debug launches now correctly resolve **mirror-mode** paths:

- If `processName` / working dir is **UNC** (`\\wsl.localhost\...`), it derives a `WslInfo` from that and converts to Linux.
- If it’s a **Windows path** and Mode 2 mirror is active, it uses `WslMirrorManager` + `WslMirrorPathMapper` to map to the mirror Linux path.
- Falls back to `/mnt` mapping only as a last resort.

This prevents the “debug tries `/mnt/c/...` in mirror mode” failure.

#### 2) **Dirty-flag “race” during rsync**
The review concern is mostly about *failure handling*, not correctness of steady-state:

- With `Interlocked.Exchange(..., 0)` before rsync, any change during rsync will flip the flag back to `1`, so it **won’t be lost**—it will sync next operation (which is fine for sync-on-build semantics).

What *was* worth fixing: if rsync (or refresh) throws, we must not “forget” that we’re dirty. I added try/catch so on failure we **re-mark dirty** before rethrowing.

#### 3) **rsync prerequisite check (friendly error)**
Added a one-time check inside `WslMirrorInstance`:
- Probes `rsync --version` once per instance.
- If missing, throws a clear message telling the user how to install (`sudo apt update && sudo apt install rsync`).

#### 4) **TOML parsing reliability (Tomlyn)**
You asked how much we depend on TOML parsing and whether Tomlyn is viable.

- **How much we depend on TOML parsing today**:
  - The *primary* dependency discovery is now via **`cargo metadata`** (robust).
  - TOML parsing is only used as a **fallback** when full metadata fails (offline/edge cases).

- **Tomlyn usage**:
  - Yes, Tomlyn is a good fit here: it supports **netstandard2.0**, and it’s fast enough for our fallback path.
  - I added **`Tomlyn` NuGet (0.19.0)** to `RustAnalyzer.TestAdapter.csproj` and updated the fallback `FindPathDepsFromManifest` logic to parse with Tomlyn first (handles inline tables, dotted keys, etc.), then fall back to the old regex scan if parsing fails.

### What’s still not implemented (from the review)
- **Workspace settings for mirror base dir / excludes / legacy mnt toggle**: not added yet. Right now behavior is still “mirror-first” with defaults, and excludes come from config defaults.

If you want, I can implement those settings next (with minimal overhead) by plumbing them through the existing workspace settings store → process env vars → `WslMirrorInstance` init.

---

**User**

One more LLM just reviewed the code and provided the below suggestions. Please check if you need to fix anything:

I’ll first read both planning docs to understand the intended behavior and the “mirror sync” scope, then I’ll review the key implementation classes (`WslMirrorInstance`, config, any sync orchestration) for correctness, edge cases, and maintainability, and finally I’ll summarize concrete issues and risks (with file/line references).

[4 tools called]

I’ve got the intended behavior from the mirror-sync plan and the current `WslMirrorInstance`/`WslMirrorConfig` implementation. Next I’ll scan the repo for all mirror-related classes and the touchpoints in `ToolChainService`/metadata/diagnostics/debug/test paths, then I’ll call out mismatches vs the plan and any correctness/perf/concurrency pitfalls.

[26 tools called]

### High-level plan vs implementation mismatches

- **“Sync only modified files” vs what you built**: the plan’s Option 1 describes syncing only the changed files (dirty queue), but `WslMirrorInstance` currently does **whole-tree rsync** when “dirty” (still incremental, but it must traverse the tree). That may be fine, but it’s a materially different perf profile on large repos.
- **External path-deps refresh is too eager**: any change inside an external dep root currently triggers **re-running cargo metadata for dependency discovery** (not just syncing files). That’s much more expensive than needed and will show up as “random pauses” on builds triggered while editing path deps.

### Correctness / functional risks

- **No serialization of sync work (possible concurrent rsync into same destination)**: `EnsureSynchronizedAsync` has no “sync lock”. If two VS operations hit WSL simultaneously (build + test discovery, etc.), you can end up with **concurrent rsync processes writing to the same mirror paths**.
  
```94:151:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\WslMirrorInstance.cs
public async Task EnsureSynchronizedAsync(CancellationToken ct)
{
    await EnsureInitializedAsync(ct);

    var depsDirty = Interlocked.Exchange(ref _depsDirty, 0) == 1;
    if (depsDirty)
    {
        try { await RefreshExternalRootsAsync(ct); }
        catch { MarkDepsDirty(); throw; }
    }

    var workspaceDirty = Interlocked.Exchange(ref _workspaceDirty, 0) == 1;
    if (workspaceDirty)
    {
        try { await RsyncWindowsRootToMirrorAsync(_workspaceRootWindows, ct); }
        catch { MarkWorkspaceDirty(); throw; }
    }

    if (_externalRoots.Count > 0)
    {
        foreach (var root in _externalRoots.ToArray())
        {
            if (depsDirty)
            {
                await RsyncWindowsRootToMirrorAsync((PathEx)root, ct);
            }
        }
    }
}
```

- **Watcher can miss events at startup**: you set `EnableRaisingEvents = true` **before** attaching handlers, so changes in the tiny window between construction and handler subscription won’t mark the mirror dirty.

```166:193:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\WslMirrorInstance.cs
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
```

- **External deps “dirty” flag conflates two different things**:
  - “need to **resync** external roots”
  - “need to **rediscover** external roots (cargo metadata parse)”
  
  Right now, *any file change* in dep roots triggers the expensive rediscovery path.

```255:268:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\WslMirrorInstance.cs
private void OnAnyDepChanged(string root, string fullPath)
{
    if (ShouldIgnore(fullPath)) { return; }
    MarkDepsDirty();

    if (IsManifestOrLock(fullPath))
    {
        MarkDepsDirty();
    }
}
```

- **Default `.git` exclusion can break real builds**: many Rust projects’ `build.rs` (or crates like `vergen`) read `.git/` or run `git`. Excluding `.git` means the mirror build can behave differently than Windows builds, and sometimes outright fail. This is a design choice, but it’s a correctness landmine unless configurable.

### Path-mapping pitfalls

- **Over-aggressive “/win/” sentinel mapping can produce wrong navigation**: `TryMirrorLinuxToWindowsPathBySentinel` maps *any* Linux path containing `"/win/<drive>/"` into `C:\...`, even if it’s unrelated to your mirror (e.g. a user’s real folder `/home/me/win/c/...`). That can mis-route diagnostics/test navigation.

```107:141:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\WslMirrorPathMapper.cs
public static bool TryMirrorLinuxToWindowsPathBySentinel(string linuxPath, out string windowsPath)
{
    windowsPath = null;
    var normalized = linuxPath.Trim().Replace('\\', '/');
    var idx = normalized.IndexOf(WinSentinel, StringComparison.Ordinal);
    if (idx < 0) { return false; }

    var suffix = normalized.Substring(idx + WinSentinel.Length);
    var drive = suffix[0];
    var rest = suffix.Substring(2).Replace('/', '\\');
    windowsPath = char.ToUpperInvariant(drive) + ":\\" + rest;
    return true;
}
```

- **Mirror mapper’s “Try…” contract is confusing**: `TryWindowsToMirrorLinuxPath` sets `mirrorLinuxPath = windowsPath` for UNC inputs but returns `false`. That’s surprising and invites future misuse (someone ignores the bool and trusts the out value).

```39:44:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\WslMirrorPathMapper.cs
if (WslInfo.IsWslPath(windowsPath))
{
    mirrorLinuxPath = windowsPath;
    return false;
}
```

- **Long-path / `\\?\C:\...` support likely broken**: both `WslPathMapper` and `WslMirrorPathMapper` key off a `C:\...` drive regex and will fail on Windows extended-length paths (`\\?\C:\...`), which is not rare on deep Rust workspaces.

### Robustness / reliability issues

- **“rsync missing” becomes sticky forever**: `EnsureRsyncAvailableAsync` caches “missing” in `_rsyncState = 3` even for transient failures (e.g. WSL temporarily not responding). After that, it will keep throwing until the VS process restarts.
- **SpinWait can burn CPU** during an rsync probe if multiple callers race:

```882:907:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\WslMirrorInstance.cs
else
{
    var sw = new SpinWait();
    while ((state = Volatile.Read(ref _rsyncState)) == 1)
    {
        sw.SpinOnce();
    }
}
```

- **FileSystemWatcher overflow handling is “one-shot”**: on `Error`, you mark dirty, but you don’t switch to a safer mode (e.g., “always rsync on build” until watchers are recreated). If the watcher becomes unreliable after overflow, you can end up with a mirror that stops updating once the dirty flag is cleared.

### VS integration / behavior risks

- **Debug provider registered for `"*"`**: I get why (extensionless Linux binaries), but this can increase the number of times VS asks your provider “do you support this?” and it now does metadata lookups in `SupportsContext`. That’s a potential perf/regression risk for non-Rust debug launches too (even with your `\target\` filter).

```21:61:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer\Debugger\DebugLaunchTargetProvider.cs
[ExportLaunchDebugTarget(LaunchDebugTargetProviderOptions.IsRuntimeSupportContext, ProviderType, new[] { "*" }, ProviderPriority.Lowest)]
public sealed class DebugLaunchTargetProvider : ILaunchDebugTargetProvider
{
    public bool SupportsContext(IWorkspace workspaceContext, string targetFilePath)
    {
        // Only handle build outputs under "target\..." ...
        if (targetFilePath.IndexOf(@"\target\", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        // ... then metadata service lookups ...
    }
}
```

### What I’d prioritize fixing first (no code changes from me—just reviewer guidance)

- **Add a per-instance sync mutex** around `EnsureSynchronizedAsync` (and ideally separate “refresh roots list” vs “sync roots” locks).
- **Split `_depsDirty` into two flags**: “deps graph dirty (rerun discovery)” vs “deps content dirty (rsync external roots)”, and only rerun discovery when a manifest/lock changes.
- **Make `.git` exclusion configurable** (or don’t exclude it by default) because it can break builds.
- **Tighten mirror path detection**: avoid the global `"/win/"` sentinel fallback unless you *know* mirror mode is active for the workspace and you can validate the prefix against the configured mirror base.

If you want, I can also produce a short checklist of “scenarios likely to break” (multi-workspace, long paths, git-versioned builds, huge path deps) to validate against your current implementation.

---

**Cursor**

Good—next I’ll harden `WslMirrorInstance` so sync work is serialized (no concurrent `rsync`), watcher setup can’t miss early events, and external path-deps changes don’t trigger expensive metadata rediscovery unless a manifest/lock actually changed.

I’ve applied the core reviewer fixes (sync serialization, watcher correctness, dep-graph vs dep-content dirty split, long-path support, safer sentinel mapping). Next I’ll inspect the failing test output and fix whatever compilation/test expectations broke from these changes.

### What I changed based on the new review (yes, these were worth fixing)

- **Prevent concurrent `rsync` into the same mirror**  
  Added a per-`WslMirrorInstance` sync mutex (`SemaphoreSlim`) so overlapping VS operations (build/test/discovery/debug) can’t run `rsync` concurrently.  
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`

- **Fix watcher startup race (missing events between create + handler hookup)**  
  `EnableRaisingEvents` is now set **after** all handlers are attached (workspace + dep watchers).  
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`

- **Stop “any dep file change” from triggering expensive dependency rediscovery**  
  Split “deps dirty” into:
  - **deps graph dirty** (only when `Cargo.toml` / `Cargo.lock` changes → re-run discovery)
  - **deps content dirty** (file edits under a dep root → *just rsync that root*)  
  Also added **per-root dirty tracking** so a change in one external root doesn’t resync *all* roots.
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`

- **Watcher overflow handling is no longer “one-shot”**  
  On watcher `Error`, we now mark dirty **and** set a flag so watchers get recreated on the next sync.
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`

- **`rsync` probe no longer spin-waits / no longer “sticky-fails forever”**  
  Replaced the CPU `SpinWait` with a probe lock, and allow periodic retry after failures (WSL transient issues).  
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorInstance.cs`

- **Tightened the `/win/` sentinel mapping heuristic**  
  `TryMirrorLinuxToWindowsPathBySentinel` now only fires for paths matching the default mirror layout `.../mirrors/<32hex>/win/<drive>/...`, preventing accidental remaps of unrelated `/home/.../win/c/...` folders.
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs`

- **Fixed confusing `TryWindowsToMirrorLinuxPath` out-param behavior for WSL UNC inputs**  
  For WSL UNC paths it now returns `false` and leaves `mirrorLinuxPath = null` (no surprising “out value but false” behavior).
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs`

- **Added Windows long-path support (`\\?\C:\...` and `\\?\UNC\...`)**  
  Both `/mnt` mapping and mirror mapping now normalize extended-length Windows paths.
  - Files:
    - `src/RustAnalyzer.TestAdapter/Common/WslPathMapper.cs`
    - `src/RustAnalyzer.TestAdapter/Common/WslMirrorPathMapper.cs`

- **Correctness tweak: don’t exclude `.git` by default**  
  This avoids breaking crates/build scripts that rely on git metadata (e.g., `vergen`). (`target` remains excluded to protect mirror build outputs from `--delete`.)
  - File: `src/RustAnalyzer.TestAdapter/Common/WslMirrorConfig.cs`

### Status
- `dotnet build src/RustAnalyzer.TestAdapter/RustAnalyzer.TestAdapter.csproj` succeeds (remaining warnings are only StyleCop member-order warnings in `ToolChainService.cs`, not functional issues).

If you want, I can also take the remaining step the reviewer hinted at (“sync only changed files” via a dirty file list) — that’s a bigger architectural change than these safety/correctness fixes, but it’s doable.


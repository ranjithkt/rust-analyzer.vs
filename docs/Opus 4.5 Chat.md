# Rust Visual Studio extension WSL support
_Exported on 12/14/2025 at 22:10:49 CST from Cursor (2.2.20)_

---

**User**

This repository contains c# code to create a Visual Studio Extension (not VS code extension) to build, debug, cargo clippy and cargo fmt commands for rust development. But it currently only works on windows machines. What I want is to provide support for WSL. I want to open a path under "\\wsl.localhost\" in Visual Studio, run all the commands mentioned before on WSL environment. Just change the most minimal code possible to support it, so that we do not mess the functionality of Windows. Your main gotchas would be to find all hardcoded paths or windows specific paths or file extensions like exe into supporting WSL paths and WSL tool commands. I have attached a screenshot on how we debug in Windows. After building, the necessary binary file shows up in the debug menu at the top in Visual Studio, I select the relevant exe from that menu and debug. That is how it works on Windows. So, we do not change any of that core logic, but find a way to support WSL. Another LLM already prepared a plan @docs/WSL_SUPPORT_PLAN.md. Your job is to go through that file, go through this entire repo and any VSSDK samples mentioned in that md file and provide your suggestions and catch anything we are missing. Do not implement anything, just review the plan, but very thoroughly and very detailed.

---

**Cursor**

I'll thoroughly review the WSL support plan and explore the repository to provide detailed feedback. Let me start by reading the plan and understanding the codebase structure.

Let me explore the key source files to understand the current implementation and validate the plan.

Let me continue exploring more files to get a complete picture.

Let me continue exploring more files to understand the command execution flow and path handling.

Let me continue exploring a few more key files.

Let me check a few more files to ensure I have a complete picture.

Now I have a comprehensive understanding of the codebase. Let me provide a detailed review of the WSL support plan.

---

# Comprehensive Review of WSL Support Plan

## Executive Summary

The plan is **generally well-structured and identifies most critical areas**, but I've found several **gaps, risks, and additional considerations** that need to be addressed before implementation. The plan correctly identifies the minimal-change philosophy, but some areas require deeper analysis.

---

## 1. What the Plan Gets Right ✅

### 1.1 Correct Identification of Hardcoded Windows Dependencies

The plan accurately identifies the key files:

```13:23:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Constants.cs
    public const string RustLanguageContentType = "rust";
    public const string RustFileExtension = ".rs";
    public const string ManifestFileName = "Cargo.toml"; // NOTE: cargo.exe requires caps 'C'.
    public const string ManifestFileExtension = ".toml";
    public const string RustupSettingsFileName = "settings.toml";

    public const string RustUpExe = "rustup.exe";
    public const string RAExeNameNoExtension = "rust-analyzer";
    public const string CargoExe = "cargo.exe";
```

### 1.2 PathEx Issue is Correctly Identified

The critical issue with `PathEx` forcing Windows separators is spot-on:

```15:19:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\PathEx.cs
    public PathEx(string path)
    {
        EnsureArg.IsNotNull(path, nameof(path));

        _path = path.Replace("/", @"\");
```

### 1.3 Debug Provider Registration is Correct

The plan correctly identifies the `.exe`-only registration:

```20:20:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer\Debugger\DebugLaunchTargetProvider.cs
[ExportLaunchDebugTarget(LaunchDebugTargetProviderOptions.IsRuntimeSupportContext, ProviderType, new[] { ".exe" }, ProviderPriority.Lowest)]
```

---

## 2. Critical Gaps and Missing Items 🔴

### 2.1 **Missing: Test Adapter / Test Explorer Support**

The plan completely **omits** the test execution pipeline. The `TestExecutor.cs` runs test binaries directly:

```113:121:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\TestExecutor.cs
            var rc = fh.LaunchProcessWithDebuggerAttached(exe, exe.GetDirectoryName(), string.Join(" ", args), envDict);
            if (rc != 0)
            {
                tl.L.WriteError("RunTestsFromOneSourceAsync launching test under debugger - returned {0}.", rc);
            }
        }
        else
        {
            using var testExeProc = await ProcessRunner.RunWithLogging(exe, args, exe.GetDirectoryName(), envDict, ct, tl.L, @throw: false);
```

**Issue**: Test executables in WSL are Linux binaries. When `fh.LaunchProcessWithDebuggerAttached()` or `ProcessRunner.RunWithLogging()` is called, they attempt to run a Linux ELF binary on Windows, which will fail.

**Required Addition to Plan**:
- `TestExecutor.cs` must be WSL-aware
- For WSL workspaces, test execution must use `wsl.exe -d <distro> --exec <test-binary> <args>`
- `LaunchProcessWithDebuggerAttached` needs WSL debug transport (similar to debug provider changes)

### 2.2 **Missing: TestExecutablePathCracker Regex**

The regex in `ToolChainService.cs` assumes Windows path format:

```21:21:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Cargo\ToolChainService.cs
    private static readonly Regex TestExecutablePathCracker = new(@"^\s*Executable( unittests)? (.*) \((.*\\(.*)\-[\da-f]{16}.exe)\)$$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

**Issue**: 
- The regex expects `\\` (backslash) separators
- The regex expects `.exe` extension
- For WSL output, cargo prints Linux paths like `/home/user/project/target/debug/mytest-abc123`

**Required Addition**:
- Create a WSL-specific regex or parameterized regex for path parsing
- Handle both `/` and no `.exe` extension

### 2.3 **Missing: rust-analyzer.exe (Language Server)**

The Language Server Protocol client is configured in `LanguageClient.cs`:

```75:75:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer\LanguageService\LanguageClient.cs
            WorkingDirectory = WorkspaceService.CurrentWorkspace?.Location ?? Path.GetDirectoryName(rlsPath),
```

And `RlsInstallerService.cs`:

```100:100:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer\Infrastructure\RlsInstallerService.cs
        return GetInstallFolder(version) + (PathEx)$"rust-analyzer.exe";
```

**Issue**: The extension bundles a Windows `rust-analyzer.exe`. For WSL workspaces, this Windows binary cannot properly analyze Linux Rust projects due to:
- Path format mismatches
- Different target platform expectations
- Potential issues with cross-filesystem access

**Required Consideration**:
- Should WSL mode use `rust-analyzer` installed *inside* the WSL distro?
- If yes, how to detect and launch it?
- This may be out of scope for Stage A (build/clippy/fmt) but critical for full functionality

### 2.4 **Missing: GetBinAndLibPathsAsync for WSL**

The debug environment setup in `ToolchainServiceExtensions.cs`:

```58:61:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Cargo\ToolChainServiceExtensions.cs
    public static async Task<(PathEx Bin, PathEx Lib)> GetBinAndLibPathsAsync(PathEx workingDirectory, CancellationToken ct)
    {
        var root = GetRustupSettingsPath().GetDirectoryName() + @$"toolchains\{await GetDefaultToolchainAsync(workingDirectory, ct)}";
        return (root + "bin", root + $@"lib\rustlib\{AlwaysAvailableTarget}\lib");
```

**Issue**: 
- `GetRustupSettingsPath()` returns a Windows path (`.rustup` folder in USERPROFILE)
- `AlwaysAvailableTarget` is hardcoded to `x86_64-pc-windows-msvc`
- For WSL debugging, paths would be different (e.g., `/home/user/.rustup/toolchains/...`)

**Required Addition**:
- WSL-specific logic to get rustup paths from inside the distro
- Different target triple for Linux (e.g., `x86_64-unknown-linux-gnu`)

### 2.5 **Missing: File Watcher / Workspace Change Events**

The plan mentions this only as an "edge case" but it's more significant. The `MetadataService.cs` relies on file system events:

```71:77:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Cargo\MetadataService.cs
    public async Task<int> OnWorkspaceUpdateAsync(IEnumerable<PathEx> filePaths, CancellationToken ct)
    {
        foreach (var filePath in filePaths.Where(fp => fp.IsTestContainer()))
        {
            OnTestContainerUpdated(filePath);
        }
```

**Issue**: File system watcher behavior on UNC WSL paths (`\\wsl.localhost\...`) may be:
- Slower than local paths
- Missing some events
- Not working at all (depending on VS version and WSL version)

**Required Investigation**:
- Test if VS fires `IFileWatcher` events for WSL UNC paths
- May need polling fallback for WSL workspaces

---

## 3. Additional Technical Concerns 🟡

### 3.1 **Path.Combine with UNC Paths**

The codebase uses `Path.Combine` in several places:

```109:109:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Cargo\BuildJsonOutputParser.cs
        msg.File = fileInfo != null && fileInfo.Value != null ? Path.Combine(workspaceRoot, fileInfo.Value) : msg.File;
```

**Issue**: `Path.Combine(@"\\wsl.localhost\Ubuntu\home\user", "/foo/bar.rs")` returns `/foo/bar.rs` (it treats the Linux absolute path as the root), not `\\wsl.localhost\Ubuntu\foo\bar.rs`.

**Recommendation**: Always use `WslInfo.ToUncPath()` for Linux paths, not `Path.Combine`. The plan mentions this for `BuildJsonOutputParser` but should be more explicit about the danger of `Path.Combine`.

### 3.2 **StringBuildMessagePreprocessor Regex Issues**

The regex patterns in this file are quite fragile:

```11:16:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer\Infrastructure\StringBuildMessagePreprocessor.cs
    private static readonly Func<PathEx, string, string>[] Processors = new[]
    {
        (PathEx rp, string x) => Regex.Replace(x, @"^(\x1b\[\d*m)+", string.Empty),
        (PathEx rp, string x) => Regex.Replace(x, @"^Diff in \\\\\?\\(.*) at line (\d*)\:", "$1($2,1): warning: diffs created by fmt"),
        (PathEx rp, string x) => Regex.Replace(x, @"^( )*\-\-\> (.*)\:(\d+):(\d+)", $"{rp.Combine((PathEx)"$2")}($3,$4): error: clippy\0$0"),
    };
```

**Issues**:
1. The `Diff in \\\\?\\` pattern is Windows-specific extended path format
2. For WSL, fmt outputs paths like `Diff in /home/user/src/main.rs at line 10:`
3. The clippy path capture `$2` is then combined with `rp` using `Combine`, which has the same `Path.Combine` issue

**Recommendation**: Need WSL-specific regex variants or smarter path detection.

### 3.3 **File.Exists for UNC Paths**

The code checks file existence in several places:

```66:68:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer\Debugger\DebugLaunchTargetProvider.cs
            if (!File.Exists(processName))
            {
                var message = string.Format("Unable to find file: '{0}'.", processName);
```

**Consideration**: `File.Exists(@"\\wsl.localhost\Ubuntu\home\user\target\debug\myapp")` should work, but may be slow. Also, this path check happens *after* the target path is computed with `.exe` extension (for non-WSL). Ensure the path is correct first.

### 3.4 **Environment Block Format Differences**

The `EnvironmentExtensions.cs` creates Windows-style environment blocks:

```40:46:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.TestAdapter\Common\EnvironmentExtensions.cs
    public static string ToEnvironmentBlock(this IDictionary<string, string> @this)
    {
        return @this
            .Aggregate(new StringBuilder(), (acc, e) => acc.Append($"{e.Key}={e.Value.Replace('=', EqEscape)}\0"))
            .Append('\0')
            .ToString();
    }
```

**Issue**: This null-terminated format is Windows-specific. When invoking `wsl.exe`, environment variables should be passed differently (via `--exec` with env, or exported in shell).

**Recommendation**: For WSL command invocation, environment handling needs separate logic.

---

## 4. Design Suggestions 📝

### 4.1 **Recommended: Create IRemoteTarget Abstraction**

I noticed there's already a stub for remote targets:

```1:17:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.Remote\RemoteTargets.cs
using System.Collections.Generic;

namespace KS.RustAnalyzer.Remote;

public interface IRemoteTargets
{
    IEnumerable<IRemoteTarget> Enumerate();
}

public class RemoteTargets : IRemoteTargets
{
    public IEnumerable<IRemoteTarget> Enumerate()
    {
        throw new System.NotImplementedException();
    }
}
```

And:

```1:7:c:\Repos3\rust-analyzer.vs\src\RustAnalyzer.Remote\Wsl.cs
namespace KS.RustAnalyzer.Remote;

public interface IRemoteTarget
{
    public string Id { get; }
}
```

**Suggestion**: Build `WslInfo` as an implementation of `IRemoteTarget` with:
- `DistroName`
- `ToLinuxPath(PathEx uncPath)`
- `ToUncPath(string linuxPath)`
- `WslExePath` (usually `wsl.exe` from `%SystemRoot%\System32\wsl.exe`)

This creates a clean extension point for future remote targets (SSH, containers, etc.).

### 4.2 **Recommended: Workspace-Scoped WSL Detection**

Following the VSSDK Open Folder sample pattern, the WSL detection should be:
- Cached per-workspace (not per-call)
- Injected into services via the workspace context
- Exposed through a service interface (e.g., `IWslWorkspaceInfo`)

### 4.3 **Recommended: Staged Feature Flags**

Add explicit feature flags for staged rollout:
- `EnableWslBuild` (Stage A)
- `EnableWslDebug` (Stage B)
- `EnableWslTests` (Stage C - new)

This allows users to opt-in to less stable features while keeping build/clippy/fmt working.

---

## 5. Missing Open Questions to Resolve 🔍

### 5.1 What if `wsl.exe` is not in PATH?

The plan assumes `wsl.exe` exists. Should verify at startup and provide clear error.

### 5.2 Multiple Distro Support

If user has multiple distros, how is the distro selected? The plan says "extract from UNC path" but:
- What if the path is `\\wsl$\Ubuntu` but cargo is only installed in `\\wsl$\Debian`?
- Should there be a distro override setting per workspace?

### 5.3 WSL1 vs WSL2

Are there behavioral differences? WSL2 has better I/O performance and full syscall compatibility. The plan doesn't distinguish.

### 5.4 Line Ending Handling

WSL cargo outputs LF-terminated lines. Windows tooling might expect CRLF. The `ProcessRunner` already splits lines correctly, but verify regex patterns work with both.

### 5.5 Path Length Limits

WSL UNC paths can be very long. Windows has 260-character limit unless long path support is enabled. Should this be documented as a requirement?

---

## 6. File-by-File Review Additions

The plan lists files to modify but misses:

| File | Required Change |
|------|-----------------|
| `TestExecutor.cs` | WSL-aware test execution via `wsl.exe` |
| `TestDiscoverer.cs` / `TestDiscovererCommon.cs` | May need WSL path mapping |
| `LanguageClient.cs` | Consider WSL rust-analyzer invocation |
| `RlsInstallerService.cs` | Potentially skip for WSL (use distro's RA) |
| `PathExExtensions.cs` | `FileExists`/`DirectoryExists` work on UNC but may be slow |
| `EnvironmentExtensions.cs` | Different env passing for WSL |

---

## 7. Testing Recommendations

Beyond the plan's testing scenarios:

### 7.1 Add Test Cases For:
- Paths with spaces in distro name (unlikely but possible)
- Paths with Unicode characters
- Very deep directory structures (path length)
- Multiple cargo workspaces within WSL
- Workspace with both Windows and WSL dependencies (if even possible)
- Test execution via Test Explorer

### 7.2 Regression Tests:
- Run full test suite with Windows paths to ensure no breakage
- Add unit tests for `WslInfo.ToLinuxPath` and `ToUncPath` with edge cases

---

## 8. Summary of Recommendations

1. **Add Test Adapter/Executor to the plan** - This is a major gap
2. **Add TestExecutablePathCracker regex handling** - Critical for test discovery
3. **Clarify rust-analyzer LSP behavior for WSL** - May need distro-local RA
4. **Add GetBinAndLibPathsAsync WSL handling** - Required for debugging
5. **Document file watcher limitations** - May need fallback
6. **Be explicit about Path.Combine dangers** - Use WslInfo mapper exclusively
7. **Update StringBuildMessagePreprocessor** for Linux output format
8. **Create IRemoteTarget-based abstraction** for clean design
9. **Add feature flags for staged rollout**
10. **Document requirements** (WSL2 preferred, long paths, etc.)

The plan is a solid foundation but needs these additions before implementation begins. I recommend creating a small experimental branch first to validate `bstrPortName = "SSH:wsl+<distro>"` and the test execution path before committing to the full implementation.

```plaintext
    public const string RustLanguageContentType = "rust";
    public const string RustFileExtension = ".rs";
    public const string ManifestFileName = "Cargo.toml"; // NOTE: cargo.exe requires caps 'C'.
    public const string ManifestFileExtension = ".toml";
    public const string RustupSettingsFileName = "settings.toml";

    public const string RustUpExe = "rustup.exe";
    public const string RAExeNameNoExtension = "rust-analyzer";
    public const string CargoExe = "cargo.exe";
```

```plaintext
    public PathEx(string path)
    {
        EnsureArg.IsNotNull(path, nameof(path));

        _path = path.Replace("/", @"\");
```

```plaintext
[ExportLaunchDebugTarget(LaunchDebugTargetProviderOptions.IsRuntimeSupportContext, ProviderType, new[] { ".exe" }, ProviderPriority.Lowest)]
```

```plaintext
            var rc = fh.LaunchProcessWithDebuggerAttached(exe, exe.GetDirectoryName(), string.Join(" ", args), envDict);
            if (rc != 0)
            {
                tl.L.WriteError("RunTestsFromOneSourceAsync launching test under debugger - returned {0}.", rc);
            }
        }
        else
        {
            using var testExeProc = await ProcessRunner.RunWithLogging(exe, args, exe.GetDirectoryName(), envDict, ct, tl.L, @throw: false);
```

```plaintext
    private static readonly Regex TestExecutablePathCracker = new(@"^\s*Executable( unittests)? (.*) \((.*\\(.*)\-[\da-f]{16}.exe)\)$$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

```plaintext
            WorkingDirectory = WorkspaceService.CurrentWorkspace?.Location ?? Path.GetDirectoryName(rlsPath),
```

```plaintext
        return GetInstallFolder(version) + (PathEx)$"rust-analyzer.exe";
```

```plaintext
    public static async Task<(PathEx Bin, PathEx Lib)> GetBinAndLibPathsAsync(PathEx workingDirectory, CancellationToken ct)
    {
        var root = GetRustupSettingsPath().GetDirectoryName() + @$"toolchains\{await GetDefaultToolchainAsync(workingDirectory, ct)}";
        return (root + "bin", root + $@"lib\rustlib\{AlwaysAvailableTarget}\lib");
```

```plaintext
    public async Task<int> OnWorkspaceUpdateAsync(IEnumerable<PathEx> filePaths, CancellationToken ct)
    {
        foreach (var filePath in filePaths.Where(fp => fp.IsTestContainer()))
        {
            OnTestContainerUpdated(filePath);
        }
```

```plaintext
        msg.File = fileInfo != null && fileInfo.Value != null ? Path.Combine(workspaceRoot, fileInfo.Value) : msg.File;
```

```plaintext
    private static readonly Func<PathEx, string, string>[] Processors = new[]
    {
        (PathEx rp, string x) => Regex.Replace(x, @"^(\x1b\[\d*m)+", string.Empty),
        (PathEx rp, string x) => Regex.Replace(x, @"^Diff in \\\\\?\\(.*) at line (\d*)\:", "$1($2,1): warning: diffs created by fmt"),
        (PathEx rp, string x) => Regex.Replace(x, @"^( )*\-\-\> (.*)\:(\d+):(\d+)", $"{rp.Combine((PathEx)"$2")}($3,$4): error: clippy\0$0"),
    };
```

```plaintext
            if (!File.Exists(processName))
            {
                var message = string.Format("Unable to find file: '{0}'.", processName);
```

```plaintext
    public static string ToEnvironmentBlock(this IDictionary<string, string> @this)
    {
        return @this
            .Aggregate(new StringBuilder(), (acc, e) => acc.Append($"{e.Key}={e.Value.Replace('=', EqEscape)}\0"))
            .Append('\0')
            .ToString();
    }
```

```plaintext
using System.Collections.Generic;

namespace KS.RustAnalyzer.Remote;

public interface IRemoteTargets
{
    IEnumerable<IRemoteTarget> Enumerate();
}

public class RemoteTargets : IRemoteTargets
{
    public IEnumerable<IRemoteTarget> Enumerate()
    {
        throw new System.NotImplementedException();
    }
}
```

```plaintext
namespace KS.RustAnalyzer.Remote;

public interface IRemoteTarget
{
    public string Id { get; }
}
```


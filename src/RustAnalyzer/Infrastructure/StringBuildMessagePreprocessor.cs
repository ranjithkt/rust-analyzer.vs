using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KS.RustAnalyzer.TestAdapter.Common;

namespace KS.RustAnalyzer.Infrastructure;

public sealed class StringBuildMessagePreprocessor
{
    // Windows-specific processors
    private static readonly Func<PathEx, WslInfo, string, string>[] WindowsProcessors = new Func<PathEx, WslInfo, string, string>[]
    {
        (PathEx rp, WslInfo _, string x) => Regex.Replace(x, @"^(\x1b\[\d*m)+", string.Empty),
        (PathEx rp, WslInfo _, string x) => Regex.Replace(x, @"^Diff in \\\\\?\\(.*) at line (\d*)\:", "$1($2,1): warning: diffs created by fmt"),
        (PathEx rp, WslInfo _, string x) => Regex.Replace(x, @"^( )*\-\-\> (.*)\:(\d+):(\d+)", $"{rp.Combine((PathEx)"$2")}($3,$4): error: clippy\0$0"),
    };

    // WSL/Linux-specific processors
    private static readonly Func<PathEx, WslInfo, string, string>[] WslProcessors = new Func<PathEx, WslInfo, string, string>[]
    {
        // Strip ANSI escape codes
        (PathEx rp, WslInfo wsl, string x) => Regex.Replace(x, @"^(\x1b\[\d*m)+", string.Empty),

        // Handle rustfmt diff output with Linux paths: "Diff in /home/.../file.rs at line N:"
        (PathEx rp, WslInfo wsl, string x) =>
        {
            var match = Regex.Match(x, @"^Diff in (\/[^\s]+) at line (\d*)\:");
            if (match.Success && wsl != null)
            {
                var linuxPath = match.Groups[1].Value;
                var line = match.Groups[2].Value;
                var uncPath = wsl.ToUncPath(linuxPath);
                return $"{uncPath}({line},1): warning: diffs created by fmt";
            }
            return x;
        },

        // Handle clippy/rustc output with Linux absolute paths: "--> /home/.../file.rs:line:col"
        (PathEx rp, WslInfo wsl, string x) =>
        {
            var match = Regex.Match(x, @"^( )*\-\-\> (\/[^\s:]+)\:(\d+):(\d+)");
            if (match.Success && wsl != null)
            {
                var indent = match.Groups[1].Value;
                var linuxPath = match.Groups[2].Value;
                var line = match.Groups[3].Value;
                var col = match.Groups[4].Value;
                var uncPath = wsl.ToUncPath(linuxPath);
                return $"{uncPath}({line},{col}): error: clippy\0{indent}--> {linuxPath}:{line}:{col}";
            }
            return x;
        },

        // Handle clippy/rustc output with relative paths (needs combining with workspace root)
        (PathEx rp, WslInfo wsl, string x) =>
        {
            var match = Regex.Match(x, @"^( )*\-\-\> ([^\/][^\s:]+)\:(\d+):(\d+)");
            if (match.Success && !match.Groups[2].Value.StartsWith("/"))
            {
                var indent = match.Groups[1].Value;
                var relativePath = match.Groups[2].Value;
                var line = match.Groups[3].Value;
                var col = match.Groups[4].Value;
                // Convert forward slashes in relative path to backslashes and combine with root
                var normalizedPath = relativePath.Replace('/', '\\');
                var fullPath = rp.Combine((PathEx)normalizedPath);
                return $"{fullPath}({line},{col}): error: clippy\0{indent}--> {relativePath}:{line}:{col}";
            }
            return x;
        },
    };

    public IEnumerable<string> Preprocess(PathEx rootPath, string message)
    {
        // Check if rootPath is a WSL workspace
        WslInfo.TryParse(rootPath, out var wslInfo);

        var processors = wslInfo != null ? WslProcessors : WindowsProcessors;
        return processors.Aggregate(message, (acc, e) => e(rootPath, wslInfo, acc)).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }
}

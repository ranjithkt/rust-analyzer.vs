using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KS.RustAnalyzer.TestAdapter.Common;
using Newtonsoft.Json.Linq;
using static KS.RustAnalyzer.TestAdapter.Common.DetailedBuildMessage;

namespace KS.RustAnalyzer.TestAdapter.Cargo;

/// <summary>
/// References
/// - https://doc.rust-lang.org/cargo/reference/external-tools.html#json-messages.
/// - https://doc.rust-lang.org/rustc/json.html.
/// </summary>
public static class BuildJsonOutputParser
{
    private static readonly IReadOnlyDictionary<string, Level> LevelToMessageTypeMap =
        new Dictionary<string, Level>()
        {
            ["error"] = Level.Error,
            ["warning"] = Level.Warning,
            ["note"] = Level.None,
            ["help"] = Level.None,
            ["failure-note"] = Level.None,
            ["error: internal compiler error"] = Level.Error,
        };

    private static readonly Regex CompilerArtifactMessageCracker1 =
        new(@"^(.*) (.*) \((.*)\+(.*)\)$", RegexOptions.Compiled);

    private static readonly Regex CompilerArtifactMessageCracker2 =
        new(@"^(.*)\+(.*)@(.*)$", RegexOptions.Compiled);

    public static BuildMessage[] Parse(PathEx workspaceRoot, string jsonLine, TL tl)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return Array.Empty<BuildMessage>();
        }

        // Fast-path:
        // `cargo --message-format json` can emit hundreds/thousands of `compiler-artifact` lines even when nothing
        // is rebuilt (fresh=true). Parsing each line as JSON is expensive and makes no-op builds feel "slow".
        // Skip those without JSON parsing.
        // Also skip build-finished records.
        var trimmed = jsonLine.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '{')
        {
            if (trimmed.IndexOf("\"reason\":\"compiler-artifact\"", StringComparison.Ordinal) >= 0 &&
                trimmed.IndexOf("\"fresh\":true", StringComparison.Ordinal) >= 0)
            {
                return Array.Empty<BuildMessage>();
            }

            if (trimmed.IndexOf("\"reason\":\"build-finished\"", StringComparison.Ordinal) >= 0)
            {
                return Array.Empty<BuildMessage>();
            }
        }

        dynamic obj;
        try
        {
            obj = JObject.Parse(jsonLine);
        }
        catch (Exception e)
        {
            tl.L.WriteLine("CargoJsonOutputParser failed to parse line: {0}. Exception {1}.", jsonLine, e);
            tl.T.TrackException(e, new[] { ("Id", "JObjectParse"), ("Line", jsonLine) });
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }

        try
        {
            if (obj.reason == "compiler-artifact")
            {
                return ParseCompilerArtifact(obj);
            }
            else if (obj.reason == "compiler-message")
            {
                return ParseCompilerMessage(workspaceRoot, obj);
            }
        }
        catch (Exception e)
        {
            tl.L.WriteLine("CargoJsonOutputParser failed to parse line: {0}. Exception {1}.", jsonLine, e);
            tl.T.TrackException(e, new[] { ("Id", "ParseCompilerX"), ("Line", jsonLine) });
            return new[] { new StringBuildMessage { Message = jsonLine } };
        }

        return Array.Empty<BuildMessage>();
    }

    private static BuildMessage[] ParseCompilerMessage(PathEx workspaceRoot, dynamic obj)
    {
        if (obj.message.spans == null || obj.message.spans.Count == 0)
        {
            return new BuildMessage[] { CreateBuildMessage(workspaceRoot, obj) };
        }

        // rustc often provides many spans for a single diagnostic (e.g., one per field),
        // but the rendered message text is identical. Emitting one message per span floods
        // the Output window with duplicates. Prefer the primary span (or the first span).
        var spans = (obj.message.spans as IEnumerable<dynamic>)?.ToArray() ?? Array.Empty<dynamic>();
        if (spans.Length == 0)
        {
            return new BuildMessage[] { CreateBuildMessage(workspaceRoot, obj) };
        }

        dynamic primary = spans.FirstOrDefault(s => s != null && s.is_primary != null && (bool)s.is_primary.Value) ?? spans[0];
        return new BuildMessage[]
        {
            CreateBuildMessage(workspaceRoot, obj, primary.file_name, primary.line_start, primary.column_start),
        };
    }

    private static int GetIntValue(dynamic obj, int defaultValue = default)
    {
        var value = 0;
        return obj != null && obj.Value != null && int.TryParse(obj.Value.ToString(), out value)
            ? value
            : defaultValue;
    }

    private static DetailedBuildMessage CreateBuildMessage(PathEx workspaceRoot, dynamic obj, dynamic fileInfo = null, dynamic lineInfo = null, dynamic colInfo = null)
    {
        // Mode 1: WSL UNC workspace
        // Mode 2: Windows-local workspace + WSL execution (if selected)
        var isWsl = TargetSystemSelection.TryGetWslExecutionContext(workspaceRoot, out var wslInfo, out _);

        var srcPath = TryGetString(obj, "target.src_path");
        var resolvedSrcPath = ResolvePathForVs(srcPath, workspaceRoot, isWsl, wslInfo);

        var msg = new DetailedBuildMessage
        {
            Code = GetMessageCode(obj.message),
            ColumnNumber = GetIntValue(colInfo, 1),
            File = resolvedSrcPath,
            HelpKeyword = GetMessageCode(obj.message),
            LineNumber = GetIntValue(lineInfo, 1),
            ProjectFile = ResolvePathForVs(GetProjectFile(obj), workspaceRoot, isWsl, wslInfo),
            SubCategory = null,
            TaskText = obj.message.message.Value,
            Type = GetMessageType(obj.message.level.Value),
        };

        if (fileInfo != null && fileInfo.Value != null)
        {
            var fileInfoPath = (string)fileInfo.Value;
            msg.File = ResolveFileInfoPath(fileInfoPath, workspaceRoot, isWsl, wslInfo);
        }

        msg.LogMessage = GetLogMessage(obj.message, msg);

        return msg;
    }

    /// <summary>
    /// Resolves a path from cargo output for VS navigation.
    /// For WSL workspaces, converts Linux absolute paths to Windows UNC paths.
    /// </summary>
    private static string ResolvePathForVs(string path, PathEx workspaceRoot, bool isWsl, WslInfo wslInfo)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (isWsl && WslInfo.IsLinuxAbsolutePath(path))
        {
            if (wslInfo != null)
            {
                return wslInfo.ToUncPath(path);
            }

            return WslPathMapper.TryWslToWindowsPath(path, out var winPath) ? winPath : path;
        }

        return path;
    }

    /// <summary>
    /// Resolves the file_name field from spans, which may be relative or absolute.
    /// </summary>
    private static string ResolveFileInfoPath(string fileInfoPath, PathEx workspaceRoot, bool isWsl, WslInfo wslInfo)
    {
        if (string.IsNullOrEmpty(fileInfoPath))
        {
            return fileInfoPath;
        }

        if (isWsl)
        {
            // For WSL, cargo outputs Linux paths
            if (WslInfo.IsLinuxAbsolutePath(fileInfoPath))
            {
                // Absolute Linux path - convert to UNC (Mode 1) or Windows drive path (Mode 2)
                if (wslInfo != null)
                {
                    return wslInfo.ToUncPath(fileInfoPath);
                }

                return WslPathMapper.TryWslToWindowsPath(fileInfoPath, out var winPath) ? winPath : fileInfoPath;
            }
            else
            {
                // Relative path - combine with workspace root.
                var normalizedRelPath = fileInfoPath.Replace('/', '\\');
                return Path.Combine(workspaceRoot, normalizedRelPath);
            }
        }
        else
        {
            // Windows workspace - use standard path combination
            return Path.Combine(workspaceRoot, fileInfoPath);
        }
    }

    private static string GetProjectFile(dynamic obj)
    {
        var manifestPath = TryGetString(obj, "manifest_path");
        if (!string.IsNullOrEmpty(manifestPath))
        {
            return manifestPath;
        }

        // Fallback: use package_id if present, else return empty string (don't throw).
        return TryGetString(obj, "package_id") ?? string.Empty;
    }

    private static string TryGetString(dynamic obj, string jsonPath)
    {
        try
        {
            if (obj == null)
            {
                return null;
            }

            var token = (JToken)obj;
            var selected = token.SelectToken(jsonPath);
            if (selected == null || selected.Type == JTokenType.Null)
            {
                return null;
            }

            return selected.Type == JTokenType.String
                ? selected.Value<string>()
                : selected.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static dynamic GetMessageCode(dynamic obj)
    {
        return obj.code != null && obj.code.code != null
            ? obj.code.code.Value
            : "RS0000";
    }

    private static dynamic GetLogMessage(dynamic message, DetailedBuildMessage msg)
    {
        var logMsgText = $@"{msg.File}({msg.LineNumber},{msg.ColumnNumber}): {msg.Type} {msg.Code}: {msg.TaskText}";
        var rendered = message.rendered != null ? $"Details:\r\n{message.rendered.Value}" : string.Empty;

        return $"{logMsgText}\r\n{rendered}";
    }

    private static Level GetMessageType(string level)
    {
        return LevelToMessageTypeMap.TryGetValue(level, out Level type)
            ? type
            : Level.Error;
    }

    private static BuildMessage[] ParseCompilerArtifact(dynamic obj)
    {
        if ((bool)obj.fresh.Value)
        {
            return Array.Empty<BuildMessage>();
        }

        var packageId = obj.package_id.Value as string;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return Array.Empty<BuildMessage>();
        }

        // Local path dependencies (common in workspaces) look like:
        //   path+file:///mnt/c/Repos/Rust/alpaca-core#0.1.0
        //   path+file:///mnt/c/Repos/Rust/trader-one/common#0.1.0
        // Show a friendly "Compiling <name> v<ver>" instead of raw JSON.
        if (packageId.StartsWith("path+file://", StringComparison.OrdinalIgnoreCase))
        {
            var hash = packageId.LastIndexOf('#');
            if (hash > 0 && hash < packageId.Length - 1)
            {
                var version = packageId.Substring(hash + 1);
                var before = packageId.Substring(0, hash);
                var name = before.TrimEnd('/').Split('/').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new[] { new StringBuildMessage { Message = $"   Compiling {name} v{version}" } };
                }
            }

            return new[] { new StringBuildMessage { Message = $"   Compiling {packageId}" } };
        }

        var matches = CompilerArtifactMessageCracker1.Matches(packageId);
        if (matches.Count != 0)
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {matches[0].Groups[1].Value} v{matches[0].Groups[2].Value} ({matches[0].Groups[4].Value})" } };
        }

        matches = CompilerArtifactMessageCracker2.Matches(packageId);
        if (matches.Count != 0)
        {
            return new[] { new StringBuildMessage { Message = $"   Compiling {matches[0].Groups[2].Value} v{matches[0].Groups[3].Value}" } };
        }

        // Unknown package_id format: be conservative and avoid dumping raw JSON into the output.
        return new[] { new StringBuildMessage { Message = $"   Compiling {packageId}" } };
    }
}
